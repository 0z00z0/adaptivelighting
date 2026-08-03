using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Subjects;

using AdaptiveLighting.Configuration;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>
///     The mode brain: reads the house-mode select, derives the active <see cref="ModeKind"/> and scene, and owns
///     the set, retain and reset lifecycle. A mode set by hand or by a period's
///     <see cref="TimePeriodConfig.SetsMode"/> is retained until a reset trigger fires; every reset returns the
///     select to the single Normal option.
/// </summary>
/// <remarks>
///     Never mutates <see cref="HouseState"/>. Every mode change it makes is an
///     <c>input_select.select_option</c> that flows back through Home Assistant and the normal
///     <see cref="Changed"/> path, like a human turning the dial. Its only clock is the injected
///     <see cref="IScheduler"/>.
/// </remarks>
public sealed class ModeMonitor : IDisposable
{
	private const string SelectDomain = "input_select";
	private const string SelectOptionService = "select_option";
	private const string PersonDomain = "person";
	private const string DeviceTrackerDomain = "device_tracker";
	private const string HomeState = "home";

	private readonly IHaContext _ha;
	private readonly GlobalConfig _global;
	private readonly ILogger _logger;
	private readonly IScheduler _scheduler;
	private readonly IReadOnlyList<TimePeriodConfig> _periods;
	private readonly CircadianCalculator _circadian;
	private readonly Func<SunTimes> _sunTimes;
	private readonly IReadOnlyCollection<string> _areaMotionSensors;

	// Which sensors may start each StartsOnMotion period. A null value means any watched sensor; an empty set is a
	// period naming rooms none of whose sensors resolved, which can never fire. Empty under Home Assistant's
	// period authority, so the dropdown stays the only boundary.
	private readonly Dictionary<string, IReadOnlySet<string>?> _motionStartPeriods = new(StringComparer.OrdinalIgnoreCase);

	// The local day each period was last entered, by whichever path entered it. Motion may not start a period the
	// day has already seen.
	private readonly Dictionary<string, DateOnly> _enteredOn = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	///     Where the period the engine is running in is written down, so the next start can tell whether a boundary
	///     went by while it was stopped. <c>null</c> reads as "we do not know".
	/// </summary>
	private readonly ILastPeriodStore? _lastPeriod;

	/// <summary>The period select, or <c>null</c> when none is configured. Which direction it grants is its own to say.</summary>
	private readonly PeriodSelectReader? _periodSelect;

	private readonly Subject<Unit> _changed = new();
	private readonly CompositeDisposable _subscriptions = [];

	// Guards every mutable field below except _warnedValues and _warnedNoNormal, which carry their own atomics.
	// Nothing that calls Home Assistant runs under it.
	private readonly object _gate = new();

	// CurrentModeValue is read from several Rx subscriptions, so TryAdd is the atomic bit; the value is unused.
	private readonly ConcurrentDictionary<string, byte> _warnedValues = new(StringComparer.OrdinalIgnoreCase);

	// Reset fires from several subscriptions; the "no Normal target" warning must not spam the log every tick.
	private int _warnedNoNormal;

	private DateTimeOffset _activatedAt;
	private DateTimeOffset _lastMotionAt;
	private bool _inactivityLatched;
	private string? _previousPeriodName;
	private bool _started;

	// The option the no-motion rule last wrote, so a mode the engine set can be told from the same mode a person
	// chose. Cleared the moment the select stops reading it, and never persisted: after a restart nobody knows why
	// the select stands where it does.
	private string? _inactivityActivated;

	// The last sentence AnnounceForcedMode wrote. Empty means nothing was being forced last time we looked.
	private string _announcedForce = "";

	// Bounds the mirror log line only. The call itself compares against what the select actually reads, so a
	// select that never echoes is asked again.
	private string? _mirroredPeriodOption;

	// Armed by Start, spent on the first tick that can read the select. Held open until the select answers,
	// because an input_select can be unavailable for a while after a Home Assistant restart.
	private bool _startPeriodModePending;

	// What the previous run left on disk, read once in Start. Null is "we do not know", which is not the same as
	// knowing a boundary was crossed.
	private string? _periodAtLastRun;

	// What this run believes is on disk now, so the file is written on a period change and not on every tick.
	private string? _persistedPeriodName;

	/// <summary>Creates the mode brain.</summary>
	/// <remarks>
	///     <paramref name="areaMotionSensors"/> is the union across every area; an option with an empty
	///     <see cref="HouseModeOptionConfig.ResetPresenceSensors"/> resets on any of them.
	///     <paramref name="motionSensorsByArea"/> is that same union split by area id, which is what
	///     <see cref="TimePeriodConfig.StartsOnMotionAreas"/> names. Without a
	///     <paramref name="lastPeriod"/> the monitor never learns that a boundary was crossed during an outage.
	///     Whichever of <paramref name="periodSelect"/>'s two directions is live is the one this uses: its read
	///     delegate goes into this monitor's own calculator, or this writes the select. The reader is what makes
	///     those mutually exclusive.
	/// </remarks>
	public ModeMonitor(
		IHaContext ha,
		GlobalConfig global,
		ILogger logger,
		IScheduler scheduler,
		IReadOnlyList<TimePeriodConfig> periods,
		Func<SunTimes> sunTimes,
		IReadOnlyCollection<string> areaMotionSensors,
		ILastPeriodStore? lastPeriod = null,
		PeriodSelectReader? periodSelect = null,
		IReadOnlyDictionary<string, IReadOnlyList<string>>? motionSensorsByArea = null)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_global = global ?? throw new ArgumentNullException(nameof(global));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
		_periods = periods ?? throw new ArgumentNullException(nameof(periods));
		_sunTimes = sunTimes ?? throw new ArgumentNullException(nameof(sunTimes));
		_areaMotionSensors = areaMotionSensors ?? throw new ArgumentNullException(nameof(areaMotionSensors));
		_lastPeriod = lastPeriod;
		_periodSelect = periodSelect;

		_circadian = new CircadianCalculator(periods, global, sunTimes, roomLevels: null, periodSelect?.ReadPeriod);

		// The one branch the motion-start rule rests on, mirroring PeriodSelectReader's: under Home Assistant's
		// period authority the dropdown is the boundary, so the table is left empty and motion starts nothing.
		if (periodSelect?.ReadPeriod is null)
			BuildMotionStartPeriods(motionSensorsByArea);
	}

	private void BuildMotionStartPeriods(IReadOnlyDictionary<string, IReadOnlyList<string>>? motionSensorsByArea)
	{
		foreach (TimePeriodConfig period in _periods)
		{
			if (!period.StartsOnMotion || period.Name is not { Length: > 0 })
				continue;

			if (period.StartsOnMotionAreas.Count == 0)
			{
				_motionStartPeriods[period.Name] = null;
				continue;
			}

			HashSet<string> sensors = new(StringComparer.OrdinalIgnoreCase);

			foreach (string areaId in period.StartsOnMotionAreas)
				if (motionSensorsByArea?.TryGetValue(areaId.Trim(), out IReadOnlyList<string>? found) == true)
					sensors.UnionWith(found);

			// Kept even when empty: a named room that resolved nothing must never read as "any room".
			_motionStartPeriods[period.Name] = sensors;
		}
	}

	/// <summary>Fires whenever the kill switch or the house-mode select changes state.</summary>
	public IObservable<Unit> Changed => _changed;

	/// <summary>
	///     Whether the engine is currently forbidden from commanding anything. Reads
	///     <see cref="GlobalConfig.EffectiveKillSwitchEntity"/> through <see cref="GlobalConfig.KillSwitchActiveWhenOff"/>.
	/// </summary>
	public bool KillSwitchActive
	{
		get
		{
			if (_global.EffectiveKillSwitchEntity is not { Length: > 0 } entityId)
				return false;

			EntityState? state = _ha.GetState(entityId);

			// An unreadable state is "not killed" whichever polarity is configured.
			if (state?.State is null)
				return false;

			// With the built-in switch defaulted in, polarity is forced to the enabled-flag reading (off means
			// muzzled); KillSwitchActiveWhenOff governs an explicit entity only. Mirrors ModeService.GetToggles.
			bool enabledFlag = _global.KillSwitchIsDefaulted || _global.KillSwitchActiveWhenOff;
			return enabledFlag ? state.IsOff() : state.IsOn();
		}
	}

	/// <summary>
	///     The raw current house-mode option string, or <c>null</c> when the select is unconfigured, unknown, or
	///     unavailable. Warns once per distinct value that no option classifies (an HA-side rename tripwire).
	/// </summary>
	public string? CurrentModeValue
	{
		get
		{
			if (_global.HouseMode?.Entity is not { Length: > 0 } entityId)
				return null;

			if (_ha.GetState(entityId).AsUsableState() is not { } value)
				return null;

			if (_global.HouseMode.OptionFor(value) is null && _warnedValues.TryAdd(value, 0))
				_logger.LogWarning(
					"House-mode select {Entity} reports '{Value}', which no option classifies; treating it as Normal.",
					entityId, value);

			return value;
		}
	}

	// The select-standing option: what the select's current value classifies to. This is the option the
	// set → retain → reset lifecycle acts on, because every reset writes the SELECT back to Normal.
	private HouseModeOptionConfig? CurrentOption => _global.HouseMode?.OptionFor(CurrentModeValue);

	// Home Assistant owns the select. Every write of it is gated on this, and so is every rule of this engine's
	// that would decide the mode: the dropdown is then the only thing that moves the house.
	private bool HouseModeIsHomeAssistants => _global.HouseMode?.HomeAssistantDecides ?? false;

	/// <summary>
	///     The first option (in list order) any of whose <see cref="HouseModeOptionConfig.ActivateWhileOn"/> entities
	///     is currently on, together with that entity, or <c>null</c> when none is. Such an option forces the
	///     effective house mode whatever the select reads; the select is never written from this, so no loop.
	/// </summary>
	/// <remarks>
	///     The entity comes back beside the option. This is the only read of "which entity holds this mode on"; a
	///     second one could name a different entity from the one the engine acted on.
	/// </remarks>
	private (HouseModeOptionConfig Option, string EntityId)? ActivatedNow
	{
		get
		{
			// Forcing a mode is this engine deciding one, which is what standing down means.
			if (HouseModeIsHomeAssistants || _global.HouseMode is not { } houseMode)
				return null;

			foreach (HouseModeOptionConfig option in houseMode.Options)
				if (option.ActivateWhileOn.FirstOrDefault(IsOn) is { Length: > 0 } entityId)
					return (option, entityId);

			return null;
		}
	}

	/// <summary>
	///     What is forcing the effective mode, or <c>null</c> when the select's own value is the whole story.
	/// </summary>
	/// <remarks>
	///     Read live, in the order the engine decides in: the <c>ActivateWhileOn</c> overlay wins over the select,
	///     so it is asked first. Every <see cref="ModeKind"/> is reported, not only <see cref="ModeKind.Away"/>.
	/// </remarks>
	public ForcedMode? Forced
	{
		get
		{
			if (ActivatedNow is { } activated)
				return new ForcedMode(
					activated.Option.Kind,
					activated.Option.Value?.Trim() ?? "",
					ModeForceSource.WhileEntityOn,
					activated.EntityId,
					_ha.GetState(activated.EntityId).AsUsableState());

			string? claimed;
			lock (_gate)
				claimed = _inactivityActivated;

			if (claimed is not { Length: > 0 })
				return null;

			// The claim holds only while the value the engine wrote is still what the select reads.
			if (!string.Equals(CurrentModeValue, claimed, StringComparison.OrdinalIgnoreCase)
				|| _global.HouseMode?.OptionFor(claimed) is not { } option)
				return null;

			return new ForcedMode(option.Kind, claimed, ModeForceSource.NoMotionTimeout);
		}
	}

	/// <summary>
	///     The option the engine actually acts on: an <see cref="HouseModeOptionConfig.ActivateWhileOn"/> override
	///     wins over the select's value, else the select decides exactly as before. Empty ActivateWhileOn lists
	///     leave this equal to the select-standing option, so the mode is behaviour-neutral without them.
	/// </summary>
	private HouseModeOptionConfig? EffectiveOption => ActivatedNow?.Option ?? CurrentOption;

	/// <summary>The kind of the effective option; <see cref="ModeKind.Normal"/> when nothing classifies.</summary>
	public ModeKind ActiveKind => EffectiveOption?.Kind ?? ModeKind.Normal;

	/// <summary>
	///     The effective option's <c>scene.*</c> when it names one, whatever its kind. Applied once on entry by the
	///     orchestrator. On Away and Guest the scene stands because they pause the engine; on Normal and Sleep it
	///     is a one-shot the ordinary commands may override. The area-pause logic stays gated on Away and Guest.
	/// </summary>
	public string? ActiveScene =>
		EffectiveOption is { Scene: { Length: > 0 } scene } ? scene : null;

	// An unreadable state is "not on", so a vanished sensor stops forcing its mode instead of pinning it.
	private bool IsOn(string entityId) =>
		!string.IsNullOrWhiteSpace(entityId) && (_ha.GetState(entityId)?.IsOn() ?? false);

	/// <summary>Subscribes and starts the evaluation tick. Safe to call once.</summary>
	public void Start()
	{
		lock (_gate)
		{
			if (_started)
				return;

			_started = true;
			_activatedAt = _scheduler.Now;
			_lastMotionAt = _scheduler.Now;
			_previousPeriodName = _circadian.ActivePeriodName(_scheduler.Now);
			_startPeriodModePending = true;

			// Read once: the answer is about the run that ended, and a later read finds what this run wrote over
			// it. A file read under _gate is safe only here, before the subscriptions and the tick exist.
			_periodAtLastRun = ReadPeriodAtLastRun();
			_persistedPeriodName = _periodAtLastRun;
		}

		if (_global.EffectiveKillSwitchEntity is { Length: > 0 } killSwitch)
		{
			_logger.LogInformation("Watching kill switch {EntityId}.", killSwitch);
			_subscriptions.Add(_ha.Entity(killSwitch)
				.StateChanges()
				.SubscribeSafe(_ => _changed.OnNext(Unit.Default), _logger));
		}

		if (_global.HouseMode?.Entity is { Length: > 0 } select)
		{
			_logger.LogInformation("Watching house-mode select {EntityId}.", select);
			_subscriptions.Add(_ha.Entity(select)
				.StateChanges()
				.SubscribeSafe(OnSelectChanged, _logger));
		}

		// Watched in either direction. Under Home Assistant authority a flip is a boundary the clock did not cross,
		// and waiting for the tick would delay a SetsMode by a whole CircadianTickSeconds. Under this
		// application's own authority it fires on the engine's echo and costs one idempotent re-evaluation.
		if (_periodSelect is { } periodSelect)
		{
			_logger.LogInformation("Watching period select {EntityId}.", periodSelect.Entity);
			_subscriptions.Add(_ha.Entity(periodSelect.Entity)
				.StateChanges()
				.SubscribeSafe(_ => OnPeriodSelectChanged(), _logger));
		}

		AnnounceDormantModeRules();
		SubscribePresenceResets();
		SubscribeActivationSensors();
		SubscribeMotion();

		// An entity already on before start forces the mode from the first instant and raises no edge, so a house
		// booting into a forced mode would otherwise say nothing until somebody toggled the entity.
		AnnounceForcedMode();

		_subscriptions.Add(_scheduler.SchedulePeriodic(
			TimeSpan.FromSeconds(_global.CircadianTickSeconds),
			OnTick));
	}

	// The select moving, for any reason, restarts the activation clock and republishes house state. Nothing else
	// ever clears a mode, so retention is free.
	private void OnSelectChanged(StateChange change)
	{
		lock (_gate)
		{
			_activatedAt = _scheduler.Now;

			// Anything that moves the select away takes ownership back from the no-motion rule, so a later move
			// onto the same option is somebody else's doing.
			if (_inactivityActivated is { Length: > 0 } claimed
				&& !string.Equals(change.New?.State?.Trim(), claimed, StringComparison.OrdinalIgnoreCase))
				_inactivityActivated = null;
		}

		AnnounceForcedMode();
		_changed.OnNext(Unit.Default);
	}

	/// <summary>
	///     The period select moved, so the period may have changed without the clock crossing anything.
	/// </summary>
	/// <remarks>
	///     Runs the whole tick body, which is idempotent: period entry is edge-triggered on a name change, the
	///     inactivity rule is latched, and the mirror write compares against what the select reads. It does not
	///     retarget the rooms; each area re-reads its own period on its own tick.
	/// </remarks>
	private void OnPeriodSelectChanged()
	{
		OnTick();
		_changed.OnNext(Unit.Default);
	}

	/// <summary>Names, once at start-up, every mode rule Home Assistant's authority has stood down.</summary>
	/// <remarks>
	///     Silence here sends somebody hunting an automation that is working as configured, so each dormant rule
	///     gets its own line and only when the document actually carries that rule.
	/// </remarks>
	private void AnnounceDormantModeRules()
	{
		if (!HouseModeIsHomeAssistants || _global.HouseMode is not { } houseMode)
			return;

		_logger.LogInformation(
			"House mode: Home Assistant decides. The engine reads {Select} and never writes it.", houseMode.EntityId);

		if (_periods.Any(period => period.SetsMode is { Length: > 0 }))
			_logger.LogInformation(
				"A period's SetsMode is dormant while Home Assistant decides the house mode; the schedule will not move {Select}.",
				houseMode.EntityId);

		if (houseMode.Options.Any(option => option.Kind != ModeKind.Normal && option.ActivateAfterNoMotionMinutes is > 0))
			_logger.LogInformation(
				"The no-motion auto-away rule is dormant while Home Assistant decides the house mode.");

		if (houseMode.Options.Any(option => option.ActivateWhileOn.Count > 0))
			_logger.LogInformation(
				"ActivateWhileOn is dormant while Home Assistant decides the house mode; the select's own value is the whole story.");

		// Kind-filtered, as SubscribePresenceResets and OnPeriodEntered both are: a Normal option's reset never
		// fired under either authority, so naming it here reports a rule that was not switched off.
		if (houseMode.Options.Any(option => option.Kind != ModeKind.Normal && option.HasResetTrigger))
			_logger.LogInformation(
				"The mode resets are dormant while Home Assistant decides the house mode; nothing here returns {Select} to Normal.",
				houseMode.EntityId);
	}

	private void SubscribePresenceResets()
	{
		// A reset writes the select, so it stands down with the rest of them.
		if (HouseModeIsHomeAssistants || _global.HouseMode is not { } houseMode)
			return;

		foreach (HouseModeOptionConfig? option in houseMode.Options.Where(o => o.Kind != ModeKind.Normal && o.ResetOnPresence))
		{
			List<string> sensors = option.ResetPresenceSensors.Count > 0
				? option.ResetPresenceSensors
				: [.. _areaMotionSensors];

			if (sensors.Count == 0)
			{
				_logger.LogWarning(
					"Option '{Option}' resets on presence but no sensors resolve (empty list and no area motion sensors); it will never reset on presence.",
					option.Value);
				continue;
			}

			foreach (string sensor in sensors)
			{
				HouseModeOptionConfig captured = option;

				// person.* and device_tracker.* report presence as their state going to "home", never as an on/off
				// edge, so they cannot take the turn-on branch. A binary_sensor arms on its turn-on.
				if (sensor.HasDomain(PersonDomain) || sensor.HasDomain(DeviceTrackerDomain))
					_subscriptions.Add(_ha.Entity(sensor)
						.StateChanges()
						.Where(IsArrival)
						.SubscribeSafe(_ => OnPresenceReset(captured, sensor), _logger));
				else
					_subscriptions.Add(_ha.Entity(sensor)
						.WhenTurnsOn(_ => OnPresenceReset(captured, sensor), _logger));
			}
		}
	}

	// The ActivateWhileOn overlay. The select is never written; EffectiveOption reads these live, so this only
	// republishes house state.
	private void SubscribeActivationSensors()
	{
		if (HouseModeIsHomeAssistants || _global.HouseMode is not { } houseMode)
			return;

		IEnumerable<string> sensors = houseMode.Options
			.SelectMany(option => option.ActivateWhileOn)
			.Where(sensor => !string.IsNullOrWhiteSpace(sensor))
			.Select(sensor => sensor.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase);

		foreach (string sensor in sensors)
		{
			_logger.LogInformation("Watching mode-activation sensor {EntityId}.", sensor);
			_subscriptions.Add(_ha.Entity(sensor)
				.StateChanges()
				.SubscribeSafe(_ => OnActivationSensorChanged(), _logger));
		}
	}

	// Announced before the state is republished: nothing writes the select here, so this is the one mode change
	// with no visible cause.
	private void OnActivationSensorChanged()
	{
		AnnounceForcedMode();
		_changed.OnNext(Unit.Default);
	}

	/// <summary>Writes one line naming what is forcing the house mode, and one when nothing is any more.</summary>
	/// <remarks>
	///     Deduplicated on the sentence, not the entity, so an entity that stays on says it once while a different
	///     entity taking over gets its own line. Every kind is announced, not only Away.
	/// </remarks>
	private void AnnounceForcedMode()
	{
		ForcedMode? forced = Forced;
		string sentence = forced?.Describe() ?? "";

		lock (_gate)
		{
			if (string.Equals(_announcedForce, sentence, StringComparison.Ordinal))
				return;

			_announcedForce = sentence;
		}

		if (forced is null)
		{
			_logger.LogInformation("Nothing is forcing the house mode any more; the select's own value decides again.");
			return;
		}

		// Passed as a property, not concatenated, so a structured sink keeps the sentence whole.
		_logger.LogInformation(
			"{ForcedMode} The house-mode select reads '{Select}' and is being overridden; this is not a presence departure.",
			sentence, CurrentModeValue ?? "(nothing)");
	}

	/// <summary>
	///     Subscribes the motion union once, for the two rules that read it: auto-away by inactivity, and a period
	///     that starts on motion.
	/// </summary>
	private void SubscribeMotion()
	{
		bool autoAway = !HouseModeIsHomeAssistants
			&& _global.HouseMode is { } houseMode
			&& houseMode.Options.Any(option => option.Kind != ModeKind.Normal && option.ActivateAfterNoMotionMinutes is > 0);

		bool startsPeriods = _motionStartPeriods.Count > 0;

		if (!autoAway && !startsPeriods)
			return;

		if (_areaMotionSensors.Count == 0)
		{
			if (autoAway)
				_logger.LogWarning("An option activates on no motion, but no area motion sensors resolve; it can never fire.");

			if (startsPeriods)
				_logger.LogWarning("A period starts on motion, but no area motion sensors resolve; it can never fire.");

			return;
		}

		foreach (KeyValuePair<string, IReadOnlySet<string>?> row in _motionStartPeriods)
			if (row.Value is { Count: 0 })
				_logger.LogWarning(
					"Period '{Period}' starts on motion in named rooms, but none of those rooms has a motion sensor the "
					+ "engine watches; it can never start on motion.",
					row.Key);

		foreach (string sensor in _areaMotionSensors)
		{
			_logger.LogInformation("Watching motion sensor {EntityId}.", sensor);
			_subscriptions.Add(_ha.Entity(sensor).WhenTurnsOn(_ => OnMotion(sensor), _logger));
		}
	}

	private void OnMotion(string sensor)
	{
		MarkMotion();
		StartPeriodOnMotion(sensor, _scheduler.Now);
	}

	// Motion on any watched sensor restarts the idle clock and re-arms activation.
	private void MarkMotion()
	{
		lock (_gate)
		{
			_lastMotionAt = _scheduler.Now;
			_inactivityLatched = false;
		}
	}

	/// <summary>Offers the movement to the period it is allowed to start, and enters that period if it may.</summary>
	/// <remarks>
	///     Under _gate for the reason <see cref="OnTick"/> gives: this runs on Home Assistant's thread, and a
	///     transition read but not claimed is entered twice.
	///     Two bounds, and both are load-bearing. A period is never placed before its own <c>Start</c>, so the
	///     wrapped case (night still running at 02:00) is refused and the 06:30 period cannot fire on a 02:00 trip
	///     to the kitchen; and once per local day, whichever path entered it, so walking back in at lunch does not
	///     re-fire <see cref="TimePeriodConfig.SetsMode"/> over a mode somebody chose since.
	/// </remarks>
	private void StartPeriodOnMotion(string sensor, DateTimeOffset now)
	{
		if (_motionStartPeriods.Count == 0)
			return;

		// Before the lock: this consults Home Assistant.
		HouseModeOptionConfig? activeOption = CurrentOption;

		// The instant's own offset, as the circadian table reads it. Never the machine's local day.
		DateOnly today = DateOnly.FromDateTime(now.Date);

		string? periodName;
		bool start;

		lock (_gate)
		{
			periodName = _circadian.ActivePeriodName(now);

			start = periodName is { Length: > 0 }
				&& MotionMayStart(periodName, sensor)
				&& !(_enteredOn.TryGetValue(periodName, out DateOnly entered) && entered == today)
				&& StartHasPassed(periodName, now);

			if (start)
			{
				_enteredOn[periodName!] = today;
				_previousPeriodName = periodName;
				_startPeriodModePending = false;
			}
		}

		if (!start)
			return;

		_logger.LogInformation("Motion on {Sensor} started period '{Period}'.", sensor, periodName);

		OnPeriodEntered(periodName!, activeOption);
		_changed.OnNext(Unit.Default);
	}

	// Under _gate; reads configuration settled in the constructor and nothing else.
	private bool MotionMayStart(string periodName, string sensor) =>
		_motionStartPeriods.TryGetValue(periodName, out IReadOnlySet<string>? rooms)
		&& (rooms is null || rooms.Contains(sensor));

	// A period whose Start has not come round today cannot be started by motion. This is what keeps the wrapped
	// period (night, still running at 02:00) from re-entering on the far side of midnight.
	private bool StartHasPassed(string periodName, DateTimeOffset now)
	{
		if (PeriodNamed(periodName) is not { } period || !PeriodStart.TryParse(period.Start, out PeriodStart? start))
			return false;

		return start!.Resolve(_sunTimes()) is { } resolved && resolved <= TimeOnly.FromTimeSpan(now.TimeOfDay);
	}

	// The note names a period and it is not this one. Asked by the tick's day latch and by the restart path, which
	// is the only one of the two that also logs.
	private static bool BoundaryWentByWhileDown(string? previousRun, string periodName) =>
		previousRun is { Length: > 0 } && !string.Equals(previousRun, periodName, StringComparison.OrdinalIgnoreCase);

	private TimePeriodConfig? PeriodNamed(string periodName) =>
		_periods.FirstOrDefault(period => string.Equals(period.Name, periodName, StringComparison.OrdinalIgnoreCase));

	private bool AnyMotionOn() => _areaMotionSensors.Any(IsOn);

	/// <summary>
	///     Poll-based, on the tick: when the whole house has been motion-free for an option's configured span,
	///     switch the select to that option. First qualifying option in list order wins, one activation per tick.
	///     It writes the select, so the option's reset triggers arm through <see cref="OnSelectChanged"/> as a
	///     manual switch would.
	/// </summary>
	private void EvaluateInactivityActivation(DateTimeOffset now)
	{
		// Stood down under Home Assistant's authority; AnnounceDormantModeRules has already said so.
		if (HouseModeIsHomeAssistants || _global.HouseMode is not { Entity: { Length: > 0 } select } houseMode)
			return;

		// Motion in progress keeps the clock at now and re-arms, so "no motion for X" counts only quiet time.
		if (AnyMotionOn())
		{
			lock (_gate)
			{
				_lastMotionAt = now;
				_inactivityLatched = false;
			}
			return;
		}

		DateTimeOffset lastMotionAt;
		bool latched;
		lock (_gate)
		{
			lastMotionAt = _lastMotionAt;
			latched = _inactivityLatched;
		}

		// Once per idle spell. The latch clears only when motion resumes; without it the switch repeats every tick
		// until Home Assistant echoes back, and re-fires against a human who switches away while the house is quiet.
		if (latched)
			return;

		foreach (HouseModeOptionConfig option in houseMode.Options
			.Where(candidate => candidate.Kind != ModeKind.Normal && candidate.ActivateAfterNoMotionMinutes is > 0))
		{
			if (string.Equals(CurrentModeValue, option.Value.Trim(), StringComparison.OrdinalIgnoreCase))
				continue;   // already standing on this mode

			if (now - lastMotionAt < TimeSpan.FromMinutes(option.ActivateAfterNoMotionMinutes!.Value))
				continue;

			_logger.LogInformation("No motion for {Minutes} min; setting {Select} to '{Mode}'.",
				option.ActivateAfterNoMotionMinutes, select, option.Value);
			_ha.CallService(SelectDomain, SelectOptionService,
				new ServiceTarget { EntityIds = [select] }, new { option = option.Value.Trim() });

			lock (_gate)
			{
				_inactivityLatched = true;

				// So this mode reports as the engine's doing and never as a presence departure. Survives only as
				// long as the select keeps reading it; see OnSelectChanged.
				_inactivityActivated = option.Value.Trim();
			}

			return;
		}
	}

	private static bool IsArrival(StateChange change) =>
		string.Equals(change.New?.State, HomeState, StringComparison.OrdinalIgnoreCase)
		&& !string.Equals(change.Old?.State, HomeState, StringComparison.OrdinalIgnoreCase);

	// Edge-triggered on a fresh turn-on or arrival. Resets only when this option is active and the grace window
	// has expired, so walking out the door does not cancel the mode you just set.
	private void OnPresenceReset(HouseModeOptionConfig option, string sensor)
	{
		if (!ReferenceEquals(CurrentOption, option))
			return;

		DateTimeOffset activatedAt;
		lock (_gate)
			activatedAt = _activatedAt;

		TimeSpan grace = TimeSpan.FromMinutes(Math.Max(0, option.ResetPresenceGraceMinutes));
		if (_scheduler.Now - activatedAt < grace)
		{
			_logger.LogDebug("Presence on {Sensor} within the {Grace}-minute grace of '{Option}'; ignored.",
				sensor, option.ResetPresenceGraceMinutes, option.Value);
			return;
		}

		Reset($"presence on {sensor}");
	}

	/// <summary>
	///     One evaluation of the clock's news: period entry, the across-restart catch-up, the period select's mirror
	///     and the auto-away timer.
	/// </summary>
	/// <remarks>
	///     Under _gate: reading the period and claiming the transition must be one step. The scheduler is no
	///     longer the only caller, since a period-select flip runs this from Home Assistant's thread, and a
	///     transition read but not claimed is acted on twice. The read is inside the gate, not before it: with
	///     only the claim under the lock, a thread holding a stale name fires a backwards entry and regresses
	///     <see cref="_previousPeriodName"/>. The read is a state-cache lookup plus a sort; it cannot re-enter.
	/// </remarks>
	private void OnTick()
	{
		DateTimeOffset now = _scheduler.Now;

		// Before the lock: these consult Home Assistant, and _gate is for this object's own fields.
		HouseModeOptionConfig? activeOption = CurrentOption;
		bool modeIsReadable = CurrentModeValue is { Length: > 0 };

		// A period select that cannot be read is not a period change. Under Home Assistant authority
		// ActivePeriodName falls through to the clock while the helper is unavailable, and that answer is
		// indistinguishable from a real one: a household holding "day" at 23:30 would have night's SetsMode latch
		// the house asleep, and nothing puts it back when the helper returns.
		bool overrideIsBlind = _periodSelect is { ReadPeriod: not null } reader && reader.CurrentValue() is null;

		string? currentPeriodName;
		bool entered;
		bool applyOnStart;

		lock (_gate)
		{
			currentPeriodName = _circadian.ActivePeriodName(now);

			// Period entry: the active period changed since the previous evaluation.
			entered = !overrideIsBlind
				&& currentPeriodName is { Length: > 0 }
				&& _previousPeriodName is { Length: > 0 }
				&& !string.Equals(currentPeriodName, _previousPeriodName, StringComparison.OrdinalIgnoreCase);

			applyOnStart = !entered
				&& !overrideIsBlind
				&& _startPeriodModePending
				&& currentPeriodName is { Length: > 0 }
				&& modeIsReadable;

			// A blind read is no evidence about which period is running, and recording the clock's guess would
			// make the helper's recovery look like a fresh entry.
			if (!overrideIsBlind)
				_previousPeriodName = currentPeriodName;

			// The one restart chance is spent by an entry or by using it. A tick that could read neither the
			// period nor the select leaves it armed.
			if (entered || applyOnStart)
				_startPeriodModePending = false;

			// Written from both paths, so the motion rule can tell a period the day has already begun from one it
			// has not. The restart path counts only when the note actually says a boundary went by: a latch on a
			// start that applied nothing would leave motion unable to start the period at all.
			bool began = entered
				|| (applyOnStart && BoundaryWentByWhileDown(_periodAtLastRun, currentPeriodName!));

			if (began && currentPeriodName is { Length: > 0 })
				_enteredOn[currentPeriodName] = DateOnly.FromDateTime(now.Date);
		}

		if (entered)
			OnPeriodEntered(currentPeriodName!, activeOption);
		else if (applyOnStart)
			ApplyPeriodModeOnStart(currentPeriodName!);

		// After the decision above, never before it. The note is the only evidence that a boundary went by while
		// the engine was down, and writing first erases it if the process dies between the two.
		RememberPeriod(currentPeriodName);

		// The period select as an output, reading the same answer the two rules above did. No-op unless this
		// application owns the select.
		MirrorPeriodSelect(currentPeriodName);

		EvaluateInactivityActivation(now);
	}

	private void OnPeriodEntered(string periodName, HouseModeOptionConfig? activeOption)
	{
		TimePeriodConfig? period = PeriodNamed(periodName);

		// Once, at entry, so a human override mid-period stands. A boundary the engine was not running for never
		// reaches here; ApplyPeriodModeOnStart handles that from the note on disk.
		if (!HouseModeIsHomeAssistants
			&& period?.SetsMode is { Length: > 0 } setsMode
			&& _global.HouseMode?.Entity is { Length: > 0 } select
			&& !string.Equals(CurrentModeValue, setsMode.Trim(), StringComparison.OrdinalIgnoreCase))
		{
			_logger.LogInformation("Period '{Period}' started; setting {Select} to '{Mode}'.", periodName, select, setsMode);
			_ha.CallService(SelectDomain, SelectOptionService,
				new ServiceTarget { EntityIds = [select] }, new { option = setsMode.Trim() });
		}

		// Period-start reset: a non-Normal option whose ResetOnPeriodStart names this period returns to Normal.
		if (activeOption is { Kind: not ModeKind.Normal, ResetOnPeriodStart: { Length: > 0 } resetPeriod }
			&& string.Equals(resetPeriod.Trim(), periodName, StringComparison.OrdinalIgnoreCase))
			Reset($"period '{periodName}' started");
	}

	/// <summary>
	///     Applies the current period's <see cref="TimePeriodConfig.SetsMode"/>, once, on the first tick after
	///     <see cref="Start"/>, and only when the period is not the one the previous run left off in.
	/// </summary>
	/// <remarks>
	///     The question is whether a boundary went by, not whether the engine restarted. Restarting inside the
	///     same period is not an event, so the standing mode is not consulted; restarting across a boundary is the
	///     event the schedule exists to act on.
	///     A null <see cref="_periodAtLastRun"/> is "we do not know", not "a boundary was crossed", and does
	///     nothing. Guessing the other way overwrites a mode on no evidence, on a path a corrupt file could
	///     trigger every start.
	///     Kept separate from <see cref="OnPeriodEntered"/> because that path also fires the period-start reset,
	///     which would cancel a retained Away or Guest mode as a side effect of a deploy.
	/// </remarks>
	private void ApplyPeriodModeOnStart(string periodName)
	{
		string? previousRun;
		lock (_gate)
			previousRun = _periodAtLastRun;

		if (previousRun is not { Length: > 0 })
		{
			_logger.LogDebug(
				"Started inside period '{Period}', but there is no note of which period the last run ended in; "
				+ "assuming nothing and leaving the house mode alone.",
				periodName);
			return;
		}

		if (!BoundaryWentByWhileDown(previousRun, periodName))
			return;   // same period as when we stopped: no boundary went by, so nothing to apply

		if (HouseModeIsHomeAssistants)
			return;

		TimePeriodConfig? period = PeriodNamed(periodName);

		if (period?.SetsMode is not { Length: > 0 } setsMode
			|| _global.HouseMode?.Entity is not { Length: > 0 } select)
			return;

		if (string.Equals(CurrentModeValue, setsMode.Trim(), StringComparison.OrdinalIgnoreCase))
			return;   // already standing where the period wants it

		_logger.LogInformation(
			"Period '{Period}' began while the engine was stopped (it was last running in '{Previous}'); setting {Select} to '{Mode}'.",
			periodName, previousRun, select, setsMode);

		_ha.CallService(SelectDomain, SelectOptionService,
			new ServiceTarget { EntityIds = [select] }, new { option = setsMode.Trim() });
	}

	/// <summary>Points the period select at the period the engine's own schedule resolved.</summary>
	/// <remarks>
	///     A no-op under <see cref="PeriodAuthority.HomeAssistant"/>: the reader hands out no
	///     <see cref="PeriodSelectReader.OptionForPeriod"/>, so the two directions are exclusive by construction
	///     and not by a flag this method could get wrong.
	///     Triggered by comparison, never by memory. A select somebody moved by hand, or one that came back from a
	///     Home Assistant restart on the wrong option, is put right; a "we already asked" latch would make both
	///     permanent. The log line is bounded separately, because an option the select does not offer is rejected
	///     and correctly retried on every tick.
	/// </remarks>
	private void MirrorPeriodSelect(string? periodName)
	{
		if (_periodSelect is not { OptionForPeriod: { } optionFor } select || periodName is not { Length: > 0 })
			return;

		if (optionFor(periodName) is not { Length: > 0 } wanted)
			return;   // no row maps this period; the validator has already said so if it matters

		if (string.Equals(select.CurrentValue(), wanted, StringComparison.OrdinalIgnoreCase))
			return;   // already showing it

		bool firstTime;
		lock (_gate)
		{
			firstTime = !string.Equals(_mirroredPeriodOption, wanted, StringComparison.OrdinalIgnoreCase);
			_mirroredPeriodOption = wanted;
		}

		if (firstTime)
			_logger.LogInformation("Period '{Period}' is in force; setting {Select} to '{Option}'.",
				periodName, select.Entity, wanted);
		else
			_logger.LogDebug("Period select {Select} still does not read '{Option}'; asking again.", select.Entity, wanted);

		_ha.CallService(SelectDomain, SelectOptionService,
			new ServiceTarget { EntityIds = [select.Entity] }, new { option = wanted });
	}

	/// <summary>Reads the previous run's period, treating any failure as "we do not know".</summary>
	/// <remarks>
	///     <see cref="LastPeriodStore"/> promises never to throw, and this catches anyway: the store is an
	///     interface a host supplies, and this runs inside <see cref="Start"/>, where a throw takes the engine down.
	/// </remarks>
	private string? ReadPeriodAtLastRun()
	{
		if (_lastPeriod is null)
			return null;

		try
		{
			return _lastPeriod.Load() is { Length: > 0 } period ? period : null;
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			_logger.LogWarning(exception, "Could not read which period the last run ended in; assuming nothing.");
			return null;
		}
	}

	/// <summary>Writes the period now current, when it has changed since the last write.</summary>
	/// <remarks>
	///     Only on a change: a handful of writes a day, and the file is read once per process. A null period, from
	///     a table with no placeable boundary, is not written; it would replace a good note with a worse one.
	/// </remarks>
	private void RememberPeriod(string? periodName)
	{
		if (_lastPeriod is null || periodName is not { Length: > 0 })
			return;

		lock (_gate)
		{
			if (string.Equals(_persistedPeriodName, periodName, StringComparison.OrdinalIgnoreCase))
				return;

			// Recorded as attempted even when the write fails, or a persistent fault warns once a minute.
			_persistedPeriodName = periodName;
		}

		try
		{
			_lastPeriod.TrySave(periodName);
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			// Losing the note costs one re-application after the next restart, never the tick it is written from.
			_logger.LogWarning(exception, "Could not record that the engine is now in period '{Period}'.", periodName);
		}
	}

	/// <summary>Returns the select to the single Normal option, logging which trigger fired. No-op when no Normal resolves.</summary>
	private void Reset(string trigger)
	{
		// Under Home Assistant's authority nothing here writes the select, resets included.
		if (HouseModeIsHomeAssistants || _global.HouseMode is not { Entity: { Length: > 0 } select } houseMode)
			return;

		// No Normal option means nothing to reset to. A no-op, never a clobber onto a tagged option.
		if (houseMode.NormalOption?.Value is not { Length: > 0 } normal)
		{
			if (System.Threading.Interlocked.Exchange(ref _warnedNoNormal, 1) == 0)
				_logger.LogWarning("A reset fired ({Trigger}) but no Normal option resolves; leaving the mode unchanged (this warns once).", trigger);
			return;
		}

		if (string.Equals(CurrentModeValue, normal.Trim(), StringComparison.OrdinalIgnoreCase))
			return;   // already Normal

		_logger.LogInformation("Resetting {Select} to '{Normal}' ({Trigger}).", select, normal, trigger);
		_ha.CallService(SelectDomain, SelectOptionService,
			new ServiceTarget { EntityIds = [select] }, new { option = normal.Trim() });
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		_subscriptions.Dispose();
		_changed.Dispose();
	}
}
