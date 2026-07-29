using System.Collections.Concurrent;
using System.Globalization;
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
	private DateTimeOffset _previousTickAt;
	private string? _previousPeriodName;
	private bool _started;

	// Armed by Start and spent on the first tick that can read the select: the one chance a restart gets to notice
	// that the period it woke up inside sets a mode. Held open until the select answers, because after a Home
	// Assistant restart an input_select can be unavailable for a while, and spending the chance on a value nobody
	// could read would waste it exactly when it is most needed. See ApplyPeriodModeOnStart for why a restart is not
	// simply treated as a period entry.
	private bool _startPeriodModePending;

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
	public ModeMonitor(
		IHaContext ha,
		GlobalConfig global,
		ILogger logger,
		IScheduler scheduler,
		IReadOnlyList<TimePeriodConfig> periods,
		Func<SunTimes> sunTimes,
		IReadOnlyCollection<string> areaMotionSensors)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_global = global ?? throw new ArgumentNullException(nameof(global));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
		_periods = periods ?? throw new ArgumentNullException(nameof(periods));
		ArgumentNullException.ThrowIfNull(sunTimes);
		_areaMotionSensors = areaMotionSensors ?? throw new ArgumentNullException(nameof(areaMotionSensors));

		_circadian = new CircadianCalculator(periods, global, sunTimes);
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
	///     is currently on, or <c>null</c> when none is. Such an option forces the effective house mode regardless of
	///     the select's value; the select is never written from this, so there is no feedback loop.
	/// </summary>
	private HouseModeOptionConfig? ActivatedOption =>
		_global.HouseMode?.Options.FirstOrDefault(option => option.ActivateWhileOn.Any(IsOn));

	/// <summary>
	///     The option the engine actually acts on: an <see cref="HouseModeOptionConfig.ActivateWhileOn"/> override
	///     wins over the select's value, else the select decides exactly as before. Empty ActivateWhileOn lists
	///     leave this equal to the select-standing option, so the mode is behaviour-neutral without them.
	/// </summary>
	private HouseModeOptionConfig? EffectiveOption => ActivatedOption ?? CurrentOption;

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
			_previousTickAt = _scheduler.Now;
			_previousPeriodName = _circadian.ActivePeriodName(_scheduler.Now);
			_startPeriodModePending = true;
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

		SubscribePresenceResets();
		SubscribeActivationSensors();
		SubscribeInactivityActivation();

		_subscriptions.Add(_scheduler.SchedulePeriodic(
			TimeSpan.FromSeconds(_global.CircadianTickSeconds),
			OnTick));
	}

	// The select moving — for any reason, human or engine — restarts the activation clock and republishes house
	// state. Retention is free: nothing else ever clears a mode.
	private void OnSelectChanged(StateChange change)
	{
		lock (_gate)
			_activatedAt = _scheduler.Now;

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
				.SubscribeSafe(_ => _changed.OnNext(Unit.Default), _logger));
		}
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
				_inactivityLatched = true;
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

	private void OnTick()
	{
		DateTimeOffset now = _scheduler.Now, previousTickAt;
		string? previousPeriodName;
		bool startPending;

		lock (_gate)
		{
			previousTickAt = _previousTickAt;
			previousPeriodName = _previousPeriodName;
			startPending = _startPeriodModePending;
		}

		HouseModeOptionConfig? activeOption = CurrentOption;
		string? currentPeriodName = _circadian.ActivePeriodName(now);

		// Period entry: the active period changed since the previous tick.
		bool entered = currentPeriodName is { Length: > 0 }
			&& previousPeriodName is { Length: > 0 }
			&& !string.Equals(currentPeriodName, previousPeriodName, StringComparison.OrdinalIgnoreCase);

		if (entered)
		{
			OnPeriodEntered(currentPeriodName!, activeOption);
			startPending = false;   // the entry has already had the say the restart rule would have had
		}
		else if (startPending && currentPeriodName is { Length: > 0 } && CurrentModeValue is { Length: > 0 })
		{
			ApplyPeriodModeOnStart(currentPeriodName);
			startPending = false;
		}

		// Time reset: the input_datetime moment crossed since the last tick (and after activation).
		EvaluateTimeReset(activeOption, now, previousTickAt);

		// Auto-away: switch TO an option once the house has been motion-free for its configured span.
		EvaluateInactivityActivation(now);

		lock (_gate)
		{
			_previousTickAt = now;
			_previousPeriodName = currentPeriodName;
			_startPeriodModePending = startPending;
		}
	}

	private void OnPeriodEntered(string periodName, HouseModeOptionConfig? activeOption)
	{
		TimePeriodConfig? period = _periods.FirstOrDefault(p => string.Equals(p.Name, periodName, StringComparison.OrdinalIgnoreCase));

		// SetsMode: entering this period sets the select — once, at entry. A human override mid-period stands
		// because the entry only fires on the tick that first sees the new period. A boundary the engine was not
		// running for never reaches here at all; ApplyPeriodModeOnStart is that case, under a stricter rule.
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
	///     Applies the <see cref="TimePeriodConfig.SetsMode"/> of the period the engine <i>woke up inside</i>, once,
	///     on the first tick after <see cref="Start"/> — and only over the Normal option.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>Without this the mode simply did not follow the schedule.</b> The entry above is edge-triggered on
	///         the tick that first sees a new period, so a boundary crossed while the engine was down was a boundary
	///         nothing ever noticed: a house restarted at 23:30 stayed on its daytime mode until 23:00 came round
	///         again. On a machine that is redeployed several times a day that is most nights.
	///     </para>
	///     <para>
	///         <b>A restart is a case of its own, not an entry.</b> Feeding it through
	///         <see cref="OnPeriodEntered"/> would have been fewer lines and wrong twice over. That path also fires
	///         the period-start reset, so every deploy inside the named period would silently cancel a retained
	///         Away or Guest mode — a restart crossed no boundary, and a reset trigger that fires because somebody
	///         redeployed is not a trigger at all. And the entry path deliberately overwrites whatever the select
	///         stands on, which is safe for a boundary the engine watched arrive and unsafe for one it did not.
	///     </para>
	///     <para>
	///         <b>Only over Normal, which is the half of the decision that can be taken without asking.</b> After a
	///         restart the select's value carries no history: "Gjester, chosen by hand an hour ago" and "Gjester,
	///         left over from last week" are the same string. Standing on Normal is the one reading with no such
	///         ambiguity — nobody has chosen anything, so nothing is being overruled. Anything else stands, which
	///         costs a night's Sover in the case where the human had in fact chosen Normal-then-something and wanted
	///         the period to win. Whether a restart should re-assert the period's mode over a tagged option too is
	///         the owner's call, not this method's.
	///     </para>
	/// </remarks>
	private void ApplyPeriodModeOnStart(string periodName)
	{
		TimePeriodConfig? period = _periods.FirstOrDefault(p => string.Equals(p.Name, periodName, StringComparison.OrdinalIgnoreCase));

		if (period?.SetsMode is not { Length: > 0 } setsMode
			|| _global.HouseMode is not { Entity: { Length: > 0 } select } houseMode)
			return;

		if (string.Equals(CurrentModeValue, setsMode.Trim(), StringComparison.OrdinalIgnoreCase))
			return;   // already standing where the period wants it

		// A value no option classifies lands here too. ActiveKind reads such a value as Normal, which is right for
		// deciding how to light a room and wrong for deciding whether to overwrite it: nobody knows what it means,
		// so nobody should be writing over it on a guess.
		if (houseMode.NormalOption is not { } normal || !ReferenceEquals(CurrentOption, normal))
		{
			_logger.LogDebug(
				"Started inside period '{Period}', which sets '{Mode}', but {Select} stands on '{Current}' rather than Normal; leaving it.",
				periodName, setsMode, select, CurrentModeValue ?? "nothing readable");
			return;
		}

		_logger.LogInformation("Started inside period '{Period}'; setting {Select} to '{Mode}'.", periodName, select, setsMode);
		_ha.CallService(SelectDomain, SelectOptionService,
			new ServiceTarget { EntityIds = [select] }, new { option = setsMode.Trim() });
	}

	private void EvaluateTimeReset(HouseModeOptionConfig? activeOption, DateTimeOffset now, DateTimeOffset previousTickAt)
	{
		if (activeOption is not { Kind: not ModeKind.Normal, ResetAtTime: { Length: > 0 } entity })
			return;

		if (ResolveResetMoment(entity, now) is not { } moment)
			return;

		DateTimeOffset activatedAt;
		lock (_gate)
			activatedAt = _activatedAt;

		// Poll-based: fire when the moment lies between the later of (activation, previous tick) and now.
		DateTimeOffset lowerBound = activatedAt > previousTickAt ? activatedAt : previousTickAt;
		if (moment > lowerBound && moment <= now)
			Reset($"time {entity} passed");
	}

	/// <summary>
	///     The wall-clock instant an <c>input_datetime</c> names, in <paramref name="now"/>'s frame. A date+time
	///     helper is that timestamp; a time-only helper is a daily reset, resolved to its most recent occurrence at
	///     or before <paramref name="now"/>.
	/// </summary>
	private DateTimeOffset? ResolveResetMoment(string entityId, DateTimeOffset now)
	{
		if (_ha.GetState(entityId).AsUsableState() is not { } text)
			return null;

		if (DateTime.TryParseExact(text, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm"],
				CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dateTime))
			return new DateTimeOffset(dateTime, now.Offset);

		if (TimeOnly.TryParseExact(text, ["HH:mm:ss", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly timeOnly))
		{
			// A time-only helper is a daily reset. Resolve it to the most recent occurrence at or before `now`, not
			// to now.Date's occurrence: a tick that straddles midnight would otherwise place yesterday's 23:59 a full
			// day ahead (tomorrow's now.Date), so the window (previousTick, now] never contains it and the reset is
			// silently skipped.
			DateTimeOffset candidate = new(now.Date.Add(timeOnly.ToTimeSpan()), now.Offset);
			return candidate > now ? candidate.AddDays(-1) : candidate;
		}

		_logger.LogWarning("Reset time {Entity} reports '{Value}', which is not a parseable input_datetime.", entityId, text);
		return null;
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
