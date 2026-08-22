using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Ha;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     One area, as a picker offers it. The counts are the resolver's, after discovery and the ghost filter, never
///     the registry's row count, which says nothing about lighting and includes disabled rows.
/// </summary>
/// <param name="Id">The registry area id: the slug the config stores.</param>
/// <param name="LightCount">Lights discovery yields here, groups de-duplicated and ghosts dropped.</param>
/// <param name="LuxCount">Illuminance sensors discovery yields here. More than one makes an area ambiguous.</param>
public sealed record AreaOption(string Id, string Name, int LightCount, int MotionCount, int LuxCount)
{
	public string Label => $"{NameAndId} — {Counts}";

	public string Counts => $"{Pluralise(LightCount, "light")}, {MotionCount} motion, {LuxCount} light-level";

	/// <summary>An area with no lights is the common, silent mistake.</summary>
	public bool HasLights => LightCount > 0;

	/// <summary>Whether discovery would refuse this area for having more than one lux sensor to choose between.</summary>
	public bool LuxIsAmbiguous => LuxCount > 1;

	private string NameAndId => string.Equals(Name, Id, StringComparison.Ordinal) ? Id : $"{Name} ({Id})";

	private static string Pluralise(int count, string noun) => $"{count} {noun}{(count == 1 ? string.Empty : "s")}";
}

/// <param name="LuxSensors">Every illuminance sensor discovery finds in the area, not just a chosen one.</param>
public sealed record AreaEntities(
	IReadOnlyList<EntityOption> Lights,
	IReadOnlyList<EntityOption> MotionSensors,
	IReadOnlyList<EntityOption> LuxSensors)
{
	public static AreaEntities Empty { get; } = new([], [], []);
}

/// <param name="FriendlyName">Home Assistant's <c>friendly_name</c>, or the id when it has none.</param>
public sealed record EntityOption(string EntityId, string FriendlyName, string? AreaName)
{
	public string Label => AreaName is { Length: > 0 } area
		? $"{FriendlyName} — {area} ({EntityId})"
		: $"{FriendlyName} ({EntityId})";
}

/// <param name="Name">The display name, and the value a label field stores. Labels are stored by name, not by id.</param>
public sealed record LabelOption(string Id, string Name);

/// <param name="Error">Why discovery fails, in the resolver's own words.</param>
public sealed record AreaPreview(ResolvedArea? Resolved, string? Error);

/// <summary>A room's lights: the ones the engine will drive, and the ones the room holds at all.</summary>
/// <remarks>
///     <see cref="Commanded"/> is what is judged, counted and named. <see cref="InTheRoom"/> is the sibling
///     check's context and is strictly wider: the colour-channel rule needs a parent lamp that group preference
///     has already removed from the commanded list.
/// </remarks>
/// <param name="Commanded">The lights the engine will drive, in the resolver's order.</param>
/// <param name="InTheRoom">
///     Every <c>light.*</c> entity Home Assistant lists in the room, plus the commanded ids. Never narrower than
///     <see cref="Commanded"/>, so a room with hand-picked lights and no area id is judged as before.
/// </param>
public sealed record RoomLights(IReadOnlyList<LightUnderReview> Commanded, IReadOnlySet<string> InTheRoom)
{
	public static RoomLights None { get; } = new([], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
///     Turns the Home Assistant registry into things a person can pick from, and answers "what would discovery do
///     with this area?" live.
/// </summary>
/// <remarks>
///     <see cref="PreviewArea"/> runs <see cref="AreaEntityResolver"/>, the same class the engine runs at
///     start-up, so a preview is the engine's answer and no lookalike of it. Scoped, as <see cref="IHaContext"/>
///     is: one per Blazor circuit, with the registry read live, never snapshotted.
/// </remarks>
public sealed class HaCatalog
{
	private const string FriendlyNameAttribute = "friendly_name";
	private const string DeviceClassAttribute = "device_class";
	private const string OptionsAttribute = "options";
	private const string SunEntityId = "sun.sun";
	private const string NextRisingAttribute = "next_rising";
	private const string NextSettingAttribute = "next_setting";

	private readonly IHaContext _ha;
	private readonly IHaRegistry _registry;
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger<HaCatalog> _logger;
	private readonly HaAreaRegistry _areas;

	/// <summary>Discovery answers for this load, by area id.</summary>
	/// <remarks>
	///     Only successful answers go in here. A discovery that threw is Home Assistant declining to answer, and
	///     caching it turns start-up into a standing "0 lights, 0 motion, 0 lux" for the rest of the circuit.
	/// </remarks>
	private readonly Dictionary<string, AreaDiscovery> _discoveries = new(StringComparer.Ordinal);

	/// <summary>The globals the cached discoveries were computed with, so an edit to them cannot go unnoticed.</summary>
	private string? _discoverySignature;

	public HaCatalog(IHaContext ha, IHaRegistry registry, ILoggerFactory loggerFactory)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_registry = registry ?? throw new ArgumentNullException(nameof(registry));
		_loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
		_logger = loggerFactory.CreateLogger<HaCatalog>();
		_areas = new HaAreaRegistry(_registry);
	}

	/// <summary>The area registry as the engine sees it, for what a page needs beyond pickers: floors, chiefly.</summary>
	public IAreaRegistry AreaRegistry => _areas;

	/// <summary>Whether Home Assistant answered the last question asked of it.</summary>
	/// <remarks>
	///     Kestrel serves the moment the process is up; NetDaemon connects afterwards and its state cache throws
	///     until it has. Every page here is reachable in that window, so <c>false</c> is a state to render, not a
	///     fault: the pickers empty and the editor falls back to free text.
	/// </remarks>
	public bool IsHomeAssistantReady { get; private set; } = true;

	/// <summary>Every area the registry knows, each labelled with what a room there would resolve to.</summary>
	/// <returns>The areas, ordered by display name. Empty when Home Assistant has not answered.</returns>
	public IReadOnlyList<AreaOption> Areas(GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		try
		{
			return [.. _registry.Areas
				.Where(area => area.Id is { Length: > 0 })
				.Select(area => Option(area.Id!, area.Name ?? area.Id!, global))
				.OrderBy(area => area.Name, StringComparer.CurrentCulture)];
		}
		catch (InvalidOperationException exception)
		{
			IsHomeAssistantReady = false;
			_logger.LogDebug(exception, "The area registry is not available yet.");
			return [];
		}
	}

	/// <summary>
	///     The lights, motion sensors and illuminance sensors <paramref name="areaId"/> yields, for scoping the
	///     pickers to the area the room is about.
	/// </summary>
	/// <remarks>Discovery's own answers, so a ghost the engine excludes is not offered here either.</remarks>
	/// <param name="areaId">The area to scope to. <c>null</c> or blank yields <see cref="AreaEntities.Empty"/>.</param>
	public AreaEntities EntitiesInArea(string? areaId, GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		if (string.IsNullOrWhiteSpace(areaId))
			return AreaEntities.Empty;

		AreaDiscovery discovered = Discover(areaId, global);

		return new AreaEntities(
			Describe(discovered.Lights),
			Describe(discovered.MotionSensors),
			Describe(discovered.LuxSensors));
	}

	/// <summary>What the area holds besides what discovery settled on, for the fold that overrules discovery.</summary>
	/// <remarks>
	///     Derived by subtraction, never by re-deciding: group membership, device identity and the label rules stay
	///     in <see cref="AreaEntityResolver"/>, and this removes whatever discovery already claimed.
	/// </remarks>
	/// <param name="areaId">The area to look in. <c>null</c> or blank yields <see cref="AreaEntities.Empty"/>.</param>
	/// <param name="labelledOnly">
	///     Whether to offer only what carries <see cref="GlobalConfig.IncludeLabel"/>. A house that configured no
	///     include label is offered everything either way. The engine's own rule.
	/// </param>
	public AreaEntities OtherEntitiesInArea(string? areaId, GlobalConfig global, bool labelledOnly)
	{
		ArgumentNullException.ThrowIfNull(global);

		if (string.IsNullOrWhiteSpace(areaId))
			return AreaEntities.Empty;

		AreaDiscovery discovered = Discover(areaId, global);
		IReadOnlyList<string> inArea = EntitiesInAreaOrNone(areaId);

		bool Offered(string entityId) =>
			!labelledOnly
			|| global.IncludeLabel is not { Length: > 0 } include
			|| _areas.LabelsOf(entityId).Contains(include, StringComparer.OrdinalIgnoreCase);

		IReadOnlyList<EntityOption> Rest(IReadOnlyList<string> claimed, Func<string, bool> qualifies)
		{
			HashSet<string> taken = new(claimed, StringComparer.Ordinal);

			return Describe(
			[
				.. inArea
					.Where(qualifies)
					.Where(entityId => !taken.Contains(entityId))
					.Where(Offered)
					.Distinct(StringComparer.Ordinal)
			]);
		}

		return new AreaEntities(
			Rest(discovered.Lights, entityId => string.Equals(entityId.Domain(), "light", StringComparison.Ordinal)),
			Rest(discovered.MotionSensors, entityId => string.Equals(entityId.Domain(), "binary_sensor", StringComparison.Ordinal)),
			Rest(discovered.LuxSensors, entityId =>
				string.Equals(entityId.Domain(), "sensor", StringComparison.Ordinal)
				&& string.Equals(DeviceClassOf(entityId), global.IlluminanceDeviceClass, StringComparison.OrdinalIgnoreCase)));
	}

	/// <summary>How many lights discovery finds in <paramref name="areaId"/>, without naming them.</summary>
	/// <remarks>
	///     The cached count and nothing else. <see cref="EntitiesInArea"/> answers the same question but reads a
	///     friendly name and an area per entity, which the dashboard's one-second tick would pay for every second.
	/// </remarks>
	/// <param name="areaId">The area to count in. Blank or <c>null</c> yields 0.</param>
	public int LightCountIn(string? areaId, GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		return string.IsNullOrWhiteSpace(areaId) ? 0 : Discover(areaId, global).Lights.Count;
	}

	/// <summary>
	///     The lights <paramref name="area"/> would command, named as Home Assistant names them, and the wider set
	///     of lights the room holds for the audit's sibling check.
	/// </summary>
	/// <remarks>
	///     The commanded half comes through <see cref="PreviewArea"/>, never a discovery count: a room that pins
	///     its own light list bypasses discovery, and its exclusions have already been applied.
	/// </remarks>
	/// <param name="area">The room, as it stands in the editor. Not mutated.</param>
	public RoomLights LightsIn(AreaConfig area, AreaSettings defaults, GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(area);
		ArgumentNullException.ThrowIfNull(defaults);
		ArgumentNullException.ThrowIfNull(global);

		if (PreviewArea(area, defaults, global).Resolved is not { } resolved)
			return RoomLights.None;

		IReadOnlyList<LightUnderReview> commanded =
			[.. resolved.Lights.Select(entityId => new LightUnderReview(entityId, FriendlyNameOf(entityId) ?? entityId))];

		// Unioned, never replaced: a room with explicit lights and no area id has no registry listing to read, and
		// the set must never be narrower than the commanded list.
		HashSet<string> inTheRoom = new(commanded.Select(light => light.EntityId), StringComparer.OrdinalIgnoreCase);
		inTheRoom.UnionWith(LightEntitiesIn(area.AreaId));

		return new RoomLights(commanded, inTheRoom);
	}

	/// <summary>Drops the cached discovery answers, so the next question is put to Home Assistant afresh.</summary>
	public void Invalidate()
	{
		_discoveries.Clear();
		_discoverySignature = null;
	}

	public IReadOnlyList<string> Labels()
	{
		try
		{
			return [.. _registry.Labels
				.Select(label => label.Name)
				.OfType<string>()
				.Distinct(StringComparer.Ordinal)
				.OrderBy(name => name, StringComparer.CurrentCulture)];
		}
		catch (InvalidOperationException)
		{
			IsHomeAssistantReady = false;
			return [];
		}
	}

	/// <summary>Every registry label with both its id and its name, for the label pickers.</summary>
	/// <remarks>
	///     Empty is a real answer, not a failure: most houses have never made a label. A picker built on this has
	///     to render that case as instructions, not an empty dropdown.
	/// </remarks>
	public IReadOnlyList<LabelOption> LabelOptions()
	{
		try
		{
			return [.. _registry.Labels
				.Where(label => label.Id is { Length: > 0 })
				.Select(label => new LabelOption(label.Id!, label.Name is { Length: > 0 } name ? name : label.Id!))
				.OrderBy(option => option.Name, StringComparer.CurrentCulture)];
		}
		catch (InvalidOperationException)
		{
			IsHomeAssistantReady = false;
			return [];
		}
	}

	/// <param name="domains">Domain prefixes without the dot: <c>light</c>, <c>input_boolean</c>.</param>
	public IReadOnlyList<EntityOption> EntitiesInDomains(params string[] domains)
	{
		ArgumentNullException.ThrowIfNull(domains);

		return Enumerate(entityId => domains.Contains(entityId.Domain() ?? "", StringComparer.Ordinal));
	}

	/// <summary>
	///     Every entity that can name a date or time, for the "reset at a time" picker: <c>input_datetime.*</c>,
	///     <c>time.*</c> and <c>datetime.*</c> helpers, plus any <c>sensor.*</c> whose <c>device_class</c> is
	///     <c>timestamp</c> or <c>date</c>. Ordered by friendly name; empty when Home Assistant has not answered.
	/// </summary>
	public IReadOnlyList<EntityOption> DateTimeEntities()
	{
		IReadOnlyList<EntityOption> helpers = EntitiesInDomains("input_datetime", "time", "datetime");
		IReadOnlyList<EntityOption> timeSensors = EntitiesWithDeviceClass("sensor", ["timestamp", "date"]);

		return [.. helpers
			.Concat(timeSensors)
			.OrderBy(option => option.FriendlyName, StringComparer.CurrentCulture)];
	}

	/// <summary>
	///     Every entity in <paramref name="domain"/> whose <c>device_class</c> is one of
	///     <paramref name="deviceClasses"/>.
	/// </summary>
	/// <param name="deviceClasses">The device classes that qualify. Empty offers the whole domain.</param>
	public IReadOnlyList<EntityOption> EntitiesWithDeviceClass(string domain, IReadOnlyList<string> deviceClasses)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(domain);
		ArgumentNullException.ThrowIfNull(deviceClasses);

		return Enumerate(entityId =>
			string.Equals(entityId.Domain(), domain, StringComparison.Ordinal)
			&& (deviceClasses.Count == 0
				|| deviceClasses.Contains(DeviceClassOf(entityId) ?? string.Empty, StringComparer.OrdinalIgnoreCase)));
	}

	/// <summary>
	///     Every <c>device_class</c> currently in use by a <c>binary_sensor</c>, so the motion device-class
	///     field offers what this house has, not a typed guess.
	/// </summary>
	public IReadOnlyList<string> BinarySensorDeviceClasses() => DeviceClassesIn("binary_sensor");

	public IReadOnlyList<string> SensorDeviceClasses() => DeviceClassesIn("sensor");

	public string? FriendlyNameOf(string? entityId)
	{
		if (string.IsNullOrWhiteSpace(entityId))
			return null;

		return ReadAttribute(TryGetState(entityId), FriendlyNameAttribute);
	}

	/// <summary>
	///     The live <c>options</c> of an <c>input_select</c>, or empty when HA cannot answer or the entity has none.
	/// </summary>
	public IReadOnlyList<string> SelectOptionsOf(string? entityId) =>
		string.IsNullOrWhiteSpace(entityId) ? [] : ReadStringArray(TryGetState(entityId), OptionsAttribute);

	/// <summary>
	///     The live state of <paramref name="entityId"/> as a usable value (trimmed; <c>unknown</c>/<c>unavailable</c>
	///     folded to <c>null</c>), or <c>null</c> when blank or Home Assistant cannot answer. Used to mark the option a
	///     house-mode select currently stands on.
	/// </summary>
	public string? CurrentStateOf(string? entityId) =>
		string.IsNullOrWhiteSpace(entityId) ? null : TryGetState(entityId).AsUsableState();

	/// <summary>
	///     Today's sunrise/sunset from <c>sun.sun</c>, or <c>(null, null)</c>. The UTC-to-local conversion mirrors
	///     <c>LightingOrchestrator.ReadSunTimes</c>; the two must agree or the readback contradicts the engine.
	/// </summary>
	public (TimeOnly? Sunrise, TimeOnly? Sunset) SunTimesToday()
	{
		EntityState? state = TryGetState(SunEntityId);
		return (ReadSunTime(state, NextRisingAttribute), ReadSunTime(state, NextSettingAttribute));

		static TimeOnly? ReadSunTime(EntityState? state, string attribute) =>
			state.AttrDateTimeOffset(attribute) is { } parsed
				? TimeOnly.FromDateTime(parsed.ToLocalTime().DateTime)
				: null;
	}

	/// <summary>Whether Home Assistant knows <paramref name="entityId"/>. Drives the "unknown entity" warning.</summary>
	public bool Knows(string? entityId) =>
		!string.IsNullOrWhiteSpace(entityId) && TryGetState(entityId) is not null;

	/// <summary>What a room's light-level sensors read now, averaged, or <c>null</c> when none answers.</summary>
	/// <remarks>
	///     Follows <see cref="IlluminanceGate"/>'s rules, so the number shown beside a curve is the one the curve
	///     would be applied to. Staleness is the one rule not copied: the gate drops a sensor that has stopped
	///     reporting, but a chart marker that vanished on a quiet afternoon would read as a broken chart.
	/// </remarks>
	/// <param name="area">The room, already resolved, so this reads the same sensors the engine would.</param>
	/// <returns>The reading in lux, or <c>null</c> when the room has no sensor that answers with a number.</returns>
	public double? LuxIn(ResolvedArea area, GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(area);
		ArgumentNullException.ThrowIfNull(global);

		IReadOnlyList<string> sensors = area.LuxSensors.Count > 0
			? area.LuxSensors
			: area.FollowOutdoorLux && global.OutdoorLuxSensor is { Length: > 0 } outdoor ? [outdoor] : [];

		List<double> readings = [];

		foreach (string entityId in sensors)
			if (TryGetState(entityId).StateAsDouble() is { } lux && double.IsFinite(lux))
				readings.Add(lux);

		if (readings.Count == 0)
			return null;

		// GEOMETRIC mean, matching IlluminanceGate.AverageLux. Illuminance spans decades: 10 lx and 10 000 lx
		// average to 316 in log space and to 5 005 arithmetically, which on the default anchors is a caption
		// promising 91 % over an engine commanding 55 %.
		List<double> positive = [.. readings.Where(lux => lux > 0)];

		// Every sensor at zero or less is pitch dark, not a missing reading: the gate's rule, so the two agree at
		// the bottom of the range too.
		if (positive.Count == 0)
			return 0;

		return positive.Count == 1 ? positive[0] : Math.Exp(positive.Sum(Math.Log) / positive.Count);
	}

	/// <summary>What one light-level sensor reads now, or <c>null</c> when it answers with no number.</summary>
	// What the daylight curve is drawn against: one sensor, never an average, because the curve reads one.
	public double? LuxOf(string? entityId) =>
		entityId is { Length: > 0 } && TryGetState(entityId).StateAsDouble() is { } lux && double.IsFinite(lux)
			? lux
			: null;

	/// <summary>Runs the engine's own area resolver against <paramref name="area"/> as it stands in the editor.</summary>
	/// <param name="area">The area being edited. Not mutated.</param>
	public AreaPreview PreviewArea(AreaConfig area, AreaSettings defaults, GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(area);
		ArgumentNullException.ThrowIfNull(defaults);
		ArgumentNullException.ThrowIfNull(global);

		try
		{
			return Resolver(global).TryResolve(area, defaults, out ResolvedArea? resolved, out var error)
				? new AreaPreview(resolved, null)
				: new AreaPreview(null, error);
		}
		catch (InvalidOperationException)
		{
			IsHomeAssistantReady = false;
			return new AreaPreview(null, "Home Assistant is not connected yet, so discovery cannot be previewed.");
		}
	}

	/// <summary>Works out what setting <paramref name="scope"/> up again would do, against the house as it is.</summary>
	/// <remarks>
	///     A pass-through to <see cref="AreaSetupService.Plan"/>; the rules stay in the engine. The resolver is
	///     built from the document being edited, so a plan honours what is on screen, not what was last saved.
	/// </remarks>
	/// <param name="config">The document being edited. Not mutated.</param>
	public SetupPlan PlanSetup(AdaptiveLightingConfig config, IReadOnlyCollection<string> scope)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(scope);

		try
		{
			return AreaSetupService.Plan(config, _areas, Resolver(config.Global), scope);
		}
		catch (InvalidOperationException exception)
		{
			IsHomeAssistantReady = false;
			_logger.LogDebug(exception, "The registry is not available, so no setup plan can be made.");
			return new SetupPlan([], [], []);
		}
	}

	private IReadOnlyList<EntityOption> Enumerate(Func<string, bool> predicate)
	{
		try
		{
			return [.. _ha.GetAllEntities()
				.Select(entity => entity.EntityId)
				.Where(predicate)
				.Select(entityId => new EntityOption(
					entityId,
					ReadAttribute(TryGetState(entityId), FriendlyNameAttribute) ?? entityId,
					AreaNameOf(entityId)))
				.OrderBy(option => option.FriendlyName, StringComparer.CurrentCulture)];
		}
		catch (InvalidOperationException exception)
		{
			IsHomeAssistantReady = false;
			_logger.LogDebug(exception, "The entity registry is not available yet.");
			return [];
		}
	}

	private IReadOnlyList<string> DeviceClassesIn(string domain)
	{
		try
		{
			return [.. _ha.GetAllEntities()
				.Select(entity => entity.EntityId)
				.Where(entityId => string.Equals(entityId.Domain(), domain, StringComparison.Ordinal))
				.Select(DeviceClassOf)
				.OfType<string>()
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(deviceClass => deviceClass, StringComparer.Ordinal)];
		}
		catch (InvalidOperationException)
		{
			IsHomeAssistantReady = false;
			return [];
		}
	}

	private AreaEntityResolver Resolver(GlobalConfig global) => new(
		_ha,
		_areas,
		global,
		_loggerFactory.CreateLogger<AreaEntityResolver>());

	// Every light.* the registry lists in an area, unfiltered. Not discovery's list, which prefers groups and so
	// hides a colour channel's parent. Nothing is named or counted from it.
	private IReadOnlyList<string> LightEntitiesIn(string? areaId) =>
		[.. EntitiesInAreaOrNone(areaId).Where(entityId => string.Equals(entityId.Domain(), "light", StringComparison.Ordinal))];

	// The registry's own listing of an area, or nothing when it cannot answer. Unfiltered: each caller applies its
	// own test.
	private IReadOnlyList<string> EntitiesInAreaOrNone(string? areaId)
	{
		if (string.IsNullOrWhiteSpace(areaId))
			return [];

		try
		{
			return _areas.EntitiesInArea(areaId);
		}
		catch (InvalidOperationException exception)
		{
			IsHomeAssistantReady = false;
			_logger.LogDebug(exception, "The registry cannot list area {Area} yet, so the room is judged on what it already names.", areaId);
			return [];
		}
	}

	private AreaOption Option(string areaId, string name, GlobalConfig global)
	{
		AreaDiscovery discovered = Discover(areaId, global);
		return new AreaOption(areaId, name, discovered.Lights.Count, discovered.MotionSensors.Count, discovered.LuxSensors.Count);
	}

	private AreaDiscovery Discover(string areaId, GlobalConfig global)
	{
		// Keyed by area id alone, the cache would survive a change to the conventions discovery reads: tick a
		// motion device class off and the counts keep insisting on the old answer.
		var signature = SignatureOf(global);

		if (!string.Equals(_discoverySignature, signature, StringComparison.Ordinal))
		{
			_discoveries.Clear();
			_discoverySignature = signature;
		}

		if (_discoveries.TryGetValue(areaId, out AreaDiscovery? cached))
			return cached;

		AreaDiscovery discovered;

		try
		{
			discovered = Resolver(global).DiscoverArea(areaId);
		}
		catch (InvalidOperationException exception)
		{
			IsHomeAssistantReady = false;
			_logger.LogDebug(exception, "Discovery for area {Area} is not available yet.", areaId);

			// Returned but never filed: caching this would turn "not answered yet" into a standing "this area
			// yields nothing" for the rest of the circuit.
			return new AreaDiscovery([], [], []);
		}

		_discoveries[areaId] = discovered;
		return discovered;
	}

	/// <summary>Everything about the globals that discovery actually reads, and nothing else.</summary>
	private static string SignatureOf(GlobalConfig global) => string.Join(
		'\n',
		global.ExcludeLabel,
		global.IncludeLabel,
		global.MotionLabel,
		global.IlluminanceDeviceClass,
		string.Join(',', global.EffectiveMotionDeviceClasses));

	private IReadOnlyList<EntityOption> Describe(IReadOnlyList<string> entityIds) =>
		[.. entityIds
			.Select(entityId => new EntityOption(
				entityId,
				ReadAttribute(TryGetState(entityId), FriendlyNameAttribute) ?? entityId,
				AreaNameOf(entityId)))
			.OrderBy(option => option.FriendlyName, StringComparer.CurrentCulture)];

	private string? AreaNameOf(string entityId)
	{
		try
		{
			return _registry.GetEntityRegistration(entityId)?.Area?.Name;
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}

	private string? DeviceClassOf(string entityId) => ReadAttribute(TryGetState(entityId), DeviceClassAttribute);

	private static string? ReadAttribute(EntityState? state, string attribute) => state.AttrString(attribute);

	/// <summary>Reads an attribute holding a list of strings, or an empty list when absent or the wrong shape.</summary>
	private static IReadOnlyList<string> ReadStringArray(EntityState? state, string attribute) => state.AttrStringList(attribute);

	private EntityState? TryGetState(string entityId)
	{
		try
		{
			return _ha.GetState(entityId);
		}
		catch (InvalidOperationException)
		{
			IsHomeAssistantReady = false;
			return null;
		}
	}

}
