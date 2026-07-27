using System.Reactive.Concurrency;
using System.Reactive.Disposables;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>
///     One area's state machine. See 02-architecture.md §5 for the diagram this implements.
/// </summary>
/// <remarks>
///     <para>
///         Every timer runs on the injected <see cref="IScheduler"/> and every "now" is <see cref="IScheduler.Now"/>.
///         The scheduler is the controller's only clock, which is what lets a test advance an hour instantly and
///         read a deterministic answer.
///     </para>
///     <para>
///         Rx callbacks and scheduler callbacks interleave on whatever thread each arrives on, so every read and
///         write of the machine's state happens under <see cref="_gate"/>. Commands are issued from inside the
///         lock; that is safe because the actuator is fire-and-forget, and it is necessary because a command must
///         not be decided from one state and sent after another has replaced it.
///     </para>
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

	// The period name last resolved, and the instant it was resolved for. Snapshot reads this instead of calling
	// GetTarget again: OnTick/ApplyTarget resolve the target for _scheduler.Now, then publish at the same instant
	// under the same lock, so the second resolution was pure re-work. Both fields are only touched under _gate.
	private DateTimeOffset _resolvedPeriodAt;
	private string? _resolvedPeriodName;
	private LightCommand? _lastCommand;
	private DateTimeOffset? _lastCommandAt;
	private DateTimeOffset? _lastMotionAt;
	private DateTimeOffset? _nextChangeAt;
	private DateTimeOffset? _nextChangeFrom;
	private bool? _lastDarkVerdict;
	private string? _lastDarknessDetail;
	private AreaSnapshot? _lastPublished;
	private bool _disposed;

	/// <summary>Creates a controller for one resolved area.</summary>
	/// <param name="ha">Source of the area's state streams.</param>
	/// <param name="scheduler">The area's only clock.</param>
	/// <param name="area">The area, already resolved to concrete entity ids.</param>
	/// <param name="global">House-wide knobs: tick rate, echo window, override policy.</param>
	/// <param name="periods">The house-wide circadian table, for resolving the sleep-clamp period (09 §4.1).</param>
	/// <param name="circadian">Supplies the target for an instant.</param>
	/// <param name="actuator">Where commands go.</param>
	/// <param name="publisher">Where snapshots go.</param>
	/// <param name="houseChanged">The house-wide state stream, owned by the orchestrator.</param>
	/// <param name="loggerFactory">Builds the area's logger.</param>
	/// <param name="areaId">
	///     The registry area id the configuration named, or <c>null</c> when it named none. Published on every
	///     snapshot so a reader can join live state to the document by identity rather than by display name.
	///     Passed alongside <paramref name="area"/> rather than carried on it: a resolved area is what the engine
	///     runs on, and it has no use for the id — only the observability seam does.
	/// </param>
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
		string? areaId = null)
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

		// An area with its own lux sensor uses it; otherwise it falls back to the house-wide outdoor lux sensor,
		// and only when neither resolves does the gate fall back to sun elevation. Empty strings normalise to null
		// so the gate reads them as "no sensor" rather than trying to read the state of an empty id.
		string? luxSensor = area.LuxSensor is { Length: > 0 } own ? own
			: global.OutdoorLuxSensor is { Length: > 0 } outdoor ? outdoor
			: null;
		_gateSensor = new IlluminanceGate(ha, luxSensor, area.Settings, _logger);

		// Reads through the gate rather than resolving a sensor of its own, so "which sensor is this room looking
		// at" has exactly one answer — including the fall-back to the house-wide outdoor sensor above, which is the
		// case the feature was asked for (one outdoor reading brightening a hallway that has no sensor at all).
		_luxBrightness = new LuxBrightnessCurve(area.Settings, _gateSensor.ReadLux);
		_detector = new OverrideDetector(global, scheduler);
	}

	/// <summary>The area's display name.</summary>
	public string Name => _area.Name;

	/// <summary>The current state. For tests and diagnostics; the engine drives itself.</summary>
	public AreaState State
	{
		get { lock (_gate) return _state; }
	}

	/// <summary>
	///     Subscribes and publishes the opening snapshot. The lights are left exactly as found — turning the
	///     house off at startup to reach a known state would be its own kind of rude — but an area found lit
	///     adopts them rather than ignoring them. See <see cref="AdoptIfLit"/>.
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
			// The opening snapshot is read by people, so it must not carry guesses. Darkness is cheap to
			// evaluate here; what cannot be known yet (last command, last motion) stays null and says so.
			RefreshDarkness();
			Publish(AdoptIfLit() ? TransitionReason.AdoptedAtStartup : TransitionReason.Startup);
		}
	}

	/// <summary>
	///     Takes charge of lights that are already on when the area starts, without commanding anything.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>The bug this fixes.</b> Every area used to start <see cref="AreaState.AutoVacant"/>, which arms
	///         no vacancy timer. An area the engine itself had lit and then forgot across a restart would burn
	///         until somebody happened to walk back into the room — indefinitely, in a room nobody enters.
	///         Restarts are frequent; that is not an edge case, it is the common one.
	///     </para>
	///     <para>
	///         <b>Adoption is not auto-on, so darkness does not gate it.</b> The darkness gate answers "should
	///         the engine turn these lights on?", and the answer in daylight is rightly no. That is a different
	///         question from "these lights are on — whose problem are they?", and answering the second with the
	///         first is what would leave a lamp burning through a bright afternoon: the exact bug, merely
	///         daylit. Any lit area is adopted. No light is left burning because the engine forgot it.
	///     </para>
	///     <para>
	///         <b>It observes; it never commands.</b> No service call is sent — not to correct brightness to the
	///         period's target, not to sweep anything off. Somebody walking past a restart notices nothing. The
	///         area's target is seeded from the period so the first tick sees a target it already matches and
	///         does not retarget a light the engine never chose; the levels a human (or a previous incarnation of
	///         the engine) set stand until the day genuinely drifts, which is what would have happened anyway.
	///     </para>
	///     <para>
	///         A muzzled or disabled area adopts nothing: arming a timer that ends in a command is a command
	///         deferred, and a disabled engine has no business making one.
	///     </para>
	/// </remarks>
	/// <returns><c>true</c> when the area adopted lit lights and is now <see cref="AreaState.AutoActive"/>.</returns>
	private bool AdoptIfLit()
	{
		if (!IsEngineAllowed())
			return false;

		if (!_area.Lights.Any(_ha.IsOn))
			return false;

		_logger.LogInformation(
			"{Area}: found lights already on at start-up; adopting them without commanding, and arming the vacancy timeout.",
			Name);

		// Seeded, not commanded: this is what stops the first tick from "correcting" levels nobody asked it to.
		_lastTarget = ResolveTarget();

		Enter(AreaState.AutoActive, TransitionReason.AdoptedAtStartup);
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
					// SceneHold records occupancy (above) but commands nothing: the scene is the look.
					return;

				case AreaState.SuppressedOff:
					// The human turned these lights off. Motion restarts the vacancy clock that will eventually
					// lift the suppression, and does nothing else: overriding them now is exactly the behaviour
					// that makes people rip an automation out. Republished because the reset deadline moved —
					// a snapshot that carries a deadline must be re-issued when the deadline does.
					RestartSuppressionTimer();
					Publish(TransitionReason.Motion);
					return;

				case AreaState.OverriddenOn:
					// Manual levels stand until the override expires. Motion only records occupancy, which
					// decides where the area lands at expiry — republished so that record is visible.
					Publish(TransitionReason.Motion);
					return;

				case AreaState.AutoActive:
					RestartVacancyTimer();
					Publish(TransitionReason.Motion);
					return;

				case AreaState.PreOff:
					_preOffTimer.Disposable = Disposable.Empty;
					Enter(AreaState.AutoActive, TransitionReason.Motion);
					RestartVacancyTimer();
					ApplyTarget(TransitionReason.Motion);
					return;

				case AreaState.AutoVacant:
					if (!CanAutoOn(out string? blockedBy))
					{
						_logger.LogDebug("Motion in {Area} but auto-on is blocked: {Reason}.", Name, blockedBy);
						return;
					}

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
			ChangeOrigin origin = _detector.Classify(change);
			if (!_detector.IsManual(origin))
				return;

			// While disabled, away or holding a scene the engine observes but does not react: there is nothing to override.
			if (_state is AreaState.Disabled or AreaState.Away or AreaState.SceneHold)
				return;

			bool turnedOn = change.TurnedOn();

			_logger.LogInformation("{Area}: manual change on {EntityId} attributed to {Origin}; light is now {State}.",
				Name, change.New?.EntityId, origin, turnedOn ? "on" : "off");

			if (turnedOn)
			{
				CancelAutoTimers();
				Enter(AreaState.OverriddenOn, TransitionReason.ManualOn);

				// Restarted on every manual touch: the override should outlast the last thing the human did,
				// not the first.
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
				// Resume from the resting state rather than trying to reconstruct what was lost while muzzled,
				// then fall through: re-enabling into an empty house should land in Away, not AutoVacant.
				Enter(AreaState.AutoVacant, TransitionReason.EnablementChanged);
				Publish(TransitionReason.EnablementChanged);
			}

			// Keyed off the composed mode, not raw presence: an away-kind option sweeps a full house too. GoAway
			// itself holds an away scene rather than sweeping, so an Away-with-scene mode is handled here.
			if (house.Mode == HouseMode.Away)
			{
				if (_state != AreaState.Away)
					GoAway();

				return;
			}

			// A scene-hold mode (Guest carrying a scene) holds the area indefinitely: the scene is the look, so
			// command nothing. This is checked BEFORE the was-Away recovery: entering a scene mode straight from
			// Away must land in SceneHold, not run the welcome-home ApplyTarget that would clobber the scene.
			if (house.Mode == HouseMode.Guest && house.ActiveScene is { Length: > 0 })
			{
				if (_state != AreaState.SceneHold)
					EnterSceneHold();

				return;
			}

			if (_state == AreaState.Away)
			{
				ComeHome();
				return;
			}

			if (_state == AreaState.SceneHold)
			{
				// The Guest scene ended (reset to Normal, or the scene was cleared). Exit to the resting state and
				// let the normal machinery re-evaluate — no welcome-home flourish.
				Enter(AreaState.AutoVacant, TransitionReason.SceneHold);
				Publish(TransitionReason.SceneHold);
				return;
			}

			// A mode switch is a command: retarget an active area whenever the kind or the mode value moved.
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

	/// <summary>
	///     The area's re-evaluation of the world, at <c>CircadianTickSeconds</c>.
	/// </summary>
	/// <remarks>
	///     <para>
	///         This used to return immediately unless the area was <see cref="AreaState.AutoActive"/>, which
	///         meant a resting area published nothing between transitions — and an area can rest all day. Its
	///         card would then show the darkness verdict, period and house mode from whenever it last moved,
	///         hours stale. Dusk was the worst of it: lux crossing the threshold is the moment a vacant area
	///         becomes eligible to light, and it is precisely a moment with no transition and no deadline, so
	///         nothing announced it.
	///     </para>
	///     <para>
	///         So every area now re-reads darkness and the period on every tick, whatever its state, and
	///         publishes — but only if the result actually differs from what it last published. A quiet area
	///         still costs nothing; an area whose world moved says so. That keeps the property that made
	///         event-on-transition attractive without the lie that came with it.
	///     </para>
	/// </remarks>
	private void OnTick()
	{
		lock (_gate)
		{
			// Reading darkness is a state read, not a subscription, so the tick is the only thing that can
			// notice dusk in an area that is not otherwise doing anything.
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

			// The guard inside Publish suppresses a tick that says nothing new, so a quiet area stays quiet.
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

			// Armed before the dim is applied so the snapshot that announces PreOff already carries its own
			// deadline — a warning without a "when" is just a dimmer room.
			ArmCountdown(_preOffTimer, TimeSpan.FromSeconds(_area.Settings.PreOffSeconds), OnPreOffElapsed);

			// The pre-off dim is the area's way of saying "speak now": a wave of the hand in the grace window
			// costs nothing, whereas being dropped into darkness mid-thought costs trust. The dim keeps the
			// period's floor, so an area whose night floor is its target simply gets no visible warning rather
			// than an illegal level.
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

	// Reaching here means VacancyResetMinutes passed without motion: the timer is restarted by every motion
	// event, so its firing is itself the proof that the area is vacant. An extra occupancy check on top would
	// silently stretch the reset out to the vacancy timeout instead.
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

	private void GoAway()
	{
		CancelAllTimers();
		Enter(AreaState.Away, TransitionReason.EveryoneLeft);

		// An away scene IS the away look, so skip the sweep and let the scene stand — same stand-down as SkipAwaySweep.
		if (_house.ActiveScene is { Length: > 0 })
		{
			_logger.LogDebug("{Area}: away scene {Scene} is holding; skipping the leaving sweep.", Name, _house.ActiveScene);
			Publish(TransitionReason.EveryoneLeft);
			return;
		}

		// The sweep beats an override: whoever set those levels is not in the house to enjoy them.
		if (_area.Settings.SkipAwaySweep)
		{
			_logger.LogDebug("{Area} opted out of the leaving sweep.", Name);
			Publish(TransitionReason.EveryoneLeft);
			return;
		}

		TurnOff(TransitionReason.EveryoneLeft);
	}

	private void ComeHome()
	{
		Enter(AreaState.AutoVacant, TransitionReason.FirstPersonArrived);

		if (!_area.Settings.WelcomeHome || !CanAutoOn(out _))
		{
			Publish(TransitionReason.FirstPersonArrived);
			return;
		}

		Enter(AreaState.AutoActive, TransitionReason.FirstPersonArrived);
		RestartVacancyTimer();
		ApplyTarget(TransitionReason.FirstPersonArrived);
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
			AutoOnBlock.Away => _house.IsAnyoneHome ? "the house is set to away" : "nobody is home",
			AutoOnBlock.Sleep => "sleep mode blocks auto-on for this area",
			AutoOnBlock.EntityOn => $"{blocker} is on",
			_ => $"not dark enough ({_gateSensor.DarknessDetail()})"
		};

		return block == AutoOnBlock.None;
	}

	/// <summary>
	///     Which gate would refuse to light this area for movement right now, judged against <paramref name="dark"/>.
	/// </summary>
	/// <remarks>
	///     The one place the auto-on gates are written down. <see cref="CanAutoOn"/> asks it before acting and
	///     <see cref="Snapshot"/> asks it to fill <see cref="AreaSnapshot.AutoOnBlockedBy"/>, so what a reader is
	///     told is the verdict the engine acted on. A second copy of these rules kept in the publisher or the page
	///     would drift from this one, and that drift is exactly how the activity page came to promise lights that
	///     were never going to come on.
	/// </remarks>
	/// <param name="dark">The darkness verdict to judge against, passed in so the caller decides which reading applies.</param>
	/// <param name="blocker">The entity holding auto-on off, when <see cref="AutoOnBlock.EntityOn"/> is the answer.</param>
	private AutoOnBlock AutoOnBlockNow(bool dark, out string? blocker)
	{
		blocker = null;

		if (!IsEngineAllowed())
			return _house.KillSwitchActive ? AutoOnBlock.KillSwitch : AutoOnBlock.Disabled;

		if (_house.Mode == HouseMode.Away)
			return AutoOnBlock.Away;

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
	///     Whether the area counts as occupied, for the questions the vacancy timer cannot answer — where an
	///     expiring override should land, and whether a suppression may lift.
	/// </summary>
	private bool IsOccupied() =>
		_lastMotionAt is { } lastMotion &&
		_scheduler.Now - lastMotion < TimeSpan.FromSeconds(_area.Settings.VacancyTimeoutSeconds);

	/// <summary>
	///     The area's target now (09 §3.4): the one shared table, then the daylight adjustment, then — only for a
	///     sleep-respecting area while the house is asleep — clamped to the sleep-clamp period's caps.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The daylight adjustment lives here rather than in <see cref="ApplyTarget"/> so that the periodic
	///         tick sees it: <see cref="OnTick"/> compares this against the standing target and retargets when they
	///         differ, which is the only thing that would ever notice the sun coming out. Applied in
	///         <c>ApplyTarget</c> alone it would raise the level on the next motion event and never before, which
	///         in a hallway is exactly never.
	///     </para>
	///     <para>
	///         And it runs <i>before</i> the sleep clamp, not after. Sleep is the stronger statement of the two: a
	///         bright reading during an afternoon nap must not lift the room past the night rules, and the clamp
	///         can only be sure of that if it has the last word.
	///     </para>
	/// </remarks>
	private LightTarget? ResolveTarget()
	{
		LightTarget? target = _circadian.GetTarget(_scheduler.Now);
		CachePeriodName(_scheduler.Now, target?.PeriodName);
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
	///     Holds <paramref name="target"/> to the sleep period's rules. Somebody up at 03:00 for a glass of water
	///     is in the same night as the sleep-clamp period, whether or not the clock has rolled over to morning —
	///     so that period's caps replace the active period's, and its level becomes the ceiling. The clamp period
	///     is resolved by the shared §4.1 chain from the currently-active sleep option.
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

		double ceiling = sleepPeriod.MaxBrightnessPct ?? sleepPeriod.BrightnessPct;
		LightTarget capped = target with
		{
			MinBrightnessPct = sleepPeriod.MinBrightnessPct,
			MaxBrightnessPct = sleepPeriod.MaxBrightnessPct
		};

		return capped with { BrightnessPct = capped.Clamp(Math.Min(target.BrightnessPct, ceiling)) };
	}

	private void ApplyTarget(TransitionReason reason, double brightnessFactor = 1.0)
	{
		LightTarget? target = ResolveTarget();
		if (target is null)
			return;

		_lastTarget = target;

		// Refreshed before every command: it picks the fade length and it is what the snapshot reports.
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
			// Declared before sending, always: the detector's primary heuristic is worthless if a command can
			// reach HA before the expectation that explains it.
			_detector.ExpectCommand(light, command);
			_actuator.Apply(light, command);
		}

		// The standing command: what the engine last told these lights, and when. The snapshot reports this
		// rather than a per-publish argument so a republish (motion moving a deadline) keeps the levels that
		// are actually holding instead of blanking them.
		_lastCommand = command;
		_lastCommandAt = _scheduler.Now;
	}

	/// <summary>
	///     The fade length. Darkness picks it rather than the period name: what matters is whether the eyes
	///     receiving the change are dark-adapted, and that is exactly what the gate already measured.
	/// </summary>
	private double TransitionSeconds() =>
		_lastDarkVerdict == true ? _area.Settings.NightTransitionSeconds : _area.Settings.DayTransitionSeconds;

	private void RestartVacancyTimer() =>
		ArmCountdown(_vacancyTimer, TimeSpan.FromSeconds(_area.Settings.VacancyTimeoutSeconds), OnVacancyTimeout);

	private void RestartSuppressionTimer() =>
		ArmCountdown(_suppressionTimer, TimeSpan.FromMinutes(_area.Settings.VacancyResetMinutes), OnSuppressionLifted);

	// Arms one countdown: records both ends of its window (_nextChangeFrom/_nextChangeAt) so the snapshot can
	// render elapsed-versus-remaining, then schedules its expiry on the given timer. The single place the four
	// state timers (vacancy, suppression, override, pre-off) share their arming, so a published deadline is never
	// out of step with the timer that will honour it.
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
		Math.Abs(left.BrightnessPct - right.BrightnessPct) < _global.BrightnessTolerancePct &&
		Math.Abs(left.ColorTempKelvin - right.ColorTempKelvin) < _global.ColorTempToleranceKelvin;

	private void Enter(AreaState state, TransitionReason reason)
	{
		if (_state != state)
			_logger.LogInformation("{Area}: {From} -> {To} ({Reason}).", Name, _state, state, reason);

		_state = state;
	}

	/// <summary>
	///     Publishes this snapshot, unless it would repeat — verbatim — the news the area last published.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Transitions and deadline changes are news by construction, so this once published unconditionally
	///         and the periodic tick had a separate diffing path. But "known-new by construction" was not quite
	///         true: two triggers can land on the same instant and resolve to the identical snapshot — a periodic
	///         tick arriving with a motion event, or a house-state re-emit behind a mode change — and each would
	///         then log and fire once, announcing one real change twice. The owner saw exactly this: a single
	///         transition logging its "Area X is AutoActive …" line twice.
	///     </para>
	///     <para>
	///         So every publish now passes through the one identical-consecutive guard. It suppresses only a
	///         snapshot that says the very same thing as the last one sent; every genuinely-distinct publish still
	///         goes out, including a same-state republish whose deadline moved (motion re-arming the vacancy
	///         timer), because the deadline is part of the compared meaning. The "as of" fields
	///         <see cref="AreaSnapshot.HasSameMeaningAs"/> excludes — the timestamp, the last-motion instant — are
	///         exactly the ones that must not, on their own, force a duplicate.
	///     </para>
	/// </remarks>
	private void Publish(TransitionReason reason)
	{
		AreaSnapshot snapshot = Snapshot(reason);

		// _lastPublished is the last snapshot actually sent, so a suppressed publish leaves it untouched and the
		// next genuine change still diffs against real news rather than against something nobody heard.
		if (snapshot.HasSameMeaningAs(_lastPublished))
			return;

		_lastPublished = snapshot;
		_publisher.Publish(snapshot);
	}

	private AreaSnapshot Snapshot(TransitionReason reason)
	{
		// The standing command's levels, or null when the last command was "off" — or when there has never
		// been one, which LastCommandAt disambiguates. The period is resolved at the snapshot's own instant
		// rather than copied from the last command, so an idle area still names the period it is sitting in.
		LightCommand? standing = _lastCommand is { On: true } ? _lastCommand : null;

		// Asked at the snapshot's own instant, against the verdict already read rather than a fresh one, so the
		// gate a report carries and the reading beside it are the same moment's answer.
		AutoOnBlock blocked = AutoOnBlockNow(_lastDarkVerdict ?? false, out string? blocker);

		return new AreaSnapshot(
			Name,
			_state,
			reason,
			_house.Mode,
			_house.KillSwitchActive,
			_lastDarkVerdict,
			PeriodNameAt(_scheduler.Now),
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
			blocker);
	}

	/// <summary>The period name for <paramref name="now"/>, resolving (and caching) it only if this instant is not the one already resolved.</summary>
	private string? PeriodNameAt(DateTimeOffset now)
	{
		if (_resolvedPeriodAt != now)
			CachePeriodName(now, _circadian.GetTarget(now)?.PeriodName);

		return _resolvedPeriodName;
	}

	private void CachePeriodName(DateTimeOffset now, string? periodName)
	{
		_resolvedPeriodAt = now;
		_resolvedPeriodName = periodName;
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
