using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Ha;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     One area, as a picker offers it, labelled with what a zone on it would actually get.
/// </summary>
/// <remarks>
///     <para>
///         The counts are the resolver's, after discovery and the ghost filter — not the registry's row count.
///         The row count was worse than useless: one live instance's <c>stue</c> reported 517 entities where Home Assistant's
///         own <c>area_entities('stue')</c> says 164 (the gap is disabled rows), and neither number said
///         anything about lighting. "1 light, 1 motion, 1 lux" is the question the person picking an area is
///         actually asking, and an area that reads "0 lights" is visibly wrong before it is saved rather than
///         after the room fails to light.
///     </para>
/// </remarks>
/// <param name="Id">The registry area id — the slug, which is what the config stores.</param>
/// <param name="Name">The display name, which is the only part a human recognises.</param>
/// <param name="LightCount">Lights discovery yields here, groups de-duplicated and ghosts dropped.</param>
/// <param name="MotionCount">Motion sensors discovery yields here, by device class or by label.</param>
/// <param name="LuxCount">Illuminance sensors discovery yields here. More than one is what makes an area ambiguous.</param>
public sealed record AreaOption(string Id, string Name, int LightCount, int MotionCount, int LuxCount)
{
	/// <summary>What the picker shows: the name a human knows, the slug they must never have to remember, and what they would get.</summary>
	public string Label => $"{NameAndId} — {Counts}";

	/// <summary>What a zone on this area resolves to, in words — the whole reason the label is worth reading.</summary>
	public string Counts => $"{Pluralise(LightCount, "light")}, {MotionCount} motion, {LuxCount} lux";

	/// <summary>Whether a zone on this area could run at all. An area with no lights is the common, silent mistake.</summary>
	public bool HasLights => LightCount > 0;

	/// <summary>Whether discovery would refuse this area for having more than one lux sensor to choose between.</summary>
	public bool LuxIsAmbiguous => LuxCount > 1;

	private string NameAndId => string.Equals(Name, Id, StringComparison.Ordinal) ? Id : $"{Name} ({Id})";

	private static string Pluralise(int count, string noun) => $"{count} {noun}{(count == 1 ? string.Empty : "s")}";
}

/// <summary>
///     The entities one area yields, as pickers offer them.
/// </summary>
/// <param name="Lights">The lights discovery finds in the area.</param>
/// <param name="MotionSensors">The motion sensors discovery finds in the area.</param>
/// <param name="LuxSensors">Every illuminance sensor discovery finds in the area, not just a chosen one.</param>
public sealed record AreaEntities(
	IReadOnlyList<EntityOption> Lights,
	IReadOnlyList<EntityOption> MotionSensors,
	IReadOnlyList<EntityOption> LuxSensors)
{
	/// <summary>An area that yields nothing, and the answer for "no area is chosen".</summary>
	public static AreaEntities Empty { get; } = new([], [], []);
}

/// <summary>
///     One entity, as a picker offers it.
/// </summary>
/// <param name="EntityId">The id the config stores.</param>
/// <param name="FriendlyName">Home Assistant's <c>friendly_name</c>, or the id when it has none.</param>
/// <param name="AreaName">The area the entity is assigned to, or <c>null</c>.</param>
public sealed record EntityOption(string EntityId, string FriendlyName, string? AreaName)
{
	/// <summary>What the picker shows.</summary>
	public string Label => AreaName is { Length: > 0 } area
		? $"{FriendlyName} — {area} ({EntityId})"
		: $"{FriendlyName} ({EntityId})";
}

/// <summary>
///     What discovery makes of a zone right now, for the editor to show before anything is saved.
/// </summary>
/// <param name="Resolved">The resolved zone, or <c>null</c> when discovery fails.</param>
/// <param name="Error">Why discovery fails, in the resolver's own words.</param>
public sealed record ZonePreview(ResolvedZone? Resolved, string? Error);

/// <summary>
///     Turns the Home Assistant registry into things a person can pick from, and answers "what would discovery
///     do with this area?" live.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the point of the whole editor.</b> An entity id is a slug a household has no reason to
///         remember and every reason to typo; an area id is worse, because it looks like the display name and
///         is not. Everything here exists so the configuration page can offer the names Home Assistant already
///         shows and store the ids the engine needs, without a human ever transcribing one.
///     </para>
///     <para>
///         <b>Discovery preview is the real resolver, not a lookalike.</b> <see cref="PreviewZone"/> runs
///         <see cref="ZoneEntityResolver"/> — the same class the engine runs at start-up — against the
///         half-finished zone in the browser. If it says a zone resolves to three lights and a lux sensor, that
///         is what the engine will do with it, because it is the same code. A second implementation that
///         "showed roughly what discovery does" would drift, and would be believed while it drifted.
///     </para>
///     <para>
///         Scoped, because <see cref="IHaContext"/> is: one per Blazor circuit is right, and the registry is
///         read live rather than snapshotted so a light assigned to an area in Home Assistant shows up on a
///         page refresh rather than on a restart.
///     </para>
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

	/// <summary>
	///     Discovery answers for this load, by area id.
	/// </summary>
	/// <remarks>
	///     Discovery is not free — every candidate costs a registry read and a state read, and the label on the
	///     area picker needs it for every area, not just the chosen one. Uncached, an eleven-area house would run
	///     eleven full discoveries on every keystroke in the editor, because Blazor re-renders the whole page and
	///     the scoped picker lists are read during that render. Cached, it runs them once per load.
	///     <para>
	///         Correct because the cache lives exactly as long as an answer stays true: <see cref="HaCatalog"/> is
	///         scoped, so this is per circuit, and <see cref="Invalidate"/> drops it whenever the page re-reads
	///         the document. A light assigned to an area in Home Assistant shows up on a page refresh, which is
	///         the freshness this class already promised.
	///     </para>
	/// </remarks>
	private readonly Dictionary<string, AreaDiscovery> _discoveries = new(StringComparer.Ordinal);

	/// <summary>The globals the cached discoveries were computed with, so an edit to them cannot go unnoticed.</summary>
	private string? _discoverySignature;

	/// <summary>Creates the catalog.</summary>
	/// <param name="ha">Reads entity state and attributes.</param>
	/// <param name="registry">Reads areas, labels and entity registrations.</param>
	/// <param name="loggerFactory">Builds the resolver's logger for <see cref="PreviewZone"/>.</param>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public HaCatalog(IHaContext ha, IHaRegistry registry, ILoggerFactory loggerFactory)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_registry = registry ?? throw new ArgumentNullException(nameof(registry));
		_loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
		_logger = loggerFactory.CreateLogger<HaCatalog>();
	}

	/// <summary>
	///     Whether Home Assistant answered the last question asked of it.
	/// </summary>
	/// <remarks>
	///     Kestrel serves the moment the process is up; NetDaemon connects afterwards, and its state cache
	///     throws until it has. So every page here is reachable in a window where no entity exists — a state to
	///     render, not a fault. When this is <c>false</c> the pickers are empty and the editor must fall back to
	///     free text, which is exactly what it does.
	/// </remarks>
	public bool IsHomeAssistantReady { get; private set; } = true;

	/// <summary>
	///     Every area the registry knows, by display name, each labelled with what a zone on it would resolve to.
	/// </summary>
	/// <param name="global">The discovery conventions — labels and device classes — the counts must honour.</param>
	/// <returns>The areas, ordered by display name. Empty when Home Assistant has not answered.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="global"/> is <c>null</c>.</exception>
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
	///     pickers to the area the zone is actually about.
	/// </summary>
	/// <remarks>
	///     These are discovery's own answers, so a ghost the engine excludes is not offered here either. That
	///     agreement is the whole point: a picker that offered a status LED the engine would never drive would be
	///     inviting somebody to configure a light that cannot work.
	/// </remarks>
	/// <param name="areaId">The area to scope to. <c>null</c> or blank yields <see cref="AreaEntities.Empty"/>.</param>
	/// <param name="global">The discovery conventions to honour.</param>
	/// <returns>What the area yields, named the way Home Assistant names it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="global"/> is <c>null</c>.</exception>
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

	/// <summary>
	///     Drops the cached discovery answers, so the next question is put to Home Assistant afresh.
	/// </summary>
	/// <remarks>
	///     Called by the page whenever it re-reads the document — a save, or a discard. Anything that was worth
	///     re-reading the file for is worth re-reading the registry for.
	/// </remarks>
	public void Invalidate()
	{
		_discoveries.Clear();
		_discoverySignature = null;
	}

	/// <summary>Every registry label, for the exclude/motion label fields.</summary>
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

	/// <summary>
	///     Every entity in the given domains, named the way Home Assistant names them.
	/// </summary>
	/// <param name="domains">Domain prefixes without the dot — <c>light</c>, <c>input_boolean</c>.</param>
	/// <returns>The matching entities, ordered by friendly name.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="domains"/> is <c>null</c>.</exception>
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
	/// <remarks>
	///     Used for the motion and illuminance pickers, so the explicit-override lists offer the same entities
	///     discovery would have found rather than every binary sensor in the house.
	/// </remarks>
	/// <param name="domain">The domain to look in.</param>
	/// <param name="deviceClasses">The device classes that qualify. Empty offers the whole domain.</param>
	/// <returns>The matching entities, ordered by friendly name.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
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
	///     field offers what this house actually has rather than a typed guess.
	/// </summary>
	public IReadOnlyList<string> BinarySensorDeviceClasses() => DeviceClassesIn("binary_sensor");

	/// <summary>Every <c>device_class</c> currently in use by a <c>sensor</c>.</summary>
	public IReadOnlyList<string> SensorDeviceClasses() => DeviceClassesIn("sensor");

	/// <summary>
	///     Home Assistant's name for an entity, for showing an id that is already in the config.
	/// </summary>
	/// <param name="entityId">The id to name.</param>
	/// <returns>The friendly name, or <c>null</c> when Home Assistant does not know the entity.</returns>
	public string? FriendlyNameOf(string? entityId)
	{
		if (string.IsNullOrWhiteSpace(entityId))
			return null;

		return ReadAttribute(TryGetState(entityId), FriendlyNameAttribute);
	}

	/// <summary>
	///     The live <c>options</c> of an <c>input_select</c>, or empty when HA cannot answer or the entity has none.
	/// </summary>
	/// <param name="entityId">The select to read. <c>null</c> or blank yields an empty list.</param>
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
	///     Today's sunrise/sunset from <c>sun.sun</c> (<c>next_rising</c>/<c>next_setting</c>), or <c>(null, null)</c>.
	///     The engine's own truth, for the inline readback (07 §7.3) — mirrors
	///     <c>LightingOrchestrator.ReadSunTimes</c>'s UTC-to-local conversion exactly.
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
	/// <param name="entityId">The id to check.</param>
	/// <returns><c>false</c> when the id is blank, unknown, or HA is not connected.</returns>
	public bool Knows(string? entityId) =>
		!string.IsNullOrWhiteSpace(entityId) && TryGetState(entityId) is not null;

	/// <summary>
	///     Runs the engine's own zone resolver against <paramref name="zone"/> as it stands in the editor.
	/// </summary>
	/// <param name="zone">The zone being edited. Not mutated.</param>
	/// <param name="defaults">The document's defaults, for the settings merge.</param>
	/// <param name="global">The document's globals, which supply the discovery conventions.</param>
	/// <returns>What discovery finds, or why it cannot.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public ZonePreview PreviewZone(ZoneConfig zone, ZoneSettings defaults, GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(zone);
		ArgumentNullException.ThrowIfNull(defaults);
		ArgumentNullException.ThrowIfNull(global);

		try
		{
			return Resolver(global).TryResolve(zone, defaults, out ResolvedZone? resolved, out var error)
				? new ZonePreview(resolved, null)
				: new ZonePreview(null, error);
		}
		catch (InvalidOperationException)
		{
			IsHomeAssistantReady = false;
			return new ZonePreview(null, "Home Assistant is not connected yet, so discovery cannot be previewed.");
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

	private ZoneEntityResolver Resolver(GlobalConfig global) => new(
		_ha,
		new HaAreaRegistry(_registry),
		global,
		_loggerFactory.CreateLogger<ZoneEntityResolver>());

	private AreaOption Option(string areaId, string name, GlobalConfig global)
	{
		AreaDiscovery discovered = Discover(areaId, global);
		return new AreaOption(areaId, name, discovered.Lights.Count, discovered.MotionSensors.Count, discovered.LuxSensors.Count);
	}

	private AreaDiscovery Discover(string areaId, GlobalConfig global)
	{
		// A cache keyed only by area id would survive a change to the very conventions discovery reads — tick a
		// motion device class off and the counts would keep insisting on the old answer. Cheaper to notice.
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
			discovered = new AreaDiscovery([], [], []);
		}

		_discoveries[areaId] = discovered;
		return discovered;
	}

	/// <summary>Everything about the globals that discovery actually reads, and nothing else.</summary>
	private static string SignatureOf(GlobalConfig global) => string.Join(
		'\n',
		global.ExcludeLabel,
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

	/// <summary>
	///     Reads a string attribute off an entity state, via the shared <c>AdaptiveLighting.Extensions</c> reader.
	/// </summary>
	/// <remarks>
	///     This was hand-rolled here while the engine's equivalent (<c>AttributeReader</c>) was <c>internal</c>.
	///     Now that the reader lives in the shared extensions library, this is a thin delegation — same tolerant
	///     JsonElement/boxed/string handling every consumer uses.
	/// </remarks>
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
