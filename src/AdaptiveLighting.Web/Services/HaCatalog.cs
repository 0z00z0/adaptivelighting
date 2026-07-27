using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Ha;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     One area, as a picker offers it, labelled with what a room there would actually get.
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

	/// <summary>What a room here resolves to, in words — the whole reason the label is worth reading.</summary>
	public string Counts => $"{Pluralise(LightCount, "light")}, {MotionCount} motion, {LuxCount} lux";

	/// <summary>Whether a room here could run at all. An area with no lights is the common, silent mistake.</summary>
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
///     One registry label, as a picker offers it.
/// </summary>
/// <remarks>
///     Both halves are carried because the two are used for different things: the id is the stable identity, the
///     name is what the household typed and what the config stores (§6.6 — <c>LabelsOf</c> matches either, names
///     are what a person reading the YAML recognises, and the shipped <c>adaptive-exclude</c> is already a name).
/// </remarks>
/// <param name="Id">The registry label id.</param>
/// <param name="Name">The display name, and the value a label field stores.</param>
public sealed record LabelOption(string Id, string Name);

/// <summary>
///     What discovery makes of an area right now, for the editor to show before anything is saved.
/// </summary>
/// <param name="Resolved">The resolved area, or <c>null</c> when discovery fails.</param>
/// <param name="Error">Why discovery fails, in the resolver's own words.</param>
public sealed record AreaPreview(ResolvedArea? Resolved, string? Error);

/// <summary>
///     A room's lights as the switch-on note needs them: the ones the engine will drive, and the ones the room
///     holds at all.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two sets, because the audit asks two different questions of them.</b> <see cref="Commanded"/> is
///         what is judged, counted and named — the resolver's own answer, groups already preferred over their
///         members, so nothing the engine will not touch is ever named. <see cref="InTheRoom"/> is only ever the
///         sibling check's context, which <c>LightAudit.ReasonFor</c> documents as "every entity id in the same
///         room" and which is a strictly wider thing.
///     </para>
///     <para>
///         <b>The miss that made this a pair rather than a list.</b> The colour-channel rule flags
///         <c>light.stue_vegglys_r</c> only when <c>light.stue_vegglys</c> is present, and the room reaches that
///         lamp through the group <c>light.stue_alle</c> — so group preference had already removed the parent from
///         the only list the audit was given, and a living room driving one lamp through a group while three
///         channel entities fought it raised nothing at all. Those channels are real entities in the house this
///         audit was commissioned for.
///     </para>
/// </remarks>
/// <param name="Commanded">The lights the engine will drive, in the resolver's order.</param>
/// <param name="InTheRoom">
///     Every <c>light.*</c> entity Home Assistant lists in the room, plus the commanded ids — never narrower than
///     <see cref="Commanded"/>, so a room with hand-picked lights and no area id is judged exactly as before.
/// </param>
public sealed record RoomLights(IReadOnlyList<LightUnderReview> Commanded, IReadOnlySet<string> InTheRoom)
{
	/// <summary>A room that resolves to nothing, and the answer when discovery cannot run at all.</summary>
	public static RoomLights None { get; } = new([], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

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
///         <b>Discovery preview is the real resolver, not a lookalike.</b> <see cref="PreviewArea"/> runs
///         <see cref="AreaEntityResolver"/> — the same class the engine runs at start-up — against the
///         half-finished area in the browser. If it says an area resolves to three lights and a lux sensor, that
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
	private readonly HaAreaRegistry _areas;

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
	///     <para>
	///         <b>Only answers go in here.</b> A discovery that threw is Home Assistant declining to answer, not an
	///         answer of "nothing here", and it used to be cached as though it were one. Kestrel serves the moment
	///         the process is up while NetDaemon connects afterwards — see <see cref="IsHomeAssistantReady"/> — so
	///         any page opened during start-up asked at least once too early, and every area then read
	///         "0 lights, 0 motion, 0 lux" for the whole circuit however long Home Assistant had since been up.
	///     </para>
	/// </remarks>
	private readonly Dictionary<string, AreaDiscovery> _discoveries = new(StringComparer.Ordinal);

	/// <summary>The globals the cached discoveries were computed with, so an edit to them cannot go unnoticed.</summary>
	private string? _discoverySignature;

	/// <summary>Creates the catalog.</summary>
	/// <param name="ha">Reads entity state and attributes.</param>
	/// <param name="registry">Reads areas, labels and entity registrations.</param>
	/// <param name="loggerFactory">Builds the resolver's logger for <see cref="PreviewArea"/>.</param>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public HaCatalog(IHaContext ha, IHaRegistry registry, ILoggerFactory loggerFactory)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_registry = registry ?? throw new ArgumentNullException(nameof(registry));
		_loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
		_logger = loggerFactory.CreateLogger<HaCatalog>();
		_areas = new HaAreaRegistry(_registry);
	}

	/// <summary>
	///     The area registry as the engine sees it, for the things a page needs beyond pickers — floors, chiefly.
	/// </summary>
	/// <remarks>
	///     Exposed rather than re-wrapped by each page so the settings list and the dashboard group rooms through
	///     one object. The engine's own seam is deliberately the one handed out: a UI-only floor lookup would be a
	///     second answer to a question the engine already answers.
	/// </remarks>
	public IAreaRegistry AreaRegistry => _areas;

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
	///     Every area the registry knows, by display name, each labelled with what a room there would resolve to.
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
	///     pickers to the area the room is actually about.
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
	///     How many lights discovery finds in <paramref name="areaId"/>, without naming them.
	/// </summary>
	/// <remarks>
	///     The dashboard's first-run state names every room with its light count, and it re-renders on the page's
	///     one-second tick. <see cref="EntitiesInArea"/> answers the same question, but reads a friendly name and an
	///     area for every entity to do it — per-second work for text nobody is reading. This is the cached count
	///     and nothing else.
	/// </remarks>
	/// <param name="areaId">The area to count in. Blank or <c>null</c> yields 0 — there is nothing to discover in.</param>
	/// <param name="global">The discovery conventions the count must honour.</param>
	/// <returns>The number of lights, or 0 when Home Assistant has not answered.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="global"/> is <c>null</c>.</exception>
	public int LightCountIn(string? areaId, GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		return string.IsNullOrWhiteSpace(areaId) ? 0 : Discover(areaId, global).Lights.Count;
	}

	/// <summary>
	///     The lights <paramref name="area"/> would actually command, named the way Home Assistant names them, and
	///     the wider set of lights the room holds for the audit's sibling check.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The commanded half is the resolver's own answer through <see cref="PreviewArea"/>, not a discovery
	///         count: a room that pins its own light list bypasses discovery entirely, groups have already won over
	///         their members, and the room's per-room exclusions have already been applied. A warning built on
	///         anything looser would name lights the engine is not going to touch, which is a worse fault in a
	///         warning than in a label. A room that cannot resolve — no motion sensor, say — yields nothing, because
	///         it will command nothing.
	///     </para>
	///     <para>
	///         <b>Which is exactly why the sibling set is carried beside it rather than folded into it.</b> Group
	///         preference removes a lamp the room still drives <i>through</i> its group, and the colour-channel rule
	///         needs to know the lamp is there — see <see cref="RoomLights"/>. Widening the commanded list to suit
	///         the rule would have made the note's count and its list of names wrong, which is the fault this method
	///         already refuses to commit.
	///     </para>
	/// </remarks>
	/// <param name="area">The room, as it stands in the editor. Not mutated.</param>
	/// <param name="defaults">The document's defaults, for the settings merge the resolver performs.</param>
	/// <param name="global">The document's globals, which supply the discovery conventions.</param>
	/// <returns>The room's lights. <see cref="RoomLights.None"/> when the room resolves to none.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public RoomLights LightsIn(AreaConfig area, AreaSettings defaults, GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(area);
		ArgumentNullException.ThrowIfNull(defaults);
		ArgumentNullException.ThrowIfNull(global);

		if (PreviewArea(area, defaults, global).Resolved is not { } resolved)
			return RoomLights.None;

		IReadOnlyList<LightUnderReview> commanded =
			[.. resolved.Lights.Select(entityId => new LightUnderReview(entityId, FriendlyNameOf(entityId) ?? entityId))];

		// Unioned rather than replaced, so the set is never narrower than the commanded list: a room configured
		// with explicit lights and no area id has no registry listing to read, and must go on being judged against
		// its own lights exactly as it was.
		HashSet<string> inTheRoom = new(commanded.Select(light => light.EntityId), StringComparer.OrdinalIgnoreCase);
		inTheRoom.UnionWith(LightEntitiesIn(area.AreaId));

		return new RoomLights(commanded, inTheRoom);
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
	///     Every registry label with both its id and its name, for the label pickers.
	/// </summary>
	/// <remarks>
	///     Empty is a real answer, not a failure: most houses have never made a label. A picker built on this has
	///     to render that case as instructions rather than as a dropdown with nothing in it (§5).
	/// </remarks>
	/// <returns>The labels, ordered by display name. Empty when the house has none or HA has not answered.</returns>
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
	///     Runs the engine's own area resolver against <paramref name="area"/> as it stands in the editor.
	/// </summary>
	/// <param name="area">The area being edited. Not mutated.</param>
	/// <param name="defaults">The document's defaults, for the settings merge.</param>
	/// <param name="global">The document's globals, which supply the discovery conventions.</param>
	/// <returns>What discovery finds, or why it cannot.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
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

	/// <summary>
	///     Works out what setting <paramref name="scope"/> up again would do, against this house as it is now.
	/// </summary>
	/// <remarks>
	///     A thin pass-through to <see cref="AreaSetupService.Plan"/> — the rules stay in the engine, so the dialog
	///     describes the same rebuild the engine performs. What is added here is the connection: the resolver is
	///     built from the document being edited, so a plan honours the labels and device classes on screen rather
	///     than the ones last saved. A registry that cannot answer yields an empty plan rather than a half-read one;
	///     the page refuses the button in that state and says why.
	/// </remarks>
	/// <param name="config">The document being edited. Not mutated.</param>
	/// <param name="scope">The area ids ticked for rebuild.</param>
	/// <returns>The plan, or an empty plan when Home Assistant is not answering.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
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

	/// <summary>
	///     Every <c>light.*</c> entity Home Assistant lists in an area, unfiltered.
	/// </summary>
	/// <remarks>
	///     The registry's own listing rather than discovery's, and that is the point: discovery has already
	///     preferred groups over their members, which is what hid a colour channel's parent from the audit. This is
	///     read only as the sibling check's context — nothing is ever named or counted from it — so a ghost row the
	///     resolver would drop costs nothing here.
	/// </remarks>
	/// <param name="areaId">The area, or <c>null</c> for a room with no area to list.</param>
	private IReadOnlyList<string> LightEntitiesIn(string? areaId)
	{
		if (string.IsNullOrWhiteSpace(areaId))
			return [];

		try
		{
			return [.. _areas.EntitiesInArea(areaId).Where(entityId => string.Equals(entityId.Domain(), "light", StringComparison.Ordinal))];
		}
		catch (InvalidOperationException exception)
		{
			IsHomeAssistantReady = false;
			_logger.LogDebug(exception, "The registry cannot list area {Area} yet, so the light audit judges the room alone.", areaId);
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

			// Returned but never filed. Caching this would turn "Home Assistant has not answered yet" into a
			// standing answer of "this area yields nothing", which is what the pickers and the first-run chips
			// would then read for the rest of the circuit — see the field's own remarks.
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
