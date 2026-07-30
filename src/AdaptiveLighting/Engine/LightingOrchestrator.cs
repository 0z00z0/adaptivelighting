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

	/// <summary>
	///     The period select, built once here and handed to both directions that can use it.
	/// </summary>
	/// <remarks>
	///     <b>This is the single construction site the authority rule rests on.</b> The calculators are handed
	///     <see cref="PeriodSelectReader.ReadPeriod"/> and the mode brain is handed the whole reader; exactly one of
	///     the reader's two delegates is non-null, so "the engine follows the select" and "the engine writes the
	///     select" are not two flags that could both come out true — they are two branches of one object built from
	///     one enum. <c>null</c> when no select is configured, which is every house today.
	/// </remarks>
	private PeriodSelectReader? _periodSelect;

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
	/// <param name="lastSeen">
	///     Tracks when each entity was genuinely last heard from, across both a Home Assistant restart and an
	///     engine restart. Optional: when absent the lux staleness rule falls back to Home Assistant's own
	///     timestamps, which reset on its restart and cannot tell a dead sensor from a quiet one.
	/// </param>
	/// <param name="lastPeriod">
	///     Where the circadian period the engine is running in is written down, so a restart can tell whether a
	///     boundary went by while it was stopped. Optional: without one a period's <c>SetsMode</c> is applied only
	///     at a boundary the engine was running to see.
	/// </param>
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
	///     <para>
	///         Settled once, in <see cref="Start"/>, and never touched again. This is a fact about the Home
	///         Assistant registry, not about any tick: nothing a room does from one minute to the next can change
	///         which area a light is filed under, so re-deciding it on the clock would be per-tick work for an
	///         answer that cannot have moved. It changes when somebody edits the registry, and the engine is rebuilt
	///         when they do.
	///     </para>
	///     <para>
	///         Held as advice rather than acted on. <see cref="LightAudit.SharedBetweenRooms"/> records why the
	///         engine goes on commanding the bulb from both rooms; this is the list the household is shown.
	///     </para>
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

			// The union of every area's motion sensors: an option that resets on presence with no explicit sensor
			// list resets on any of these (09 owner refinement). Collected before the mode monitor is built.
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

	/// <summary>
	///     Every bulb an area will actually put a command on, named the way Home Assistant names it.
	/// </summary>
	/// <remarks>
	///     The resolver's own list is what the area <i>holds</i>; a light group in it stands for the bulbs inside,
	///     and it is the bulbs that get commanded. Two rooms sharing a bulb through two different groups hold
	///     nothing in common, so following each id down to its leaves is the only way the sharing is visible at all
	///     — see <see cref="LightAudit.SharedBetweenRooms"/>.
	/// </remarks>
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

	/// <summary>
	///     Finds the bulbs two rooms will both be commanding, and says so once for each.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Once per start, never per tick: which area a light belongs to is a registry fact, and the engine is
	///         rebuilt whenever the household changes one. So the warning is loud rather than frequent — a line per
	///         bulb in the log a household reads when something is wrong, and the same advice held on
	///         <see cref="SharedLights"/> for the surfaces that render it.
	///     </para>
	///     <para>
	///         Every failure here is swallowed on purpose. This is advice about the house, arriving after every room
	///         has already been resolved and built; a registry that stopped answering between the two must cost the
	///         advice and not the lighting.
	///     </para>
	/// </remarks>
	/// <param name="running">Every room that resolved and is about to be commanded.</param>
	/// <param name="resolver">Follows each room's lights down to the bulbs they stand for.</param>
	/// <param name="registry">Where "has Home Assistant put this light in a room?" is answered.</param>
	private void ReportSharedLights(IReadOnlyList<ResolvedArea> running, AreaEntityResolver resolver, IAreaRegistry registry)
	{
		try
		{
			// Swept on first use and once. It is a pass over every area in the house, and the ordinary house has no
			// bulb reaching two rooms to spend one on — so the audit asks only about the bulbs that got that far.
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

	/// <summary>
	///     Builds one area's controller.
	/// </summary>
	/// <param name="resolved">The area with every entity reference turned into a concrete id.</param>
	/// <param name="config">
	///     The document row behind it, for the two things the resolver has no business resolving: the registry area
	///     id, and the levels this room runs instead of the schedule.
	/// </param>
	private AreaController BuildArea(ResolvedArea resolved, AreaConfig config)
	{
		// One calculator per area: the periods are house-wide but the sun entity is an area setting, and a
		// calculator that reads the wrong sun would place every boundary wrong. That it is already per-area is
		// what makes it the right home for the room's own levels too — see CircadianCalculator's remarks.
		CircadianCalculator circadian = new(
			_config.Periods,
			_config.Global,
			() => ReadSunTimes(resolved.Settings.SunEntity),
			config.Levels,
			// Null unless Home Assistant owns the periods, so a house without the select — every house today —
			// builds the calculator it always built rather than one carrying an override that answers null.
			_periodSelect?.ReadPeriod);

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
			_motionSensorUnion,
			_lastPeriod,
			// The same reader the calculators got. Its own calculator therefore follows the same override, so the
			// period boundary the mode brain acts on is the one the rooms are lit for.
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

		// Scene apply on entry (09 §3.3): once per entry, never re-asserted. The areas' pause is their own doing —
		// GoAway skips the sweep and Guest enters SceneHold — this only applies the scene the mode names.
		if (!string.Equals(previous.ActiveScene, state.ActiveScene, StringComparison.Ordinal)
			&& state.ActiveScene is { Length: > 0 } scene)
			_actuator.ActivateScene(scene);

		// The forcing clause is on this line as well as on ModeMonitor's own: this is the line that says the house
		// went Away, and a reader who finds it has no reason to go looking for a second one that explains why.
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
