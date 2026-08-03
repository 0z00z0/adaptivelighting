using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>
///     An area with every entity reference turned into a concrete id, ready to build a controller from.
/// </summary>
/// <remarks>
///     <c>Lights</c> and <c>MotionSensors</c> are never empty; <c>LuxSensors</c> may be. <c>FollowOutdoorLux</c>
///     stays unresolved so a room that follows the house keeps following when the house changes its mind.
///     <c>LightsSupportColorTemp</c> is read once here, because the alternative is a state read per light per tick.
/// </remarks>
public sealed record ResolvedArea(
	string Name,
	AreaSettings Settings,
	IReadOnlyList<string> Lights,
	IReadOnlyList<string> MotionSensors,
	IReadOnlyList<string> LuxSensors,
	IReadOnlyList<string> IgnoreWhenOn,
	bool FollowOutdoorLux = false,
	bool? LightsSupportColorTemp = null)
{
	/// <summary>Entities that stop the engine switching this area's lights off while they apply.</summary>
	public IReadOnlyList<string> KeepLitWhenOn { get; init; } = [];

	/// <summary>Whether <see cref="IgnoreWhenOn"/> applies while its entities read off instead of on.</summary>
	public bool IgnoreWhenOnInverted { get; init; }

	/// <summary>Whether <see cref="KeepLitWhenOn"/> applies while its entities read off instead of on.</summary>
	public bool KeepLitWhenOnInverted { get; init; }

	/// <summary>A scene run in place of this area's levels when movement lights it, or <c>null</c>.</summary>
	public string? SceneOnMotion { get; init; }

	/// <summary>A scene run in place of switching this area off when it goes empty, or <c>null</c>.</summary>
	public string? SceneWhenEmpty { get; init; }

	/// <summary>How this area's warmth is commanded, with <see cref="ColorControl.Auto"/> already decided.</summary>
	/// <remarks>
	///     Null capability means no light could be read, which is not evidence that none has a colour temperature,
	///     so Auto stays on <see cref="ColorControl.Kelvin"/> until a fixture says otherwise.
	/// </remarks>
	public ColorControl EffectiveColorControl =>
		Settings.ColorControl is not ColorControl.Auto ? Settings.ColorControl
		: LightsSupportColorTemp == false ? ColorControl.EqualChannels
		: ColorControl.Kelvin;
}

/// <summary>What discovery finds in one area, before that area's explicit lists have had their say.</summary>
/// <remarks>
///     Lets the configuration page ask "what would this area resolve to?" through the engine's own rules rather
///     than an approximation that would drift. <c>LuxSensors</c> is the candidate list, not a chosen sensor.
/// </remarks>
public sealed record AreaDiscovery(
	IReadOnlyList<string> Lights,
	IReadOnlyList<string> MotionSensors,
	IReadOnlyList<string> LuxSensors);

/// <summary>
///     Turns an <see cref="AreaConfig"/> into a <see cref="ResolvedArea"/>, discovering from the HA registry what
///     the config did not spell out.
/// </summary>
/// <remarks>
///     Discovery-first: an area id and a device class stay true across a rename, where a hand-listed entity does
///     not.
/// </remarks>
public sealed class AreaEntityResolver
{
	private const string LightDomain = "light";
	private const string BinarySensorDomain = "binary_sensor";
	private const string SensorDomain = "sensor";
	private const string DeviceClassAttribute = "device_class";
	private const string GroupMembersAttribute = "entity_id";
	private const string SupportedColorModesAttribute = "supported_color_modes";
	private const string ColorTempMode = "color_temp";

	// The modes that give equal channels somewhere to land. Nothing else counts as evidence against kelvin.
	private static readonly string[] ColourChannelModes = ["rgb", "rgbw", "rgbww", "hs", "xy"];
	private const string UnavailableState = "unavailable";
	private const string UnknownState = "unknown";

	private readonly IHaContext _ha;
	private readonly IAreaRegistry _registry;
	private readonly GlobalConfig _global;
	private readonly ILogger _logger;

	/// <summary>Creates a resolver. Device classes and group membership come from <c>ha</c>, not the registry.</summary>
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
	///     The same filtering from the same code: the exclude label, the group de-duplication and <c>IsLive</c>
	///     all apply. An area the registry does not know yields nothing.
	/// </remarks>
	public AreaDiscovery DiscoverArea(string areaId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(areaId);

		return new AreaDiscovery(
			DiscoverLights(areaId),
			DiscoverMotionSensors(areaId),
			DiscoverLuxSensors(areaId));
	}

	/// <summary>Resolves <paramref name="area"/>, or explains why it cannot be.</summary>
	/// <returns><c>false</c> when the area must be skipped. A skipped area is never a reason to fail the house.</returns>
	public bool TryResolve(AreaConfig area, AreaSettings defaults, out ResolvedArea? resolved, out string? error)
	{
		ArgumentNullException.ThrowIfNull(area);
		ArgumentNullException.ThrowIfNull(defaults);

		resolved = null;
		error = null;

		// The one place a name enters the engine. Everything downstream reads the snapshot's AreaName.
		string name = AreaNaming.DisplayName(area, _registry);
		string? areaId = null;

		if (area.AreaId is { Length: > 0 } configuredArea)
		{
			if (!_registry.AreaExists(configuredArea))
			{
				// A registry that has not answered yet lists nothing, and "Known ids: ." reads as a fault here
				// and not as a connection still coming up.
				error = _registry.AreaIds.Count > 0
					? $"Home Assistant has no area '{configuredArea}'. It must be the area's id, the slug, not its display name. Known ids: {string.Join(", ", _registry.AreaIds.Order(StringComparer.Ordinal))}."
					: $"Home Assistant has no area '{configuredArea}', and lists no areas at all yet; it may still be starting up.";
				return false;
			}

			areaId = configuredArea;
		}

		// An explicit list bypasses both labels, discovery and the per-room exclude. The rules get no veto.
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

		// One sensor by construction, which is how a room opts out of an average it disagrees with.
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

		bool? colorTemp = ColorTempCapabilityOf(lights);

		if (settings.ColorControl is ColorControl.Auto && colorTemp == false)
			_logger.LogInformation(
				"Area {Area}: no light reports a '{Mode}' colour mode, so its warmth is driven as equal colour channels "
				+ "and the schedule's kelvin figure does not reach it. Set ColorControl to Kelvin to overrule that.",
				name, ColorTempMode);

		resolved = new ResolvedArea(
			name, settings, lights, motion, lux, [.. area.IgnoreWhenOn ?? []], area.FollowOutdoorLux == true, colorTemp)
		{
			KeepLitWhenOn = [.. area.KeepLitWhenOn ?? []],
			IgnoreWhenOnInverted = area.IgnoreWhenOnInverted == true,
			KeepLitWhenOnInverted = area.KeepLitWhenOnInverted == true,
			SceneOnMotion = Trimmed(area.SceneOnMotion),
			SceneWhenEmpty = Trimmed(area.SceneWhenEmpty)
		};

		return true;
	}

	// A blank scene id would reach scene.turn_on, which throws on one, from inside an area's lock.
	private static string? Trimmed(string? entityId) =>
		string.IsNullOrWhiteSpace(entityId) ? null : entityId.Trim();

	/// <summary>Whether any of <paramref name="lights"/> advertises a colour temperature, or <c>null</c> when none said.</summary>
	/// <remarks>
	///     A light with no readable <c>supported_color_modes</c> is no evidence either way, so it is not counted.
	///     False needs at least one fixture that answered, or a house still starting up would resolve every room to
	///     equal channels and leave it there until the next rebuild.
	/// </remarks>
	private bool? ColorTempCapabilityOf(List<string> lights)
	{
		bool anyColourChannel = false;

		foreach (string light in lights)
		{
			IReadOnlyList<string> modes = _ha.AttrStringList(light, SupportedColorModesAttribute);
			if (modes.Count == 0)
				continue;

			if (modes.Contains(ColorTempMode, StringComparer.OrdinalIgnoreCase))
				return true;

			// Only a fixture with somewhere to put equal channels is evidence for them. A brightness-only dimmer
			// answers this attribute and offers no colour at all, and reading it as "no colour temperature" would
			// take the kelvin away from every room holding one beside a real lamp.
			if (ColourChannelModes.Any(mode => modes.Contains(mode, StringComparer.OrdinalIgnoreCase)))
				anyColourChannel = true;
		}

		return anyColourChannel ? false : null;
	}

	/// <summary>Why an area yielded no lights, in the words that name the fix.</summary>
	/// <remarks>
	///     Re-runs discovery without the include label, because an area with no lights at all must not be told to
	///     go and label them.
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

	// respectIncludeLabel is false only on the failure path, to tell "no lights" from "all filtered out".
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
	///     The selection is identical across the three domains; only the wording of the harm differs.
	/// </summary>
	/// <remarks>
	///     <c>OneEntityPerDevice</c> says whether several entities of this kind on one device are the same thing.
	///     True for lights and illuminance, false for motion; see the instances below.
	/// </remarks>
	private sealed record GroupRules(
		string Noun,
		string Members,
		string DoubleCost,
		string ReachCost,
		bool OneEntityPerDevice)
	{
		public static readonly GroupRules Lights = new(
			"light",
			"bulbs",
			"commanding both would command those bulbs twice",
			"Two areas commanding the same bulbs set each other's brightness and switch each other off.",
			OneEntityPerDevice: true);

		/// <summary>
		///     The device rule is off here. For a light a device is one lamp, but for motion it is a controller,
		///     and a multi-zone presence sensor exposes genuinely different detection zones on one. Collapsing
		///     those blinds the room wherever the dropped entity was watching, silently.
		/// </summary>
		public static readonly GroupRules Motion = new(
			"motion",
			"sensors",
			"listening to both would fire this room's motion two or three times for every movement",
			"Movement in the other room would light this one, and hold its lights on from empty.",
			OneEntityPerDevice: false);

		/// <summary>
		///     The device rule is on because the area averages its sensors, and one instrument exposed as two
		///     entities would carry double weight in that mean.
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
	///     A group and its members are the same entities twice; the members lose and the group wins. Three shapes
	///     defeat a one-level comparison. Nesting: a group of groups lists groups, so membership is followed all
	///     the way down through <see cref="LeavesOf"/>. Reach: a group may hold entities another room owns, which
	///     <see cref="WithoutGroupsReachingIntoAnotherArea"/> decides. Overlap: two groups can share members
	///     without either containing the other, so the widest coverage wins and the narrower group is traded for
	///     the members only it holds. A bulb dropped from a room is worse than a bulb commanded twice.
	///     <paramref name="qualifies"/> is the domain test a promoted member must still pass; see the promotion
	///     below for why that is not a formality.
	/// </remarks>
	private List<string> PreferGroups(string areaId, List<string> candidates, GroupRules rules, Func<string, bool> qualifies)
	{
		// Nearly every room is plain bulbs, with nothing for the group pass to settle. The device pass still runs:
		// duplicate channels of one fixture need no group to exist.
		List<string> settled = candidates.Any(IsGroup)
			? SettleGroups(areaId, candidates, rules, qualifies)
			: candidates;

		return rules.OneEntityPerDevice ? OneEntityPerDevice(areaId, settled, rules) : settled;
	}

	private List<string> SettleGroups(string areaId, List<string> candidates, GroupRules rules, Func<string, bool> qualifies)
	{
		Dictionary<string, IReadOnlySet<string>> coverage =
			candidates.ToDictionary(id => id, LeavesOf, StringComparer.Ordinal);

		// Widest first, so the group holding the most members claims them before a narrower rival. OrderByDescending
		// is stable, so equally wide candidates keep the registry's order.
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

			// Overlapping siblings: the members only this group holds are taken individually. The domain check is
			// load-bearing, because group membership is whatever Home Assistant put in the attribute. A switch
			// promoted out of a light group reaches ILightActuator, which calls light.turn_on unconditionally, so
			// every command is a service call HA rejects. For illuminance `qualifies` carries the device class too.
			List<string> alone = [.. unclaimed
				.Where(qualifies)
				.Where(member => !IsExcluded(member))
				.Where(IsLive)];

			_logger.LogWarning(
				"Area '{Area}': {Kind} group '{Group}' shares {Shared} of its {Members} with {Rivals} while containing neither, "
				+ "so it is not used as a group here: {Cost}. The {Alone} {AloneMembers} only it holds are used on their own "
				+ "({Ids}). Nest the groups, or stop them overlapping, to settle it properly.",
				areaId, rules.Noun, candidate, covers.Count - unclaimed.Count, rules.Members,
				RivalsOf(kept, coverage, covers), rules.DoubleCost, alone.Count, rules.Members,
				alone.Count > 0 ? string.Join(", ", alone) : "none; the rest are excluded or unavailable");

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
	///     A device id is a registry fact where a name suffix is a guess, so where both could answer this one is
	///     believed. A group helper has no device of its own, so it is never a duplicate; it stands for the
	///     devices beneath it and, having claimed them, leaves nothing for the loose entities on the same hardware.
	///     For a device no group covers the order is: group, then breadth, then the entity its siblings extend
	///     with an underscore, then the shortest id ordinally. The last two keep the answer independent of
	///     registry order.
	///     A candidate covering several devices of which only some are claimed is kept: dropping it would lose the
	///     hardware nobody else holds.
	/// </remarks>
	private List<string> OneEntityPerDevice(string areaId, List<string> candidates, GroupRules rules)
	{
		Dictionary<string, IReadOnlySet<string>> devices =
			candidates.ToDictionary(id => id, DevicesOf, StringComparer.Ordinal);

		// Answered before the ordering below, so a healthy room's list is never reshuffled by a rule with no work.
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

			// No device beneath it: a group of template entities, or a helper. Nothing to duplicate, nothing to
			// claim, so it is kept and stays out of this rule's way.
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
				+ "one fixture, most often a combined entity beside its own colour channels, and using them separately "
				+ "sends one piece of hardware several commands at once.",
				areaId, folded.Count, rules.Noun, winner, string.Join(", ", folded.Order(StringComparer.Ordinal)), winner);

		return kept;
	}

	/// <summary>
	///     The candidates whose siblings on the same device are named as extensions of them: the combined entity
	///     of a multi-channel fixture.
	/// </summary>
	/// <remarks>
	///     The underscore is load-bearing. Without it <c>light.stue_tak_1</c> counts as the parent of
	///     <c>light.stue_tak_11</c>, a different lamp on a different device.
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
	///     Every device <paramref name="entityId"/> stands for: its own, or those of everything it groups. A group
	///     has no device of its own, so without following the membership it would stand for and claim nothing.
	/// </summary>
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
	///     Home Assistant lets a group hold entities from anywhere, and there is no way to keep the group without
	///     commanding what is inside it, so the area boundary wins and the room falls back to what it owns.
	///     Assigned to no area at all does not count as foreign; an entity whose group carries the area assignment
	///     is the ordinary way to set a house up, and treating it as another room's would clip nearly every group.
	///     An area left with nothing after the clip keeps its reaching group, and the warning is the only signal.
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
	///     A reverse sweep, because <see cref="IAreaRegistry"/> answers "what is in this area" and the question is
	///     the other way round. An entity this area holds itself is never foreign, whatever another area claims.
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

	/// <summary>Every leaf entity <paramref name="entityId"/> stands for, following group membership all the way down.</summary>
	/// <remarks>
	///     A plain entity stands for itself, which is what makes a group and a member comparable at all, so one
	///     selection pass settles every combination without special cases.
	///     Home Assistant lets a household build a group containing itself, so the walk visits each id once. A
	///     loop bottoms out with no leaves, and something must still be returned, so such a group stands for
	///     itself and everything it reaches.
	///     Public because the cross-room audit asks it too: two areas reaching one bulb through different groups
	///     settle on different ids, so the comparison has to be made down here.
	/// </remarks>
	/// <returns>The leaves, never empty.</returns>
	public IReadOnlySet<string> LeavesOf(string entityId)
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

	// The entity_id attribute and nothing else. A name is no evidence: light.kontorlys_alle is a real group of two
	// ceiling lights while light.kontor_taklys_alle carries no membership at all despite reading like one.
	private bool IsGroup(string entityId) => _ha.AttrStringList(entityId, GroupMembersAttribute).Count > 0;

	private static string RivalsOf(List<string> kept, Dictionary<string, IReadOnlySet<string>> coverage, IReadOnlySet<string> covers) =>
		string.Join(", ", kept.Where(id => coverage.TryGetValue(id, out IReadOnlySet<string>? theirs) && theirs.Overlaps(covers)));

	/// <summary>Whether the entity is something Home Assistant can actually act on right now.</summary>
	/// <remarks>
	///     The registry lists rows, not devices: a disabled or never-loaded entity still comes back from
	///     <see cref="IAreaRegistry.EntitiesInArea"/> with no state at all. <c>unavailable</c> and <c>unknown</c>
	///     go too. The cost is that a lamp merely offline at startup stays out of its area until the engine is
	///     rebuilt, which the Configuration page can do without a restart.
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
	///     Every motion source the area yields, its groups already preferred over their own members. A group and
	///     its members are the same movement two or three times, each re-arming the vacancy timer.
	/// </summary>
	private List<string> DiscoverMotionSensors(string? areaId)
	{
		if (areaId is null)
			return [];

		IReadOnlyList<string> inArea = _registry.EntitiesInArea(areaId);

		IEnumerable<string> byDeviceClass = inArea
			.Where(id => id.HasDomain(BinarySensorDomain))
			.Where(id => _global.EffectiveMotionDeviceClasses.Contains(DeviceClassOf(id), StringComparer.OrdinalIgnoreCase));

		// mmWave and other presence hardware routinely reports an unexpected device class, so the label is how a
		// household says "this one counts".
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
	///     Two passes settle false plurality before anything counts the candidates: a group beats the sensors
	///     inside it, and the device rule keeps one entity per instrument. What survives both is genuinely several
	///     instruments, which is what the area averages.
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

	private bool IsIlluminance(string entityId) =>
		entityId.HasDomain(SensorDomain)
		&& string.Equals(DeviceClassOf(entityId), _global.IlluminanceDeviceClass, StringComparison.OrdinalIgnoreCase);

	/// <summary>The illuminance sensors the area will read, all of them, for the gate to average.</summary>
	/// <remarks>
	///     The per-room exclude is applied here, not earlier, so a household can take one sensor out of the mean
	///     by id. A fridge's own probe is answered by naming it, not by the whole room going without.
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

	// The per-room twin of the exclude label, by id. Discovered lists only: an explicit Lights or MotionSensors
	// list is the owner overruling discovery, and no rule re-filters it.
	private static List<string> WithoutExcluded(List<string> discovered, AreaConfig area)
	{
		if (area.ExcludeEntities is not { Count: > 0 } excluded)
			return discovered;

		HashSet<string> drop = new(excluded, StringComparer.Ordinal);
		return [.. discovered.Where(id => !drop.Contains(id))];
	}

	private string? DeviceClassOf(string entityId) => _ha.AttrString(entityId, DeviceClassAttribute);

	private bool IsExcluded(string entityId) => HasLabel(entityId, _global.ExcludeLabel);

	// No label configured means everything passes. Checked after the exclusion, never instead of it, so a light
	// carrying both labels stays out.
	private bool IsIncluded(string entityId) =>
		_global.IncludeLabel is not { Length: > 0 } include || HasLabel(entityId, include);

	private bool HasLabel(string entityId, string label) =>
		_registry.LabelsOf(entityId).Contains(label, StringComparer.OrdinalIgnoreCase);
}
