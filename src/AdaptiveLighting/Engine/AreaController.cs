using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.LastSeen;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>One area's state machine.</summary>
// Every timer runs on the injected IScheduler and every "now" is IScheduler.Now. Rx and scheduler callbacks
// interleave on whatever thread each arrives on, so every read and write of the machine's state happens under
// _gate. Commands go out from inside the lock: safe because the actuator is fire-and-forget, and necessary
// because a command must not be decided from one state and sent after another has replaced it.
public sealed class AreaController : IDisposable
{
	private readonly IHaContext _ha;
	private readonly IScheduler _scheduler;
	private readonly ResolvedArea _area;
	private readonly string? _areaId;
	private readonly GlobalConfig _global;

	private readonly IlluminanceGate _gateSensor;
	private readonly OverrideDetector _detector;
	private readonly IStatePublisher _publisher;
	private readonly IObservable<HouseState> _houseChanged;
	private readonly ILogger _logger;

	// Where a level becomes a command, and where a command reaches the fixtures. Both are asked from inside
	// _gate and keep no state of their own.
	private readonly TargetResolver _targets;
	private readonly CommandFanOut _fanOut;

	// The lights not answering. Kept from the subscriptions, so a snapshot never reads state to count them.
	private readonly HashSet<string> _notResponding = new(StringComparer.OrdinalIgnoreCase);

	// The motion sensors whose battery is low, kept from the battery subscriptions the same way.
	private IReadOnlyList<SensorBattery> _lowBatteries = [];

	// The battery entities watched, and the lookup that finds one Home Assistant adds later. Null looks up nothing.
	private IReadOnlyList<MotionBattery> _batteries;
	private readonly Func<IReadOnlyList<MotionBattery>>? _findBatteries;

	private readonly object _gate = new();
	private readonly CompositeDisposable _subscriptions = [];
	private readonly SerialDisposable _vacancyTimer = new();
	private readonly SerialDisposable _preOffTimer = new();
	private readonly SerialDisposable _overrideTimer = new();
	private readonly SerialDisposable _suppressionTimer = new();

	// The one level test this room may be running, and the return it owes. Never a state of the machine: the
	// test changes nothing the area decides on, so every timer, hold and gate carries on around it.
	private readonly LevelTest _levelTest;

	// The entity gate, read only where it is asked. Kept as one function so a snapshot does not allocate one.
	private readonly Func<string?> _blockingEntity;

	// Wakes the area at the boundary itself, so a lit room reaches the new period's levels then and not up to a
	// whole CircadianTickSeconds later. The periodic tick below is the safety net and still runs.
	private readonly BoundaryTimer _boundary;

	// The room's sun entity announcing that it has moved. Null for a room whose boundaries the tick alone re-arms.
	private readonly IObservable<Unit>? _sunMoved;

	private AreaState _state = AreaState.AutoVacant;
	private HouseState _house = HouseState.Initial;
	private AreaTargets? _lastTargets;

	// The period last resolved and the instant it was resolved for. Snapshot reads these instead of calling
	// GetTarget again, because OnTick and ApplyTarget already resolved for the same instant under the same lock.
	private DateTimeOffset _resolvedPeriodAt;
	private string? _resolvedPeriodName;
	private RoomLevelSource _resolvedLevelsFromRoom;
	private LightCommand? _lastCommand;

	// Beside _lastCommand: what each light on levels of its own was last told. Null for a room with no such light.
	private IReadOnlyDictionary<string, LightCommand>? _lastLightCommands;

	// Set for the one publish where a light moved and the room's own target did not.
	private IReadOnlyList<string>? _lightsMoved;

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

	// A hold-lit entity refused the engine's own off after the countdown that would have sent it had already fired.
	// Nothing else would ever run it again, so OnTick settles it once the hold releases.
	private bool _offHeldBack;

	// PreOff was entered from a lead-in. Cleared by Enter on leaving PreOff.
	private bool _leadIn;

	// Names who caused a change for the log. Null names nobody.
	private readonly ChangeOriginNames? _originNames;

	// The newest change somebody else made to these lights, manual or left alone, and when it was seen.
	private string? _changedBy;
	private DateTimeOffset? _changedAt;

	// When the running hold's countdown last started. Only meaningful in OverriddenOn or SuppressedOff.
	private DateTimeOffset? _holdStartedAt;

	// A hold handed over by the room this one replaced, taken up or dropped in Start.
	private AreaHold? _carriedHold;

	// A level test handed over the same way. Until Start settles it, the room's lit state is read from here.
	private CarriedLevelTest? _carriedTest;

	// The automation run whose change was last reported as left alone, so one run's burst of updates is one row.
	private string? _ignoredContextId;

	// The scene the area is sitting on, or null. Set by ApplyScene and cleared by Send: any light command is the
	// engine aiming the room itself, which the scene no longer describes.
	private string? _standingScene;

	// The orchestrator composes and publishes the opening house state before any room starts, so the first thing
	// this controller reads off that stream is it. The mode it carries was read, not changed, and every rebuild
	// would otherwise report a mode change nobody made.
	private bool _openingHouseState = true;

	private bool _disposed;

	// areaId goes on every snapshot so readers join live state to the document by id, never by name. Without
	// sunMoved only the periodic tick re-arms the boundaries.
	public AreaController(
		HouseWiring house,
		ResolvedArea area,
		CircadianCalculator circadian,
		string? areaId = null,
		IObservable<Unit>? sunMoved = null,
		IReadOnlyDictionary<string, CircadianCalculator>? lightCalculators = null,
		Func<IReadOnlyList<MotionBattery>>? findBatteries = null)
	{
		ArgumentNullException.ThrowIfNull(house);
		ArgumentNullException.ThrowIfNull(house.LoggerFactory);

		_sunMoved = sunMoved;
		_findBatteries = findBatteries;
		_originNames = house.OriginNames;

		IReadOnlyDictionary<string, CircadianCalculator> calculators = lightCalculators is { Count: > 0 } stated
			? stated
			: new Dictionary<string, CircadianCalculator>(StringComparer.OrdinalIgnoreCase);

		// The record's members are checked at its own construction site, not here.
		_ha = house.Ha;
		_scheduler = house.Scheduler;
		_area = area ?? throw new ArgumentNullException(nameof(area));
		_batteries = area.MotionBatteries;
		_global = house.Global;
		IReadOnlyList<TimePeriodConfig> schedule = house.Periods;
		CircadianCalculator calculator = circadian ?? throw new ArgumentNullException(nameof(circadian));
		ILightActuator lights = house.Actuator;
		_publisher = house.Publisher;
		_houseChanged = house.HouseChanged;
		_areaId = areaId is { Length: > 0 } ? areaId : null;
		IEntityLastSeen? lastSeen = house.LastSeen;

		_logger = house.LoggerFactory.CreateLogger($"{typeof(AreaController).FullName}.{area.Name}");

		// Own sensors, averaged; otherwise the house's outdoor one only if the room asked. A room with neither has
		// no reading at all, and IlluminanceGate treats that as dark.
		IReadOnlyList<string> luxSensors = area.LuxSensors is { Count: > 0 } own ? own
			: area.FollowOutdoorLux && _global.OutdoorLuxSensor is { Length: > 0 } outdoor ? [outdoor]
			: [];

		TimeSpan staleAfter = TimeSpan.FromMinutes(_global.LuxSensorStaleAfterMinutes);

		_gateSensor = new IlluminanceGate(
			_ha,
			luxSensors,
			area.Settings,
			staleAfter,
			() => _scheduler.Now,
			_logger,
			lastSeen);

		// The house's outdoor reading unless the room named its own. Never the darkness sensor: an indoor one
		// measures the lamps the curve is setting, so the curve would chase itself.
		IReadOnlyList<string> daylightSensors = area.DaylightSensor is { Length: > 0 } chosen ? [chosen]
			: _global.OutdoorLuxSensor is { Length: > 0 } shared ? [shared]
			: [];

		LuxBrightnessCurve luxBrightness = new(
			area.Settings,
			new LuxReader(_ha, daylightSensors, staleAfter, () => _scheduler.Now, lastSeen).Read);

		_detector = new OverrideDetector(_global, _scheduler, area.TreatAutomationsAsManual, house.OwnUserId);
		_fanOut = new CommandFanOut(_area, _ha, _detector, lights);

		_targets = new TargetResolver(
			_area,
			_global,
			schedule,
			calculator,
			calculators,
			luxBrightness,
			TransitionSeconds,
			_logger);

		_boundary = new BoundaryTimer(_scheduler, () => _targets.NextBoundary(_scheduler.Now), OnTick, _logger);
		_levelTest = new LevelTest(_scheduler, TimeSpan.FromSeconds(LevelTestSeconds), OnLevelTestElapsed);
		_blockingEntity = () => FirstThatApplies(_area.IgnoreWhenOn, _area.IgnoreWhenOnInverted);
	}

	/// <summary>How long a level test holds the room before the engine takes it back.</summary>
	public const double LevelTestSeconds = 5;

	public string Name => _area.Name;

	/// <summary>The Home Assistant area this controller was built for, or <c>null</c> for one configured without.</summary>
	public string? AreaId => _areaId;

	/// <summary>The current state, for tests and diagnostics only; the engine drives itself.</summary>
	public AreaState State
	{
		get { lock (_gate) return _state; }
	}

	/// <summary>Whether a level test is holding this room's fixtures right now.</summary>
	public bool IsTestingLevels
	{
		get { lock (_gate) return _levelTest.IsRunning; }
	}

	/// <summary>The level test running here, or <c>null</c> when none is.</summary>
	public LevelTestNow? CurrentLevelTest
	{
		get
		{
			lock (_gate)
				return _levelTest.Peek() is { } running ? new LevelTestNow(running.PeriodId, running.LightId, running.EndsAt) : null;
		}
	}

	/// <summary>Why a level test cannot run here, or <c>null</c> when one can.</summary>
	public string? LevelTestRefusal()
	{
		lock (_gate)
			return RefuseLevelTest();
	}

	/// <summary>Ends a running level test now, putting back what the lights showed before it.</summary>
	/// <remarks>Nothing happens when no test is running.</remarks>
	public void EndTest()
	{
		lock (_gate)
		{
			if (_disposed || !_levelTest.IsRunning)
				return;

			EndLevelTest();
			Publish(TransitionReason.LevelTestEnded);
		}
	}

	/// <summary>
	///     Puts the period <paramref name="periodKey"/> names on this room's real lights for
	///     <see cref="LevelTestSeconds"/> seconds, then hands the room back to the engine.
	/// </summary>
	/// <returns><c>null</c> once the test is running, or the sentence saying why it is not.</returns>
	/// <remarks>
	///     The room's own state machine is untouched: no hold is started or cleared, and no timer is armed,
	///     cancelled or restarted. The return is scheduled on the engine's own scheduler, so it happens whether or
	///     not whoever pressed is still watching. A snapshot is published regardless, carrying <paramref
	///     name="periodKey"/> and the deadline: the only way a page that reloads or navigates back mid-test can
	///     redraw the countdown instead of a plain Test button.
	/// </remarks>
	public string? TestPeriod(string periodKey)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);

		lock (_gate)
		{
			if (RefuseLevelTest() is { } refusal)
				return refusal;

			if (_targets.PeriodTarget(periodKey) is not { } target)
				return "That period is no longer in the schedule.";

			// A lamp tested alone is owed a narrower return than a room test gives, so it is settled first and this
			// test starts from the room as it stood.
			if (_levelTest.IsLightTest)
				EndLevelTest();

			RefreshDarkness();

			// Give the levels back to whoever owns them: the engine is asked for its own when the test ends, and a
			// person's cannot be derived, so they are read now. Not on a second press, which would read the first
			// test's levels as if they were somebody's.
			if (_levelTest.Start(periodKey, lightId: null, _area.Lights.Any(_ha.IsOn)))
				_levelTest.Capture(LevelsAreSomebodyElses() ? CaptureLights(_area.Lights) : null);

			// The engine's own answer for that period, curve included, so the room shows what it would really do
			// and no second reading of the settings can drift from this one. Resolved per light too, or a test
			// would show every lamp at the room's level and the room would look wrong when the engine took over.
			_fanOut.Send(_targets.PeriodCommand(target), _targets.LightCommandsFor(periodKey));

			_logger.LogInformation("{Area}: testing period '{Period}' for {Seconds}s.", Name, target.PeriodName, LevelTestSeconds);

			Publish(TransitionReason.LevelTestStarted);
			return null;
		}
	}

	/// <summary>
	///     Puts the period <paramref name="periodKey"/> names on one light alone for <see cref="LevelTestSeconds"/>
	///     seconds, then gives that light back.
	/// </summary>
	/// <returns><c>null</c> once the test is running, or the sentence saying why it is not.</returns>
	/// <remarks>
	///     The same gates, return and capture rule as <see cref="TestPeriod"/>, narrowed to the lights tested. A room
	///     test already running keeps its room-wide return, which covers this light too.
	/// </remarks>
	public string? TestLight(string lightEntityId, string periodKey)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(lightEntityId);
		ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);

		lock (_gate)
		{
			if (RefuseLevelTest() is { } refusal)
				return refusal;

			if (!_fanOut.Leaves.Contains(lightEntityId))
				return "This room does not command that light.";

			if (_targets.PeriodTarget(lightEntityId, periodKey) is not { } target)
				return "That period is no longer in the schedule.";

			RefreshDarkness();

			if (_levelTest.Start(periodKey, lightEntityId, _area.Lights.Any(_ha.IsOn)))
				_levelTest.Capture(LevelsAreSomebodyElses() ? CaptureLights([lightEntityId]) : null);
			else if (_levelTest.AddLight(lightEntityId))
			{
				// Read before this light is moved, so its return is what it showed and not the test's level.
				_levelTest.Append(CaptureLights([lightEntityId]));
			}

			_fanOut.SendToLight(lightEntityId, _targets.PeriodCommand(target));

			_logger.LogInformation("{Area}: testing period '{Period}' on {Light} for {Seconds}s.",
				Name, target.PeriodName, lightEntityId, LevelTestSeconds);

			Publish(TransitionReason.LevelTestStarted);
			return null;
		}
	}

	/// <summary>Why this room cannot be lit by hand right now, or <c>null</c> when it can.</summary>
	public string? LightNowRefusal()
	{
		lock (_gate)
			return RefuseLightNow();
	}

	/// <summary>Lights this room the way walking into it would, and starts the same vacancy countdown.</summary>
	/// <returns><c>null</c> once the room is lit, or the sentence saying why it is not.</returns>
	/// <remarks>
	///     Nothing here is new machinery: the room ends in <see cref="AreaState.AutoActive"/> holding the levels
	///     the engine itself resolves for this moment, so it keeps following the time of day and it goes off on
	///     the room's own vacancy timeout. Pressed from <see cref="AreaState.OverriddenOn"/> or
	///     <see cref="AreaState.SuppressedOff"/> it is the room being handed back to the engine, which is why the
	///     hold and the suppression both go.
	/// </remarks>
	public string? LightNow()
	{
		lock (_gate)
		{
			if (RefuseLightNow() is { } refusal)
				return refusal;

			// The newest word on these levels, so a running test's return is dropped instead of landing
			// seconds later over the top of what was just asked for.
			AbandonLevelTest();

			CancelAllTimers();
			ForgetDeclinedMotion();
			Enter(AreaState.AutoActive, TransitionReason.ManualLightOn);
			RestartVacancyTimer();

			_logger.LogInformation("{Area}: lit by hand from the app; the vacancy timeout runs as usual.", Name);

			// Through LightUp and nothing else: it declares the detector's expectation per light, so the area
			// cannot read its own command back as a person at the switch.
			LightUp(TransitionReason.ManualLightOn);
			return null;
		}
	}

	/// <summary>Declares a house scene about to run, so its changes to this room's lights are not read as a person's.</summary>
	// Must precede the orchestrator's scene call, as ExpectScene precedes the room's own. A Normal or Sleep scene
	// leaves the room automating, so its echo reaches OnLightChanged.
	public void ExpectHouseScene()
	{
		lock (_gate)
			_fanOut.ExpectSceneOnEveryLight(TransitionSeconds());
	}

	/// <summary>Subscribes and publishes the opening snapshot, leaving the lights as found.</summary>
	// An area found lit adopts them; see AdoptIfLit.
	public void Start()
	{
		foreach (string sensor in _area.MotionSensors)
			_subscriptions.Add(_ha.Entity(sensor).WhenTurnsOn(_ => OnMotion(), _logger));

		foreach (string sensor in _area.LeadInSensors)
		{
			string leadIn = sensor;
			_subscriptions.Add(_ha.Entity(leadIn).WhenTurnsOn(_ => OnLeadIn(leadIn), _logger));
		}

		foreach (string light in _area.Lights)
			_subscriptions.Add(_ha.Entity(light)
				.StateAllChanges()
				.SubscribeSafe(OnLightChanged, _logger));

		// A member that is also an entry is already heard through OnLightChanged.
		foreach (string member in _fanOut.GroupMembers)
			if (!_area.Lights.Contains(member, StringComparer.OrdinalIgnoreCase))
				_subscriptions.Add(_ha.Entity(member)
					.StateAllChanges()
					.SubscribeSafe(OnMemberChanged, _logger));

		WatchBatteries(_batteries, []);

		// A battery Home Assistant adds to a sensor's device after this room was built.
		if (_findBatteries is not null && _area.MotionSensors.Count > 0)
			_subscriptions.Add(_ha.StateAllChanges()
				.Where(change => AreaEntityResolver.CouldBeBattery(change.New))
				.SubscribeSafe(OnPossibleBattery, _logger));

		_subscriptions.Add(_houseChanged.SubscribeSafe(OnHouseChanged, _logger));

		// A moved sun time can put a boundary in the past as easily as the future, so this evaluates before it re-arms.
		if (_sunMoved is { } sunMoved)
			_subscriptions.Add(sunMoved.SubscribeSafe((Unit _) => OnTick(), _logger));

		_subscriptions.Add(_scheduler.SchedulePeriodic(
			TimeSpan.FromSeconds(_global.CircadianTickSeconds),
			OnTick));

		lock (_gate)
		{
			foreach (string leaf in _fanOut.Leaves)
				if (!IsOnOrOff(_ha.GetState(leaf)))
					_notResponding.Add(leaf);

			_lowBatteries = LowBatteries();

			// The last command cannot be known yet, so it stays null instead of being guessed.
			RefreshDarkness();
			TransitionReason opening = ResumeCarriedHold() ? TransitionReason.Startup
				: AdoptIfLit() ? TransitionReason.AdoptedAtStartup
				: TransitionReason.Startup;

			// After the hold and adoption, which read the room's lit state from the carried test.
			TakeUpCarriedTest();
			Publish(opening);

			// Same gate as every other reach into the calculator: the tick above is already subscribed.
			_boundary.Arm();
		}
	}

	/// <summary>The history and hold a replacement for this room should take over.</summary>
	/// <param name="handOnTest">
	///     Whether a running level test goes with it. The test then leaves this room, which no longer returns it
	///     when disposed.
	/// </param>
	public AreaCarryOver CarryOver(bool handOnTest = false)
	{
		lock (_gate)
		{
			AreaHold? hold = _state is AreaState.OverriddenOn or AreaState.SuppressedOff && _holdStartedAt is { } started
				? new AreaHold(_state, started)
				: null;

			CarriedLevelTest? test = handOnTest && _levelTest.Take() is { } running
				? new CarriedLevelTest(running, _state switch
				{
					AreaState.AutoActive or AreaState.PreOff or AreaState.OverriddenOn => true,
					AreaState.SuppressedOff => false,
					_ => running.LitBefore
				})
				: null;

			return new AreaCarryOver(new AreaHistory(_lastMotionAt, _changedAt, _changedBy), hold) { Test = test };
		}
	}

	/// <summary>Takes over what the room this one replaces handed on. Call before <see cref="Start"/>.</summary>
	public void Inherit(AreaCarryOver carried)
	{
		ArgumentNullException.ThrowIfNull(carried);

		lock (_gate)
		{
			_lastMotionAt = carried.History.LastMotionAt;
			_changedAt = carried.History.ChangedAt;
			_changedBy = carried.History.ChangedBy;
			_carriedHold = carried.Hold;
			_carriedTest = carried.Test;
		}
	}

	// Taken up only where a test could start now, so a gate that forbids a test forbids its return as well and
	// the fixtures stay as the test left them. A test the saved settings no longer fit returns at once.
	private void TakeUpCarriedTest()
	{
		CarriedLevelTest? carried = _carriedTest;
		_carriedTest = null;

		if (carried is null || RefuseLevelTest() is not null)
			return;

		_levelTest.Resume(carried.Test);

		if (!StillApplies(carried.Test))
			EndLevelTest();
	}

	private bool StillApplies(RunningLevelTest test)
	{
		if (test.Lights is { } lights)
			return lights.All(light => _fanOut.Leaves.Contains(light) && _targets.PeriodTarget(light, test.PeriodId) is not null);

		return _targets.PeriodTarget(test.PeriodId) is not null
			&& (test.Levels is not { } captured
				|| captured.All(level => _area.Lights.Contains(level.Light, StringComparer.OrdinalIgnoreCase)));
	}

	// A carried test has lit the fixtures itself, so the room's own state comes with it.
	private bool LitWithoutTest() => _carriedTest is { } carried ? carried.RoomLit : _area.Lights.Any(_ha.IsOn);

	// Only from the resting state, as adoption: the opening house state may already have disabled or swept the room.
	// The deadline counts from the hold's own start under the new length; one already passed fires at once.
	private bool ResumeCarriedHold()
	{
		AreaHold? hold = _carriedHold;
		_carriedHold = null;

		if (hold is null || !IsEngineAllowed() || _state != AreaState.AutoVacant)
			return false;

		// Dropped when the lights no longer say what the hold says.
		bool lit = LitWithoutTest();

		switch (hold.State)
		{
			case AreaState.OverriddenOn when lit:
				Enter(AreaState.OverriddenOn, TransitionReason.Startup);
				RestartOverrideTimer(hold.StartedAt);
				return true;

			case AreaState.SuppressedOff when !lit:
				Enter(AreaState.SuppressedOff, TransitionReason.Startup);
				RestartSuppressionTimer(hold.StartedAt);
				return true;

			default:
				return false;
		}
	}

	// Adoption is no auto-on, so darkness does not gate it: any lit area is adopted, or a lamp burns through a
	// bright afternoon because AutoVacant arms no vacancy timer. It observes and never commands, and the target is
	// seeded from the period so the first tick already matches it. A muzzled or disabled area adopts nothing,
	// because arming a timer that ends in a command is a deferred command; OnHouseChanged adopts on kill-switch
	// release for the same reason.
	/// <summary>Takes charge of lights that are already on when the area starts, without commanding anything.</summary>
	/// <returns><c>true</c> when the area adopted lit lights and is now <see cref="AreaState.AutoActive"/>.</returns>
	private bool AdoptIfLit(TransitionReason reason = TransitionReason.AdoptedAtStartup)
	{
		if (!IsEngineAllowed())
			return false;

		// Only from the resting state. The house may already have put this room in Away, SceneHold or Disabled,
		// and adopting out of one of those would undo a standing instruction the room was just given.
		if (_state != AreaState.AutoVacant)
			return false;

		if (!LitWithoutTest())
			return false;

		_logger.LogInformation(
			"{Area}: found lights already on ({Reason}); adopting them without commanding, and arming the vacancy timeout.",
			Name, reason);

		// Seeded, not commanded: this stops the first tick correcting levels nobody asked it to.
		_lastTargets = ResolveTargets();

		Enter(AreaState.AutoActive, reason);
		RestartVacancyTimer();
		return true;
	}

	private void OnMotion()
	{
		lock (_gate)
		{
			if (_disposed)
				return;

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
					// Manual levels stand either way. Under a movement-led hold motion restarts the clock, which is
					// what makes the hold outlast a person who is still in the room; under a fixed one it only
					// records occupancy, which decides where expiry lands.
					if (_area.Settings.OverrideUntilVacant)
						RestartOverrideTimer();

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
					LightUp(TransitionReason.Motion);
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
					LightUp(TransitionReason.Motion);
					return;

				default:
					return;
			}
		}
	}

	/// <summary>Lights a dark, empty room at its dim light when a sensor outside it sees movement.</summary>
	// Lands in PreOff so the dim light, the motion rescue and the off are the warning dim's own. Only from the resting
	// state with every light off: a lit, held or hand-switched-off room ignores it. No vacancy countdown starts.
	private void OnLeadIn(string sensor)
	{
		lock (_gate)
		{
			if (_disposed)
				return;

			if (_state == AreaState.PreOff && _leadIn)
			{
				ArmCountdown(_preOffTimer, TimeSpan.FromSeconds(_area.Settings.PreOffSeconds), OnPreOffElapsed);
				Publish(TransitionReason.LeadIn);
				return;
			}

			if (_state != AreaState.AutoVacant || _area.Lights.Any(_ha.IsOn))
			{
				_logger.LogDebug("{Area}: lead-in from {Sensor} ignored while the room is {State}.", Name, sensor, _state);
				return;
			}

			if (!CanAutoOn(out string blockedBy))
			{
				_logger.LogDebug("{Area}: lead-in from {Sensor} but auto-on is blocked: {Reason}.", Name, sensor, blockedBy);
				return;
			}

			_logger.LogInformation("{Area}: lead-in from {Sensor}; lit at the dim light for {Seconds}s unless someone comes in.",
				Name, sensor, _area.Settings.PreOffSeconds);

			Enter(AreaState.PreOff, TransitionReason.LeadIn);
			_leadIn = true;

			ArmCountdown(_preOffTimer, TimeSpan.FromSeconds(_area.Settings.PreOffSeconds), OnPreOffElapsed);
			ApplyTarget(TransitionReason.LeadIn, _area.Settings.PreOffBrightnessFactor);
		}
	}

	private void OnLightChanged(StateChange change)
	{
		lock (_gate)
		{
			if (_disposed)
				return;

			NoteAvailability(change);

			// A radio, not a hand. See IsHandAtTheSwitch.
			if (!IsHandAtTheSwitch(change))
				return;

			ChangeOrigin origin = _detector.Classify(change);
			bool manual = _detector.IsManual(origin);

			// While disabled, away or holding a scene there is nothing to override, and nothing to report.
			if ((!manual && origin != ChangeOrigin.Automation) || _state is AreaState.Disabled or AreaState.Away or AreaState.SceneHold)
				return;

			Context? context = change.New?.Context;

			if (!manual)
			{
				ReportAutomationLeftAlone(change, context);
				return;
			}

			// A hand at the switch is the newest word on these levels, so a running test's return is dropped.
			AbandonLevelTest();

			bool turnedOn = change.TurnedOn();

			_changedBy = _originNames?.Describe(origin, context);
			_changedAt = _scheduler.Now;

			_logger.LogInformation("{Area}: manual change on {EntityId} attributed to {Origin} ({By}); light is now {State}.",
				Name, change.New?.EntityId, origin, _changedBy ?? "not named", turnedOn ? "on" : "off");

			if (turnedOn)
			{
				CancelAutoTimers();
				Enter(AreaState.OverriddenOn, TransitionReason.ManualOn);

				// Restarted on every manual touch: the override outlasts the last thing the human did.
				RestartOverrideTimer();

				Publish(TransitionReason.ManualOn);
				return;
			}

			CancelAutoTimers();
			Enter(AreaState.SuppressedOff, TransitionReason.ManualOff);
			RestartSuppressionTimer();
			Publish(TransitionReason.ManualOff);
		}
	}

	/// <summary>Reports an automation's change this room leaves alone, once per automation run.</summary>
	// Bypasses Publish's guard as ReportDeclinedMotion does: nothing the snapshot compares has moved.
	private void ReportAutomationLeftAlone(StateChange change, Context? context)
	{
		if (context?.Id is { Length: > 0 } run && string.Equals(run, _ignoredContextId, StringComparison.Ordinal))
			return;

		_ignoredContextId = context?.Id;
		_changedBy = _originNames?.Describe(ChangeOrigin.Automation, context);
		_changedAt = _scheduler.Now;

		_logger.LogInformation("{Area}: change on {EntityId} made by an automation ({By}) is left alone.",
			Name, change.New?.EntityId, _changedBy ?? "not named");

		PublishUnguarded(TransitionReason.AutomationIgnored);
	}

	/// <summary>Whether the change could have been a person at a switch at all, before anyone asks who caused it.</summary>
	// A bulb dropping off the radio looks like a human: Home Assistant writes unavailable with a context carrying
	// neither a user nor a parent, which OverrideDetector reads as PhysicalDevice, and TurnedOn reads as not-on.
	// Both ends of the change must therefore be a state the engine could have commanded. The cost is that a wall
	// switch flipped on an unavailable bulb goes unnoticed and the area keeps automating.
	private static bool IsHandAtTheSwitch(StateChange change) =>
		IsOnOrOff(change.Old) && IsOnOrOff(change.New);

	// On or off, as opposed to unavailable, unknown or absent.
	private static bool IsOnOrOff(EntityState? state) => state is not null && (state.IsOn() || state.IsOff());

	private void OnMemberChanged(StateChange change)
	{
		lock (_gate)
			if (!_disposed)
				NoteAvailability(change);
	}

	// Must run before the group's own change is classified. Home Assistant writes the group while handling the
	// member's change and NetDaemon delivers one app's events in order, so the member always arrives first.
	private void NoteAvailability(StateChange change)
	{
		if (IsOnOrOff(change.Old) == IsOnOrOff(change.New) || change.EntityId() is not { } entityId)
			return;

		_fanOut.ExpectMemberAvailability(entityId);

		// A group entity going unavailable is counted through its members, never as a bulb of its own.
		if (!_fanOut.Leaves.Contains(entityId))
			return;

		if (IsOnOrOff(change.New) ? _notResponding.Remove(entityId) : _notResponding.Add(entityId))
			Publish(TransitionReason.LightAvailability);
	}

	private void OnBatteryChanged()
	{
		lock (_gate)
		{
			if (_disposed)
				return;

			_lowBatteries = LowBatteries();
			Publish(TransitionReason.SensorBattery);
		}
	}

	// Asked again on every change of a battery this room does not know, not only its first: Home Assistant can
	// report the new entity's state before its registry entry names the device.
	private void OnPossibleBattery(StateChange change)
	{
		lock (_gate)
		{
			if (_disposed || change.EntityId() is not { } entityId || _findBatteries is not { } find)
				return;

			if (_batteries.Any(battery => Names(battery, entityId)))
				return;

			IReadOnlyList<MotionBattery> found = find();
			if (!found.Any(battery => Names(battery, entityId)))
				return;

			IReadOnlyList<MotionBattery> watched = _batteries;
			_batteries = found;
			WatchBatteries(found, watched);

			_lowBatteries = LowBatteries();
			Publish(TransitionReason.SensorBattery);
		}

		static bool Names(MotionBattery battery, string entityId) =>
			string.Equals(battery.LowEntity, entityId, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(battery.LevelEntity, entityId, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Subscribes to each battery entity in <paramref name="batteries"/> not already in <paramref name="watched"/>.</summary>
	private void WatchBatteries(IReadOnlyList<MotionBattery> batteries, IReadOnlyList<MotionBattery> watched)
	{
		HashSet<string> known = new(
			watched.SelectMany(battery => (string?[])[battery.LowEntity, battery.LevelEntity]).OfType<string>(),
			StringComparer.OrdinalIgnoreCase);

		foreach (MotionBattery battery in batteries)
			foreach (string? entity in (string?[])[battery.LowEntity, battery.LevelEntity])
				if (entity is not null && known.Add(entity))
					_subscriptions.Add(_ha.Entity(entity)
						.StateChanges()
						.SubscribeSafe((StateChange _) => OnBatteryChanged(), _logger));
	}

	private List<SensorBattery> LowBatteries() =>
	[
		.. _batteries
			.Select(battery => battery.LowFrom(StateOf(battery.LowEntity), StateOf(battery.LevelEntity)))
			.OfType<SensorBattery>()
	];

	private string? StateOf(string? entityId) => entityId is null ? null : _ha.GetState(entityId)?.State;

	private void OnHouseChanged(HouseState house)
	{
		lock (_gate)
		{
			if (_disposed)
				return;

			HouseState previous = _house;
			_house = house;

			bool opening = _openingHouseState;
			_openingHouseState = false;

			if (!IsEngineAllowed())
			{
				// Its return runs through the ordinary command path, so it would command a light the master
				// switch has just forbidden. The fixtures stay where the test left them, which is the muzzle's
				// promise: nothing is commanded either way.
				AbandonLevelTest();

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

			if (house.ActiveKind == ModeKind.Away)
			{
				if (_state != AreaState.Away)
					GoAway(opening);

				return;
			}

			// Before the was-Away recovery: entering a scene mode straight from Away must land in SceneHold, not
			// run the welcome-home ApplyTarget that would clobber the scene.
			if (house.ActiveKind == ModeKind.Guest && house.ActiveScene is { Length: > 0 })
			{
				if (_state != AreaState.SceneHold)
					EnterSceneHold();

				return;
			}

			if (_state == AreaState.Away)
			{
				ComeHome(TransitionReason.HouseModeChanged);
				return;
			}

			if (_state == AreaState.SceneHold)
			{
				// The Guest scene ended. Exit to the resting state and let the normal machinery re-evaluate.
				Enter(AreaState.AutoVacant, TransitionReason.SceneHold);

				// The scene left these lights on, and AutoVacant arms nothing that would ever end them.
				AdoptIfLit(TransitionReason.SceneHold);

				Publish(TransitionReason.SceneHold);
				return;
			}

			// A mode switch is a command: retarget an active area when the kind or the mode value moved. A room
			// sitting on its own scene is not retargeted, on the same rule the tick follows.
			if (_state == AreaState.AutoActive
				&& _standingScene is null
				&& (previous.ActiveKind != house.ActiveKind
					|| !string.Equals(previous.ModeValue, house.ModeValue, StringComparison.OrdinalIgnoreCase)))
				ApplyTarget(ModeReason(opening));
		}
	}

	private void EnterSceneHold()
	{
		// The house's scene is the newest word on these lights, for the same reason it is in GoAway.
		AbandonLevelTest();

		CancelAllTimers();

		// The house's scene is the look now, so the room's own no longer describes these lights.
		_standingScene = null;

		Enter(AreaState.SceneHold, TransitionReason.SceneHold);
		Publish(TransitionReason.SceneHold);
	}

	/// <summary>The area's re-evaluation of the world, at <c>CircadianTickSeconds</c> and at every boundary.</summary>
	// Runs whatever the state, because lux crossing the threshold at dusk is a moment with no transition and no
	// deadline, so nothing else would announce it. The guard in Publish keeps a quiet area quiet.
	private void OnTick()
	{
		Evaluate();

		// Re-asked every evaluation, so a sun time that has moved, a table a save rebuilt or a clock the box
		// corrected all re-arm within one tick. Under the gate: the arm reads the calculator, which the sun's own
		// subscription can be inside on another thread.
		lock (_gate)
			if (!_disposed)
				_boundary.Arm();
	}

	private void Evaluate()
	{
		lock (_gate)
		{
			// Every callback checks this: a timer or event already on its way still arrives after Dispose, and the
			// replacement is running on the saved configuration.
			if (_disposed)
				return;

			// A state read, not a subscription, so the tick is the only thing that can notice dusk in an area
			// that is otherwise idle.
			RefreshDarkness();

			// Before the retarget below: an area whose hold has just released is on its way off, and commanding it
			// to this instant's levels first would be a visible flash.
			if (_offHeldBack && HoldingLit() is null && SettleHeldBackOff())
				return;

			// A standing scene is the room's look, so nothing here re-aims it.
			if (_state == AreaState.AutoActive && _standingScene is null)
			{
				AreaTargets? targets = ResolveTargets();

				if (targets is not null && !TargetsMatch(targets, _lastTargets))
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
			if (!_disposed)
				VacancyTimedOut();
	}

	private void VacancyTimedOut()
	{
		if (_state != AreaState.AutoActive)
			return;

		// A sensor held on sends no second edge, so the countdown its first one started runs out with somebody
		// still there. Checked before the dim, and on IsOn so an unreadable sensor holds nothing.
		if (MotionStillOn() is { } moving)
		{
			_logger.LogDebug("{Area}: {Sensor} still reads on, so the vacancy countdown starts again.", Name, moving);
			RestartVacancyTimer();
			Publish(TransitionReason.Motion);
			return;
		}

		// The warning dim is a step towards off, so a held area does not take it either.
		if (HoldingLit() is { } holder)
		{
			HoldOffBack(holder, TransitionReason.VacancyTimeout);
			return;
		}

		// Nothing is about to go off, so there is nothing to warn about and the dim would be a step to nowhere.
		if (_area.SceneWhenEmpty is { Length: > 0 })
		{
			ClearCountdown();
			Enter(AreaState.AutoVacant, TransitionReason.VacancyTimeout);
			SettleEmpty(TransitionReason.VacancyTimeout);
			return;
		}

		Enter(AreaState.PreOff, TransitionReason.VacancyTimeout);

		// Armed before the dim, so the snapshot announcing PreOff already carries its own deadline.
		ArmCountdown(_preOffTimer, TimeSpan.FromSeconds(_area.Settings.PreOffSeconds), OnPreOffElapsed);

		ApplyTarget(TransitionReason.VacancyTimeout, _area.Settings.PreOffBrightnessFactor);
	}

	private void OnPreOffElapsed()
	{
		lock (_gate)
			if (!_disposed)
				PreOffElapsed();
	}

	private void PreOffElapsed()
	{
		if (_state != AreaState.PreOff)
			return;

		TransitionReason reason = _leadIn ? TransitionReason.LeadInUnanswered : TransitionReason.PreOffElapsed;

		// Held at the dimmed level, which is still lit. Restoring the full target would be the engine commanding
		// a room up, which the hold never does.
		if (HoldingLit() is { } holder)
		{
			HoldOffBack(holder, reason);
			return;
		}

		ClearCountdown();
		Enter(AreaState.AutoVacant, reason);
		SettleEmpty(reason);
	}

	private void OnOverrideExpired()
	{
		lock (_gate)
		{
			if (_disposed || _state != AreaState.OverriddenOn)
				return;

			ClearCountdown();

			if (IsOccupied())
			{
				Enter(AreaState.AutoActive, TransitionReason.OverrideExpired);
				RestartVacancyTimer();
				ApplyTarget(TransitionReason.OverrideExpired);
				return;
			}

			Enter(AreaState.AutoVacant, TransitionReason.OverrideExpired);

			if (HoldingLit() is { } holder)
			{
				HoldOffBack(holder, TransitionReason.OverrideExpired);
				return;
			}

			SettleEmpty(TransitionReason.OverrideExpired);
		}
	}

	// Every motion event restarts this timer, so its firing is itself the proof the area is vacant. An extra
	// occupancy check here would stretch the reset out to the vacancy timeout.
	private void OnSuppressionLifted()
	{
		lock (_gate)
		{
			if (_disposed || _state != AreaState.SuppressedOff)
				return;

			ClearCountdown();
			Enter(AreaState.AutoVacant, TransitionReason.SuppressionLifted);
			Publish(TransitionReason.SuppressionLifted);
		}
	}

	// A change, unless this is the mode the area found when it started.
	private static TransitionReason ModeReason(bool opening) =>
		opening ? TransitionReason.Startup : TransitionReason.HouseModeChanged;

	private void GoAway(bool opening)
	{
		TransitionReason reason = ModeReason(opening);

		// The leaving sweep, or the away scene, is the newest word on these lights. A test's return landing
		// seconds later would sweep the room dark over the top of a standing away scene.
		AbandonLevelTest();

		CancelAllTimers();
		Enter(AreaState.Away, reason);

		// An away scene is the away look, so skip the sweep and let the scene stand.
		if (_house.ActiveScene is { Length: > 0 })
		{
			_logger.LogDebug("{Area}: away scene {Scene} is holding; skipping the leaving sweep.", Name, _house.ActiveScene);
			_standingScene = null;
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

		if (HoldingLit() is { } holder)
		{
			HoldOffBack(holder, reason);
			return;
		}

		TurnOff(reason);
	}

	/// <summary>Leaves the Away state.</summary>
	private void ComeHome(TransitionReason reason)
	{
		// The sweep a hold refused was the leaving sweep, and the house is no longer leaving. Without this the
		// hold releasing later settles an off nobody asked for, since WelcomeHome is off by default and the
		// branch below that re-arms the vacancy timer never runs.
		_offHeldBack = false;

		Enter(AreaState.AutoVacant, reason);

		if (!_area.Settings.WelcomeHome || !CanAutoOn(out _))
		{
			// A room the leaving sweep left on (SkipAwaySweep, or a hold that refused the off) is still lit, and
			// AutoVacant arms no vacancy timeout, so without this it burns with nothing to end it.
			AdoptIfLit(reason);

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
			// The forced telling is checked first: nothing else in the log would ever have named it.
			AutoOnBlock.Away => _house.Forced is { Kind: ModeKind.Away } forced ? forced.Describe()
				: "the house is set to away",
			AutoOnBlock.SceneHold => $"a guest scene ({_house.ActiveScene}) is holding this area",
			AutoOnBlock.Sleep => "sleep mode blocks auto-on for this area",
			AutoOnBlock.EntityOn => $"{blocker} is on",
			_ => $"not dark enough ({_gateSensor.DarknessDetail()})"
		};

		return block == AutoOnBlock.None;
	}

	/// <summary>Reports movement the area declined to light, and what stopped it, when that gate has changed.</summary>
	// The bound is on the refusing gate and never on the reading behind it: N movements under an unchanged block
	// produce one report, and a lux value drifting under an unchanged NotDark produces none. This bypasses
	// Publish's identical-consecutive guard, which would suppress every one of these, because a declined movement
	// moves only the fields AreaSnapshot.HasSameMeaningAs excludes.
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

		PublishUnguarded(TransitionReason.Motion);
	}

	// Called wherever the area actually lights on movement, or a second spell under the same gate goes unreported.
	private void ForgetDeclinedMotion()
	{
		_reportedDecline = null;
		_reportedDeclineEntity = null;
	}

	/// <summary>Which gate would refuse to light this area for movement right now, judged against <paramref name="dark"/>.</summary>
	// The single place the auto-on gates are decided. CanAutoOn asks it before acting and Snapshot asks it to fill
	// AreaSnapshot.AutoOnBlockedBy, so a reader is told the verdict the engine acted on; a second copy in the
	// publisher or a page would drift. dark is passed in so the caller decides which reading applies. The rebuilt
	// gate is skipped: movement reaching a replaced controller is judged on the gates alone.
	private AutoOnBlock AutoOnBlockNow(bool dark, out string? blocker) =>
		HouseGates.FirstClosed(GateState(dark), HouseGate.KillSwitch, HouseGate.NotDark, out blocker) switch
		{
			HouseGate.KillSwitch => AutoOnBlock.KillSwitch,
			HouseGate.Disabled => AutoOnBlock.Disabled,
			HouseGate.Away => AutoOnBlock.Away,
			// Named here, not left to fall through to the darkness gate: a scene-held area reporting "not dark
			// enough" would send somebody to the lux sensor over a mode they set themselves.
			HouseGate.SceneHold => AutoOnBlock.SceneHold,
			HouseGate.Sleep => AutoOnBlock.Sleep,
			HouseGate.EntityOn => AutoOnBlock.EntityOn,
			HouseGate.NotDark => AutoOnBlock.NotDark,
			_ => AutoOnBlock.None
		};

	/// <summary>The house-wide gates as they stand, for <see cref="HouseGates"/> to walk.</summary>
	// dark only decides the last gate, so a caller that stops before it passes nothing.
	private HouseGateState GateState(bool dark = false) => new(
		Rebuilding: _disposed,
		KillSwitchActive: _house.KillSwitchActive,
		Enabled: _area.Settings.Enabled,
		Mode: _house.ActiveKind,
		ActiveScene: _house.ActiveScene,
		SleepBlocksAutoOn: _area.Settings.SleepBlocksAutoOn,
		BlockingEntity: _blockingEntity,
		Dark: dark);

	/// <summary>The first of <paramref name="entities"/> whose state applies, or <c>null</c> when none does.</summary>
	// IsOff differs from !IsOn: both are false for an absent, unavailable or unknown entity, so one that cannot
	// be read applies under neither polarity. A vanished helper must pin a room neither dark nor lit.
	private string? FirstThatApplies(IReadOnlyList<string> entities, bool inverted)
	{
		Func<string, bool> applies = inverted ? _ha.IsOff : _ha.IsOn;
		return entities.FirstOrDefault(applies);
	}

	/// <summary>The entity holding this area's lights on, or <c>null</c> when none is.</summary>
	// Suppresses the engine's own off-commands only. It never turns anything on, and a hand at the switch is
	// still obeyed, so OnLightChanged does not consult it.
	private string? HoldingLit() => FirstThatApplies(_area.KeepLitWhenOn, _area.KeepLitWhenOnInverted);

	/// <summary>Records that a hold refused an off already counted down to, and publishes without that deadline.</summary>
	private void HoldOffBack(string holder, TransitionReason reason)
	{
		_logger.LogDebug("{Area}: {Holder} is holding the lights on, so {Reason} commands nothing.", Name, holder, reason);

		_offHeldBack = true;
		ClearCountdown();
		Publish(reason);
	}

	/// <summary>Runs the off a hold refused, once it has released; called from the tick and nowhere else.</summary>
	/// <returns><c>true</c> when it acted and published; the caller must not publish over it.</returns>
	private bool SettleHeldBackOff()
	{
		_offHeldBack = false;

		switch (_state)
		{
			case AreaState.AutoActive:
				VacancyTimedOut();
				return true;

			case AreaState.PreOff:
				PreOffElapsed();
				return true;

			case AreaState.Away:
				// From the tick, so never the opening state however long the hold has been refusing the off.
				TurnOff(ModeReason(opening: false));
				return true;

			case AreaState.AutoVacant:
				// Where an expiring override left the area: still lit, with nothing else armed to switch it off.
				SettleEmpty(TransitionReason.OverrideExpired);
				return true;

			default:
				return false;
		}
	}

	// Re-reads the darkness gate, keeping the verdict the snapshot and the fade length both use.
	private bool RefreshDarkness()
	{
		bool dark = _gateSensor.IsDarkEnough();
		_lastDarkVerdict = dark;
		_lastDarknessDetail = _gateSensor.DarknessDetail();
		return dark;
	}

	// For the two questions the vacancy timer cannot answer: where an expiring override lands, and whether a
	// suppression may lift.
	private bool IsOccupied() =>
		MotionStillOn() is not null ||
		(_lastMotionAt is { } lastMotion &&
			_scheduler.Now - lastMotion < TimeSpan.FromSeconds(_area.Settings.VacancyTimeoutSeconds));

	private string? MotionStillOn() => FirstThatApplies(_area.MotionSensors, inverted: false);

	/// <summary>This instant's targets, keeping the period they resolved from for the snapshot.</summary>
	// Resolved on the tick as well as on a command, so a level the daylight curve moves is set at the tick and
	// not only on the next motion event.
	private AreaTargets? ResolveTargets()
	{
		AreaTargets? targets = _targets.Resolve(_scheduler.Now, _house, out LightTarget? period);
		CacheResolvedPeriod(_scheduler.Now, period);
		return targets;
	}

	private void ApplyTarget(TransitionReason reason, double brightnessFactor = 1.0)
	{
		AreaTargets? targets = ResolveTargets();
		if (targets is null)
			return;

		AreaTargets? previous = _lastTargets;
		_lastTargets = targets;

		// Before every command: it picks the fade length and it is what the snapshot reports.
		RefreshDarkness();

		Send(_targets.TargetCommand(targets.Room, brightnessFactor), _targets.LightCommands(targets, brightnessFactor));

		_lightsMoved = reason is TransitionReason.CircadianTick ? LightsMovedAlone(targets, previous) : null;
		Publish(reason);
		_lightsMoved = null;
	}

	/// <summary>The lights whose own target moved while the room's did not, or <c>null</c>.</summary>
	private static IReadOnlyList<string>? LightsMovedAlone(AreaTargets now, AreaTargets? before)
	{
		if (before is null || now.Lights.Count == 0 || !TargetsMatch(now.Room, before.Room))
			return null;

		List<string> moved =
		[
			.. now.Lights
				.Where(own => !before.Lights.TryGetValue(own.Key, out LightTarget? was) || !TargetsMatch(own.Value, was))
				.Select(own => own.Key)
				.Order(StringComparer.Ordinal)
		];

		return moved.Count > 0 ? moved : null;
	}

	/// <summary>Lights the area for movement: its scene when it names one, otherwise the period's levels.</summary>
	private void LightUp(TransitionReason reason)
	{
		if (_area.SceneOnMotion is { Length: > 0 } scene)
		{
			ApplyScene(scene, reason);
			return;
		}

		ApplyTarget(reason);
	}

	/// <summary>What this area does when it goes empty: its scene when it names one, otherwise off.</summary>
	// The single answer to what the area was about to do, so the off a KeepLitWhenOn hold refused settles as a
	// scene for a room that names one. The leaving sweep does not come through here: an empty house is not a room
	// going empty.
	private void SettleEmpty(TransitionReason reason)
	{
		if (_area.SceneWhenEmpty is { Length: > 0 } scene)
		{
			ApplyScene(scene, reason);
			return;
		}

		TurnOff(reason);
	}

	private void ApplyScene(string sceneId, TransitionReason reason)
	{
		RefreshDarkness();
		_fanOut.RunScene(sceneId, TransitionSeconds());

		_standingScene = sceneId;
		_lastTargets = null;
		_lastCommand = null;
		_lastLightCommands = null;
		_lastCommandAt = _scheduler.Now;

		Publish(reason);
	}

	private void TurnOff(TransitionReason reason)
	{
		RefreshDarkness();

		LightCommand command = LightCommand.TurnOff(TransitionSeconds());
		Send(command);
		Publish(reason);
	}

	private void Send(LightCommand command, IReadOnlyDictionary<string, LightCommand>? lightCommands = null)
	{
		_standingScene = null;
		_fanOut.Send(command, lightCommands);

		// The standing command, so a republish keeps the levels that are actually holding instead of blanking them.
		_lastCommand = command;
		_lastLightCommands = lightCommands;
		_lastCommandAt = _scheduler.Now;
	}

	/// <summary>Why a level test cannot run here, or <c>null</c> when one can.</summary>
	// The reason a button carries and the refusal a press would actually get are one answer, because both come
	// from the one ladder. A second copy in the web project would drift, as AutoOnBlockNow's would. The ladder
	// stops at Away: a guest scene is not refused, because those levels are read off the fixtures and put back,
	// and sleep, a blocking entity and darkness never refused a test.
	private string? RefuseLevelTest() =>
		HouseGates.FirstClosed(GateState(), HouseGate.Rebuilt, HouseGate.Away) switch
		{
			HouseGate.Rebuilt => HouseGates.BeingRebuilt,
			HouseGate.KillSwitch => "The master switch is on, so nothing may command a light.",
			HouseGate.Disabled => "Automatic lighting is switched off for this room, so its lights are not the engine's to move.",
			// A test here has nothing to give the room back: an away room's levels are the sweep's or the away
			// scene's, and neither is captured, so the return would hand it back by sweeping it dark.
			HouseGate.Away => "The house is set to away, so its lights are not being moved for a test.",
			_ => null
		};

	/// <summary>Why <see cref="LightNow"/> would refuse, or <c>null</c> when it would light the room.</summary>
	// The gates a press does not defeat: either the engine may command nothing here at all, or the house carries
	// a standing instruction this room is not free to ignore. The ladder stops at the guest scene, so darkness,
	// sleep and a blocking entity are left out, because overriding those is what the button is for.
	private string? RefuseLightNow() =>
		HouseGates.FirstClosed(GateState(), HouseGate.Rebuilt, HouseGate.SceneHold) switch
		{
			HouseGate.Rebuilt => HouseGates.BeingRebuilt,
			HouseGate.KillSwitch => "The master switch is on, so nothing may command a light.",
			HouseGate.Disabled => "This room is not enabled, so its lights are not this app's to switch on.",
			HouseGate.Away => "The house is set to away, so nothing here is switched on.",
			HouseGate.SceneHold when _house.ActiveScene is { } scene => $"A guest scene ({scene}) is holding this room.",
			_ => null
		};

	// The two states whose levels the engine did not choose and cannot resolve again: a hand at the switch, and a
	// house scene. EnterSceneHold clears _standingScene, so ReassertLights would take a scene-held room to dark.
	private bool LevelsAreSomebodyElses() =>
		_state is AreaState.OverriddenOn or AreaState.SceneHold;

	/// <summary>Reads this room's fixtures as they stand, as commands that would put them back.</summary>
	private IReadOnlyList<(string Light, LightCommand Command)> CaptureLights(IEnumerable<string> lights)
	{
		List<(string Light, LightCommand Command)> captured = [];

		foreach (string light in lights)
		{
			EntityState? state = _ha.GetState(light);
			if (state is null)
				continue;

			if (!state.IsOn())
			{
				captured.Add((light, LightCommand.TurnOff(TransitionSeconds())));
				continue;
			}

			double? raw = state.AttrDouble(LightAttributes.Brightness);
			double? kelvin = state.AttrDouble(LightAttributes.ColorTempKelvin);

			captured.Add((light, new LightCommand(
				true,
				raw is { } brightness ? brightness / LightAttributes.MaxRawBrightness * 100 : null,
				kelvin is { } warmth ? (int)Math.Round(warmth) : null,
				TransitionSeconds(),
				Channels: ReadChannels(state))));
		}

		return captured;
	}

	/// <summary>The colour channel vector a fixture currently reports, or <c>null</c> when it reports none.</summary>
	private static IReadOnlyList<int>? ReadChannels(EntityState state)
	{
		foreach (string attribute in LightAttributes.ColourChannelAttributes)
		{
			IReadOnlyList<double> values = state.AttrDoubleList(attribute);
			if (values.Count > 0)
				return [.. values.Select(value => (int)Math.Round(value))];
		}

		return null;
	}

	/// <summary>Drops a running test's return, leaving the fixtures where they are.</summary>
	// For a hand at the switch mid-test: the person has just said what these lights are, so the capture is stale
	// and the return would arrive seconds later over the top of it.
	private void AbandonLevelTest() => _levelTest.Take();

	private void OnLevelTestElapsed()
	{
		lock (_gate)
		{
			if (_disposed)
				return;

			EndLevelTest();

			// The report stops naming the test, so a page reading it does not wait on the clock.
			Publish(TransitionReason.LevelTestEnded);
		}
	}

	/// <summary>Ends a running level test by giving the levels back to whoever owns them.</summary>
	private void EndLevelTest()
	{
		// Taken before any of it is used, so a second return cannot apply the same capture twice.
		if (_levelTest.Take() is not { } finished)
			return;

		HashSet<string>? tested = finished.Lights;

		// Same rule, two owners: the engine resolves its own levels afresh, and a person's are the ones read
		// before the test.
		if (finished.Levels is not { } captured)
		{
			if (tested is null)
				ReassertLights();
			else
				ReassertLights(tested);

			return;
		}

		// Declared before each command, as the way in does it, or the room reads the return as a person
		// and falls into a fresh hold on top of the one it is already keeping.
		foreach ((string light, LightCommand command) in captured)
		{
			if (tested is not null)
			{
				_fanOut.SendToLight(light, command);
				continue;
			}

			_fanOut.SendAlone(light, command);
		}
	}

	/// <summary>Re-sends what the engine wants this room to be right now, changing no state and arming no timer.</summary>
	// Resolved at this instant and never captured before the test: a few seconds is long enough for movement, a
	// boundary or a hand at a switch to have moved the answer, and the room has to end where it would have been
	// had nobody pressed anything. Nothing is published, because none of it is news: the area decided nothing.
	private void ReassertLights()
	{
		RefreshDarkness();

		// Standing scene first: the room's look is that scene, and no level command describes it.
		if (_standingScene is { Length: > 0 } scene)
		{
			_fanOut.RunScene(scene, TransitionSeconds());
			_lastCommandAt = _scheduler.Now;
			return;
		}

		// The two lit states. PreOff is holding the same target at its warning dim.
		if ((_state is AreaState.AutoActive or AreaState.PreOff) && ResolveTargets() is { } targets)
		{
			_lastTargets = targets;

			double factor = _state is AreaState.PreOff ? _area.Settings.PreOffBrightnessFactor : 1.0;
			Send(_targets.TargetCommand(targets.Room, factor), _targets.LightCommands(targets, factor));
			return;
		}

		Send(LightCommand.TurnOff(TransitionSeconds()));
	}

	/// <summary>Re-sends what the engine wants for <paramref name="lights"/> alone: the return a light test owes.</summary>
	// Nothing about the room is recorded. A boundary crossed during the test leaves _lastTargets behind, and the next
	// tick re-applies the room.
	private void ReassertLights(IReadOnlyCollection<string> lights)
	{
		if (_standingScene is { Length: > 0 })
		{
			ReassertLights();
			return;
		}

		RefreshDarkness();

		IOrderedEnumerable<string> ordered = lights.Order(StringComparer.Ordinal);

		if ((_state is AreaState.AutoActive or AreaState.PreOff) && ResolveTargets() is { } targets)
		{
			double factor = _state is AreaState.PreOff ? _area.Settings.PreOffBrightnessFactor : 1.0;
			LightCommand room = _targets.TargetCommand(targets.Room, factor);

			foreach (string light in ordered)
				_fanOut.SendToLight(
					light,
					targets.Lights.TryGetValue(light, out LightTarget? own) ? _targets.TargetCommand(own, factor) : room);

			return;
		}

		foreach (string light in ordered)
			_fanOut.SendToLight(light, LightCommand.TurnOff(TransitionSeconds()));
	}

	// The fade length, picked by darkness and never by the period name: what matters is whether the eyes receiving
	// the change are dark-adapted, which the gate already measured.
	private double TransitionSeconds() =>
		_lastDarkVerdict == true ? _area.Settings.NightTransitionSeconds : _area.Settings.DayTransitionSeconds;

	// Clears the held-back off: a fresh countdown supersedes the spent one the hold refused.
	private void RestartVacancyTimer()
	{
		_offHeldBack = false;
		ArmCountdown(_vacancyTimer, TimeSpan.FromSeconds(_area.Settings.VacancyTimeoutSeconds), OnVacancyTimeout);
	}

	private void RestartSuppressionTimer(DateTimeOffset? startedAt = null)
	{
		_holdStartedAt = startedAt ?? _scheduler.Now;
		ArmCountdown(_suppressionTimer, TimeSpan.FromMinutes(_area.Settings.VacancyResetMinutes), OnSuppressionLifted, _holdStartedAt);
	}

	private void RestartOverrideTimer(DateTimeOffset? startedAt = null)
	{
		_holdStartedAt = startedAt ?? _scheduler.Now;
		ArmCountdown(_overrideTimer, OverrideHold(), OnOverrideExpired, _holdStartedAt);
	}

	// A movement-led hold runs for the vacancy timeout and motion restarts it, so IsOccupied is false when it fires
	// unless a sensor still reads on. Re-arming it while IsOccupied is false holds a sensorless room lit for ever.
	private TimeSpan OverrideHold() =>
		_area.Settings.OverrideUntilVacant
			? TimeSpan.FromSeconds(_area.Settings.VacancyTimeoutSeconds)
			: TimeSpan.FromMinutes(_area.Settings.OverrideDurationMinutes);

	// The single place all four state timers are armed, so a published deadline is never out of step with the
	// timer that will honour it. Records both ends of the window for the snapshot's elapsed-versus-remaining.
	private void ArmCountdown(SerialDisposable timer, TimeSpan delay, Action onElapsed, DateTimeOffset? from = null)
	{
		DateTimeOffset start = from ?? _scheduler.Now;
		DateTimeOffset due = start + delay;

		_nextChangeFrom = start;
		_nextChangeAt = due;
		timer.Disposable = _scheduler.Schedule(due > _scheduler.Now ? due - _scheduler.Now : TimeSpan.Zero, onElapsed);
	}

	private void ClearCountdown()
	{
		_nextChangeAt = null;
		_nextChangeFrom = null;
	}

	private void CancelAutoTimers()
	{
		_offHeldBack = false;
		ClearCountdown();
		_vacancyTimer.Disposable = Disposable.Empty;
		_preOffTimer.Disposable = Disposable.Empty;
	}

	private void CancelAllTimers()
	{
		CancelAutoTimers();
		_overrideTimer.Disposable = Disposable.Empty;
		_suppressionTimer.Disposable = Disposable.Empty;
	}

	private static bool TargetsMatch(LightTarget left, LightTarget? right) =>
		right is not null &&
		Math.Abs(left.BrightnessPct - right.BrightnessPct) < LightTolerance.BrightnessPct &&
		Math.Abs(left.ColorTempKelvin - right.ColorTempKelvin) < LightTolerance.ColorTempKelvin;

	// The tick re-applies when any one of them has moved, so a light on its own levels crosses its own boundary
	// without waiting for the room to cross one.
	private static bool TargetsMatch(AreaTargets left, AreaTargets? right) =>
		right is not null
		&& TargetsMatch(left.Room, right.Room)
		&& left.Lights.Count == right.Lights.Count
		&& left.Lights.All(own =>
			right.Lights.TryGetValue(own.Key, out LightTarget? theirs) && TargetsMatch(own.Value, theirs));

	private void Enter(AreaState state, TransitionReason reason)
	{
		if (_state != state)
			_logger.LogInformation("{Area}: {From} -> {To} ({Reason}).", Name, _state, state, reason);

		if (state != AreaState.PreOff)
			_leadIn = false;

		_state = state;
	}

	/// <summary>Publishes this snapshot, unless it repeats the news the area last published.</summary>
	// Every publish passes through the one identical-consecutive guard, because two triggers can land on the same
	// instant and resolve to the identical snapshot: a tick arriving with a motion event, or a house-state
	// re-emit behind a mode change. A same-state republish whose deadline moved still goes out, because the
	// deadline is part of the compared meaning.
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

	// For news the snapshot's compared fields do not carry, which Publish's guard would drop.
	private void PublishUnguarded(TransitionReason reason)
	{
		AreaSnapshot snapshot = Snapshot(reason);
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

		string? heldLitBy = HoldingLit();

		return new AreaSnapshot(
			AreaName: Name,
			State: _state,
			Reason: reason,
			Mode: _house.ActiveKind,
			KillSwitchActive: _house.KillSwitchActive,
			IsDark: _lastDarkVerdict,
			PeriodName: _resolvedPeriodName,
			BrightnessPct: standing?.BrightnessPct,
			ColorTempKelvin: standing?.ColorTempKelvin,
			Timestamp: _scheduler.Now,
			LastCommandAt: _lastCommandAt,
			LastMotionAt: _lastMotionAt,
			NextChangeAt: _nextChangeAt,
			NextChangeFrom: _nextChangeFrom,
			HouseModeValue: _house.ModeValue,
			DarknessDetail: _lastDarknessDetail,
			AreaId: _areaId,
			AutoOnBlockedBy: blocked,
			AutoOnBlockingEntity: blocker,
			LevelsFromRoom: _resolvedLevelsFromRoom,
			IsAnyoneHome: _house.IsAnyoneHome,
			Forced: _house.Forced,
			IsHeldLit: heldLitBy is not null,
			HeldLitBy: heldLitBy,
			SceneApplied: _standingScene,
			TestingPeriodId: _levelTest.PeriodId,
			TestEndsAt: _levelTest.EndsAt,
			LightLevels: standing is not null ? LightStandings() : null,
			TestingLightId: _levelTest.LightId,
			LightsMoved: _lightsMoved,
			LightsNotResponding: _notResponding.Count,
			LightCount: _fanOut.Leaves.Count,
			ChangedBy: _changedBy,
			ChangedAt: _changedAt,
			// Enter clears _leadIn on leaving PreOff.
			IsLeadIn: _leadIn,
			LowBatteries: _lowBatteries);
	}

	/// <summary>What each light on levels of its own was last commanded, or <c>null</c> for a room with none.</summary>
	private IReadOnlyList<LightStanding>? LightStandings() =>
		_lastLightCommands is { Count: > 0 } commands
			?
			[
				.. commands
					.OrderBy(pair => pair.Key, StringComparer.Ordinal)
					.Select(pair => new LightStanding(
						pair.Key,
						pair.Value.On ? pair.Value.BrightnessPct : null,
						pair.Value.On ? pair.Value.ColorTempKelvin : null))
			]
			: null;

	private void ResolvePeriodAt(DateTimeOffset now)
	{
		if (_resolvedPeriodAt != now)
			CacheResolvedPeriod(now, _targets.PeriodAt(now));
	}

	// The name and the room-levels flag are cached together because they are one answer from one resolution.
	private void CacheResolvedPeriod(DateTimeOffset now, LightTarget? target)
	{
		_resolvedPeriodAt = now;
		_resolvedPeriodName = target?.PeriodName;
		_resolvedLevelsFromRoom = target?.FromRoom ?? RoomLevelSource.None;
	}

	/// <summary>Unsubscribes and cancels every timer, leaving the lights as they are.</summary>
	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
				return;

			// Before the flag, because the return runs through the ordinary command path. A save rebuilds every
			// controller, and a test left running would hold the room until the replacement's next tick, which is
			// CircadianTickSeconds away.
			EndLevelTest();

			_disposed = true;
		}

		// The boundary first: it is the one timer that can fire after the subscriptions are gone, and a boundary
		// landing in that window would have this controller command lights against a table that has been replaced.
		_boundary.Dispose();
		_subscriptions.Dispose();
		_vacancyTimer.Dispose();
		_preOffTimer.Dispose();
		_overrideTimer.Dispose();
		_suppressionTimer.Dispose();
		_levelTest.Dispose();
	}
}
