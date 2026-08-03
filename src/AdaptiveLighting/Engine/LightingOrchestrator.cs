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

/// <summary>
///     The engine's composition root: resolves the configured areas, builds a controller for each, and owns the
///     house-wide state every area reads.
/// </summary>
/// <remarks>
///     One orchestrator per host, fanning out to one controller per area. Not a NetDaemon app:
///     <c>AdaptiveLighting</c> is never scanned by <c>AddAppsFromAssembly</c>, so a <c>[NetDaemonApp]</c>
///     attribute here would be inert. The per-project bootstrap constructs this.
/// </remarks>
public sealed class LightingOrchestrator : IDisposable
{
	private const string NextRisingAttribute = "next_rising";
	private const string NextSettingAttribute = "next_setting";
	private const string FriendlyNameAttribute = "friendly_name";

	private readonly IHaContext _ha;
	private readonly IHaRegistry _registry;
	private readonly IScheduler _scheduler;
	private readonly AdaptiveLightingConfig _config;
	private readonly ILightActuator _actuator;
	private readonly IStatePublisher _publisher;
	private readonly INotifier _notifier;
	private readonly ILoggerFactory _loggerFactory;

	/// <summary>Passed to each area so its lux staleness survives a Home Assistant restart.</summary>
	private readonly IEntityLastSeen? _lastSeen;

	/// <summary>Passed to the mode brain so a period boundary crossed during a restart is not lost.</summary>
	private readonly ILastPeriodStore? _lastPeriod;
	private readonly ILogger _logger;

	private readonly BehaviorSubject<HouseState> _house = new(HouseState.Initial);
	private readonly List<AreaController> _areas = [];
	private readonly List<SuspectLight> _sharedLights = [];
	private readonly HashSet<string> _motionSensorUnion = new(StringComparer.OrdinalIgnoreCase);
	private readonly CompositeDisposable _subscriptions = [];

	private PresenceMonitor? _presence;
	private ModeMonitor? _modes;

	// The single construction site the period authority rests on. Both directions are handed this one object,
	// which has exactly one of its two delegates assigned. Null when no select is configured.
	private PeriodSelectReader? _periodSelect;

	private bool _started;

	/// <summary>Creates an orchestrator. Nothing is wired until <see cref="Start"/>.</summary>
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
		ILastPeriodStore? lastPeriod = null)
	{
		_lastSeen = lastSeen;
		_lastPeriod = lastPeriod;
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

	/// <summary>The areas that resolved and are running. Areas that failed resolution are absent.</summary>
	public IReadOnlyList<AreaController> Areas => _areas;

	/// <summary>
	///     The bulbs more than one room commands, because Home Assistant has not put them in a room of their own.
	///     Empty in the ordinary house.
	/// </summary>
	/// <remarks>
	///     Settled once in <see cref="Start"/> and never touched again: which area a light is filed under is a
	///     registry fact, and the engine is rebuilt when the registry changes. Advice only; the engine still
	///     commands the bulb from both rooms.
	/// </remarks>
	public IReadOnlyList<SuspectLight> SharedLights => _sharedLights;

	/// <summary>
	///     Resolves the areas and starts the engine. Areas that fail to resolve are skipped and reported in one
	///     notification: an entity renamed in HA costs that room, not the house.
	/// </summary>
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

		HaAreaRegistry registry = new(_registry);
		AreaEntityResolver resolver = new(
			_ha,
			registry,
			_config.Global,
			_loggerFactory.CreateLogger<AreaEntityResolver>());
		List<string> failures = new();
		List<ResolvedArea> running = [];

		foreach (AreaConfig areaConfig in _config.Areas)
		{
			if (!resolver.TryResolve(areaConfig, _config.Defaults, out ResolvedArea? resolved, out string? error))
			{
				_logger.LogError("Area {Area} disabled: {Error}", areaConfig.DisplayName, error);
				failures.Add($"{areaConfig.DisplayName}: {error}");
				continue;
			}

			// An option resetting on presence with no explicit sensor list resets on any of these. Must be complete
			// before StartHouseMonitors builds the mode monitor.
			_motionSensorUnion.UnionWith(resolved!.MotionSensors);
			running.Add(resolved!);
			_areas.Add(BuildArea(resolved!, areaConfig));
		}

		ReportSharedLights(running, resolver, registry);
		StartHouseMonitors();

		foreach (AreaController area in _areas)
			area.Start();

		PublishHouseState();
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
	/// <remarks>
	///     Runs after every room is resolved and built, so any failure here costs the advice and not the lighting.
	/// </remarks>
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

	/// <summary>Every entity Home Assistant has actually filed under an area. One pass over the house.</summary>
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
			_periodSelect?.ReadPeriod);

		// The calculator stays pure and the logging happens here. Parse failures are already known, so drain
		// DroppedPeriods; sun-anchor failures surface per day through the event.
		circadian.PeriodDropped += drop => LogDroppedPeriod(resolved.Name, drop);
		foreach (DroppedPeriod drop in circadian.DroppedPeriods)
			LogDroppedPeriod(resolved.Name, drop);

		return new AreaController(
			_ha, _scheduler, resolved, _config.Global, _config.Periods, circadian,
			_actuator, _publisher, _house, _loggerFactory, config.AreaId, _lastSeen);
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
			_periodSelect);

		_subscriptions.Add(_presence.Events.SubscribeSafe((PresenceEvent _) => PublishHouseState(), _logger));
		_subscriptions.Add(_modes.Changed.SubscribeSafe((Unit _) => PublishHouseState(), _logger));

		_presence.Start();
		_modes.Start();
	}

	private void PublishHouseState()
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

		if (state == previous)
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

	private void ReportFailures(List<string> failures)
	{
		if (failures.Count == 0)
			return;

		string body = string.Join("", failures.Select(failure => $"<li>{failure}</li>"));
		_notifier.Notify(
			"Adaptive lighting: areas disabled",
			$"{failures.Count} of {_config.Areas.Count} areas could not be resolved and are not being managed:<ul>{body}</ul>");
	}

	/// <summary>Reads the day's sun times off the sun entity.</summary>
	/// <remarks>
	///     Home Assistant publishes the next rising and setting, so after today's sunrise the value names
	///     tomorrow's. Only its time of day is used, and sunrise moves by minutes a day.
	/// </remarks>
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

	/// <summary>Tears the engine down. Lights are left as they are; the household keeps whatever it has.</summary>
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
