using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>
///     An area with every entity reference turned into a concrete id, ready to build a controller from.
/// </summary>
/// <param name="Name">Display name.</param>
/// <param name="Settings">The area's settings, already merged over the document defaults.</param>
/// <param name="Lights">The lights the area commands. Never empty.</param>
/// <param name="MotionSensors">The area's motion sources. Never empty.</param>
/// <param name="LuxSensors">
///     The area's illuminance sensors — every one of them, empty when it has none.
/// </param>
/// <param name="IgnoreWhenOn">Entities that block auto-on while they are on.</param>
/// <param name="FollowOutdoorLux">
///     Whether the area asked to read <see cref="GlobalConfig.OutdoorLuxSensor"/> when <paramref name="LuxSensors"/>
///     is empty. Carried unresolved rather than folded in here because the resolver has no
///     <see cref="GlobalConfig"/> reading to fold — the area's own sensors are a registry question and the house's
///     outdoor one is not — and because a room that follows the house should keep following it when the house
///     changes its mind. Defaulted so the many call sites that build an area by hand stay honest: not saying
///     anything means not following, which is the new default everywhere.
/// </param>
public sealed record ResolvedArea(
	string Name,
	AreaSettings Settings,
	IReadOnlyList<string> Lights,
	IReadOnlyList<string> MotionSensors,
	IReadOnlyList<string> LuxSensors,
	IReadOnlyList<string> IgnoreWhenOn,
	bool FollowOutdoorLux = false);

/// <summary>
///     What discovery finds in one area, before that area's explicit lists have had their say.
/// </summary>
/// <remarks>
///     Exists so something other than <see cref="AreaEntityResolver.TryResolve"/> can ask "what would this area
///     resolve to?" and get the answer from the engine's own rules. The configuration page asks it twice: once
///     per area to label the area picker with what a room there would actually get, and once per area to scope
///     the entity pickers to it. Both must agree with the engine, and the only way to guarantee that is for
///     both to be the engine.
/// </remarks>
/// <param name="Lights">The lights the area yields, ghosts dropped and light groups already de-duplicated.</param>
/// <param name="MotionSensors">The motion sources the area yields, by device class or by label.</param>
/// <param name="LuxSensors">
///     Every illuminance sensor the area yields. More than one is what makes an area ambiguous, so this is the
///     candidate list rather than the single chosen sensor — the caller decides what to make of the count.
/// </param>
public sealed record AreaDiscovery(
	IReadOnlyList<string> Lights,
	IReadOnlyList<string> MotionSensors,
	IReadOnlyList<string> LuxSensors);

/// <summary>
///     Turns an <see cref="AreaConfig"/> into a <see cref="ResolvedArea"/>, discovering from the HA registry what
///     the config did not spell out.
/// </summary>
/// <remarks>
///     Discovery-first is the point. Hand-listing every light re-creates the drift the old config model suffered:
///     the YAML and the house disagree, silently, until somebody notices a room that stopped working. An area id
///     and a device class stay true across a rename.
/// </remarks>
public sealed class AreaEntityResolver
{
	private const string LightDomain = "light";
	private const string BinarySensorDomain = "binary_sensor";
	private const string SensorDomain = "sensor";
	private const string DeviceClassAttribute = "device_class";
	private const string GroupMembersAttribute = "entity_id";
	private const string UnavailableState = "unavailable";
	private const string UnknownState = "unknown";

	private readonly IHaContext _ha;
	private readonly IAreaRegistry _registry;
	private readonly GlobalConfig _global;
	private readonly ILogger _logger;

	/// <summary>Creates a resolver.</summary>
	/// <param name="ha">Used to read device classes and group membership, which the registry does not carry.</param>
	/// <param name="registry">Source of areas and labels.</param>
	/// <param name="global">Supplies the label and device-class conventions.</param>
	/// <param name="logger">Diagnostics.</param>
	public AreaEntityResolver(IHaContext ha, IAreaRegistry registry, GlobalConfig global, ILogger logger)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_registry = registry ?? throw new ArgumentNullException(nameof(registry));
		_global = global ?? throw new ArgumentNullException(nameof(global));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>
	///     What <paramref name="areaId"/> yields on its own, by exactly the rules <see cref="TryResolve"/> uses.
	/// </summary>
	/// <remarks>
	///     The same filtering, from the same code: the exclude label, the light-group de-duplication and
	///     <c>IsLive</c> all apply. A caller that shows this is showing what the engine would do, not an
	///     approximation of it — which is the point, because an approximation would drift and be believed.
	/// </remarks>
	/// <param name="areaId">The registry area id. An area the registry does not know yields nothing.</param>
	/// <returns>The lights, motion sensors and illuminance sensors the area yields.</returns>
	/// <exception cref="ArgumentException"><paramref name="areaId"/> is <c>null</c>, empty or whitespace.</exception>
	public AreaDiscovery DiscoverArea(string areaId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(areaId);

		return new AreaDiscovery(
			DiscoverLights(areaId),
			DiscoverMotionSensors(areaId),
			DiscoverLuxSensors(areaId));
	}

	/// <summary>
	///     Resolves <paramref name="area"/>, or explains why it cannot be.
	/// </summary>
	/// <param name="area">The configured area.</param>
	/// <param name="defaults">The document defaults to merge the area's overrides onto.</param>
	/// <param name="resolved">The resolved area on success.</param>
	/// <param name="error">A message for the household on failure.</param>
	/// <returns><c>false</c> when the area must be skipped. A skipped area is never a reason to fail the house.</returns>
	public bool TryResolve(AreaConfig area, AreaSettings defaults, out ResolvedArea? resolved, out string? error)
	{
		ArgumentNullException.ThrowIfNull(area);
		ArgumentNullException.ThrowIfNull(defaults);

		resolved = null;
		error = null;

		// The one place a name enters the engine. Everything downstream — the snapshot's AreaName, and through it
		// the board's lanes, the activity log and the room page — takes what is decided here, so a room proposed
		// with nothing but an area id is called what Home Assistant calls it rather than by its slug.
		string name = AreaNaming.DisplayName(area, _registry);
		string? areaId = null;

		if (area.AreaId is { Length: > 0 } configuredArea)
		{
			if (!_registry.AreaExists(configuredArea))
			{
				error = $"AreaId '{configuredArea}' is not a registry area id (it is the slug, not the display name). Known ids: {string.Join(", ", _registry.AreaIds.Order(StringComparer.Ordinal))}.";
				return false;
			}

			areaId = configuredArea;
		}

		// An explicit list bypasses both labels, exactly as it bypasses discovery: an explicit pick is the owner
		// overruling the rules, and the rules do not get a veto — including the per-room exclude below.
		List<string> lights = area.Lights is { Count: > 0 } explicitLights
			? [.. explicitLights]
			: WithoutExcluded(DiscoverLights(areaId), area);

		if (lights.Count == 0)
		{
			error = NoLightsError(areaId);
			return false;
		}

		List<string> motion = area.MotionSensors is { Count: > 0 } explicitMotion
			? [.. explicitMotion]
			: WithoutExcluded(DiscoverMotionSensors(areaId), area);

		if (motion.Count == 0)
		{
			error = areaId is null
				? "No motion sensors: the area has neither an AreaId to discover from nor an explicit MotionSensors list."
				: $"No motion sensors discovered in area '{areaId}'. Assign one to the area, label it '{_global.MotionLabel}', or list it explicitly.";
			return false;
		}

		// An explicit LuxSensor is one sensor by construction: it is the owner naming the room's reading, which is
		// also how a room opts out of an average it disagrees with.
		List<string> lux = area.LuxSensor is { Length: > 0 } explicitLux
			? [explicitLux]
			: DiscoverLuxSensorsFor(areaId, area);

		AreaSettings settings = area.Effective(defaults);

		_logger.LogInformation(
			"Area {Area}: {LightCount} lights ({Lights}), {MotionCount} motion sensors ({Motion}), lux sensors {Lux}.",
			name, lights.Count, string.Join(", ", lights), motion.Count, string.Join(", ", motion),
			lux.Count > 0
				? string.Join(", ", lux)
				: area.FollowOutdoorLux == true ? "(the house's outdoor sensor)" : "(none)");

		resolved = new ResolvedArea(
			name, settings, lights, motion, lux, [.. area.IgnoreWhenOn ?? []], area.FollowOutdoorLux == true);
		return true;
	}

	/// <summary>
	///     Why an area yielded no lights, in the words that name the fix.
	/// </summary>
	/// <remarks>
	///     The include-label wording is only earned when the label is what emptied the list, which is why this
	///     re-runs discovery without it. An area that has no lights at all must not be told to go and label them:
	///     that sends a household looking for lights Home Assistant never assigned to the room.
	/// </remarks>
	private string NoLightsError(string? areaId)
	{
		if (areaId is null)
			return "No lights: the area has neither an AreaId to discover from nor an explicit Lights list.";

		if (_global.IncludeLabel is { Length: > 0 } include && DiscoverLights(areaId, respectIncludeLabel: false).Count > 0)
			return $"No lights in '{areaId}' carry the label '{include}'. Remove the include-label filter or label the lights in Home Assistant.";

		return $"No lights discovered in area '{areaId}'. Assign lights to the area in HA, or list them explicitly.";
	}

	private List<string> DiscoverLights(string? areaId) => DiscoverLights(areaId, respectIncludeLabel: true);

	/// <param name="areaId">The area to look in, or <c>null</c> for "nothing to discover from".</param>
	/// <param name="respectIncludeLabel">
	///     <c>false</c> only on the failure path, to tell "this room has no lights" apart from "this room's lights
	///     are all filtered out" — the two need different advice.
	/// </param>
	private List<string> DiscoverLights(string? areaId, bool respectIncludeLabel)
	{
		if (areaId is null)
			return [];

		List<string> candidates = _registry.EntitiesInArea(areaId)
			.Where(id => id.HasDomain(LightDomain))
			.Where(id => !IsExcluded(id))
			.Where(id => !respectIncludeLabel || IsIncluded(id))
			.Where(IsLive)
			.Distinct(StringComparer.Ordinal)
			.ToList();

		return PreferGroups(areaId, candidates, GroupRules.Lights, id => id.HasDomain(LightDomain));
	}

	/// <summary>
	///     What one domain's groups are called, and what keeping a group alongside its own members costs there.
	/// </summary>
	/// <remarks>
	///     The selection itself is identical in all three domains — that is the point of having one copy of it —
	///     but the harm a household is being warned about is not, and a warning that cannot say what the mistake
	///     will actually do is a warning nobody acts on.
	/// </remarks>
	/// <param name="Noun">What kind of group this is, for the messages: <c>light</c>, <c>motion</c>, <c>illuminance</c>.</param>
	/// <param name="Members">What its members are called: <c>bulbs</c>, <c>sensors</c>.</param>
	/// <param name="DoubleCost">What using a group and its members together does, in this domain.</param>
	/// <param name="ReachCost">What letting a group reach into another area does, in this domain.</param>
	/// <param name="OneEntityPerDevice">
	///     Whether several entities of this kind on one Home Assistant device are the same thing, so that only one
	///     of them should be used. True for lights and illuminance; deliberately false for motion — see the
	///     instances below, where the reason is written down.
	/// </param>
	private sealed record GroupRules(
		string Noun,
		string Members,
		string DoubleCost,
		string ReachCost,
		bool OneEntityPerDevice)
	{
		/// <summary>
		///     Measured on one live instance (2026-07-28): the office's <c>light.kontor_taklys_alle</c>,
		///     <c>_nw</c>, <c>_ww</c> and two <c>trening_taklys_*</c> entities are all one device — one RGBW
		///     fixture, its combined entity beside its own colour channels — and the area commanded all five.
		/// </summary>
		public static readonly GroupRules Lights = new(
			"light",
			"bulbs",
			"commanding both would command those bulbs twice",
			"Two areas commanding the same bulbs set each other's brightness and switch each other off.",
			OneEntityPerDevice: true);

		/// <summary>
		///     Measured on one live instance (2026-07-28): <c>binary_sensor.kontor_trening_bevegelse</c> is a
		///     <c>motion</c> group of two sensors, and the room was subscribed to all three — so every movement in
		///     that office fired the area three times, re-arming its vacancy timer and republishing on each.
		///     <para>
		///         <b>The device rule is deliberately off here, and the asymmetry is the point.</b> For a light a
		///         device is one lamp, so collapsing costs nothing: the lamp is still commanded. For motion a device
		///         is a <i>controller</i>, and a multi-zone presence sensor exposes genuinely different detection
		///         zones on one of them — as would two PIRs wired to one board. Collapsing those makes the room
		///         blind wherever the dropped entity was watching, which is silent, and is the exact failure this
		///         whole change exists to end. A group is the household saying "these are one"; a shared device is
		///         not, so only the group is believed.
		///     </para>
		/// </summary>
		public static readonly GroupRules Motion = new(
			"motion",
			"sensors",
			"listening to both would fire this room's motion two or three times for every movement",
			"Movement in the other room would light this one, and hold its lights on from empty.",
			OneEntityPerDevice: false);

		/// <summary>
		///     On here because the area now <i>averages</i> its illuminance sensors: one instrument exposed as two
		///     entities would otherwise carry double weight in that mean, which is a quieter mistake than a doubled
		///     service call and a harder one to see.
		/// </summary>
		public static readonly GroupRules Illuminance = new(
			"illuminance",
			"sensors",
			"counting both would weight one instrument twice in the area's average",
			"The room would decide whether it is dark from another room's light level.",
			OneEntityPerDevice: true);
	}

	/// <summary>
	///     Settles an area's groups against each other and against their own members, in whichever domain.
	/// </summary>
	/// <remarks>
	///     A group and its members are the same entities twice. For lights that doubles every service call and
	///     makes the group's own state a lie mid-transition; for motion it fires the area two or three times per
	///     movement; for illuminance it offers one reading under several names and leaves the area unable to
	///     choose. In every case the members lose and the group wins, and <see cref="GroupRules"/> supplies the
	///     words for saying so.
	///     <para>
	///         Comparing a group only against the ids it lists is not enough for that rule to hold, because a real
	///         house breaks it three ways — all three measured on one live instance (2026-07-27):
	///     </para>
	///     <para>
	///         <b>Nesting.</b> A group of groups lists groups, not members, so membership is followed all the way
	///         down (<see cref="LeavesOf"/>). One level of comparison worked only while every intermediate group
	///         happened to sit in the same area; unassign one and its leaves survive and are used alongside the
	///         outer group that already holds them.
	///     </para>
	///     <para>
	///         <b>Reach.</b> A group may hold entities Home Assistant assigns to another room — see
	///         <see cref="WithoutGroupsReachingIntoAnotherArea"/>, which is where that is decided.
	///     </para>
	///     <para>
	///         <b>Overlap.</b> Two groups can share members without either containing the other, so neither drops
	///         the other and the shared members are used twice. The widest coverage wins and the narrower group is
	///         traded for the members only it holds: nothing twice, and nothing quietly missing from its own room —
	///         a bulb dropped from the room is a worse fault than a bulb commanded twice.
	///     </para>
	/// </remarks>
	/// <param name="areaId">The area being discovered, for the messages and for deciding what counts as foreign.</param>
	/// <param name="candidates">The area's candidates in one domain, already filtered by label and liveness.</param>
	/// <param name="rules">What this domain's groups are called and what getting them wrong costs.</param>
	/// <param name="qualifies">
	///     Whether a member promoted out of an overlapping group belongs in this list on its own — the domain test
	///     the caller's own discovery applied. Not a formality: see the promotion below.
	/// </param>
	private List<string> PreferGroups(string areaId, List<string> candidates, GroupRules rules, Func<string, bool> qualifies)
	{
		// A room of plain bulbs has nothing for the group pass to settle, and this is nearly every room. Answered
		// before the transitive walk and the registry sweep, neither of which would find anything to do — but the
		// device pass still runs, because duplicate channels of one fixture need no group to exist.
		List<string> settled = candidates.Any(IsGroup)
			? SettleGroups(areaId, candidates, rules, qualifies)
			: candidates;

		return rules.OneEntityPerDevice ? OneEntityPerDevice(areaId, settled, rules) : settled;
	}

	/// <summary>The group half of <see cref="PreferGroups"/>: everything the remarks there describe.</summary>
	private List<string> SettleGroups(string areaId, List<string> candidates, GroupRules rules, Func<string, bool> qualifies)
	{
		Dictionary<string, IReadOnlySet<string>> coverage =
			candidates.ToDictionary(id => id, LeavesOf, StringComparer.Ordinal);

		// Widest first, so the group that holds the most members claims them before any narrower rival gets a say.
		// OrderByDescending is stable, so equally wide candidates keep the registry's order and the answer never
		// depends on how a dictionary felt like enumerating.
		List<string> ordered = [.. WithoutGroupsReachingIntoAnotherArea(areaId, candidates, coverage, rules)
			.OrderByDescending(id => coverage[id].Count)];

		List<string> kept = [];
		HashSet<string> claimed = new(StringComparer.Ordinal);

		foreach (string candidate in ordered)
		{
			IReadOnlySet<string> covers = coverage[candidate];
			List<string> unclaimed = [.. covers.Where(bulb => !claimed.Contains(bulb))];

			// Nothing of its own left: a member of a group already kept, or the same bulbs under another name.
			if (unclaimed.Count == 0)
				continue;

			if (unclaimed.Count == covers.Count)
			{
				kept.Add(candidate);
				claimed.UnionWith(covers);
				continue;
			}

			// Overlapping siblings. The members only this group holds still have to be used, so they are taken
			// individually — the group they came in through already passed every filter, so they are not
			// strangers to the room; only the exclude label and liveness get to stop them here.
			//
			// The domain is checked as well, and it is not a formality: a group's membership is whatever Home
			// Assistant put in the attribute, and a member outside the light domain would arrive here as an id
			// the area then hands to ILightActuator — which calls light.turn_on unconditionally, so a switch
			// promoted out of a group produces a service call HA rejects, on every command, for ever. Discovery
			// filters the domain for exactly this reason; anything entering by the back door gets the same filter,
			// which is what `qualifies` carries — and for illuminance it carries the device class too, because a
			// temperature sensor promoted into the lux candidates would be read as a light level.
			List<string> alone = [.. unclaimed
				.Where(qualifies)
				.Where(member => !IsExcluded(member))
				.Where(IsLive)];

			_logger.LogWarning(
				"Area '{Area}': {Kind} group '{Group}' shares {Shared} of its {Members} with {Rivals} while containing neither, "
				+ "so it is not used as a group here — {Cost}. The {Alone} {AloneMembers} only it holds are used on their own "
				+ "({Ids}). Nest the groups, or stop them overlapping, to settle it properly.",
				areaId, rules.Noun, candidate, covers.Count - unclaimed.Count, rules.Members,
				RivalsOf(kept, coverage, covers), rules.DoubleCost, alone.Count, rules.Members,
				alone.Count > 0 ? string.Join(", ", alone) : "none — the rest are excluded or unavailable");

			kept.AddRange(alone);
			claimed.UnionWith(covers);
		}

		if (kept.Count != candidates.Count)
			_logger.LogDebug("Area {Area}: {Total} discovered {Kind} entities settle into {Count} once their groups have had their say.",
				areaId, candidates.Count, rules.Noun, kept.Count);

		return kept;
	}

	/// <summary>
	///     Keeps one entity per piece of hardware: several entities on one Home Assistant device are one thing.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>The exact rule the naming conventions were approximating.</b> Measured on one live instance
	///         (2026-07-28): the office holds <c>light.kontor_taklys_alle</c>, <c>_nw</c>, <c>_ww</c>,
	///         <c>light.trening_taklys_nw</c> and <c>_rl</c> — five entities, one device, one physical fixture —
	///         and the area commanded all five, which is one lamp told five things and five times the service
	///         calls. The living room is worse: colour channels and twenty-five <c>stue_tak_N_M</c> entities across
	///         a handful of devices. A device id is a fact the registry records rather than a suffix somebody
	///         guessed at, so where both could answer, this one is believed.
	///     </para>
	///     <para>
	///         <b>Groups keep their precedence, and that is what makes the office come out right.</b> A group
	///         helper has no device of its own, so it is never a duplicate of anything; it stands for the devices
	///         of everything beneath it, and having claimed them it leaves nothing for the loose entities on the
	///         same hardware to claim. The office therefore resolves to <c>light.kontorlys_alle</c> alone, which is
	///         what the owner expects of that room.
	///     </para>
	///     <para>
	///         <b>Which entity wins for a device no group covers.</b> In order: a group beats a plain entity, then
	///         breadth (a candidate standing for more devices), then the entity the others are channels <i>of</i> —
	///         an id its siblings extend with an underscore, which is how Home Assistant names an RGBW fixture's
	///         combined entity beside <c>_r</c>, <c>_w</c> and friends — and then the shortest id, ordinally. The
	///         last two exist so the answer never depends on registry order, which can change under the house
	///         without anything having changed in it.
	///     </para>
	///     <para>
	///         A candidate covering several devices of which only some are claimed is <i>kept</i>. Dropping it
	///         would lose the hardware nobody else holds, and losing a lamp is worse than commanding one twice —
	///         the same trade the overlap rule above makes, for the same reason.
	///     </para>
	/// </remarks>
	/// <param name="areaId">The area being discovered, for the message.</param>
	/// <param name="candidates">What the group pass left.</param>
	/// <param name="rules">What this domain's entities are called, for the message.</param>
	private List<string> OneEntityPerDevice(string areaId, List<string> candidates, GroupRules rules)
	{
		Dictionary<string, IReadOnlySet<string>> devices =
			candidates.ToDictionary(id => id, DevicesOf, StringComparer.Ordinal);

		// The common room: every candidate is its own piece of hardware, or has none at all. Answered before the
		// ordering below so nothing about a healthy room's list can be reshuffled by a rule with no work to do.
		if (!devices.Values.SelectMany(set => set).GroupBy(device => device, StringComparer.Ordinal).Any(same => same.Count() > 1))
			return candidates;

		HashSet<string> parents = ChannelParents(devices);

		List<string> ordered = [.. candidates
			.OrderByDescending(IsGroup)
			.ThenByDescending(id => devices[id].Count)
			.ThenByDescending(parents.Contains)
			.ThenBy(id => id.Length)
			.ThenBy(id => id, StringComparer.Ordinal)];

		Dictionary<string, string> claimedBy = new(StringComparer.Ordinal);
		Dictionary<string, List<string>> foldedInto = new(StringComparer.Ordinal);
		List<string> kept = [];

		foreach (string candidate in ordered)
		{
			IReadOnlySet<string> mine = devices[candidate];

			// No device beneath it at all: a group of template entities, or a helper. Nothing to be a duplicate
			// of, and nothing to claim — so it is kept, and kept out of this rule's way entirely.
			if (mine.Count == 0)
			{
				kept.Add(candidate);
				continue;
			}

			if (mine.All(claimedBy.ContainsKey))
			{
				string winner = claimedBy[mine.First()];

				if (!foldedInto.TryGetValue(winner, out List<string>? folded))
					foldedInto[winner] = folded = [];

				folded.Add(candidate);
				continue;
			}

			kept.Add(candidate);

			foreach (string device in mine)
				claimedBy.TryAdd(device, candidate);
		}

		foreach ((string winner, List<string> folded) in foldedInto)
			_logger.LogWarning(
				"Area '{Area}': {Count} further {Kind} entities sit on the same Home Assistant device as '{Winner}' "
				+ "({Folded}), so they are the same hardware and only '{Kept}' is used. Several entities on one device are "
				+ "one fixture — a combined entity beside its own colour channels, most often — and using them separately "
				+ "sends one piece of hardware several commands at once.",
				areaId, folded.Count, rules.Noun, winner, string.Join(", ", folded.Order(StringComparer.Ordinal)), winner);

		return kept;
	}

	/// <summary>
	///     The candidates whose siblings on the same device are named as extensions of them — the combined entity
	///     of a multi-channel fixture, in the only form the registry actually spells out.
	/// </summary>
	/// <remarks>
	///     The underscore is load-bearing: without it <c>light.stue_tak_1</c> would count as the parent of
	///     <c>light.stue_tak_11</c>, which is a different lamp on a different device and no relation at all.
	/// </remarks>
	private static HashSet<string> ChannelParents(Dictionary<string, IReadOnlySet<string>> devices)
	{
		HashSet<string> parents = new(StringComparer.Ordinal);

		IEnumerable<IGrouping<string, string>> byDevice = devices
			.SelectMany(entry => entry.Value.Select(device => (Device: device, Id: entry.Key)))
			.GroupBy(pair => pair.Device, pair => pair.Id, StringComparer.Ordinal);

		foreach (IGrouping<string, string> device in byDevice)
		{
			List<string> siblings = [.. device];

			foreach (string id in siblings)
				if (siblings.Any(other => other.StartsWith(id + "_", StringComparison.Ordinal)))
					parents.Add(id);
		}

		return parents;
	}

	/// <summary>
	///     Every device <paramref name="entityId"/> stands for: its own, or those of everything it groups.
	/// </summary>
	/// <remarks>
	///     Built on <see cref="LeavesOf"/> so a group and a plain entity answer the same question, exactly as they
	///     do for coverage. A group has no device of its own — that is what a group <i>is</i> in the registry — so
	///     without following the membership it would stand for nothing and claim nothing.
	/// </remarks>
	private IReadOnlySet<string> DevicesOf(string entityId)
	{
		HashSet<string> devices = new(StringComparer.Ordinal);

		foreach (string leaf in LeavesOf(entityId))
			if (_registry.DeviceOf(leaf) is { Length: > 0 } device)
				devices.Add(device);

		return devices;
	}

	/// <summary>
	///     Drops the groups that reach past the area's own walls, naming the room they were reaching into.
	/// </summary>
	/// <remarks>
	///     Home Assistant lets a group hold entities from anywhere, and one live instance has a living-room group
	///     holding the kitchen's group (2026-07-27). Preferring that group would put the living room in charge of
	///     the kitchen's lighting: the two areas take turns setting each other's brightness, and whichever vacancy
	///     timeout fires first switches the lights off on somebody standing in the other room. There is no way to
	///     keep the group and not command what is inside it, so the area boundary wins and the room falls back to
	///     the lights it owns — usually the inner group that stays inside it, which is still a group, so
	///     "prefer groups" survives whole and only the reach is clipped.
	///     <para>
	///         The same boundary matters just as much for the other two domains, which is why this is shared rather
	///         than copied: a motion group reaching next door lights this room when somebody moves in that one, and
	///         an illuminance group reaching next door decides this room's darkness from that room's windows.
	///     </para>
	///     <para>
	///         Assigned to no area at all does not count as foreign: an entity whose group carries the area
	///         assignment is the ordinary way to set a house up, and treating it as another room's would clip nearly
	///         every group in the house.
	///     </para>
	///     <para>
	///         The one thing worse than a shared bulb is a dark room, so an area left with nothing after the clip
	///         keeps its reaching group, and the warning is then the only signal there is.
	///     </para>
	/// </remarks>
	private List<string> WithoutGroupsReachingIntoAnotherArea(
		string areaId,
		List<string> candidates,
		Dictionary<string, IReadOnlySet<string>> coverage,
		GroupRules rules)
	{
		IReadOnlyDictionary<string, string> elsewhere =
			AreasHolding(areaId, [.. coverage.Values.SelectMany(bulbs => bulbs)]);

		if (elsewhere.Count == 0)
			return candidates;

		List<string> kept = [];
		List<(string Group, string Areas, string Bulbs)> reaching = [];

		foreach (string candidate in candidates)
		{
			List<string> foreign = [.. coverage[candidate].Where(elsewhere.ContainsKey).Order(StringComparer.Ordinal)];

			if (foreign.Count == 0)
			{
				kept.Add(candidate);
				continue;
			}

			reaching.Add((
				candidate,
				string.Join(", ", foreign.Select(bulb => elsewhere[bulb]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
				string.Join(", ", foreign)));
		}

		foreach ((string group, string areas, string members) in reaching)
			if (kept.Count > 0)
				_logger.LogWarning(
					"Area '{Area}': {Kind} group '{Group}' reaches into area '{Other}' ({Ids}), so the area uses what it owns "
					+ "instead of the group. {Cost} Split the group, or move those entities, to have the area use it again.",
					areaId, rules.Noun, group, areas, members, rules.ReachCost);
			else
				_logger.LogWarning(
					"Area '{Area}': {Kind} group '{Group}' reaches into area '{Other}' ({Ids}), and the area has nothing of its "
					+ "own to fall back on, so it keeps the group and both areas share it. {Cost} Split the group, or give the "
					+ "area its own {Members}.",
					areaId, rules.Noun, group, areas, members, rules.ReachCost, rules.Members);

		return kept.Count > 0 ? kept : candidates;
	}

	/// <summary>
	///     Which other area holds each of <paramref name="members"/>, for the ones another area holds at all.
	/// </summary>
	/// <remarks>
	///     A reverse sweep of the registry rather than a per-entity lookup, because <see cref="IAreaRegistry"/>
	///     answers "what is in this area" and the question here is the other way round. One pass over the house, and
	///     only when the area actually has a group to check. An entity this area holds itself is never foreign,
	///     whatever any other area claims — the room being resolved is the one asking.
	/// </remarks>
	private IReadOnlyDictionary<string, string> AreasHolding(string areaId, HashSet<string> members)
	{
		HashSet<string> own = [.. _registry.EntitiesInArea(areaId)];
		Dictionary<string, string> holders = new(StringComparer.Ordinal);

		foreach (string other in _registry.AreaIds)
		{
			if (string.Equals(other, areaId, StringComparison.Ordinal))
				continue;

			foreach (string entityId in _registry.EntitiesInArea(other))
				if (members.Contains(entityId) && !own.Contains(entityId))
					holders.TryAdd(entityId, other);
		}

		return holders;
	}

	/// <summary>
	///     Every leaf entity <paramref name="entityId"/> actually stands for, following group membership all the
	///     way down.
	/// </summary>
	/// <remarks>
	///     A plain entity stands for itself, which is what makes a group and a member comparable at all: both answer
	///     the same question, so one selection pass settles group-versus-member, group-versus-group and
	///     member-versus-member without special cases. Nothing here is specific to lights, which is why all three
	///     domains share it.
	///     <para>
	///         Home Assistant will happily let a household build a group that contains itself, directly or round a
	///         longer loop, and a resolver that hangs on a misconfiguration takes the whole house down with it. The
	///         walk therefore visits each id once. A loop bottoms out with no leaves at the end of it, and something
	///         still has to be returned or the room resolves to nothing, so such a group stands for itself and
	///         everything it reaches — which lets the widest one win and the rest fold into it, exactly as a healthy
	///         nest does.
	///     </para>
	/// </remarks>
	private IReadOnlySet<string> LeavesOf(string entityId)
	{
		HashSet<string> bulbs = new(StringComparer.Ordinal);
		HashSet<string> seen = new(StringComparer.Ordinal) { entityId };
		Stack<string> pending = new();
		pending.Push(entityId);

		while (pending.Count > 0)
		{
			string current = pending.Pop();
			IReadOnlyList<string> members = _ha.AttrStringList(current, GroupMembersAttribute);

			if (members.Count == 0)
			{
				bulbs.Add(current);
				continue;
			}

			foreach (string member in members)
				if (seen.Add(member))
					pending.Push(member);
		}

		return bulbs.Count > 0 ? bulbs : seen;
	}

	/// <summary>
	///     Whether the entity lists members, which is the only thing that makes anything a group.
	/// </summary>
	/// <remarks>
	///     The <c>entity_id</c> attribute and nothing else — not the name. Measured on one live instance
	///     (2026-07-28): <c>light.kontorlys_alle</c> is a real group of two ceiling lights, while
	///     <c>light.kontor_taklys_alle</c> carries no membership at all despite reading like one. A rule that went
	///     by naming convention would have got both of those backwards.
	/// </remarks>
	private bool IsGroup(string entityId) => _ha.AttrStringList(entityId, GroupMembersAttribute).Count > 0;

	/// <summary>The already-kept candidates an overlapping group is overlapping, so the warning can name them.</summary>
	private static string RivalsOf(List<string> kept, Dictionary<string, IReadOnlySet<string>> coverage, IReadOnlySet<string> covers) =>
		string.Join(", ", kept.Where(id => coverage.TryGetValue(id, out IReadOnlySet<string>? theirs) && theirs.Overlaps(covers)));

	/// <summary>
	///     Whether the entity is something Home Assistant can actually act on right now.
	/// </summary>
	/// <remarks>
	///     The registry lists rows, not devices. A disabled or never-loaded entity is still a registry row and
	///     still comes back from <see cref="IAreaRegistry.EntitiesInArea"/>, but it has no state at all — on one live instance
	///     that swept up <c>light.router_socket_status_led</c> and a water sensor's indicator LED and called them
	///     room lighting (2026-07-17). <c>unavailable</c> is dropped too: a light the engine cannot reach is a
	///     light it cannot dim, and including it only produces commands that go nowhere. <c>unknown</c> is dropped
	///     for the same reason: an entity sitting on <c>unknown</c> has never reported, which is indistinguishable
	///     from absent for discovery's purposes, and a sensor that has never reported is as dead as an unavailable one.
	///     <para>
	///         The cost is that a lamp which is merely offline at startup — now including one that starts on
	///         <c>unknown</c> — stays out of its area until the engine is next rebuilt, which the Configuration page
	///         can do without a restart. That cost is acceptable for the same reason it was for <c>unavailable</c>:
	///         a device that cannot answer is one the engine cannot drive, and inviting it in only produces commands
	///         that go nowhere.
	///     </para>
	/// </remarks>
	private bool IsLive(string entityId)
	{
		EntityState? state = _ha.GetState(entityId);

		if (state is null)
		{
			_logger.LogDebug("Ignoring {EntityId}: a registry entry with no state is not a device.", entityId);
			return false;
		}

		if (string.Equals(state.State, UnavailableState, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(state.State, UnknownState, StringComparison.OrdinalIgnoreCase))
		{
			_logger.LogDebug("Ignoring {EntityId}: {State}.", entityId, state.State);
			return false;
		}

		return true;
	}

	/// <summary>
	///     Every motion source the area yields, its groups already preferred over their own members.
	/// </summary>
	/// <remarks>
	///     A motion group and its members are the same movement two or three times. Measured on one live instance
	///     (2026-07-28): an office held <c>binary_sensor.kontor_trening_bevegelse</c> — a genuine <c>motion</c>
	///     group — alongside both of the sensors it contains, and the area subscribed to all three, so a single
	///     wave of a hand re-armed the vacancy timer three times and published three reports. Groups are preferred
	///     here by exactly the machinery lights use, cross-area clip and overlap rule included; see
	///     <see cref="PreferGroups"/>.
	/// </remarks>
	private List<string> DiscoverMotionSensors(string? areaId)
	{
		if (areaId is null)
			return [];

		IReadOnlyList<string> inArea = _registry.EntitiesInArea(areaId);

		IEnumerable<string> byDeviceClass = inArea
			.Where(id => id.HasDomain(BinarySensorDomain))
			.Where(id => _global.EffectiveMotionDeviceClasses.Contains(DeviceClassOf(id), StringComparer.OrdinalIgnoreCase));

		// mmWave and other presence hardware routinely reports a device class nobody expected. The label is the
		// household's way of saying "this one counts" without the engine having to guess.
		IEnumerable<string> byLabel = inArea.Where(id => HasLabel(id, _global.MotionLabel));

		List<string> candidates = [.. byDeviceClass.Concat(byLabel)
			.Where(id => !IsExcluded(id))
			.Where(IsLive)
			.Distinct(StringComparer.Ordinal)];

		return PreferGroups(areaId, candidates, GroupRules.Motion, id => id.HasDomain(BinarySensorDomain));
	}

	/// <summary>
	///     Every illuminance sensor the area yields, its groups already preferred over their own members.
	/// </summary>
	/// <remarks>
	///     <para>
	///         IsLive matters most here: an area with one working lux sensor and one dead one is ambiguous only if
	///         the dead one counts. On one live instance, an annex area had exactly that pair, and refusing the area
	///         over a sensor that reports nothing would be a poor trade.
	///     </para>
	///     <para>
	///         Two passes settle false plurality before anything counts the candidates. A group and the sensors
	///         inside it are one reading under several names, and the group wins; several entities of one physical
	///         instrument are likewise one reading, and the device rule keeps one. What survives both is genuinely
	///         several instruments in one room — the measured office has two plain illuminance sensors with no
	///         group and no shared device, and those two are real — which is what the area then averages.
	///     </para>
	/// </remarks>
	private List<string> DiscoverLuxSensors(string? areaId)
	{
		if (areaId is null)
			return [];

		List<string> candidates = [.. _registry.EntitiesInArea(areaId)
			.Where(IsIlluminance)
			.Where(id => !IsExcluded(id))
			.Where(IsLive)
			.Distinct(StringComparer.Ordinal)];

		return PreferGroups(areaId, candidates, GroupRules.Illuminance, IsIlluminance);
	}

	/// <summary>Whether the entity is a sensor of the configured illuminance device class.</summary>
	private bool IsIlluminance(string entityId) =>
		entityId.HasDomain(SensorDomain)
		&& string.Equals(DeviceClassOf(entityId), _global.IlluminanceDeviceClass, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	///     The illuminance sensors the area will read — all of them, for the gate to average.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>This used to refuse.</b> An area with more than one candidate used none of them, on the ground
	///         that the engine could not tell which sensor was the room's — a real house offered the probe inside
	///         its fridge — and that picking one arbitrarily was worse than picking none. The caution was right;
	///         the conclusion was not, because it left a better-instrumented room strictly worse off than a bare
	///         one. Averaging is a better answer than either: it uses every reading the room has, and no single
	///         eccentric sensor decides on its own.
	///     </para>
	///     <para>
	///         By the time the count is taken, the two shapes of false plurality are already gone: a group listed
	///         beside the sensors inside it has folded into the group, and several entities of one instrument have
	///         folded into one entity. What is left is genuinely several instruments, which is exactly the case an
	///         average is for.
	///     </para>
	///     <para>
	///         The per-room exclude is applied here rather than earlier so that a household can still take one
	///         sensor out of the mean by id — the fridge probe stays answerable, it is just answered by naming it
	///         instead of by the whole room going without.
	///     </para>
	/// </remarks>
	private List<string> DiscoverLuxSensorsFor(string? areaId, AreaConfig area)
	{
		if (areaId is null)
			return [];

		List<string> candidates = WithoutExcluded(DiscoverLuxSensors(areaId), area);

		if (candidates.Count > 1)
			_logger.LogInformation(
				"Area '{Area}' has {Count} illuminance sensors ({Candidates}) and none of them contains the others, so it "
				+ "reads the average of whichever are still reporting. Set LuxSensor on the area to use exactly one, or "
				+ "ExcludeEntities to drop one from the average.",
				areaId, candidates.Count, string.Join(", ", candidates));

		return candidates;
	}

	/// <summary>
	///     Drops the ids the area lists under <see cref="AreaConfig.ExcludeEntities"/> from a discovered list.
	/// </summary>
	/// <remarks>
	///     The per-room twin of the exclude label, and the same "discover, then remove" shape — but by id and for
	///     this room only, so a sensor sitting in the room's Home Assistant area (a fridge's own illuminance probe
	///     was the motivating case) can be kept out of its lighting without touching any other room. Applied to
	///     discovered lists only: an explicit <see cref="AreaConfig.Lights"/> or <see cref="AreaConfig.MotionSensors"/>
	///     list is already the owner overruling discovery by hand, and the rules — this one included — do not re-filter it.
	/// </remarks>
	private static List<string> WithoutExcluded(List<string> discovered, AreaConfig area)
	{
		if (area.ExcludeEntities is not { Count: > 0 } excluded)
			return discovered;

		HashSet<string> drop = new(excluded, StringComparer.Ordinal);
		return [.. discovered.Where(id => !drop.Contains(id))];
	}

	private string? DeviceClassOf(string entityId) => _ha.AttrString(entityId, DeviceClassAttribute);

	private bool IsExcluded(string entityId) => HasLabel(entityId, _global.ExcludeLabel);

	/// <summary>
	///     Whether the include-label filter lets <paramref name="entityId"/> through.
	/// </summary>
	/// <remarks>
	///     No label configured means everything passes — the filter is strictly opt-in, and saying nothing has
	///     always meant "manage every light discovery finds". Checked after the exclusion, never instead of it:
	///     a light carrying both labels stays out, because "never touch" must not lose an argument.
	/// </remarks>
	private bool IsIncluded(string entityId) =>
		_global.IncludeLabel is not { Length: > 0 } include || HasLabel(entityId, include);

	private bool HasLabel(string entityId, string label) =>
		_registry.LabelsOf(entityId).Contains(label, StringComparer.OrdinalIgnoreCase);
}
