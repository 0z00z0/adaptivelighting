using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Subjects;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.LastSeen;
using AdaptiveLighting.Ha;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>The engine's composition root: resolves the areas, builds a controller for each, and owns the house-wide state.</summary>
// One orchestrator per host, fanning out to one controller per area. AdaptiveLighting is never scanned by
// AddAppsFromAssembly, so a [NetDaemonApp] attribute here would be inert; the per-project bootstrap constructs
// this.
public sealed class LightingOrchestrator : IDisposable
{
	private const string NextRisingAttribute = "next_rising";
	private const string NextSettingAttribute = "next_setting";
	private const string FriendlyNameAttribute = "friendly_name";

	/// <summary>The title of the setup-failure card, which is also what fixes its notification id in Home Assistant.</summary>
	// HaNotifier derives the id from this, so changing the wording leaves any card raised under the old title
	// standing beside the new one until Home Assistant restarts.
	internal const string SetupFailureTitle = "Adaptive lighting: rooms that could not be set up";

	private readonly IHaContext _ha;
	private readonly IHaRegistry _registry;
	private readonly IScheduler _scheduler;
	private readonly AdaptiveLightingConfig _config;
	private readonly ILightActuator _actuator;
	private readonly IStatePublisher _publisher;
	private readonly INotifier _notifier;
	private readonly ILoggerFactory _loggerFactory;

	// Passed to each area so its lux staleness survives a Home Assistant restart.
	private readonly IEntityLastSeen? _lastSeen;

	// Passed to the mode brain so a period boundary crossed during a restart is not lost.
	private readonly ILastPeriodStore? _lastPeriod;

	// Keeps the setup notification to once per problem. Null notifies on every start, as it did before there was one.
	private readonly IAreaSetupMemory? _setupMemory;
	private readonly ILogger _logger;

	private readonly BehaviorSubject<HouseState> _house = new(HouseState.Initial);
	private readonly List<AreaController> _areas = [];
	private readonly List<SuspectLight> _sharedLights = [];
	private readonly HashSet<string> _motionSensorUnion = new(StringComparer.OrdinalIgnoreCase);

	// The same sensors split by area id, for a period whose StartsOnMotionAreas names rooms. An area configured
	// without an AreaId cannot be named, so it is absent here.
	private readonly Dictionary<string, IReadOnlyList<string>> _motionSensorsByArea = new(StringComparer.OrdinalIgnoreCase);

	private readonly CompositeDisposable _subscriptions = [];

	private PresenceMonitor? _presence;
	private ModeMonitor? _modes;

	// The single construction site the period authority rests on. Both directions are handed this one object,
	// which has one of its two delegates assigned and never both. Null when no select is configured.
	private PeriodSelectReader? _periodSelect;

	// One latch for the house: every area's calculator reads it and the mode brain writes it, so no two of them can
	// disagree about whether a period that waits for movement has begun.
	private MotionPeriodLatch? _motionPeriods;

	private bool _started;

	// Nothing is wired until Start.
	public LightingOrchestrator(
		IHaContext ha,
		IHaRegistry registry,
		IScheduler scheduler,
		AdaptiveLightingConfig config,
		ILightActuator actuator,
		IStatePublisher publisher,
		INotifier notifier,
		ILoggerFactory loggerFactory,
		IEntityLastSeen? lastSeen = null,
		ILastPeriodStore? lastPeriod = null,
		IAreaSetupMemory? setupMemory = null)
	{
		_lastSeen = lastSeen;
		_lastPeriod = lastPeriod;
		_setupMemory = setupMemory;
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_registry = registry ?? throw new ArgumentNullException(nameof(registry));
		_scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
		_config = config ?? throw new ArgumentNullException(nameof(config));
		_actuator = actuator ?? throw new ArgumentNullException(nameof(actuator));
		_publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
		_notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
		_loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

		_logger = loggerFactory.CreateLogger<LightingOrchestrator>();
	}

	/// <summary>The areas that resolved and are running; an area that failed resolution is absent.</summary>
	public IReadOnlyList<AreaController> Areas => _areas;

	/// <summary>The bulbs more than one room commands, because Home Assistant has not put them in a room of their own.</summary>
	// Settled once in Start and never touched again: which area a light is filed under is a registry fact, and the
	// engine is rebuilt when the registry changes. Advice only; the engine still commands the bulb from both rooms.
	public IReadOnlyList<SuspectLight> SharedLights => _sharedLights;

	/// <summary>The house's one latch, or <c>null</c> before <see cref="Start"/>.</summary>
	// Exposed so a read-only surface asks the same object every area's calculator reads; a fresh one would answer
	// "not begun" for every held period.
	public MotionPeriodLatch? MotionPeriods => _motionPeriods;

	/// <summary>Resolves the areas and starts the engine.</summary>
	// Areas that fail to resolve are skipped and reported in one notification, so an entity renamed in HA costs
	// that room and never the house.
	public void Start()
	{
		if (_started)
			throw new InvalidOperationException("The orchestrator has already been started.");

		_started = true;

		_logger.LogInformation("Starting adaptive lighting: {ConfigName}, {AreaCount} areas configured.",
			_config.ConfigName ?? "(unnamed)", _config.Areas.Count);

		// Built before the areas, because every one of their calculators is handed its read delegate.
		_periodSelect = PeriodSelectReader.For(_ha, _config.Global, _loggerFactory.CreateLogger<PeriodSelectReader>());

		if (_periodSelect is { } select)
			_logger.LogInformation(
				"Period select {Entity}: {Authority} decides the time of day.",
				select.Entity,
				select.ReadPeriod is not null ? "Home Assistant" : "adaptive lighting (the select mirrors the schedule)");

		// Also before the areas: their calculators leave a held period out of the table until this says it began.
		_motionPeriods = MotionPeriodLatch.For(_config.Periods, _config.Global);

		HaAreaRegistry registry = new(_registry);
		AreaEntityResolver resolver = new(
			_ha,
			registry,
			_config.Global,
			_loggerFactory.CreateLogger<AreaEntityResolver>());
		List<AreaSetupFault> failures = [];
		List<ResolvedArea> running = [];

		foreach (AreaConfig areaConfig in _config.Areas)
		{
			if (!resolver.TryResolve(areaConfig, _config.Defaults, out ResolvedArea? resolved, out string? error))
			{
				// Labelling the area in HA is as much the owner's act as Enabled: false, so it is no fault.
				// Checked before the disabled skip, so a disabled area that is also labelled gets this INF line
				// and the choice is visible without DBG logging switched on.
				if (resolver.IsExcludedArea(areaConfig.AreaId))
				{
					_logger.LogInformation(
						"Area {Area} carries the label '{Label}' in Home Assistant and is treated as not there.",
						areaConfig.DisplayName, _config.Global.ExcludeLabel);
					continue;
				}

				// An area switched off was never going to be commanded, so failing to resolve is no fault: a
				// whole-house area holding no lights would otherwise notify on every start.
				if (!areaConfig.Effective(_config.Defaults).Enabled)
				{
					// Every resolver error ends its sentence, so no period before "Not".
					_logger.LogDebug(
						"Area {Area} is switched off and does not resolve: {Error} Not counted as a fault.",
						areaConfig.DisplayName, error);
					continue;
				}

				_logger.LogError("Area {Area} could not be set up: {Error}", areaConfig.DisplayName, error);

				// Keyed on the area id where there is one, so renaming a room in the document does not re-report it.
				failures.Add(new AreaSetupFault(
					areaConfig.AreaId is { Length: > 0 } key ? key : areaConfig.DisplayName,
					areaConfig.DisplayName,
					error ?? "It could not be set up."));

				continue;
			}

			// An option resetting on presence with no explicit sensor list resets on any of these. Must be complete
			// before StartHouseMonitors builds the mode monitor.
			_motionSensorUnion.UnionWith(resolved!.MotionSensors);

			if (areaConfig.AreaId is { Length: > 0 } areaId && areaId.Trim() is { Length: > 0 } trimmed)
				_motionSensorsByArea[trimmed] = resolved!.MotionSensors;

			running.Add(resolved!);
			_areas.Add(BuildArea(resolved!, areaConfig));
		}

		ReportSharedLights(running, resolver, registry);
		StartHouseMonitors();

		foreach (AreaController area in _areas)
			area.Start();

		PublishHouseState(opening: true);
		ReportFailures(failures);
	}

	/// <summary>Every bulb an area will actually put a command on, named the way Home Assistant names it.</summary>
	// Down to the leaves: two rooms sharing a bulb through two different groups hold no id in common.
	private RoomUnderReview BulbsOf(ResolvedArea area, AreaEntityResolver resolver)
	{
		List<LightUnderReview> bulbs = [];
		HashSet<string> seen = new(StringComparer.Ordinal);

		foreach (string entityId in area.Lights)
			foreach (string bulb in resolver.LeavesOf(entityId))
				if (seen.Add(bulb))
					bulbs.Add(new LightUnderReview(bulb, _ha.AttrString(bulb, FriendlyNameAttribute) ?? bulb));

		return new RoomUnderReview(area.Name, bulbs);
	}

	/// <summary>Finds the bulbs two rooms will both be commanding, and says so once for each.</summary>
	// Runs after every room is resolved and built, so a failure here costs the advice and never the lighting.
	private void ReportSharedLights(IReadOnlyList<ResolvedArea> running, AreaEntityResolver resolver, IAreaRegistry registry)
	{
		try
		{
			// Swept lazily and once: the ordinary house has no shared bulb to spend a whole-house pass on.
			HashSet<string>? assigned = null;

			bool HasOwnArea(string entityId) => (assigned ??= EntitiesWithAnArea(registry)).Contains(entityId);

			_sharedLights.AddRange(LightAudit.SharedBetweenRooms(
				[.. running.Select(area => BulbsOf(area, resolver))], HasOwnArea));
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			_logger.LogDebug(exception, "Could not check whether any light is commanded by two rooms at once.");
			return;
		}

		foreach (SuspectLight shared in _sharedLights)
			_logger.LogWarning(
				"Light '{Light}' ({EntityId}) is {Reason}. Until then both rooms set its brightness, and whichever "
				+ "empties first switches it off on somebody standing in the other.",
				shared.Name, shared.EntityId, shared.Reason);
	}

	// Every entity Home Assistant has actually filed under an area, in one pass over the house.
	private static HashSet<string> EntitiesWithAnArea(IAreaRegistry registry)
	{
		HashSet<string> assigned = new(StringComparer.Ordinal);

		foreach (string areaId in registry.AreaIds)
			assigned.UnionWith(registry.EntitiesInArea(areaId));

		return assigned;
	}

	private AreaController BuildArea(ResolvedArea resolved, AreaConfig config)
	{
		// One calculator per area: the periods are house-wide but the sun entity is an area setting, and the wrong
		// sun places every boundary wrong.
		CircadianCalculator circadian = new(
			_config.Periods,
			_config.Global,
			() => ReadSunTimes(resolved.Settings.SunEntity),
			config.Levels,
			// Null unless Home Assistant owns the periods.
			_periodSelect?.ReadPeriod,
			_motionPeriods!.IsHeldBack);

		// The calculator stays pure and the logging happens here. Parse failures are already known, so drain
		// DroppedPeriods; sun-anchor failures surface per day through the event.
		circadian.PeriodDropped += drop => LogDroppedPeriod(resolved.Name, drop);
		foreach (DroppedPeriod drop in circadian.DroppedPeriods)
			LogDroppedPeriod(resolved.Name, drop);

		return new AreaController(
			_ha, _scheduler, resolved, _config.Global, _config.Periods, circadian,
			_actuator, _publisher, _house, _loggerFactory, config.AreaId, _lastSeen,
			SunMoved(resolved.Settings.SunEntity));
	}

	private void LogDroppedPeriod(string areaName, DroppedPeriod drop)
	{
		string why = drop.Reason switch
		{
			PeriodDropReason.Unparseable => "its Start could not be parsed as a clock time or a sun anchor",
			PeriodDropReason.Unresolvable => "its sun-anchored Start has no sun time to resolve against today",
			_ => "it could not be placed"
		};

		_logger.LogWarning(
			"Area {Area}: circadian period '{Period}' (Start '{Start}') is dropped from the table because {Why}. "
			+ "The remaining periods still cover the day by wrapping, so a boundary that should exist may be missing. "
			+ "Check this period's Start if an area lands in the wrong period.",
			areaName, drop.PeriodName, drop.Start, why);
	}

	private void StartHouseMonitors()
	{
		_presence = new PresenceMonitor(_ha, _scheduler, _config.Global, _loggerFactory.CreateLogger<PresenceMonitor>());
		_modes = new ModeMonitor(
			_ha,
			_config.Global,
			_loggerFactory.CreateLogger<ModeMonitor>(),
			_scheduler,
			_config.Periods,
			() => ReadSunTimes(_config.Defaults.SunEntity),
			_motionSensorUnion,
			_lastPeriod,
			// The same reader the calculators got, so the mode brain acts on the boundary the rooms are lit for.
			_periodSelect,
			_motionSensorsByArea,
			// The same latch, for the same reason.
			_motionPeriods,
			sunMoved: SunMoved(_config.Defaults.SunEntity));

		_subscriptions.Add(_presence.Events.SubscribeSafe((PresenceEvent _) => PublishHouseState(), _logger));
		_subscriptions.Add(_modes.Changed.SubscribeSafe((Unit _) => PublishHouseState(), _logger));

		_presence.Start();
		_modes.Start();
	}

	/// <summary>Composes the house-wide state and hands it to the areas, unless it says the same as the standing one.</summary>
	// The opening publication goes out even when it matches the seed the stream was created on: each area waits
	// for it to know which mode it found at start-up as opposed to saw change.
	private void PublishHouseState(bool opening = false)
	{
		HouseState previous = _house.Value;
		HouseState state = new(
			_presence?.IsAnyoneHome ?? true,
			_modes?.ActiveKind ?? ModeKind.Normal,
			_modes?.KillSwitchActive ?? false)
		{
			ModeValue = _modes?.CurrentModeValue,
			ActiveScene = _modes?.ActiveScene,
			Forced = _modes?.Forced
		};

		if (state == previous && !opening)
			return;

		// Applied once on entry, never re-asserted. The areas pause themselves; this only fires the scene.
		if (!string.Equals(previous.ActiveScene, state.ActiveScene, StringComparison.Ordinal)
			&& state.ActiveScene is { Length: > 0 } scene)
			_actuator.ActivateScene(scene);

		// The forcing clause repeats ModeMonitor's, because this is the line that says the house went Away.
		if (state.Forced is { } forced)
			_logger.LogInformation("House is now {Mode} (kill switch {KillSwitch}). {ForcedMode}",
				state.Mode, state.KillSwitchActive ? "active" : "inactive", forced.Describe());
		else
			_logger.LogInformation("House is now {Mode} (kill switch {KillSwitch}).",
				state.Mode, state.KillSwitchActive ? "active" : "inactive");

		_house.OnNext(state);
	}

	/// <summary>Raises one card naming every room that is switched on but could not be set up, the first time each is seen.</summary>
	// Recorded even when nothing failed: forgetting a room that now resolves is what lets a later regression notify
	// again. The card names every standing problem, not only the new one, because it replaces the previous card.
	private void ReportFailures(IReadOnlyList<AreaSetupFault> failures)
	{
		IReadOnlyList<AreaSetupFault> unreported = _setupMemory?.Record(failures) ?? failures;

		if (unreported.Count == 0)
			return;

		string body = string.Join("", failures.Select(failure => $"<li>{failure.Area}: {failure.Problem}</li>"));
		_notifier.Notify(
			SetupFailureTitle,
			$"{failures.Count} of {_config.ManagedAreaCount} rooms are switched on but could not be set up, so they are "
			+ $"not being managed:<ul>{body}</ul>");
	}

	/// <summary>Reads the day's sun times off the sun entity.</summary>
	// Home Assistant publishes the next rising and setting, so after today's sunrise the value names tomorrow's.
	// Only its time of day is used, and sunrise moves by minutes a day.
	private SunTimes ReadSunTimes(string sunEntityId)
	{
		EntityState? state = _ha.GetState(sunEntityId);
		if (state is null)
		{
			_logger.LogWarning("Sun entity {EntityId} is unknown; sun-anchored periods cannot be placed.", sunEntityId);
			return SunTimes.Unknown;
		}

		return new SunTimes(ReadTime(state, NextRisingAttribute), ReadTime(state, NextSettingAttribute));

		// UTC ISO-8601 in, local out: the period table is written in wall-clock terms.
		static TimeOnly? ReadTime(EntityState state, string attribute) =>
			state.AttrDateTimeOffset(attribute) is { } parsed
				? TimeOnly.FromDateTime(parsed.ToLocalTime().DateTime)
				: null;
	}

	/// <summary>Fires when a sun entity's rising or setting moves, or <c>null</c> for a house that names no sun entity.</summary>
	// Only the two anchors the table resolves against: the same entity's elevation and azimuth move every half
	// minute and are no boundaries. The read is off the change's own payload and cannot throw, so a sun reporting
	// nonsense costs the subscription nothing.
	internal IObservable<Unit>? SunMoved(string? sunEntityId)
	{
		if (sunEntityId is not { Length: > 0 })
			return null;

		return _ha.Entity(sunEntityId)
			.StateAllChanges()
			.Select(change => SunAnchorsOf(change.New))
			.DistinctUntilChanged()
			.Select(_ => Unit.Default);
	}

	/// <summary>The two instants a sun-anchored boundary can move with; elevation and azimuth are not among them.</summary>
	internal static (DateTimeOffset? Rising, DateTimeOffset? Setting) SunAnchorsOf(EntityState? state) =>
		(state.AttrDateTimeOffset(NextRisingAttribute), state.AttrDateTimeOffset(NextSettingAttribute));

	/// <summary>Tears the engine down, leaving the lights as they are.</summary>
	public void Dispose()
	{
		_subscriptions.Dispose();

		foreach (AreaController area in _areas)
			area.Dispose();

		_areas.Clear();
		_presence?.Dispose();
		_modes?.Dispose();
		_house.Dispose();
	}
}
