using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Subjects;

using AdaptiveLighting.Configuration;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>The mode brain: reads the house-mode select, derives the active kind and scene, and owns the set, retain and reset lifecycle.</summary>
// A mode set by hand or by a period's SetsModeId is retained until a reset trigger fires, and every reset returns
// the select to the single Normal option. HouseState is never mutated here: every mode change is an
// input_select.select_option that flows back through Home Assistant and the normal Changed path, like a hand on
// the dial. Its only clock is the injected IScheduler.
public sealed class ModeMonitor : IDisposable
{
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
	private readonly TimeZoneInfo _zone;

	// The two selects this engine may drive. Same mechanics, different rules: the rules stay below, the ownership
	// test and the call live in SelectMirror.
	private readonly SelectMirror _modeSelect;
	private readonly SelectMirror _periodMirror;
	private readonly IReadOnlyCollection<string> _areaMotionSensors;

	// Which sensors may start each held period. A null value means any watched sensor; an empty set is a period
	// naming rooms none of whose sensors resolved, which can never fire.
	private readonly Dictionary<string, IReadOnlySet<string>?> _motionStartPeriods = new(StringComparer.OrdinalIgnoreCase);

	// Shared with every area's calculator: which periods wait for movement, and which have begun today. The only
	// writer, and the reason the rooms and this monitor cannot disagree about whether a period has started.
	private readonly MotionPeriodLatch _motionPeriods;

	// Where the period the engine is running in is written down, so the next start can tell whether a boundary
	// went by while it was stopped. Null reads as unknown.
	private readonly ILastPeriodStore? _lastPeriod;

	// Null when no period select is configured. Which direction it grants is its own to say.
	private readonly PeriodSelectReader? _periodSelect;

	// Wakes this monitor at the boundary itself, so a period's SetsModeId and the period mirror do not wait out a
	// whole CircadianTickSeconds. The tick below is the safety net and still runs.
	private readonly BoundaryTimer _boundary;

	// The house's sun entity announcing that it has moved. Null for a house whose boundaries the tick alone re-arms.
	private readonly IObservable<Unit>? _sunMoved;

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
	private string? _previousPeriodId;
	private bool _started;

	// The option the no-motion rule last wrote, so a mode the engine set can be told from the same mode a person
	// chose. Cleared the moment the select stops reading it, and never persisted: after a restart nobody knows why
	// the select stands where it does.
	private string? _inactivityActivated;

	// The last sentence AnnounceForcedMode wrote. Empty means nothing was being forced at the previous read.
	private string _announcedForce = "";

	// Bounds the mirror log line only. The call itself compares against what the select actually reads, so a
	// select that never echoes is asked again.
	private string? _mirroredPeriodOption;

	// Armed by Start, spent on the first tick that can read the select. Held open until the select answers,
	// because an input_select can be unavailable for a while after a Home Assistant restart.
	private bool _startPeriodModePending;

	// What the previous run left on disk, read once in Start. Null is unknown, which differs from knowing a
	// boundary was crossed.
	private string? _periodAtLastRun;

	// What this run believes is on disk now, so the file is written on a period change and not on every tick.
	private string? _persistedPeriodId;

	// areaMotionSensors is the union across every area, and an option with an empty ResetPresenceSensors resets on
	// any of them; motionSensorsByArea is that same union split by area id, which is what StartsOnMotionAreas
	// names. Without a lastPeriod the monitor never learns that a boundary was crossed during an outage.
	// motionPeriods must be the same instance every area's calculator was built with, or the rooms and this
	// monitor answer differently about whether a held period has begun. sunMoved is the sun entity behind
	// sunTimes announcing that its rising or setting moved; omitting it leaves the boundaries to the tick alone.
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
		IReadOnlyDictionary<string, IReadOnlyList<string>>? motionSensorsByArea = null,
		MotionPeriodLatch? motionPeriods = null,
		TimeZoneInfo? zone = null,
		IObservable<Unit>? sunMoved = null)
	{
		_zone = zone ?? TimeZoneInfo.Local;
		_sunMoved = sunMoved;
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_global = global ?? throw new ArgumentNullException(nameof(global));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
		_periods = periods ?? throw new ArgumentNullException(nameof(periods));
		_sunTimes = sunTimes ?? throw new ArgumentNullException(nameof(sunTimes));
		_areaMotionSensors = areaMotionSensors ?? throw new ArgumentNullException(nameof(areaMotionSensors));
		_lastPeriod = lastPeriod;
		_periodSelect = periodSelect;

		_modeSelect = new SelectMirror(ha, global.HouseMode?.Entity, !HouseModeIsHomeAssistants);

		// OptionForPeriod is non-null on the one authority that permits a write; PeriodSelectReader is where that
		// branch is decided, and it is decided once.
		_periodMirror = new SelectMirror(ha, periodSelect?.Entity, periodSelect?.OptionForPeriod is not null);

		_motionPeriods = motionPeriods ?? MotionPeriodLatch.For(periods, global);

		_circadian = new CircadianCalculator(
			periods, global, sunTimes, roomLevels: null, periodSelect?.ReadPeriod, _motionPeriods.IsHeldBack, _zone);

		_boundary = new BoundaryTimer(_scheduler, () => _circadian.NextBoundary(_scheduler.Now), OnTick, _logger);

		BuildMotionStartPeriods(motionSensorsByArea);
	}

	// Only periods the latch holds. That is where the authority branch lives, so a dropdown-owned house builds no
	// rows here and motion starts nothing.
	private void BuildMotionStartPeriods(IReadOnlyDictionary<string, IReadOnlyList<string>>? motionSensorsByArea)
	{
		foreach (TimePeriodConfig period in _periods)
		{
			if (!_motionPeriods.Holds(period.Key))
				continue;

			if (period.StartsOnMotionAreas is not { Count: > 0 })
			{
				_motionStartPeriods[period.Key] = null;
				continue;
			}

			HashSet<string> sensors = new(StringComparer.OrdinalIgnoreCase);

			foreach (string areaId in period.StartsOnMotionAreas)
				if (motionSensorsByArea?.TryGetValue(areaId.Trim(), out IReadOnlyList<string>? found) == true)
					sensors.UnionWith(found);

			// Kept even when empty: a named room that resolved nothing must never read as "any room".
			_motionStartPeriods[period.Key] = sensors;
		}
	}

	/// <summary>Fires whenever the kill switch or the house-mode select changes state.</summary>
	public IObservable<Unit> Changed => _changed;

	/// <summary>Whether the engine is currently forbidden from commanding anything.</summary>
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
			// muzzled); KillSwitchActiveWhenOff governs an explicit entity only. ModeService.GetToggles mirrors it.
			bool enabledFlag = _global.KillSwitchIsDefaulted || _global.KillSwitchActiveWhenOff;
			return enabledFlag ? state.IsOff() : state.IsOn();
		}
	}

	/// <summary>The raw house-mode option string, or <c>null</c> when the select is unconfigured, unknown or unavailable.</summary>
	// Warns once per distinct value no option classifies, which is the tripwire for a rename in Home Assistant.
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

	// What the select's current value classifies to. The set, retain and reset lifecycle acts on this option,
	// because every reset writes the select itself back to Normal.
	private HouseModeOptionConfig? CurrentOption => _global.HouseMode?.OptionFor(CurrentModeValue);

	// Home Assistant owns the select. Every write of it is gated on this, and so is every engine rule that would
	// decide the mode: the dropdown is then the only thing that moves the house.
	private bool HouseModeIsHomeAssistants => _global.HouseMode?.HomeAssistantDecides ?? false;

	// The first option in list order any of whose ActivateWhileOn entities is on, with that entity, or null. Such
	// an option forces the effective house mode whatever the select reads, and the select is never written from
	// here, so there is no loop. The only read of which entity holds a mode on: a second one could name a
	// different entity from the one the engine acted on.
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

	/// <summary>What is forcing the effective mode, or <c>null</c> when the select's own value is the whole story.</summary>
	// Read live, in the order the engine decides in: the ActivateWhileOn overlay wins over the select, so it is
	// asked first. Every kind is reported, not only Away.
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

	// The option the engine acts on: an ActivateWhileOn override wins over the select's value, and empty
	// ActivateWhileOn lists leave this equal to the select-standing option.
	private HouseModeOptionConfig? EffectiveOption => ActivatedNow?.Option ?? CurrentOption;

	/// <summary>The kind of the effective option; <see cref="ModeKind.Normal"/> when nothing classifies.</summary>
	public ModeKind ActiveKind => EffectiveOption?.Kind ?? ModeKind.Normal;

	/// <summary>The effective option's <c>scene.*</c> when it names one, whatever its kind.</summary>
	// Applied once on entry by the orchestrator. On Away and Guest the scene stands because they pause the
	// engine; on Normal and Sleep it is a one-shot the ordinary commands may override.
	public string? ActiveScene =>
		EffectiveOption is { Scene: { Length: > 0 } scene } ? scene : null;

	// An unreadable state is "not on", so a vanished sensor stops forcing its mode instead of pinning it.
	private bool IsOn(string entityId) =>
		!string.IsNullOrWhiteSpace(entityId) && (_ha.GetState(entityId)?.IsOn() ?? false);

	/// <summary>Subscribes and starts the evaluation tick.</summary>
	public void Start()
	{
		string? startingPeriod;

		lock (_gate)
		{
			if (_started)
				return;

			_started = true;
			_activatedAt = _scheduler.Now;
			_lastMotionAt = _scheduler.Now;
			_startPeriodModePending = true;

			// Read once: the answer is about the run that ended, and a later read finds what this run wrote over
			// it. A file read under _gate is safe only here, before the subscriptions and the tick exist.
			_periodAtLastRun = ReadPeriodAtLastRun();
			_persistedPeriodId = _periodAtLastRun;

			// Before ActivePeriodName: seeding may put a held period back in the table.
			SeedPeriodLatch(_scheduler.Now);
			_previousPeriodId = _circadian.ActivePeriodId(_scheduler.Now);
			startingPeriod = _previousPeriodId;
		}

		if (_global.EffectiveKillSwitchEntity is { Length: > 0 } killSwitch)
		{
			_logger.LogInformation("Watching kill switch {EntityId}.", killSwitch);
			_subscriptions.Add(_ha.Entity(killSwitch)
				.StateChanges()
				.SubscribeSafe(
					_ =>
					{
						// Coming back from the master switch crosses no boundary, so nothing else re-asserts the
						// helper and it would read the period paused in until the next tick.
						if (!KillSwitchActive)
							MirrorPeriodSelect(_circadian.ActivePeriodId(_scheduler.Now));

						_changed.OnNext(Unit.Default);
					},
					_logger));
		}

		if (_global.HouseMode?.Entity is { Length: > 0 } select)
		{
			_logger.LogInformation("Watching house-mode select {EntityId}.", select);
			_subscriptions.Add(_ha.Entity(select)
				.StateChanges()
				.SubscribeSafe(OnSelectChanged, _logger));
		}

		// Watched in either direction. Under Home Assistant authority a flip is a boundary the clock did not cross,
		// and waiting for the tick would delay a SetsMode by a whole CircadianTickSeconds. Under the engine's own
		// authority it fires on the engine's echo and costs one idempotent re-evaluation.
		if (_periodSelect is { } periodSelect)
		{
			_logger.LogInformation("Watching period select {EntityId}.", periodSelect.Entity);
			_subscriptions.Add(_ha.Entity(periodSelect.Entity)
				.StateChanges()
				.SubscribeSafe(_ => OnPeriodSelectChanged(), _logger));
		}

		AnnounceDormantModeRules();
		AnnounceHeldPeriods();
		SubscribePresenceResets();
		SubscribeActivationSensors();
		SubscribeMotion();

		// An entity already on before start forces the mode from the first instant and raises no edge, so a house
		// booting into a forced mode would otherwise say nothing until somebody toggled the entity.
		AnnounceForcedMode();

		// SchedulePeriodic's first callback is a whole CircadianTickSeconds away, so without this a restart inside a
		// period leaves the select naming the period before it: at 300 s that is five minutes of a dashboard
		// reading the wrong time of day. A no-op under Home Assistant's authority, and when it already reads right.
		MirrorPeriodSelect(startingPeriod);

		// A moved sun time can put a boundary in the past as easily as the future, so this evaluates before it re-arms.
		if (_sunMoved is { } sunMoved)
			_subscriptions.Add(sunMoved.SubscribeSafe((Unit _) => OnTick(), _logger));

		_subscriptions.Add(_scheduler.SchedulePeriodic(
			TimeSpan.FromSeconds(_global.CircadianTickSeconds),
			OnTick));

		_boundary.Arm();
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
				&& !(change.New?.State).SameName(claimed))
				_inactivityActivated = null;
		}

		AnnounceForcedMode();
		_changed.OnNext(Unit.Default);
	}

	/// <summary>The period select moved, so the period may have changed without the clock crossing anything.</summary>
	// Runs the whole tick body, which is idempotent: period entry is edge-triggered on a name change, the
	// inactivity rule is latched, and the mirror write compares against what the select reads. The rooms are not
	// retargeted here; each area re-reads its own period on its own tick.
	private void OnPeriodSelectChanged()
	{
		OnTick();
		_changed.OnNext(Unit.Default);
	}

	/// <summary>Names, once at start-up, every mode rule Home Assistant's authority has stood down.</summary>
	// Silence here sends somebody hunting an automation that is working as configured, so each dormant rule gets
	// its own line, and only when the document actually carries that rule.
	private void AnnounceDormantModeRules()
	{
		if (!HouseModeIsHomeAssistants || _global.HouseMode is not { } houseMode)
			return;

		_logger.LogInformation(
			"House mode: Home Assistant decides. The engine reads {Select} and never writes it.", houseMode.EntityId);

		if (_periods.Any(period => period.SetsModeId is { Length: > 0 }))
			_logger.LogInformation(
				"A period switching the house mode is dormant while Home Assistant decides it; the schedule will not move {Select}.",
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

	/// <summary>Names the periods that will not begin on the clock, and says so when the rule is stood down.</summary>
	private void AnnounceHeldPeriods()
	{
		if (!_periods.Any(period => period.StartsOnMotion))
			return;

		if (_motionPeriods.HeldPeriods.Count == 0)
		{
			_logger.LogInformation(
				"StartsOnMotion is dormant while Home Assistant decides the time of day; the period select is the only "
				+ "boundary and movement does not start a period.");
			return;
		}

		foreach (string periodKey in _motionPeriods.HeldPeriods)
			_logger.LogInformation(
				"Period '{Period}' does not begin at its Start; the period before it keeps running until somebody moves, "
				+ "and the next period's Start overtakes it if nobody does.",
				DisplayName(periodKey));
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
	// Deduplicated on the sentence and never on the entity, so an entity that stays on says it once while a
	// different entity taking over gets its own line. Every kind is announced, not only Away.
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

		// Passed as a property and never concatenated, so a structured sink keeps the sentence whole.
		_logger.LogInformation(
			"{ForcedMode} The house-mode select reads '{Select}' and is being overridden; this is not a presence departure.",
			sentence, CurrentModeValue ?? "(nothing)");
	}

	// Subscribes the motion union once, for the two rules that read it: auto-away by inactivity, and a period that
	// starts on motion.
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
					DisplayName(row.Key));

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

	private void MarkMotion()
	{
		lock (_gate)
		{
			_lastMotionAt = _scheduler.Now;
			_inactivityLatched = false;
		}
	}

	/// <summary>Offers the movement to the period it is allowed to start, and enters that period if it may.</summary>
	// Under _gate for the reason OnTick gives: this runs on Home Assistant's thread, and a transition read but not
	// claimed is entered twice. Asked of the schedule and never of what is in force, because the period movement
	// may start is the one the calculators hold out of the table. Two bounds are load-bearing: a period is never
	// placed before its own Start, so night still running at 02:00 cannot let the 06:30 period fire on a trip to
	// the kitchen; and once per local day, so walking back in at lunch does not re-fire SetsModeId over a mode
	// somebody chose since.
	private void StartPeriodOnMotion(string sensor, DateTimeOffset now)
	{
		if (_motionStartPeriods.Count == 0)
			return;

		// Before the lock: this consults Home Assistant.
		HouseModeOptionConfig? activeOption = CurrentOption;

		// The household's day, as the circadian table reads it. The two must agree or a period's mode switch is
		// filed against a different day from the one the table placed it on.
		DateOnly today = now.DayIn(_zone);

		string? periodKey;
		bool start;

		lock (_gate)
		{
			periodKey = _circadian.ScheduledPeriodId(now);

			// TryBegin last: it claims the day, so nothing after it may refuse the start.
			start = periodKey is { Length: > 0 }
				&& MotionMayStart(periodKey, sensor)
				&& StartHasPassed(periodKey, now)
				&& _motionPeriods.TryBegin(periodKey, today);

			if (start)
			{
				_previousPeriodId = periodKey;
				_startPeriodModePending = false;
			}
		}

		if (!start)
			return;

		_logger.LogInformation("Motion on {Sensor} started period '{Period}'.", sensor, DisplayName(periodKey));

		// Written now, because the latch is in memory and a config save rebuilds the engine: the note is the only
		// thing that tells the rebuilt monitor this period had already begun.
		RememberPeriod(periodKey);

		OnPeriodEntered(periodKey!, activeOption);
		_changed.OnNext(Unit.Default);
	}

	// Under _gate; reads configuration settled in the constructor and nothing else.
	private bool MotionMayStart(string periodKey, string sensor) =>
		_motionStartPeriods.TryGetValue(periodKey, out IReadOnlySet<string>? rooms)
		&& (rooms is null || rooms.Contains(sensor));

	// A period whose Start has not come round today cannot be started by motion. This is what keeps the wrapped
	// period (night, still running at 02:00) from re-entering on the far side of midnight.
	private bool StartHasPassed(string periodKey, DateTimeOffset now)
	{
		if (PeriodWithKey(periodKey) is not { } period || !PeriodStart.TryParse(period.Start, out PeriodStart? start))
			return false;

		return start!.Resolve(_sunTimes()) is { } resolved && resolved <= now.TimeIn(_zone);
	}

	/// <summary>Seeds the latch, under _gate, so the period the clock places now counts as begun for this run.</summary>
	// Restarting inside a period is no entry, so a later movement must not re-fire its SetsModeId or its
	// period-start reset over a mode a person chose. A period that waits for movement is seeded only when the note
	// on disk says the last run was already inside it; without the note there is no evidence it ever began.
	private void SeedPeriodLatch(DateTimeOffset now)
	{
		if (_circadian.ScheduledPeriodId(now) is not { Length: > 0 } scheduled)
			return;

		if (_motionPeriods.Holds(scheduled)
			&& !string.Equals(_periodAtLastRun, scheduled, StringComparison.OrdinalIgnoreCase))
			return;

		_motionPeriods.MarkBegun(scheduled, InstanceDay(scheduled, now));
	}

	// The local day the running instance of this period began on. A Start still ahead of now belongs to yesterday's
	// instance, which is the one the table's wrap puts in force.
	private DateOnly InstanceDay(string periodKey, DateTimeOffset now)
	{
		DateOnly today = now.DayIn(_zone);
		return StartHasPassed(periodKey, now) ? today : today.AddDays(-1);
	}

	private static bool BoundaryWentByWhileDown(string? previousRun, string periodKey) =>
		previousRun is { Length: > 0 } && !string.Equals(previousRun, periodKey, StringComparison.OrdinalIgnoreCase);

	private TimePeriodConfig? PeriodWithKey(string periodKey) => _periods.ByKey(periodKey);

	// Every log line names the period a person would recognise, never the id the engine resolved by.
	private string DisplayName(string? periodKey) =>
		periodKey is { Length: > 0 } && PeriodWithKey(periodKey)?.Name is { Length: > 0 } name ? name : periodKey ?? "";

	private bool AnyMotionOn() => _areaMotionSensors.Any(IsOn);

	/// <summary>Switches the select to an option once the whole house has been motion-free for its configured span.</summary>
	// Polled on the tick. First qualifying option in list order wins, one activation per tick. It writes the
	// select, so the option's reset triggers arm through OnSelectChanged as a manual switch would.
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
		// until Home Assistant echoes back, and re-fires against a person who switches away while the house is quiet.
		if (latched)
			return;

		foreach (HouseModeOptionConfig option in houseMode.Options
			.Where(candidate => candidate.Kind != ModeKind.Normal && candidate.ActivateAfterNoMotionMinutes is > 0))
		{
			if (_modeSelect.AlreadyShows(option.Value))
				continue;   // already standing on this mode

			if (now - lastMotionAt < TimeSpan.FromMinutes(option.ActivateAfterNoMotionMinutes!.Value))
				continue;

			_modeSelect.Ensure(option.Value, entity => _logger.LogInformation(
				"No motion for {Minutes} min; setting {Select} to '{Mode}'.",
				option.ActivateAfterNoMotionMinutes, entity, option.Value));

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
	// has expired, so walking out the door does not cancel the mode just set.
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

	/// <summary>One evaluation of the clock's news: period entry, the across-restart catch-up, the mirror and the auto-away timer.</summary>
	// Under _gate, because reading the period and claiming the transition must be one step: a period-select flip
	// runs this from Home Assistant's thread, and a transition read but not claimed is acted on twice. The read
	// is inside the gate too; with only the claim under the lock, a thread holding a stale name fires a backwards
	// entry and regresses _previousPeriodId. The read is a state-cache lookup plus a sort and cannot re-enter.
	private void OnTick()
	{
		DateTimeOffset now = _scheduler.Now;

		// Before the lock: these consult Home Assistant, and _gate is for this object's own fields.
		HouseModeOptionConfig? activeOption = CurrentOption;
		bool modeIsReadable = CurrentModeValue is { Length: > 0 };

		// A period select that cannot be read is no period change. Under Home Assistant authority the period falls
		// through to the clock while the helper is unavailable, and that answer is indistinguishable from a real
		// one: a household holding "day" at 23:30 would have night's SetsMode latch the house asleep, and nothing
		// puts it back when the helper returns.
		bool overrideIsBlind = _periodSelect is { ReadPeriod: not null } reader && reader.CurrentValue() is null;

		string? currentPeriodId;
		bool entered;
		bool applyOnStart;

		lock (_gate)
		{
			currentPeriodId = _circadian.ActivePeriodId(now);

			entered = !overrideIsBlind
				&& currentPeriodId is { Length: > 0 }
				&& _previousPeriodId is { Length: > 0 }
				&& !string.Equals(currentPeriodId, _previousPeriodId, StringComparison.OrdinalIgnoreCase);

			applyOnStart = !entered
				&& !overrideIsBlind
				&& _startPeriodModePending
				&& currentPeriodId is { Length: > 0 }
				&& modeIsReadable;

			// A blind read is no evidence about which period is running, and recording the clock's guess would
			// make the helper's recovery look like a fresh entry.
			if (!overrideIsBlind)
				_previousPeriodId = currentPeriodId;

			// The one restart chance is spent by an entry or by using it. A tick that could read neither the
			// period nor the select leaves it armed.
			if (entered || applyOnStart)
				_startPeriodModePending = false;

			// Nothing latches a period here: the clock can only enter one the latch already lets through, and
			// Start seeded whichever period this run came up inside.
		}

		if (entered)
			OnPeriodEntered(currentPeriodId!, activeOption);
		else if (applyOnStart)
			ApplyPeriodModeOnStart(currentPeriodId!);

		// After the decision above, never before it. The note is the only evidence that a boundary went by while
		// the engine was down, and writing first erases it if the process dies between the two.
		RememberPeriod(currentPeriodId);

		// The period select as an output, reading the same answer the two rules above did. No-op unless this
		// application owns the select.
		MirrorPeriodSelect(currentPeriodId);

		EvaluateInactivityActivation(now);

		// Re-asked every evaluation, so a sun time that has moved, a table a save rebuilt or a clock the box
		// corrected all re-arm within one tick.
		_boundary.Arm();
	}

	private void OnPeriodEntered(string periodKey, HouseModeOptionConfig? activeOption)
	{
		TimePeriodConfig? period = PeriodWithKey(periodKey);

		// Once, at entry, so a human override mid-period stands. A boundary the engine was not running for never
		// reaches here; ApplyPeriodModeOnStart handles that from the note on disk.
		if (period?.SetsModeId is { Length: > 0 } setsMode
			&& _global.HouseMode?.OptionValueFor(setsMode) is { Length: > 0 } wanted)
			_modeSelect.Ensure(wanted, entity => _logger.LogInformation(
				"Period '{Period}' started; setting {Select} to '{Mode}'.", DisplayName(periodKey), entity, wanted));

		if (activeOption is { Kind: not ModeKind.Normal, ResetOnPeriodStartId: { Length: > 0 } resetPeriod }
			&& resetPeriod.SameName(periodKey))
			Reset($"period '{DisplayName(periodKey)}' started");
	}

	/// <summary>Applies the current period's <see cref="TimePeriodConfig.SetsModeId"/> once, on the first tick after <see cref="Start"/>.</summary>
	// The question is whether a boundary went by, never whether the engine restarted: restarting inside the same
	// period is no event, while restarting across a boundary is the event the schedule exists to act on. A null
	// _periodAtLastRun is unknown and does nothing, because guessing the other way overwrites a mode on no
	// evidence, on a path a corrupt file could trigger every start. Kept separate from OnPeriodEntered because
	// that path also fires the period-start reset, which would cancel a retained Away or Guest mode.
	private void ApplyPeriodModeOnStart(string periodKey)
	{
		string? previousRun;
		lock (_gate)
			previousRun = _periodAtLastRun;

		if (previousRun is not { Length: > 0 })
		{
			_logger.LogDebug(
				"Started inside period '{Period}', but there is no note of which period the last run ended in; "
				+ "assuming nothing and leaving the house mode alone.",
				DisplayName(periodKey));
			return;
		}

		if (!BoundaryWentByWhileDown(previousRun, periodKey))
			return;   // the same period the last run ended in: no boundary went by, so nothing to apply

		if (HouseModeIsHomeAssistants)
			return;

		TimePeriodConfig? period = PeriodWithKey(periodKey);

		if (period?.SetsModeId is not { Length: > 0 } setsMode
			|| _global.HouseMode?.OptionValueFor(setsMode) is not { Length: > 0 } wanted)
			return;

		_modeSelect.Ensure(wanted, entity => _logger.LogInformation(
			"Period '{Period}' began while the engine was stopped (it was last running in '{Previous}'); setting {Select} to '{Mode}'.",
			DisplayName(periodKey), DisplayName(previousRun), entity, wanted));
	}

	/// <summary>Points the period select at the period the engine's own schedule resolved.</summary>
	// A no-op under PeriodAuthority.HomeAssistant, where the reader hands out no OptionForPeriod, so the two
	// directions are exclusive by construction and never by a flag this method could get wrong. Triggered by
	// comparison, never by memory: a select moved by hand, or one back from a Home Assistant restart on the wrong
	// option, is put right, where an already-asked latch would make both permanent. The log line is bounded
	// separately, because an option the select does not offer is rejected and correctly retried on every tick.
	private void MirrorPeriodSelect(string? periodKey)
	{
		if (_periodSelect?.OptionForPeriod is not { } optionFor || periodKey is not { Length: > 0 })
			return;

		if (optionFor(periodKey) is not { Length: > 0 } wanted)
			return;   // no row maps this period; the validator has already said so if it matters

		_periodMirror.Ensure(wanted, entity =>
		{
			// Info the first time a value is asked for, Debug on every retry: an option the select does not offer
			// is rejected and correctly re-asked on every tick, and that must not fill the log.
			bool firstTime;
			lock (_gate)
			{
				firstTime = !string.Equals(_mirroredPeriodOption, wanted, StringComparison.OrdinalIgnoreCase);
				_mirroredPeriodOption = wanted;
			}

			if (firstTime)
				_logger.LogInformation("Period '{Period}' is in force; setting {Select} to '{Option}'.",
					DisplayName(periodKey), entity, wanted);
			else
				_logger.LogDebug("Period select {Select} still does not read '{Option}'; asking again.", entity, wanted);
		});
	}

	/// <summary>Reads the previous run's period, treating any failure as unknown.</summary>
	// LastPeriodStore promises never to throw, and this catches anyway: the store is an interface a host
	// supplies, and this runs inside Start, where a throw takes the engine down. A note written before periods
	// had ids holds a name, and without translating it a start compares a name against a key and reads a
	// boundary crossing that never happened.
	private string? ReadPeriodAtLastRun()
	{
		if (_lastPeriod is null)
			return null;

		try
		{
			if (_lastPeriod.Load() is not { Length: > 0 } stored)
				return null;

			if (PeriodWithKey(stored) is not null)
				return stored;

			return _periods.ByName(stored)?.Key ?? stored;
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			_logger.LogWarning(exception, "Could not read which period the last run ended in; assuming nothing.");
			return null;
		}
	}

	/// <summary>Writes the period now current, when it has changed since the last write.</summary>
	// Only on a change: a handful of writes a day, and the file is read once per process. A null period, from a
	// table with no placeable boundary, is left unwritten, or a good note is replaced by a worse one.
	private void RememberPeriod(string? periodKey)
	{
		if (_lastPeriod is null || periodKey is not { Length: > 0 })
			return;

		lock (_gate)
		{
			if (string.Equals(_persistedPeriodId, periodKey, StringComparison.OrdinalIgnoreCase))
				return;

			// Recorded as attempted even when the write fails, or a persistent fault warns once a minute.
			_persistedPeriodId = periodKey;
		}

		try
		{
			_lastPeriod.TrySave(periodKey);
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			// Losing the note costs one re-application after the next restart, never the tick it is written from.
			_logger.LogWarning(exception, "Could not record that the engine is now in period '{Period}'.", DisplayName(periodKey));
		}
	}

	/// <summary>Returns the select to the single Normal option, logging which trigger fired.</summary>
	private void Reset(string trigger)
	{
		// Under Home Assistant's authority nothing here writes the select, resets included.
		if (HouseModeIsHomeAssistants || _global.HouseMode is not { Entity: { Length: > 0 } select } houseMode)
			return;

		// No Normal option means nothing to reset to: a no-op, never a clobber onto a tagged option.
		if (houseMode.NormalOption?.Value is not { Length: > 0 } normal)
		{
			if (System.Threading.Interlocked.Exchange(ref _warnedNoNormal, 1) == 0)
				_logger.LogWarning("A reset fired ({Trigger}) but no Normal option resolves; leaving the mode unchanged (this warns once).", trigger);
			return;
		}

		_modeSelect.Ensure(normal, entity =>
			_logger.LogInformation("Resetting {Select} to '{Normal}' ({Trigger}).", entity, normal, trigger));
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		_boundary.Dispose();
		_subscriptions.Dispose();
		_changed.Dispose();
	}
}
