using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>What the daylight-sensor picker offers, in order: the house's own outdoor sensor, then the
/// illuminance sensors this room's own Home Assistant area resolves to.</summary>
/// <remarks>
///     Order is not alphabetical, unlike <see cref="RoomPageModel"/>'s other pickers: leaving the field blank
///     already means "the house's", so the house sensor has to read first rather than fall wherever its name
///     sorts. A sensor already saved on the room is kept even when it falls outside both of those, so a saved
///     configuration never appears to lose its sensor.
/// </remarks>
public static class DaylightSensorScope
{
	/// <param name="scoped">Whether the picker is scoped to the room at all. Unscoped, it offers
	/// <paramref name="everywhere"/> exactly as before — the same fallback <see cref="RoomPageModel"/>'s other
	/// pickers use once "offer entities from the whole house" is ticked.</param>
	/// <param name="ownArea">This room's own illuminance sensors.</param>
	/// <param name="everywhere">Every illuminance sensor in the house, offered once the scope is widened, and
	/// searched for a friendly name before falling back to the id.</param>
	/// <param name="houseSensor">The house's outdoor sensor id, or <c>null</c> while none is named.</param>
	/// <param name="configured">The sensor already saved on this room, or <c>null</c>.</param>
	/// <param name="nameOf">A friendly name for an id neither list resolves — a saved sensor of a device class
	/// this house does not treat as illuminance still reads by name rather than by a bare id.</param>
	public static IReadOnlyList<EntityOption> For(
		bool scoped,
		IReadOnlyList<EntityOption> ownArea,
		IReadOnlyList<EntityOption> everywhere,
		string? houseSensor,
		string? configured,
		Func<string, string> nameOf)
	{
		ArgumentNullException.ThrowIfNull(ownArea);
		ArgumentNullException.ThrowIfNull(everywhere);
		ArgumentNullException.ThrowIfNull(nameOf);

		if (!scoped)
			return everywhere;

		List<EntityOption> ordered = [];
		HashSet<string> added = new(StringComparer.Ordinal);

		void Add(string? entityId)
		{
			if (entityId is not { Length: > 0 } || !added.Add(entityId))
				return;

			ordered.Add(Find(entityId, everywhere) ?? Find(entityId, ownArea) ?? new EntityOption(entityId, nameOf(entityId), null));
		}

		Add(houseSensor);

		foreach (EntityOption option in ownArea)
			Add(option.EntityId);

		// Kept even from outside both lists above, so a saved room never appears to lose its sensor.
		Add(configured);

		return ordered;
	}

	private static EntityOption? Find(string entityId, IReadOnlyList<EntityOption> options) =>
		options.FirstOrDefault(option => string.Equals(option.EntityId, entityId, StringComparison.Ordinal));
}
