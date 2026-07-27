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
/// <param name="LuxSensor">The area's lux sensor, or <c>null</c> when it has none.</param>
/// <param name="IgnoreWhenOn">Entities that block auto-on while they are on.</param>
public sealed record ResolvedArea(
	string Name,
	AreaSettings Settings,
	IReadOnlyList<string> Lights,
	IReadOnlyList<string> MotionSensors,
	string? LuxSensor,
	IReadOnlyList<string> IgnoreWhenOn);

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

		string? lux;
		if (area.LuxSensor is { Length: > 0 } explicitLux)
		{
			lux = explicitLux;
		}
		else if (!TryDiscoverLuxSensor(areaId, area, out lux, out string? luxError))
		{
			error = luxError;
			return false;
		}

		AreaSettings settings = area.Effective(defaults);

		_logger.LogInformation(
			"Area {Area}: {LightCount} lights ({Lights}), {MotionCount} motion sensors ({Motion}), lux sensor {Lux}.",
			name, lights.Count, string.Join(", ", lights), motion.Count, string.Join(", ", motion), lux ?? "(none)");

		resolved = new ResolvedArea(name, settings, lights, motion, lux, [.. area.IgnoreWhenOn ?? []]);
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

		return PreferGroups(areaId, candidates);
	}

	/// <summary>
	///     Settles an area's light groups against each other and against their own bulbs.
	/// </summary>
	/// <remarks>
	///     A light group and its members are the same bulbs twice. Commanding both doubles every service call
	///     and makes the group's own state a lie mid-transition, so the members lose and the group wins.
	///     <para>
	///         Comparing a group only against the ids it lists is not enough for that rule to hold, because a real
	///         house breaks it three ways — all three measured on one live instance (2026-07-27):
	///     </para>
	///     <para>
	///         <b>Nesting.</b> A group of groups lists groups, not bulbs, so membership is followed all the way down
	///         (<see cref="LeavesOf"/>). One level of comparison worked only while every intermediate group happened
	///         to sit in the same area; unassign one and its leaf bulbs survive and are commanded alongside the outer
	///         group that already holds them.
	///     </para>
	///     <para>
	///         <b>Reach.</b> A group may hold bulbs Home Assistant assigns to another room — see
	///         <see cref="WithoutGroupsReachingIntoAnotherArea"/>, which is where that is decided.
	///     </para>
	///     <para>
	///         <b>Overlap.</b> Two groups can share bulbs without either containing the other, so neither drops the
	///         other and the shared bulbs are commanded twice. The widest coverage wins and the narrower group is
	///         traded for the bulbs only it holds: no bulb twice, and no bulb quietly missing from its own room —
	///         a bulb dropped from the room is a worse fault than a bulb commanded twice.
	///     </para>
	/// </remarks>
	/// <param name="areaId">The area being discovered, for the messages and for deciding what counts as foreign.</param>
	/// <param name="candidates">The area's lights, already filtered by label and liveness.</param>
	private List<string> PreferGroups(string areaId, List<string> candidates)
	{
		// A room of plain bulbs has nothing to settle, and this is nearly every room. Answered before the
		// transitive walk and the registry sweep below, neither of which would find anything to do.
		if (!candidates.Any(IsGroup))
			return candidates;

		Dictionary<string, IReadOnlySet<string>> coverage =
			candidates.ToDictionary(id => id, LeavesOf, StringComparer.Ordinal);

		// Widest first, so the group that holds the most bulbs claims them before any narrower rival gets a say.
		// OrderByDescending is stable, so equally wide lights keep the registry's order and the answer never
		// depends on how a dictionary felt like enumerating.
		List<string> ordered = [.. WithoutGroupsReachingIntoAnotherArea(areaId, candidates, coverage)
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

			// Overlapping siblings. The bulbs only this group holds still have to be lit, so they are managed
			// individually — the group they came in through already passed every filter, so they are not
			// strangers to the room; only the exclude label and liveness get to stop them here.
			List<string> alone = [.. unclaimed.Where(bulb => !IsExcluded(bulb)).Where(IsLive)];

			_logger.LogWarning(
				"Area '{Area}': light group '{Group}' shares {Shared} of its bulbs with {Rivals} while containing neither, "
				+ "so it is not used as a group here — commanding both would command those bulbs twice. The {Alone} bulbs only "
				+ "it holds are managed on their own ({Bulbs}). Nest the groups, or stop them overlapping, to settle it properly.",
				areaId, candidate, covers.Count - unclaimed.Count, RivalsOf(kept, coverage, covers), alone.Count,
				alone.Count > 0 ? string.Join(", ", alone) : "none — the rest are excluded or unavailable");

			kept.AddRange(alone);
			claimed.UnionWith(covers);
		}

		if (kept.Count != candidates.Count)
			_logger.LogDebug("Area {Area}: {Total} discovered lights settle into {Count} once their groups have had their say.",
				areaId, candidates.Count, kept.Count);

		return kept;
	}

	/// <summary>
	///     Drops the groups that reach past the area's own walls, naming the room they were reaching into.
	/// </summary>
	/// <remarks>
	///     Home Assistant lets a group hold bulbs from anywhere, and one live instance has a living-room group
	///     holding the kitchen's group (2026-07-27). Preferring that group would put the living room in charge of
	///     the kitchen's lighting: the two areas take turns setting each other's brightness, and whichever vacancy
	///     timeout fires first switches the lights off on somebody standing in the other room. There is no way to
	///     keep the group and not command what is inside it, so the area boundary wins and the room falls back to
	///     the lights it owns — usually the inner group that stays inside it, which is still a group, so
	///     "prefer groups" survives whole and only the reach is clipped.
	///     <para>
	///         Assigned to no area at all does not count as foreign: a bulb whose group carries the area assignment
	///         is the ordinary way to set a house up, and treating it as another room's would clip nearly every group
	///         in the house.
	///     </para>
	///     <para>
	///         The one thing worse than a shared bulb is a dark room, so an area left with nothing after the clip
	///         keeps its reaching group, and the warning is then the only signal there is.
	///     </para>
	/// </remarks>
	private List<string> WithoutGroupsReachingIntoAnotherArea(
		string areaId,
		List<string> candidates,
		Dictionary<string, IReadOnlySet<string>> coverage)
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

		foreach ((string group, string areas, string bulbs) in reaching)
			if (kept.Count > 0)
				_logger.LogWarning(
					"Area '{Area}': light group '{Group}' reaches into area '{Other}' ({Bulbs}), so the area drives the lights "
					+ "it owns instead of the group. Two areas commanding the same bulbs set each other's brightness and switch "
					+ "each other off. Split the group, or move those lights, to have the area use it again.",
					areaId, group, areas, bulbs);
			else
				_logger.LogWarning(
					"Area '{Area}': light group '{Group}' reaches into area '{Other}' ({Bulbs}), and the area has no lights of "
					+ "its own to fall back on, so it keeps the group and both areas command those bulbs. Split the group, or "
					+ "assign the area its own lights.",
					areaId, group, areas, bulbs);

		return kept.Count > 0 ? kept : candidates;
	}

	/// <summary>
	///     Which other area holds each of <paramref name="bulbs"/>, for the ones another area holds at all.
	/// </summary>
	/// <remarks>
	///     A reverse sweep of the registry rather than a per-bulb lookup, because <see cref="IAreaRegistry"/> answers
	///     "what is in this area" and the question here is the other way round. One pass over the house, and only
	///     when the area actually has a group to check. A bulb this area holds itself is never foreign, whatever any
	///     other area claims — the room being resolved is the one asking.
	/// </remarks>
	private IReadOnlyDictionary<string, string> AreasHolding(string areaId, HashSet<string> bulbs)
	{
		HashSet<string> own = [.. _registry.EntitiesInArea(areaId)];
		Dictionary<string, string> holders = new(StringComparer.Ordinal);

		foreach (string other in _registry.AreaIds)
		{
			if (string.Equals(other, areaId, StringComparison.Ordinal))
				continue;

			foreach (string entityId in _registry.EntitiesInArea(other))
				if (bulbs.Contains(entityId) && !own.Contains(entityId))
					holders.TryAdd(entityId, other);
		}

		return holders;
	}

	/// <summary>
	///     Every bulb <paramref name="entityId"/> actually commands, following group membership all the way down.
	/// </summary>
	/// <remarks>
	///     A plain bulb commands itself, which is what makes a group and a bulb comparable at all: both answer the
	///     same question, so one selection pass settles group-versus-member, group-versus-group and bulb-versus-bulb
	///     without special cases.
	///     <para>
	///         Home Assistant will happily let a household build a group that contains itself, directly or round a
	///         longer loop, and a resolver that hangs on a misconfiguration takes the whole house down with it. The
	///         walk therefore visits each id once. A loop bottoms out with no bulbs at the end of it, and something
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

	/// <summary>Whether the entity lists members, which is the only thing that makes a light a group.</summary>
	private bool IsGroup(string entityId) => _ha.AttrStringList(entityId, GroupMembersAttribute).Count > 0;

	/// <summary>The already-kept lights an overlapping group is overlapping, so the warning can name them.</summary>
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

		return [.. byDeviceClass.Concat(byLabel)
			.Where(id => !IsExcluded(id))
			.Where(IsLive)
			.Distinct(StringComparer.Ordinal)];
	}

	/// <summary>
	///     Every illuminance sensor the area yields.
	/// </summary>
	/// <remarks>
	///     IsLive matters most here: an area with one working lux sensor and one dead one is ambiguous only if
	///     the dead one counts. On one live instance, an annex area had exactly that pair, and refusing the area over a sensor that
	///     reports nothing would be a poor trade.
	/// </remarks>
	private List<string> DiscoverLuxSensors(string? areaId)
	{
		if (areaId is null)
			return [];

		return [.. _registry.EntitiesInArea(areaId)
			.Where(id => id.HasDomain(SensorDomain))
			.Where(id => string.Equals(DeviceClassOf(id), _global.IlluminanceDeviceClass, StringComparison.OrdinalIgnoreCase))
			.Where(id => !IsExcluded(id))
			.Where(IsLive)
			.Distinct(StringComparer.Ordinal)];
	}

	private bool TryDiscoverLuxSensor(string? areaId, AreaConfig area, out string? luxSensor, out string? error)
	{
		luxSensor = null;
		error = null;

		if (areaId is null)
			return true;

		// Excluded before the ambiguity decision, not after: excluding one of two candidates must leave the other
		// as the single chosen sensor, and excluding the only candidate must leave the room on the sun — never a
		// choice deferred over a sensor the room was told to ignore.
		List<string> candidates = WithoutExcluded(DiscoverLuxSensors(areaId), area);

		switch (candidates.Count)
		{
			case 0:
				// Not an error: an area may legitimately gate on the sun alone.
				return true;

			case 1:
				luxSensor = candidates[0];
				return true;

			default:
				// Two rules pulling against each other, and the resolution matters.
				//
				// Do NOT pick one: an area often holds an illuminance reading that has nothing to do with whether
				// the room is dark. A real house had a kitchen whose candidates included the sensor inside its
				// fridge; gating the ceiling lights on that is the kind of bug nobody ever tracks down.
				//
				// But refusing the area outright was worse. It left a room with two lux sensors strictly worse off
				// than a room with none — the room with none gates on the sun and works — so the better-instrumented
				// the house, the more rooms simply stopped. On one installation this disabled 8 of 17 rooms,
				// including the living room, kitchen and hallway.
				//
				// So do exactly what a room with no sensor does: gate on the house-wide outdoor sensor or the sun,
				// and say clearly which sensors were found and how to choose between them.
				_logger.LogWarning(
					"Area '{Area}' has {Count} illuminance sensors ({Candidates}), so none is used on its own: this area "
					+ "decides darkness from the outdoor lux sensor or the sun. Set LuxSensor on the area to use one of them.",
					areaId, candidates.Count, string.Join(", ", candidates));

				return true;
		}
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
