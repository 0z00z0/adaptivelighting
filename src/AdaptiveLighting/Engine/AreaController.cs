using System.Reactive.Concurrency;
using System.Reactive.Disposables;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.LastSeen;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>One area's state machine.</summary>
/// <remarks>
///     Every timer runs on the injected <see cref="IScheduler"/> and every "now" is <see cref="IScheduler.Now"/>.
///     Rx and scheduler callbacks interleave on whatever thread each arrives on, so every read and write of the
///     machine's state happens under <see cref="_gate"/>. Commands go out from inside the lock: safe because the
///     actuator is fire-and-forget, and necessary because a command must not be decided from one state and sent
///     after another has replaced it.
/// </remarks>
public sealed class AreaController : IDisposable
{
	private readonly IHaContext _ha;
	private readonly IScheduler _scheduler;
	private readonly ResolvedArea _area;
	private readonly string? _areaId;
	private readonly GlobalConfig _global;
	private readonly IReadOnlyList<TimePeriodConfig> _periods;
	private readonly CircadianCalculator _circadian;
	private readonly IlluminanceGate _gateSensor;
	private readonly LuxBrightnessCurve _luxBrightness;
	private readonly OverrideDetector _detector;
	private readonly ILightActuator _actuator;
	private readonly IStatePublisher _publisher;
	private readonly IObservable<HouseState> _houseChanged;
	private readonly ILogger _logger;

	private readonly object _gate = new();
	private readonly CompositeDisposable _subscriptions = [];
	private readonly SerialDisposable _vacancyTimer = new();
	private readonly SerialDisposable _preOffTimer = new();
	private readonly SerialDisposable _overrideTimer = new();
	private readonly SerialDisposable _suppressionTimer = new();

	private AreaState _state = AreaState.AutoVacant;
	private HouseState _house = HouseState.Initial;
	private LightTarget? _lastTarget;

	// The period last resolved and the instant it was resolved for. Snapshot reads these instead of calling
	// GetTarget again, because OnTick and ApplyTarget already resolved for the same instant under the same lock.
	private DateTimeOffset _resolvedPeriodAt;
	private string? _resolvedPeriodName;
	private RoomLevelSource _resolvedLevelsFromRoom;
	private LightCommand? _lastCommand;
	private DateTimeOffset? _lastCommandAt;
	private DateTimeOffset? _lastMotionAt;
	private DateTimeOffset? _nextChangeAt;
	private DateTimeOffset? _nextChangeFrom;
	private bool? _lastDarkVerdict;
	private string? _lastDarknessDetail;
	private AreaSnapshot? _lastPublished;

	// The gate named by the last declined-motion report, and the entity behind it. These are what bound
	// ReportDeclinedMotion. Both are cleared the moment the area actually lights.
	private AutoOnBlock? _reportedDecline;
	private string? _reportedDeclineEntity;

	private bool _disposed;

	/// <summary>Creates a controller for one resolved area.</summary>
	/// <remarks>
	///     <paramref name="areaId"/> is published on every snapshot so a reader can join live state to the
	///     document by identity, not by display name. It travels beside <paramref name="area"/> because the engine
	///     itself has no use for it.
	/// </remarks>
	public AreaController(
		IHaContext ha,
		IScheduler scheduler,
		ResolvedArea area,
		GlobalConfig global,
		IReadOnlyList<TimePeriodConfig> periods,
		CircadianCalculator circadian,
		ILightActuator actuator,
		IStatePublisher publisher,
		IObservable<HouseState> houseChanged,
		ILoggerFactory loggerFactory,
		string? areaId = null,
		IEntityLastSeen? lastSeen = null)
	{
		ArgumentNullException.ThrowIfNull(loggerFactory);

		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
		_area = area ?? throw new ArgumentNullException(nameof(area));
		_global = global ?? throw new ArgumentNullException(nameof(global));
		_periods = periods ?? throw new ArgumentNullException(nameof(periods));
		_circadian = circadian ?? throw new ArgumentNullException(nameof(circadian));
		_actuator = actuator ?? throw new ArgumentNullException(nameof(actuator));
		_publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
		_houseChanged = houseChanged ?? throw new ArgumentNullException(nameof(houseChanged));
		_areaId = areaId is { Length: > 0 } ? areaId : null;

		_logger = loggerFactory.CreateLogger($"{typeof(AreaController).FullName}.{area.Name}");

		// Own sensors, averaged; otherwise the house's outdoor one only if the room asked. A room with neither has
		// no reading at all, and IlluminanceGate treats that as dark.
		IReadOnlyList<string> luxSensors = area.LuxSensors is { Count: > 0 } own ? own
			: area.FollowOutdoorLux && global.OutdoorLuxSensor is { Length: > 0 } outdoor ? [outdoor]
			: [];

		_gateSensor = new IlluminanceGate(
			ha,
			luxSensors,
			area.Settings,
			TimeSpan.FromMinutes(global.LuxSensorStaleAfterMinutes),
			() => _scheduler.Now,
			_logger,
			lastSeen);

		// Through the gate, not a sensor of its own, so "which sensor is this room reading" has one answer.
		_luxBrightness = new LuxBrightnessCurve(area.Settings, _gateSensor.ReadLux);
		_detector = new OverrideDetector(global, scheduler);
	}

	/// <summary>The area's display name.</summary>
	public string Name => _area.Name;

	/// <summary>The current state. For tests and diagnostics only; the engine drives itself.</summary>
	public AreaState State
	{
		get { lock (_gate) return _state; }
	}

	/// <summary>
	///     Subscribes and publishes the opening snapshot. The lights are left as found, but an area found lit
	///     adopts them. See <see cref="AdoptIfLit"/>.
	/// </summary>
	public void Start()
	{
		foreach (string sensor in _area.MotionSensors)
			_subscriptions.Add(_ha.Entity(sensor).WhenTurnsOn(_ => OnMotion(), _logger));

		foreach (string light in _area.Lights)
			_subscriptions.Add(_ha.Entity(light)
				.StateAllChanges()
				.SubscribeSafe(OnLightChanged, _logger));

		_subscriptions.Add(_houseChanged.SubscribeSafe(OnHouseChanged, _logger));

		_subscriptions.Add(_scheduler.SchedulePeriodic(
			TimeSpan.FromSeconds(_global.CircadianTickSeconds),
			OnTick));

		lock (_gate)
		{
			// What cannot be known yet, the last command and last motion, stays null instead of being guessed.
			RefreshDarkness();
			Publish(AdoptIfLit() ? TransitionReason.AdoptedAtStartup : TransitionReason.Startup);
		}
	}

	/// <summary>
	///     Takes charge of lights that are already on when the area starts, without commanding anything.
	/// </summary>
	/// <remarks>
	///     Adoption is not auto-on, so darkness does not gate it: any lit area is adopted, or a lamp burns through
	///     a bright afternoon because <see cref="AreaState.AutoVacant"/> arms no vacancy timer.
	///     It observes and never commands. The target is seeded from the period so the first tick already matches
	///     it and does not retarget a light the engine never chose.
	///     A muzzled or disabled area adopts nothing: arming a timer that ends in a command is a deferred command.
	///     <see cref="OnHouseChanged"/> adopts on kill-switch release too, which is the same hole by the other
	///     door.
	/// </remarks>
	/// <returns><c>true</c> when the area adopted lit lights and is now <see cref="AreaState.AutoActive"/>.</returns>
	private bool AdoptIfLit(TransitionReason reason = TransitionReason.AdoptedAtStartup)
	{
		if (!IsEngineAllowed())
			return false;

		if (!_area.Lights.Any(_ha.IsOn))
			return false;

		_logger.LogInformation(
			"{Area}: found lights already on ({Reason}); adopting them without commanding, and arming the vacancy timeout.",
			Name, reason);

		// Seeded, not commanded: this stops the first tick correcting levels nobody asked it to.
		_lastTarget = ResolveTarget();

		Enter(AreaState.AutoActive, reason);
		RestartVacancyTimer();
		return true;
	}

	private void OnMotion()
	{
		lock (_gate)
		{
			_lastMotionAt = _scheduler.Now;

			switch (_state)
			{
				case AreaState.Disabled or AreaState.Away or AreaState.SceneHold:
					// Occupancy is recorded above; all three command nothing, and all three are reported as
					// refusals, never passed over in silence.
					ReportDeclinedMotion();
					return;

				case AreaState.SuppressedOff:
					// Motion restarts the clock that will lift the suppression and does nothing else. Republished
					// because the reset deadline moved, and a snapshot carrying a deadline must be re-issued when
					// the deadline does.
					RestartSuppressionTimer();
					Publish(TransitionReason.Motion);
					return;

				case AreaState.OverriddenOn:
					// Manual levels stand. Motion only records occupancy, which decides where expiry lands.
					Publish(TransitionReason.Motion);
					return;

				case AreaState.AutoActive:
					RestartVacancyTimer();
					Publish(TransitionReason.Motion);
					return;

				case AreaState.PreOff:
					_preOffTimer.Disposable = Disposable.Empty;
					ForgetDeclinedMotion();
					Enter(AreaState.AutoActive, TransitionReason.Motion);
					RestartVacancyTimer();
					ApplyTarget(TransitionReason.Motion);
					return;

				case AreaState.AutoVacant:
					if (!CanAutoOn(out string? blockedBy))
					{
						_logger.LogDebug("Motion in {Area} but auto-on is blocked: {Reason}.", Name, blockedBy);
						ReportDeclinedMotion();
						return;
					}

					ForgetDeclinedMotion();
					Enter(AreaState.AutoActive, TransitionReason.Motion);
					RestartVacancyTimer();
					ApplyTarget(TransitionReason.Motion);
					return;

				default:
					return;
			}
		}
	}

	private void OnLightChanged(StateChange change)
	{
		lock (_gate)
		{
			// A radio, not a hand. See IsHandAtTheSwitch.
			if (!IsHandAtTheSwitch(change))
				return;

			ChangeOrigin origin = _detector.Classify(change);
			if (!_detector.IsManual(origin))
				return;

			// While disabled, away or holding a scene there is nothing to override.
			if (_state is AreaState.Disabled or AreaState.Away or AreaState.SceneHold)
				return;

			bool turnedOn = change.TurnedOn();

			_logger.LogInformation("{Area}: manual change on {EntityId} attributed to {Origin}; light is now {State}.",
				Name, change.New?.EntityId, origin, turnedOn ? "on" : "off");

			if (turnedOn)
			{
				CancelAutoTimers();
				Enter(AreaState.OverriddenOn, TransitionReason.ManualOn);

				// Restarted on every manual touch: the override outlasts the last thing the human did.
				ArmCountdown(_overrideTimer, TimeSpan.FromMinutes(_area.Settings.OverrideDurationMinutes), OnOverrideExpired);

				Publish(TransitionReason.ManualOn);
				return;
			}

			CancelAutoTimers();
			Enter(AreaState.SuppressedOff, TransitionReason.ManualOff);
			RestartSuppressionTimer();
			Publish(TransitionReason.ManualOff);
		}
	}

	/// <summary>
	///     Whether <paramref name="change"/> could have been a person at a switch at all, before anyone asks who
	///     caused it.
	/// </summary>
	/// <remarks>
	///     A bulb dropping off the radio looks exactly like a human: Home Assistant writes <c>unavailable</c> with
	///     a context carrying neither a user nor a parent, which is <see cref="OverrideDetector"/>'s definition of
	///     <see cref="ChangeOrigin.PhysicalDevice"/>, and <see cref="StateChangeExtensions.TurnedOn"/> reads it as
	///     not-on. Both ends of the change must therefore be a state the engine could have commanded. The cost is
	///     that a real wall switch flipped on an unavailable bulb goes unnoticed and the area keeps automating.
	/// </remarks>
	private static bool IsHandAtTheSwitch(StateChange change) =>
		IsOnOrOff(change.Old) && IsOnOrOff(change.New);

	/// <summary>Whether the state reads on or off, as opposed to unavailable, unknown or absent.</summary>
	private static bool IsOnOrOff(EntityState? state) => state is not null && (state.IsOn() || state.IsOff());

	private void OnHouseChanged(HouseState house)
	{
		lock (_gate)
		{
			HouseState previous = _house;
			_house = house;

			if (!IsEngineAllowed())
			{
				if (_state != AreaState.Disabled)
				{
					CancelAllTimers();
					Enter(AreaState.Disabled, TransitionReason.EnablementChanged);
					Publish(TransitionReason.EnablementChanged);
				}

				return;
			}

			if (_state == AreaState.Disabled)
			{
				// Resume at the resting state, then fall through: re-enabling into an empty house lands in Away.
				Enter(AreaState.AutoVacant, TransitionReason.EnablementChanged);

				// AutoVacant arms no vacancy timeout, so a room left lit under the muzzle would burn.
				AdoptIfLit(TransitionReason.EnablementChanged);

				Publish(TransitionReason.EnablementChanged);
			}

			// Keyed off the composed mode, not raw presence: an away-kind option sweeps a full house too.
			if (house.Mode == HouseMode.Away)
			{
				if (_state != AreaState.Away)
					GoAway();

				return;
			}

			// Before the was-Away recovery: entering a scene mode straight from Away must land in SceneHold, not
			// run the welcome-home ApplyTarget that would clobber the scene.
			if (house.Mode == HouseMode.Guest && house.ActiveScene is { Length: > 0 })
			{
				if (_state != AreaState.SceneHold)
					EnterSceneHold();

				return;
			}

			if (_state == AreaState.Away)
			{
				// Read from what the house was: what it is no longer says Away either way.
				ComeHome(previous.ActiveKind == ModeKind.Away
					? TransitionReason.HouseModeChanged
					: TransitionReason.FirstPersonArrived);

				return;
			}

			if (_state == AreaState.SceneHold)
			{
				// The Guest scene ended. Exit to the resting state and let the normal machinery re-evaluate.
				Enter(AreaState.AutoVacant, TransitionReason.SceneHold);
				Publish(TransitionReason.SceneHold);
				return;
			}

			// A mode switch is a command: retarget an active area when the kind or the mode value moved.
			if (_state == AreaState.AutoActive
				&& (previous.ActiveKind != house.ActiveKind
					|| !string.Equals(previous.ModeValue, house.ModeValue, StringComparison.OrdinalIgnoreCase)))
				ApplyTarget(TransitionReason.HouseModeChanged);
		}
	}

	private void EnterSceneHold()
	{
		CancelAllTimers();
		Enter(AreaState.SceneHold, TransitionReason.SceneHold);
		Publish(TransitionReason.SceneHold);
	}

	/// <summary>The area's re-evaluation of the world, at <c>CircadianTickSeconds</c>.</summary>
	/// <remarks>
	///     Runs whatever the state, not only in <see cref="AreaState.AutoActive"/>: lux crossing the threshold at
	///     dusk is a moment with no transition and no deadline, so nothing else would announce it. The guard in
	///     <see cref="Publish"/> keeps a quiet area quiet.
	/// </remarks>
	private void OnTick()
	{
		lock (_gate)
		{
			// A state read, not a subscription, so the tick is the only thing that can notice dusk in an area
			// that is otherwise idle.
			RefreshDarkness();

			if (_state == AreaState.AutoActive)
			{
				LightTarget? target = ResolveTarget();

				if (target is not null && !TargetsMatch(target, _lastTarget))
				{
					// Publishes on its own, and by then this tick's news is already out.
					ApplyTarget(TransitionReason.CircadianTick);
					return;
				}
			}

			Publish(TransitionReason.CircadianTick);
		}
	}

	private void OnVacancyTimeout()
	{
		lock (_gate)
		{
			if (_state != AreaState.AutoActive)
				return;

			Enter(AreaState.PreOff, TransitionReason.VacancyTimeout);

			// Armed before the dim, so the snapshot announcing PreOff already carries its own deadline.
			ArmCountdown(_preOffTimer, TimeSpan.FromSeconds(_area.Settings.PreOffSeconds), OnPreOffElapsed);

			ApplyTarget(TransitionReason.VacancyTimeout, _area.Settings.PreOffBrightnessFactor);
		}
	}

	private void OnPreOffElapsed()
	{
		lock (_gate)
		{
			if (_state != AreaState.PreOff)
				return;

			_nextChangeAt = null;
			_nextChangeFrom = null;
			Enter(AreaState.AutoVacant, TransitionReason.PreOffElapsed);
			TurnOff(TransitionReason.PreOffElapsed);
		}
	}

	private void OnOverrideExpired()
	{
		lock (_gate)
		{
			if (_state != AreaState.OverriddenOn)
				return;

			_nextChangeAt = null;
			_nextChangeFrom = null;

			if (IsOccupied())
			{
				Enter(AreaState.AutoActive, TransitionReason.OverrideExpired);
				RestartVacancyTimer();
				ApplyTarget(TransitionReason.OverrideExpired);
				return;
			}

			Enter(AreaState.AutoVacant, TransitionReason.OverrideExpired);
			TurnOff(TransitionReason.OverrideExpired);
		}
	}

	// Every motion event restarts this timer, so its firing is itself the proof the area is vacant. An extra
	// occupancy check here would stretch the reset out to the vacancy timeout.
	private void OnSuppressionLifted()
	{
		lock (_gate)
		{
			if (_state != AreaState.SuppressedOff)
				return;

			_nextChangeAt = null;
			_nextChangeFrom = null;
			Enter(AreaState.AutoVacant, TransitionReason.SuppressionLifted);
			Publish(TransitionReason.SuppressionLifted);
		}
	}

	/// <summary>
	///     Why this area is going Away: because presence says the house is empty, or because the house mode says
	///     Away whatever presence reads.
	/// </summary>
	/// <remarks>
	///     <see cref="HouseState.Mode"/> is <see cref="HouseMode.Away"/> when presence is empty or the standing
	///     option is away-kind, so the mode is checked first: it holds the house dark after somebody walks back
	///     in, and that is the state a reader has to be able to see.
	///     <see cref="AdaptiveLighting.Abstractions.AreaSnapshot.Forced"/> then names which route put it there.
	/// </remarks>
	private TransitionReason AwayReason() =>
		_house.ActiveKind == ModeKind.Away ? TransitionReason.HouseModeChanged : TransitionReason.EveryoneLeft;

	private void GoAway()
	{
		TransitionReason reason = AwayReason();

		CancelAllTimers();
		Enter(AreaState.Away, reason);

		// An away scene is the away look, so skip the sweep and let the scene stand.
		if (_house.ActiveScene is { Length: > 0 })
		{
			_logger.LogDebug("{Area}: away scene {Scene} is holding; skipping the leaving sweep.", Name, _house.ActiveScene);
			Publish(reason);
			return;
		}

		// The sweep beats an override: whoever set those levels is not in the house to enjoy them.
		if (_area.Settings.SkipAwaySweep)
		{
			_logger.LogDebug("{Area} opted out of the leaving sweep.", Name);
			Publish(reason);
			return;
		}

		TurnOff(reason);
	}

	/// <summary>
	///     Leaves the Away state. <paramref name="reason"/> mirrors <see cref="AwayReason"/>: an away-kind mode
	///     letting go is a mode change, not somebody walking in the door.
	/// </summary>
	private void ComeHome(TransitionReason reason)
	{
		Enter(AreaState.AutoVacant, reason);

		if (!_area.Settings.WelcomeHome || !CanAutoOn(out _))
		{
			Publish(reason);
			return;
		}

		Enter(AreaState.AutoActive, reason);
		RestartVacancyTimer();
		ApplyTarget(reason);
	}

	/// <summary>Whether the engine may command this area at all, ignoring presence and darkness.</summary>
	private bool IsEngineAllowed() => _area.Settings.Enabled && !_house.KillSwitchActive;

	private bool CanAutoOn(out string blockedBy)
	{
		AutoOnBlock block = AutoOnBlockNow(RefreshDarkness(), out string? blocker);

		blockedBy = block switch
		{
			AutoOnBlock.None => "",
			AutoOnBlock.KillSwitch => "kill switch is active",
			AutoOnBlock.Disabled => "area is disabled",
			// Away has three tellings and only one is a departure. The forced one is checked first: nothing else
			// in the log would ever have named it.
			AutoOnBlock.Away => _house.Forced is { Kind: ModeKind.Away } forced ? forced.Describe()
				: _house.IsAnyoneHome ? "the house is set to away"
				: "nobody is home",
			AutoOnBlock.SceneHold => $"a guest scene ({_house.ActiveScene}) is holding this area",
			AutoOnBlock.Sleep => "sleep mode blocks auto-on for this area",
			AutoOnBlock.EntityOn => $"{blocker} is on",
			_ => $"not dark enough ({_gateSensor.DarknessDetail()})"
		};

		return block == AutoOnBlock.None;
	}

	/// <summary>
	///     Publishes "somebody moved in here and I did not light it, and here is what stopped me", but only when
	///     what stopped it has changed since the last such report.
	/// </summary>
	/// <remarks>
	///     The bound is on the refusing gate, not on the reading behind it: N movements under an unchanged block
	///     produce one report, and a lux value drifting under an unchanged <see cref="AutoOnBlock.NotDark"/>
	///     produces none. The report count is bounded by how often the gate changes, never by footfall.
	///     This bypasses <see cref="Publish"/>'s identical-consecutive guard, which would suppress every one of
	///     these: a declined movement moves only the fields
	///     <see cref="AreaSnapshot.HasSameMeaningAs"/> excludes on purpose.
	/// </remarks>
	private void ReportDeclinedMotion()
	{
		AutoOnBlock block = AutoOnBlockNow(RefreshDarkness(), out string? blocker);

		// Nothing is refusing. Reachable from the states that decline before the gates are consulted at all.
		if (block == AutoOnBlock.None)
		{
			ForgetDeclinedMotion();
			return;
		}

		if (_reportedDecline == block && string.Equals(_reportedDeclineEntity, blocker, StringComparison.Ordinal))
			return;

		_reportedDecline = block;
		_reportedDeclineEntity = blocker;

		AreaSnapshot snapshot = Snapshot(TransitionReason.Motion);
		_lastPublished = snapshot;
		_publisher.Publish(snapshot);
	}

	/// <summary>
	///     Forgets the last declined-motion report, so the next refusal is news again. Called wherever the area
	///     actually lights on movement, or a second spell under the same gate would go unreported.
	/// </summary>
	private void ForgetDeclinedMotion()
	{
		_reportedDecline = null;
		_reportedDeclineEntity = null;
	}

	/// <summary>
	///     Which gate would refuse to light this area for movement right now, judged against <paramref name="dark"/>.
	/// </summary>
	/// <remarks>
	///     The single place the auto-on gates are written. <see cref="CanAutoOn"/> asks it before acting and
	///     <see cref="Snapshot"/> asks it to fill <see cref="AreaSnapshot.AutoOnBlockedBy"/>, so a reader is told
	///     the verdict the engine acted on. A second copy in the publisher or a page would drift.
	///     <paramref name="dark"/> is passed in so the caller decides which reading applies.
	/// </remarks>
	private AutoOnBlock AutoOnBlockNow(bool dark, out string? blocker)
	{
		blocker = null;

		if (!IsEngineAllowed())
			return _house.KillSwitchActive ? AutoOnBlock.KillSwitch : AutoOnBlock.Disabled;

		if (_house.Mode == HouseMode.Away)
			return AutoOnBlock.Away;

		// Named here, not left to fall through to the darkness gate: a scene-held area reporting "not dark
		// enough" would send somebody to the lux sensor over a mode they set themselves.
		if (_house.Mode == HouseMode.Guest && _house.ActiveScene is { Length: > 0 })
			return AutoOnBlock.SceneHold;

		if (_area.Settings.SleepBlocksAutoOn && _house.Mode == HouseMode.Sleep)
			return AutoOnBlock.Sleep;

		if (_area.IgnoreWhenOn.FirstOrDefault(_ha.IsOn) is { } blocking)
		{
			blocker = blocking;
			return AutoOnBlock.EntityOn;
		}

		return dark ? AutoOnBlock.None : AutoOnBlock.NotDark;
	}

	/// <summary>Re-reads the darkness gate, keeping the verdict the snapshot and the fade length both use.</summary>
	private bool RefreshDarkness()
	{
		bool dark = _gateSensor.IsDarkEnough();
		_lastDarkVerdict = dark;
		_lastDarknessDetail = _gateSensor.DarknessDetail();
		return dark;
	}

	/// <summary>
	///     Whether the area counts as occupied, for the two questions the vacancy timer cannot answer: where an
	///     expiring override lands, and whether a suppression may lift.
	/// </summary>
	private bool IsOccupied() =>
		_lastMotionAt is { } lastMotion &&
		_scheduler.Now - lastMotion < TimeSpan.FromSeconds(_area.Settings.VacancyTimeoutSeconds);

	/// <summary>
	///     The area's target now: the shared table, then the daylight adjustment, then, for a sleep-respecting
	///     area while the house is asleep, the sleep clamp.
	/// </summary>
	/// <remarks>
	///     The daylight adjustment lives here and not in <see cref="ApplyTarget"/>, so <see cref="OnTick"/> sees
	///     it; in <c>ApplyTarget</c> alone it would raise the level on the next motion event and never before.
	///     It runs before the sleep clamp: a bright reading during an afternoon nap must not lift the room past
	///     the night rules, so the clamp has the last word.
	/// </remarks>
	private LightTarget? ResolveTarget()
	{
		LightTarget? target = _circadian.GetTarget(_scheduler.Now);
		CacheResolvedPeriod(_scheduler.Now, target);
		if (target is null)
		{
			_logger.LogWarning("{Area}: no circadian period resolves at {Now}; commanding nothing.", Name, _scheduler.Now);
			return null;
		}

		LightTarget adjusted = _luxBrightness.Apply(target);

		if (_house.Mode == HouseMode.Sleep && _area.Settings.RespectSleepMode)
			return ClampToSleepCaps(adjusted);

		return adjusted;
	}

	/// <summary>
	///     Holds <paramref name="target"/> to the sleep period's level. Somebody up at 03:00 is in the same night
	///     as the sleep-clamp period whether or not the clock has rolled over to morning. The clamp period is
	///     resolved from the currently-active sleep option.
	/// </summary>
	private LightTarget ClampToSleepCaps(LightTarget target)
	{
		HouseModeOptionConfig? option = _global.HouseMode?.OptionFor(_house.ModeValue);
		string? sleepPeriodName = option is not null ? HouseModeConfig.SleepClampPeriodFor(option, _periods) : null;
		LightTarget? sleepPeriod = sleepPeriodName is { Length: > 0 } ? _circadian.GetPeriodTarget(sleepPeriodName) : null;
		if (sleepPeriod is null)
		{
			_logger.LogWarning("{Area} respects sleep mode but no clamp period resolves ('{Period}'); leaving the target alone.",
				Name, sleepPeriodName ?? "(none)");
			return target;
		}

		// The clamp period's own brightness is the ceiling; the per-period caps that used to supply it are gone.
		return target with { BrightnessPct = target.Clamp(Math.Min(target.BrightnessPct, sleepPeriod.BrightnessPct)) };
	}

	private void ApplyTarget(TransitionReason reason, double brightnessFactor = 1.0)
	{
		LightTarget? target = ResolveTarget();
		if (target is null)
			return;

		_lastTarget = target;

		// Before every command: it picks the fade length and it is what the snapshot reports.
		RefreshDarkness();

		double brightness = target.Clamp(target.BrightnessPct * brightnessFactor);
		LightCommand command = new(true, brightness, target.ColorTempKelvin, TransitionSeconds());

		Send(command);
		Publish(reason);
	}

	private void TurnOff(TransitionReason reason)
	{
		RefreshDarkness();

		LightCommand command = LightCommand.TurnOff(TransitionSeconds());
		Send(command);
		Publish(reason);
	}

	private void Send(LightCommand command)
	{
		foreach (string light in _area.Lights)
		{
			// Always declared before sending: a command reaching HA before the expectation that explains it
			// defeats the detector's primary heuristic.
			_detector.ExpectCommand(light, command);
			_actuator.Apply(light, command);
		}

		// The standing command, so a republish keeps the levels that are actually holding instead of blanking them.
		_lastCommand = command;
		_lastCommandAt = _scheduler.Now;
	}

	/// <summary>
	///     The fade length. Darkness picks it, not the period name: what matters is whether the eyes receiving
	///     the change are dark-adapted, which is what the gate already measured.
	/// </summary>
	private double TransitionSeconds() =>
		_lastDarkVerdict == true ? _area.Settings.NightTransitionSeconds : _area.Settings.DayTransitionSeconds;

	private void RestartVacancyTimer() =>
		ArmCountdown(_vacancyTimer, TimeSpan.FromSeconds(_area.Settings.VacancyTimeoutSeconds), OnVacancyTimeout);

	private void RestartSuppressionTimer() =>
		ArmCountdown(_suppressionTimer, TimeSpan.FromMinutes(_area.Settings.VacancyResetMinutes), OnSuppressionLifted);

	// The single place all four state timers are armed, so a published deadline is never out of step with the
	// timer that will honour it. Records both ends of the window for the snapshot's elapsed-versus-remaining.
	private void ArmCountdown(SerialDisposable timer, TimeSpan delay, Action onElapsed)
	{
		_nextChangeFrom = _scheduler.Now;
		_nextChangeAt = _scheduler.Now + delay;
		timer.Disposable = _scheduler.Schedule(delay, onElapsed);
	}

	private void CancelAutoTimers()
	{
		_nextChangeAt = null;
		_nextChangeFrom = null;
		_vacancyTimer.Disposable = Disposable.Empty;
		_preOffTimer.Disposable = Disposable.Empty;
	}

	private void CancelAllTimers()
	{
		CancelAutoTimers();
		_overrideTimer.Disposable = Disposable.Empty;
		_suppressionTimer.Disposable = Disposable.Empty;
	}

	private bool TargetsMatch(LightTarget left, LightTarget? right) =>
		right is not null &&
		Math.Abs(left.BrightnessPct - right.BrightnessPct) < GlobalConfig.BrightnessTolerancePct &&
		Math.Abs(left.ColorTempKelvin - right.ColorTempKelvin) < GlobalConfig.ColorTempToleranceKelvin;

	private void Enter(AreaState state, TransitionReason reason)
	{
		if (_state != state)
			_logger.LogInformation("{Area}: {From} -> {To} ({Reason}).", Name, _state, state, reason);

		_state = state;
	}

	/// <summary>Publishes this snapshot, unless it repeats the news the area last published.</summary>
	/// <remarks>
	///     Every publish passes through the one identical-consecutive guard, because two triggers can land on the
	///     same instant and resolve to the identical snapshot: a tick arriving with a motion event, or a
	///     house-state re-emit behind a mode change. A same-state republish whose deadline moved still goes out,
	///     because the deadline is part of the compared meaning.
	/// </remarks>
	private void Publish(TransitionReason reason)
	{
		AreaSnapshot snapshot = Snapshot(reason);

		// _lastPublished is the last snapshot actually sent, so a suppressed publish leaves it untouched and the
		// next genuine change diffs against real news.
		if (snapshot.HasSameMeaningAs(_lastPublished))
			return;

		_lastPublished = snapshot;
		_publisher.Publish(snapshot);
	}

	private AreaSnapshot Snapshot(TransitionReason reason)
	{
		// Null when the last command was "off", and also when there has never been one, which LastCommandAt
		// disambiguates.
		LightCommand? standing = _lastCommand is { On: true } ? _lastCommand : null;

		// Against the verdict already read, not a fresh one, so the gate and the reading beside it are the same
		// moment's answer.
		AutoOnBlock blocked = AutoOnBlockNow(_lastDarkVerdict ?? false, out string? blocker);

		// At the snapshot's own instant, so an idle area still names the period it is sitting in, and the
		// room-levels flag describes that period, not the standing command.
		ResolvePeriodAt(_scheduler.Now);

		return new AreaSnapshot(
			Name,
			_state,
			reason,
			_house.Mode,
			_house.KillSwitchActive,
			_lastDarkVerdict,
			_resolvedPeriodName,
			standing?.BrightnessPct,
			standing?.ColorTempKelvin,
			_scheduler.Now,
			_lastCommandAt,
			_lastMotionAt,
			_nextChangeAt,
			_nextChangeFrom,
			_house.ModeValue,
			_lastDarknessDetail,
			_areaId,
			blocked,
			blocker,
			_resolvedLevelsFromRoom,
			_house.IsAnyoneHome,
			_house.Forced);
	}

	/// <summary>Resolves and caches the period for <paramref name="now"/>, unless that instant is already cached.</summary>
	private void ResolvePeriodAt(DateTimeOffset now)
	{
		if (_resolvedPeriodAt != now)
			CacheResolvedPeriod(now, _circadian.GetTarget(now));
	}

	/// <summary>
	///     Records what <paramref name="target"/> said about the period at <paramref name="now"/>. The name and
	///     the room-levels flag are cached together because they are one answer from one resolution.
	/// </summary>
	private void CacheResolvedPeriod(DateTimeOffset now, LightTarget? target)
	{
		_resolvedPeriodAt = now;
		_resolvedPeriodName = target?.PeriodName;
		_resolvedLevelsFromRoom = target?.FromRoom ?? RoomLevelSource.None;
	}

	/// <summary>Unsubscribes and cancels every timer. The lights are left exactly as they are.</summary>
	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
				return;

			_disposed = true;
		}

		_subscriptions.Dispose();
		_vacancyTimer.Dispose();
		_preOffTimer.Dispose();
		_overrideTimer.Dispose();
		_suppressionTimer.Dispose();
	}
}
