using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>
///     A zone with every entity reference turned into a concrete id, ready to build a controller from.
/// </summary>
/// <param name="Name">Display name.</param>
/// <param name="Settings">The zone's settings, already merged over the document defaults.</param>
/// <param name="Lights">The lights the zone commands. Never empty.</param>
/// <param name="MotionSensors">The zone's motion sources. Never empty.</param>
/// <param name="LuxSensor">The zone's lux sensor, or <c>null</c> when it has none.</param>
/// <param name="IgnoreWhenOn">Entities that block auto-on while they are on.</param>
public sealed record ResolvedZone(
	string Name,
	ZoneSettings Settings,
	IReadOnlyList<string> Lights,
	IReadOnlyList<string> MotionSensors,
	string? LuxSensor,
	IReadOnlyList<string> IgnoreWhenOn);

/// <summary>
///     What discovery finds in one area, before any zone's explicit lists have had their say.
/// </summary>
/// <remarks>
///     Exists so something other than <see cref="ZoneEntityResolver.TryResolve"/> can ask "what would this area
///     give a zone?" and get the answer from the engine's own rules. The configuration page asks it twice: once
///     per area to label the area picker with what the zone would actually get, and once per zone to scope the
///     entity pickers to the area. Both must agree with the engine, and the only way to guarantee that is for
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
///     Turns a <see cref="ZoneConfig"/> into a <see cref="ResolvedZone"/>, discovering from the HA registry what
///     the config did not spell out.
/// </summary>
/// <remarks>
///     Discovery-first is the point. Hand-listing every light re-creates the drift the old config model suffered:
///     the YAML and the house disagree, silently, until somebody notices a room that stopped working. An area id
///     and a device class stay true across a rename.
/// </remarks>
public sealed class ZoneEntityResolver
{
	private const string LightDomain = "light";
	private const string BinarySensorDomain = "binary_sensor";
	private const string SensorDomain = "sensor";
	private const string DeviceClassAttribute = "device_class";
	private const string GroupMembersAttribute = "entity_id";
	private const string UnavailableState = "unavailable";

	private readonly IHaContext _ha;
	private readonly IAreaRegistry _registry;
	private readonly GlobalConfig _global;
	private readonly ILogger _logger;

	/// <summary>Creates a resolver.</summary>
	/// <param name="ha">Used to read device classes and group membership, which the registry does not carry.</param>
	/// <param name="registry">Source of areas and labels.</param>
	/// <param name="global">Supplies the label and device-class conventions.</param>
	/// <param name="logger">Diagnostics.</param>
	public ZoneEntityResolver(IHaContext ha, IAreaRegistry registry, GlobalConfig global, ILogger logger)
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
	///     Resolves <paramref name="zone"/>, or explains why it cannot be.
	/// </summary>
	/// <param name="zone">The configured zone.</param>
	/// <param name="defaults">The document defaults to merge the zone's overrides onto.</param>
	/// <param name="resolved">The resolved zone on success.</param>
	/// <param name="error">A message for the household on failure.</param>
	/// <returns><c>false</c> when the zone must be skipped. A skipped zone is never a reason to fail the house.</returns>
	public bool TryResolve(ZoneConfig zone, ZoneSettings defaults, out ResolvedZone? resolved, out string? error)
	{
		ArgumentNullException.ThrowIfNull(zone);
		ArgumentNullException.ThrowIfNull(defaults);

		resolved = null;
		error = null;

		string name = zone.DisplayName;
		string? areaId = null;

		if (zone.AreaId is { Length: > 0 } configuredArea)
		{
			if (!_registry.AreaExists(configuredArea))
			{
				error = $"AreaId '{configuredArea}' is not a registry area id (it is the slug, not the display name). Known ids: {string.Join(", ", _registry.AreaIds.Order(StringComparer.Ordinal))}.";
				return false;
			}

			areaId = configuredArea;
		}

		List<string> lights = zone.Lights is { Count: > 0 } explicitLights
			? [.. explicitLights]
			: DiscoverLights(areaId);

		if (lights.Count == 0)
		{
			error = areaId is null
				? "No lights: the zone has neither an AreaId to discover from nor an explicit Lights list."
				: $"No lights discovered in area '{areaId}'. Assign lights to the area in HA, or list them explicitly.";
			return false;
		}

		List<string> motion = zone.MotionSensors is { Count: > 0 } explicitMotion
			? [.. explicitMotion]
			: DiscoverMotionSensors(areaId);

		if (motion.Count == 0)
		{
			error = areaId is null
				? "No motion sensors: the zone has neither an AreaId to discover from nor an explicit MotionSensors list."
				: $"No motion sensors discovered in area '{areaId}'. Assign one to the area, label it '{_global.MotionLabel}', or list it explicitly.";
			return false;
		}

		string? lux;
		if (zone.LuxSensor is { Length: > 0 } explicitLux)
		{
			lux = explicitLux;
		}
		else if (!TryDiscoverLuxSensor(areaId, out lux, out string? luxError))
		{
			error = luxError;
			return false;
		}

		ZoneSettings settings = zone.Effective(defaults);

		_logger.LogInformation(
			"Zone {Zone}: {LightCount} lights ({Lights}), {MotionCount} motion sensors ({Motion}), lux sensor {Lux}.",
			name, lights.Count, string.Join(", ", lights), motion.Count, string.Join(", ", motion), lux ?? "(none)");

		resolved = new ResolvedZone(name, settings, lights, motion, lux, [.. zone.IgnoreWhenOn ?? []]);
		return true;
	}

	private List<string> DiscoverLights(string? areaId)
	{
		if (areaId is null)
			return [];

		List<string> candidates = _registry.EntitiesInArea(areaId)
			.Where(id => id.HasDomain(LightDomain))
			.Where(id => !IsExcluded(id))
			.Where(IsLive)
			.Distinct(StringComparer.Ordinal)
			.ToList();

		// A light group and its members are the same bulbs twice. Commanding both doubles every service call
		// and makes the group's own state a lie mid-transition, so the members lose and the group wins.
		HashSet<string> members = candidates
			.SelectMany(id => _ha.AttrStringList(id, GroupMembersAttribute))
			.ToHashSet(StringComparer.Ordinal);

		if (members.Count > 0)
			_logger.LogDebug("Area {Area}: dropping {Count} light group members in favour of their groups.", areaId, members.Count);

		return [.. candidates.Where(id => !members.Contains(id))];
	}

	/// <summary>
	///     Whether the entity is something Home Assistant can actually act on right now.
	/// </summary>
	/// <remarks>
	///     The registry lists rows, not devices. A disabled or never-loaded entity is still a registry row and
	///     still comes back from <see cref="IAreaRegistry.EntitiesInArea"/>, but it has no state at all — on one live instance
	///     that swept up <c>light.router_socket_status_led</c> and a water sensor's indicator LED and called them
	///     room lighting (2026-07-17). <c>unavailable</c> is dropped too: a light the engine cannot reach is a
	///     light it cannot dim, and including it only produces commands that go nowhere.
	///     <para>
	///         The cost is that a lamp which is merely offline at startup stays out of its zone until the engine
	///         is next rebuilt — which the Configuration page can do without a restart.
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

		if (string.Equals(state.State, UnavailableState, StringComparison.OrdinalIgnoreCase))
		{
			_logger.LogDebug("Ignoring {EntityId}: unavailable.", entityId);
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
	///     the dead one counts. On one live instance, an annex zone had exactly that pair, and refusing the zone over a sensor that
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

	private bool TryDiscoverLuxSensor(string? areaId, out string? luxSensor, out string? error)
	{
		luxSensor = null;
		error = null;

		if (areaId is null)
			return true;

		List<string> candidates = DiscoverLuxSensors(areaId);

		switch (candidates.Count)
		{
			case 0:
				// Not an error: a zone may legitimately gate on the sun alone.
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
				// But refusing the zone outright was worse. It left a room with two lux sensors strictly worse off
				// than a room with none — the room with none gates on the sun and works — so the better-instrumented
				// the house, the more rooms simply stopped. On one installation this disabled 8 of 17 rooms,
				// including the living room, kitchen and hallway.
				//
				// So do exactly what a room with no sensor does: gate on the house-wide outdoor sensor or the sun,
				// and say clearly which sensors were found and how to choose between them.
				_logger.LogWarning(
					"Area '{Area}' has {Count} illuminance sensors ({Candidates}), so none is used on its own: this zone "
					+ "decides darkness from the outdoor lux sensor or the sun. Set LuxSensor on the zone to use one of them.",
					areaId, candidates.Count, string.Join(", ", candidates));

				return true;
		}
	}

	private string? DeviceClassOf(string entityId) => _ha.AttrString(entityId, DeviceClassAttribute);

	private bool IsExcluded(string entityId) => HasLabel(entityId, _global.ExcludeLabel);

	private bool HasLabel(string entityId, string label) =>
		_registry.LabelsOf(entityId).Contains(label, StringComparer.OrdinalIgnoreCase);
}
