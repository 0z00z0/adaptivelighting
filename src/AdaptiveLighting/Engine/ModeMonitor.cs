using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Subjects;

using AdaptiveLighting.Configuration;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>
///     The mode brain (09 §3.2): reads the house-mode select, derives the active <see cref="ModeKind"/> and scene,
///     and owns the set → retain → reset lifecycle. A mode, once set (by hand or by a period's
///     <see cref="TimePeriodConfig.SetsMode"/>), is retained until a reset trigger fires; every reset returns the
///     select to the single Normal option.
/// </summary>
/// <remarks>
///     The monitor never mutates <see cref="HouseState"/>: every mode change it makes is an
///     <c>input_select.select_option</c> that flows back through Home Assistant and the normal <see cref="Changed"/>
///     path, exactly like a human turning the dial. Its only clock is the injected <see cref="IScheduler"/>, so a
///     virtual scheduler makes every trigger deterministic.
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
	private readonly IReadOnlyCollection<string> _areaMotionSensors;

	/// <summary>
	///     Where the period the engine is running in is written down, so the next start can tell whether a boundary
	///     went by while it was stopped. <c>null</c> when nobody handed one over, which reads as "we do not know".
	/// </summary>
	private readonly ILastPeriodStore? _lastPeriod;

	/// <summary>The period select, or <c>null</c> when none is configured. Which direction it grants is its own to say.</summary>
	private readonly PeriodSelectReader? _periodSelect;

	private readonly Subject<Unit> _changed = new();
	private readonly CompositeDisposable _subscriptions = [];
	private readonly object _gate = new();

	// CurrentModeValue is read from several Rx subscriptions, so the "warn once per distinct value" set can be
	// touched concurrently. A ConcurrentDictionary makes the TryAdd atomic; the byte value is unused.
	private readonly ConcurrentDictionary<string, byte> _warnedValues = new(StringComparer.OrdinalIgnoreCase);

	// Reset fires from several subscriptions; the "no Normal target" warning must not spam the log every tick.
	private int _warnedNoNormal;

	private DateTimeOffset _activatedAt;
	private DateTimeOffset _lastMotionAt;
	private bool _inactivityLatched;
	private string? _previousPeriodName;
	private bool _started;

	// The option value the no-motion rule last wrote to the select, so a mode the engine set because the house went
	// quiet can be told apart from the same mode a person chose. Cleared the moment the select stops reading it.
	// Deliberately not persisted: after a restart nobody knows why the select stands where it does, and a guess is
	// exactly the invented cause this reporting exists to stop.
	private string? _inactivityActivated;

	// The last sentence AnnounceForcedMode wrote, so a forcing entity that stays on is stated once rather than on
	// every edge that re-reads it. Empty means "nothing was being forced last time we looked".
	private string _announcedForce = "";

	// The option MirrorPeriodSelect last asked the period select for. It bounds the log line only — the call itself
	// is decided by comparing against what the select actually reads, so a select that never echoes is asked again.
	private string? _mirroredPeriodOption;

	// Armed by Start and spent on the first tick that can read the select: the one chance a restart gets to notice
	// that a boundary went by while it was stopped. Held open until the select answers, because after a Home
	// Assistant restart an input_select can be unavailable for a while, and spending the chance on a value nobody
	// could read would waste it exactly when it is most needed. See ApplyPeriodModeOnStart for why a restart is not
	// simply treated as a period entry.
	private bool _startPeriodModePending;

	// What the previous run left on disk, read once in Start. Null means "we do not know" — a first run, a deleted
	// file or a corrupt one — which is deliberately not the same as knowing a boundary was crossed.
	private string? _periodAtLastRun;

	// What this run believes is on disk now, so the file is written when the period changes and not on every tick.
	private string? _persistedPeriodName;

	/// <summary>Creates the mode brain.</summary>
	/// <param name="ha">Source of state and state changes, and where <c>select_option</c> calls go.</param>
	/// <param name="global">Supplies the select, the kill switch and the option kinds.</param>
	/// <param name="logger">Diagnostics, and the sink for exceptions from the subscriptions.</param>
	/// <param name="scheduler">The monitor's only clock: the evaluation tick and every grace/reset comparison.</param>
	/// <param name="periods">The house-wide circadian table, for period-entry detection.</param>
	/// <param name="sunTimes">Supplies the day's sun times on demand, for placing sun-anchored boundaries.</param>
	/// <param name="areaMotionSensors">
	///     The union of every motion sensor configured across all areas. An option whose
	///     <see cref="HouseModeOptionConfig.ResetPresenceSensors"/> is empty resets on any of these (09 owner refinement).
	/// </param>
	/// <param name="lastPeriod">
	///     Where the period this run is in is written down, so the next start can tell whether a boundary went by
	///     while it was stopped — see <see cref="ApplyPeriodModeOnStart"/>. Optional: without one the monitor never
	///     knows that a boundary was crossed during an outage, and therefore never re-applies a mode after a restart,
	///     which is the behaviour every build before this one had.
	/// </param>
	/// <param name="periodSelect">
	///     The period <c>input_select</c>, or <c>null</c> when none is configured. Whichever of its two directions is
	///     live is the one this uses: under Home Assistant authority its read delegate goes into this monitor's own
	///     calculator, so the boundary the mode brain watches for is the one the rooms are lit for; under adaptive
	///     lighting's own authority this writes it, beside <see cref="OnPeriodEntered"/>'s <c>SetsMode</c> and the
	///     across-restart catch-up. The object is what makes those mutually exclusive — see
	///     <see cref="PeriodSelectReader"/>.
	/// </param>
	public ModeMonitor(
		IHaContext ha,
		GlobalConfig global,
		ILogger logger,
		IScheduler scheduler,
		IReadOnlyList<TimePeriodConfig> periods,
		Func<SunTimes> sunTimes,
		IReadOnlyCollection<string> areaMotionSensors,
		ILastPeriodStore? lastPeriod = null,
		PeriodSelectReader? periodSelect = null)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_global = global ?? throw new ArgumentNullException(nameof(global));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
		_periods = periods ?? throw new ArgumentNullException(nameof(periods));
		ArgumentNullException.ThrowIfNull(sunTimes);
		_areaMotionSensors = areaMotionSensors ?? throw new ArgumentNullException(nameof(areaMotionSensors));
		_lastPeriod = lastPeriod;
		_periodSelect = periodSelect;

		_circadian = new CircadianCalculator(periods, global, sunTimes, roomLevels: null, periodSelect?.ReadPeriod);
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

			// An unavailable kill switch must not silently disable the house's lighting, so an unreadable
			// state is read as "not killed" whichever polarity is configured.
			if (state?.State is null)
				return false;

			// While the built-in switch is defaulted in, polarity is forced to the enabled-flag reading (off =
			// muzzled), whatever KillSwitchActiveWhenOff says — that flag only governs an explicit entity. This
			// mirrors ModeService.GetToggles so the engine and the UI can never disagree about the master switch.
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

	/// <summary>
	///     The first option (in list order) any of whose <see cref="HouseModeOptionConfig.ActivateWhileOn"/> entities
	///     is currently on, together with the entity that is on — or <c>null</c> when none is. Such an option forces
	///     the effective house mode regardless of the select's value; the select is never written from this, so there
	///     is no feedback loop.
	/// </summary>
	/// <remarks>
	///     The entity is returned beside the option rather than looked up again by whoever needs to name it. There is
	///     only one read of "which entity is holding this mode on", and it is this one — a second copy would be free
	///     to name a different entity from the one the engine actually acted on, which is the whole class of fault
	///     <see cref="ForcedMode"/> exists to close.
	/// </remarks>
	private (HouseModeOptionConfig Option, string EntityId)? ActivatedNow
	{
		get
		{
			if (_global.HouseMode is not { } houseMode)
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
	///     <para>
	///         <b>Read live, in the same order the engine decides in.</b> The <c>ActivateWhileOn</c> overlay wins over
	///         the select, so it is asked first; the no-motion rule wrote the select and therefore only holds while
	///         the value it wrote still stands.
	///     </para>
	///     <para>
	///         Every <see cref="ModeKind"/> is reported, not only <see cref="ModeKind.Away"/>. Sleep has exactly the
	///         same shape — an entity holding the house asleep looks, from a room, identical to a household that
	///         chose it — and a rule that only covered Away would leave the second half of the same fault in place.
	///     </para>
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

			// The claim only holds while the value the engine wrote is still the value the select reads. Anything
			// that moved it since — a person, a period's SetsMode, a reset — took ownership of it back.
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
	///     orchestrator. On Away/Guest the scene stands (they pause the engine); on Normal/Sleep the engine keeps
	///     adjusting, so it is a one-shot the ordinary commands may soon override — the area-pause logic that reads
	///     this stays gated on <c>Mode == Away/Guest</c>, so a Normal/Sleep scene never pauses an area.
	/// </summary>
	public string? ActiveScene =>
		EffectiveOption is { Scene: { Length: > 0 } scene } ? scene : null;

	// Whether a boolean-ish entity currently reads on. An unreadable state is "not on", so a vanished sensor
	// simply stops forcing its mode rather than pinning it.
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

			// Read once, here, rather than on the tick: the answer is about the run that ended, so a later read
			// could only find what this run has since written over it. Reading a file under the gate is safe only
			// because nothing can be holding it yet — the subscriptions and the tick are all created below.
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

		// The period select is watched the same way the house-mode select is, whichever direction it runs in: under
		// Home Assistant authority a flip is a period boundary that did not come from the clock, and waiting for the
		// tick to notice it would delay a SetsMode by up to a whole CircadianTickSeconds. Under this application's
		// own authority it fires on the engine's own echo and costs one idempotent re-evaluation.
		if (_periodSelect is { } periodSelect)
		{
			_logger.LogInformation("Watching period select {EntityId}.", periodSelect.Entity);
			_subscriptions.Add(_ha.Entity(periodSelect.Entity)
				.StateChanges()
				.SubscribeSafe(_ => OnPeriodSelectChanged(), _logger));
		}

		SubscribePresenceResets();
		SubscribeActivationSensors();
		SubscribeInactivityActivation();

		// An entity that was already on before the engine started forces the mode from the first instant and raises
		// no edge to announce it. Said here so a house that boots into a forced mode says so once at start-up rather
		// than only when somebody happens to toggle the entity — which, in the incident this comes from, is a toggle
		// nobody was going to make.
		AnnounceForcedMode();

		_subscriptions.Add(_scheduler.SchedulePeriodic(
			TimeSpan.FromSeconds(_global.CircadianTickSeconds),
			OnTick));
	}

	// The select moving — for any reason, human or engine — restarts the activation clock and republishes house
	// state. Retention is free: nothing else ever clears a mode.
	private void OnSelectChanged(StateChange change)
	{
		lock (_gate)
		{
			_activatedAt = _scheduler.Now;

			// The no-motion rule's claim on the value survives only while the value it wrote still stands. Anything
			// that moves the select away — a person, a period's SetsMode, a reset — takes ownership back, and a
			// later move onto the same option is then somebody else's doing rather than the rule's.
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
	///     <para>
	///         Runs the tick body rather than a copy of the period half of it: <see cref="OnTick"/> is idempotent —
	///         period entry is edge-triggered on a name change, the inactivity rule is latched, and the mirror write
	///         compares against what the select already reads — so running it early costs nothing and running a
	///         second implementation of it would be a rule with two homes.
	///     </para>
	///     <para>
	///         <b>What this does not do is retarget the rooms.</b> Each area re-reads its own period on its own tick,
	///         so a flip reaches the lamps within <see cref="GlobalConfig.CircadianTickSeconds"/> — the same latency
	///         a clock-driven boundary already has. What it does buy is the mode: a period whose <c>SetsMode</c> puts
	///         the house to sleep now does so the instant the household selects it.
	///     </para>
	/// </remarks>
	private void OnPeriodSelectChanged()
	{
		OnTick();
		_changed.OnNext(Unit.Default);
	}

	private void SubscribePresenceResets()
	{
		if (_global.HouseMode is not { } houseMode)
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

				// person.* and device_tracker.* report presence as their state going to "home", not as an on/off
				// edge; a binary_sensor arms on its turn-on. A device_tracker used to fall to the turn-on branch and
				// never fire, because "home" is not "on".
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

	// The ActivateWhileOn overlay: watch every distinct entity any option lists, and re-evaluate the mode whenever
	// one changes. The select is never written — EffectiveOption reads these live — so this only republishes house
	// state, exactly like a select change would. No entities listed anywhere means no subscriptions and no overlay.
	private void SubscribeActivationSensors()
	{
		if (_global.HouseMode is not { } houseMode)
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

	// An overlay entity moved. Said out loud before the house state is republished, because this is the one mode
	// change with no visible cause: nothing writes the select, so the log used to carry only what the areas then
	// did — "Everyone left the house", while everybody was standing in the room.
	private void OnActivationSensorChanged()
	{
		AnnounceForcedMode();
		_changed.OnNext(Unit.Default);
	}

	/// <summary>
	///     Writes one line naming what is forcing the house mode, and one when nothing is any more.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>The line this exists to write is <c>Away mode is forced while input_boolean.occupancy is on.</c></b>
	///         Its absence cost an hour: a cabin's Away option listed a boolean that had been on for hours, every
	///         settings save rebuilt every area controller and re-asserted Away, and the only evidence in the log was
	///         a presence departure that had not happened. The entity <i>and</i> its state are named because
	///         "something is forcing Away" sends the reader hunting the same way "something here is on" once did.
	///     </para>
	///     <para>
	///         Deduplicated on the sentence rather than on the entity, so an entity that stays on says it once while
	///         a different entity taking over still gets its own line. Every kind is announced, not only Away.
	///     </para>
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

		// The sentence comes from ForcedMode.Describe so the log and whatever renders the house cannot word one
		// fact two ways. It is still passed as a property, not concatenated, so a structured sink keeps it whole.
		_logger.LogInformation(
			"{ForcedMode} The house-mode select reads '{Select}' and is being overridden — this is not a presence departure.",
			sentence, CurrentModeValue ?? "(nothing)");
	}

	// Auto-away by inactivity: any option with ActivateAfterNoMotionMinutes watches the house-wide motion union and,
	// after that long with no motion, becomes the mode. The idle clock advances on every motion turn-on; the tick
	// checks the elapsed time. No option opts in → no subscriptions, no clock, no behaviour.
	private void SubscribeInactivityActivation()
	{
		if (_global.HouseMode is not { } houseMode)
			return;

		if (!houseMode.Options.Any(option => option.Kind != ModeKind.Normal && option.ActivateAfterNoMotionMinutes is > 0))
			return;

		if (_areaMotionSensors.Count == 0)
		{
			_logger.LogWarning("An option activates on no motion, but no area motion sensors resolve; it can never fire.");
			return;
		}

		foreach (string sensor in _areaMotionSensors)
		{
			_logger.LogInformation("Watching motion sensor {EntityId} for auto-away.", sensor);
			_subscriptions.Add(_ha.Entity(sensor).WhenTurnsOn(_ => MarkMotion(), _logger));
		}
	}

	// Motion anywhere restarts the idle clock and re-arms activation, on any watched sensor's rising edge.
	private void MarkMotion()
	{
		lock (_gate)
		{
			_lastMotionAt = _scheduler.Now;
			_inactivityLatched = false;
		}
	}

	// Whether any watched motion sensor currently reads on — motion in progress, so the house is not idle.
	private bool AnyMotionOn() => _areaMotionSensors.Any(IsOn);

	/// <summary>
	///     Poll-based, on the tick: when the whole house has been motion-free for an option's configured span, switch
	///     the select to that option. The first qualifying option (list order) wins, one activation per tick. Skipped
	///     when motion is live or the option is already standing; it writes the SELECT, so the option's reset triggers
	///     arm through the normal <see cref="OnSelectChanged"/> path exactly as a manual switch would.
	/// </summary>
	private void EvaluateInactivityActivation(DateTimeOffset now)
	{
		if (_global.HouseMode is not { Entity: { Length: > 0 } select } houseMode)
			return;

		// Motion in progress keeps the clock at now and re-arms, so "no motion for X" only ever counts genuinely
		// quiet time and a fresh idle spell can activate again.
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

		// Fire once per idle spell. Without the latch the switch would repeat every tick until Home Assistant echoes
		// the new mode back, and would re-fire against a human who switches away while the house is still quiet — the
		// latch only clears when motion resumes (MarkMotion / a motion-on tick above).
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

				// Remembered so the mode this wrote is reported as the engine's doing rather than as a household
				// decision — and, crucially, never as a presence departure. It survives only as long as the select
				// keeps reading it; see OnSelectChanged.
				_inactivityActivated = option.Value.Trim();
			}

			return;
		}
	}

	private static bool IsArrival(StateChange change) =>
		string.Equals(change.New?.State, HomeState, StringComparison.OrdinalIgnoreCase)
		&& !string.Equals(change.Old?.State, HomeState, StringComparison.OrdinalIgnoreCase);

	// Edge-triggered: a fresh turn-on / arrival. Resets only when this option is the active one and the grace
	// window has expired — you walking out the door must not cancel your own Borte.
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
	///     <b>The transition is claimed under the same lock that read it, and that is not cosmetic.</b> This used to
	///     run only from the scheduler, so reading the previous period at the top and writing the new one at the
	///     bottom could not overlap with itself. A period-select flip now calls it too
	///     (<see cref="OnPeriodSelectChanged"/>), from Home Assistant's thread, and a transition read but not yet
	///     recorded would be acted on by both callers — two <c>SetsMode</c> writes, and a period-start reset firing
	///     twice. Deciding and claiming together makes the second caller see a boundary already taken.
	///     <para>
	///         <b>The period is read inside the gate, not before it.</b> Moving only the claim under the lock left
	///         the <i>input</i> to the decision outside it, which is the same bug wearing a hat: thread A reads
	///         "evening", thread B runs the whole method and claims "night", then A takes the gate still holding
	///         "evening" and fires a backwards entry that never happened — and regresses
	///         <see cref="_previousPeriodName"/> so the night entry fires again on the next tick. The read is a
	///         dictionary lookup against NetDaemon's state cache plus a sort; it blocks on nothing and cannot
	///         re-enter this gate.
	///     </para>
	/// </remarks>
	private void OnTick()
	{
		DateTimeOffset now = _scheduler.Now;

		// Read before the lock: these consult Home Assistant, and the gate is for this object's own fields.
		HouseModeOptionConfig? activeOption = CurrentOption;
		bool modeIsReadable = CurrentModeValue is { Length: > 0 };

		// A period select that cannot be read right now is NOT a period change. Under Home Assistant authority
		// OverriddenPeriod() returns null while the helper is unavailable — an HA restart, a reload, an edit — and
		// ActivePeriodName then falls through to the clock. That answer is indistinguishable from a real one, so a
		// household holding "day" at 23:30 would have the clock's "night" counted as an entry: night's SetsMode
		// latches the house to Sover, and when the helper comes back nothing puts it right, because "day" normally
		// sets no mode. Levels revert on their own; a latched mode does not, and that asymmetry is the whole defect.
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

			// A blind read is not evidence about which period is running, so it must not overwrite the last thing
			// that was. Recording the clock's guess here would make the helper's recovery look like a fresh entry.
			if (!overrideIsBlind)
				_previousPeriodName = currentPeriodName;

			// The one restart chance is spent by an entry — which has already had the say the restart rule would
			// have had — or by using it. A tick that could read neither the period nor the select leaves it armed.
			if (entered || applyOnStart)
				_startPeriodModePending = false;
		}

		if (entered)
			OnPeriodEntered(currentPeriodName!, activeOption);
		else if (applyOnStart)
			ApplyPeriodModeOnStart(currentPeriodName!);

		// Written after the decision above, never before it: the note is the only evidence that a boundary went by
		// while the engine was down, and recording the new period first would erase it in the window where the
		// process dies between the two.
		RememberPeriod(currentPeriodName);

		// The period select as an output. Beside the two rules above because it answers the same question they do —
		// which period is in force — and reads the same answer, so the select can never point somewhere the rooms
		// are not. A no-op unless this application owns the select.
		MirrorPeriodSelect(currentPeriodName);

		// Auto-away: switch TO an option once the house has been motion-free for its configured span.
		EvaluateInactivityActivation(now);
	}

	private void OnPeriodEntered(string periodName, HouseModeOptionConfig? activeOption)
	{
		TimePeriodConfig? period = _periods.FirstOrDefault(p => string.Equals(p.Name, periodName, StringComparison.OrdinalIgnoreCase));

		// SetsMode: entering this period sets the select — once, at entry. A human override mid-period stands
		// because the entry only fires on the tick that first sees the new period. A boundary the engine was not
		// running for never reaches here at all; ApplyPeriodModeOnStart is that case, detecting the crossing from
		// the note the previous run left on disk instead of from a tick.
		if (period?.SetsMode is { Length: > 0 } setsMode
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
	///     <see cref="Start"/> — and only when the period is not the one the previous run left off in.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>Without this the mode simply did not follow the schedule.</b> The entry above is edge-triggered on
	///         the tick that first sees a new period, so a boundary crossed while the engine was down was a boundary
	///         nothing ever noticed: a house restarted at 23:30 stayed on its daytime mode until 23:00 came round
	///         again. On a machine that is redeployed several times a day that is most nights.
	///     </para>
	///     <para>
	///         <b>The question is whether a boundary went by, not whether the engine restarted.</b> Restarting
	///         inside the period you were already in is not an event, and re-asserting a mode for it would silently
	///         undo whatever a person chose an hour ago — every deploy, several times a day. Restarting <i>across</i>
	///         a boundary is an event, and it is exactly the event the schedule exists to act on: night began, so
	///         night's mode applies, whatever the select happens to read. That is why the standing mode is not
	///         consulted here. It is the same rule <see cref="OnPeriodEntered"/> follows for a boundary the engine
	///         watched arrive; all that differs is how the crossing is detected.
	///     </para>
	///     <para>
	///         <b>Not knowing is not the same as knowing a boundary was crossed.</b> A first run, a deleted note and
	///         a corrupt one all leave <see cref="_periodAtLastRun"/> null, and all three fall to doing nothing.
	///         Inertia is the safe half: the cost is one missed re-application, after which the note exists and the
	///         rule works; the cost of guessing the other way is a mode overwritten on no evidence at all, on a path
	///         that a corrupt file could trigger on every single start.
	///     </para>
	///     <para>
	///         <b>A restart is still a case of its own, not an entry.</b> Feeding it through
	///         <see cref="OnPeriodEntered"/> would have been fewer lines and wrong: that path also fires the
	///         period-start reset, so a restart across the named boundary would cancel a retained Away or Guest mode
	///         as a side effect of a deploy, and a reset trigger that fires because somebody redeployed is not a
	///         trigger at all.
	///     </para>
	/// </remarks>
	/// <param name="periodName">The period the engine has started inside.</param>
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

		if (string.Equals(previousRun, periodName, StringComparison.OrdinalIgnoreCase))
			return;   // same period as when we stopped: no boundary went by, so nothing to apply

		TimePeriodConfig? period = _periods.FirstOrDefault(p => string.Equals(p.Name, periodName, StringComparison.OrdinalIgnoreCase));

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

	/// <summary>
	///     Points the period select at the period the engine's own schedule resolved.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>A no-op unless this application owns the select.</b> Under
	///         <see cref="PeriodAuthority.HomeAssistant"/> the reader hands out no
	///         <see cref="PeriodSelectReader.OptionForPeriod"/> at all, so there is nothing here to write with — the
	///         two directions are exclusive by construction rather than by a flag this method could get wrong.
	///     </para>
	///     <para>
	///         <b>Edge-triggered by comparison, not by memory.</b> The write happens only when the select is not
	///         already showing the wanted option, which makes it one call per period change in the ordinary day and
	///         also self-healing: a select somebody moved by hand, or one that came back from a Home Assistant
	///         restart on the wrong option, is put right rather than left disagreeing with the lights for hours. A
	///         remembered "we already asked for this" latch would have been fewer calls and would have made both of
	///         those permanent.
	///     </para>
	///     <para>
	///         The log line is bounded separately from the call, on the distinct option: an option the select does
	///         not actually offer is rejected by Home Assistant and would otherwise be retried — correctly — on every
	///         tick, with a line each time.
	///     </para>
	/// </remarks>
	/// <param name="periodName">The period the schedule resolved, or <c>null</c> when none could be placed.</param>
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

	/// <summary>
	///     Reads the previous run's period, treating any failure as "we do not know".
	/// </summary>
	/// <remarks>
	///     <see cref="LastPeriodStore"/> already promises never to throw, and this catches anyway. The store is an
	///     interface a host supplies, this runs inside <see cref="Start"/>, and a start that throws takes the engine
	///     down with it — a blank line in a configuration file once did exactly that. A note nobody can read must be
	///     inert, not fatal.
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

	/// <summary>
	///     Writes the period now current, when it has changed since the last write.
	/// </summary>
	/// <remarks>
	///     Only on a change, because that is a handful of writes a day rather than one per tick, and the file is
	///     read exactly once per process. A null period — a circadian table with no placeable boundary at all — is
	///     not written: it would replace a good note with a worse one, and the previous note stays true for longer.
	/// </remarks>
	/// <param name="periodName">The active period, or <c>null</c> when none resolved.</param>
	private void RememberPeriod(string? periodName)
	{
		if (_lastPeriod is null || periodName is not { Length: > 0 })
			return;

		lock (_gate)
		{
			if (string.Equals(_persistedPeriodName, periodName, StringComparison.OrdinalIgnoreCase))
				return;

			// Recorded as attempted even when the write fails: a store that cannot write has already warned, and
			// retrying it on every tick would turn one warning into one per minute for as long as the fault lasts.
			_persistedPeriodName = periodName;
		}

		try
		{
			_lastPeriod.TrySave(periodName);
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			// Same reasoning as the read: losing the note costs one re-application after the next restart, and
			// must never cost the tick it is written from.
			_logger.LogWarning(exception, "Could not record that the engine is now in period '{Period}'.", periodName);
		}
	}



	/// <summary>Returns the select to the single Normal option, logging which trigger fired. No-op when no Normal resolves.</summary>
	private void Reset(string trigger)
	{
		if (_global.HouseMode is not { Entity: { Length: > 0 } select } houseMode)
			return;

		// No Normal option → nothing to reset to. A no-op rather than a clobber onto a tagged option, warned once so
		// a household that has forgotten to mark a Normal sees it without the log filling up every tick.
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
