using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Subjects;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Ha;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>
///     The engine's composition root: resolves the configured areas, builds a controller for each, and owns the
///     house-wide state every area reads.
/// </summary>
/// <remarks>
///     <para>
///         One orchestrator per host, fanning out to one controller per area. The app model creates exactly one
///         instance per <c>[NetDaemonApp]</c> class, so "one app per room" is not on the table — and would be the
///         worse design anyway, since presence, mode and the registry snapshot are all shared.
///     </para>
///     <para>
///         Not a NetDaemon app itself, and deliberately: <c>AdaptiveLighting</c> is never scanned by
///         <c>AddAppsFromAssembly</c>, so a <c>[NetDaemonApp]</c> attribute here would be silently inert. The
///         per-project bootstrap constructs this.
///     </para>
/// </remarks>
public sealed class LightingOrchestrator : IDisposable
{
	private const string NextRisingAttribute = "next_rising";
	private const string NextSettingAttribute = "next_setting";

	private readonly IHaContext _ha;
	private readonly IHaRegistry _registry;
	private readonly IScheduler _scheduler;
	private readonly AdaptiveLightingConfig _config;
	private readonly ILightActuator _actuator;
	private readonly IStatePublisher _publisher;
	private readonly INotifier _notifier;
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger _logger;

	private readonly BehaviorSubject<HouseState> _house = new(HouseState.Initial);
	private readonly List<AreaController> _areas = [];
	private readonly HashSet<string> _motionSensorUnion = new(StringComparer.OrdinalIgnoreCase);
	private readonly CompositeDisposable _subscriptions = [];

	private PresenceMonitor? _presence;
	private ModeMonitor? _modes;
	private bool _started;

	/// <summary>Creates an orchestrator. Nothing is wired until <see cref="Start"/>.</summary>
	/// <param name="ha">The HA context every area reads from.</param>
	/// <param name="registry">Source of areas and labels for discovery.</param>
	/// <param name="scheduler">The engine's only clock.</param>
	/// <param name="config">The validated configuration document.</param>
	/// <param name="actuator">Where light commands go.</param>
	/// <param name="publisher">Where area snapshots go.</param>
	/// <param name="notifier">How area failures reach a human.</param>
	/// <param name="loggerFactory">Builds the loggers for every part of the engine.</param>
	public LightingOrchestrator(
		IHaContext ha,
		IHaRegistry registry,
		IScheduler scheduler,
		AdaptiveLightingConfig config,
		ILightActuator actuator,
		IStatePublisher publisher,
		INotifier notifier,
		ILoggerFactory loggerFactory)
	{
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

		AreaEntityResolver resolver = new(
			_ha,
			new HaAreaRegistry(_registry),
			_config.Global,
			_loggerFactory.CreateLogger<AreaEntityResolver>());
		List<string> failures = new();

		foreach (AreaConfig areaConfig in _config.Areas)
		{
			if (!resolver.TryResolve(areaConfig, _config.Defaults, out ResolvedArea? resolved, out string? error))
			{
				_logger.LogError("Area {Area} disabled: {Error}", areaConfig.DisplayName, error);
				failures.Add($"{areaConfig.DisplayName}: {error}");
				continue;
			}

			// The union of every area's motion sensors: an option that resets on presence with no explicit sensor
			// list resets on any of these (09 owner refinement). Collected before the mode monitor is built.
			_motionSensorUnion.UnionWith(resolved!.MotionSensors);
			_areas.Add(BuildArea(resolved!));
		}

		StartHouseMonitors();

		foreach (AreaController area in _areas)
			area.Start();

		PublishHouseState();
		ReportFailures(failures);
	}

	private AreaController BuildArea(ResolvedArea resolved)
	{
		// One calculator per area: the periods are house-wide but the sun entity is an area setting, and a
		// calculator that reads the wrong sun would place every boundary wrong.
		CircadianCalculator circadian = new(
			_config.Periods,
			_config.Global,
			() => ReadSunTimes(resolved.Settings.SunEntity));

		// Surface any period the calculator cannot use, so a dropped boundary is a logged warning rather than a
		// silent hole the table wraps over — the failure behind an area "showing night at 04:16" when its
		// sun-anchored morning could not be placed. The calculator stays pure and does the logging here, once
		// each: parse failures are known now (read from DroppedPeriods); sun-anchor failures surface per day, on
		// the event, deduplicated so a persistently-unresolvable period logs once rather than every tick.
		circadian.PeriodDropped += drop => LogDroppedPeriod(resolved.Name, drop);
		foreach (DroppedPeriod drop in circadian.DroppedPeriods)
			LogDroppedPeriod(resolved.Name, drop);

		return new AreaController(
			_ha, _scheduler, resolved, _config.Global, _config.Periods, circadian,
			_actuator, _publisher, _house, _loggerFactory);
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
			+ "The remaining periods still cover the day by wrapping, so a boundary that should exist may be missing — "
			+ "check this period's Start if an area lands in the wrong period.",
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
			_motionSensorUnion);

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
			ActiveScene = _modes?.ActiveScene
		};

		if (state == previous)
			return;

		// Scene apply on entry (09 §3.3): once per entry, never re-asserted. The areas' pause is their own doing —
		// GoAway skips the sweep and Guest enters SceneHold — this only applies the scene the mode names.
		if (!string.Equals(previous.ActiveScene, state.ActiveScene, StringComparison.Ordinal)
			&& state.ActiveScene is { Length: > 0 } scene)
			_actuator.ActivateScene(scene);

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

	/// <summary>
	///     Reads the day's sun times off the sun entity.
	/// </summary>
	/// <remarks>
	///     HA publishes the <i>next</i> rising and setting, so after today's sunrise the value names tomorrow's.
	///     Its time of day is what a period boundary needs, and sunrise moves by minutes a day — an error far
	///     below anything a lighting boundary can notice.
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

		// HA publishes these as UTC ISO-8601. AttrDateTimeOffset parses them the same way (invariant culture,
		// AssumeUniversal|AdjustToUniversal); the period table is written in wall-clock terms, so the boundary
		// has to land in local time.
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
