namespace AdaptiveLighting.Abstractions;

/// <summary>A floor as the engine and UI need it: identity, display name, and stacking order.</summary>
/// <remarks><c>Level</c> is HA's own number and orders floors; null when the house never set one.</remarks>
public sealed record AreaFloor(string Id, string Name, int? Level);

/// <summary>Everything the engine needs from the Home Assistant area registry, and nothing else.</summary>
/// <remarks>
///     A seam because <c>NetDaemon.HassModel.Entities.Area</c> cannot be constructed outside its own assembly, so
///     a test can implement <c>IHaContext</c> but never return an area with entities in it.
/// </remarks>
public interface IAreaRegistry
{
	IReadOnlyList<string> AreaIds { get; }

	bool AreaExists(string areaId);

	/// <remarks>Read, never stored. <see cref="Engine.AreaNaming"/> is the only place the fallback order lives.</remarks>
	string? NameOf(string areaId);

	/// <summary>The entity ids assigned to <paramref name="areaId"/>, directly or through a device.</summary>
	IReadOnlyList<string> EntitiesInArea(string areaId);

	/// <summary>The registry labels on <paramref name="entityId"/>, by id and by name, since HA accepts either.</summary>
	IReadOnlyList<string> LabelsOf(string entityId);

	/// <summary>
	///     The registry labels on the area itself, by id and by name. Distinct from <see cref="LabelsOf"/>: labelling
	///     an area does not label the entities in it.
	/// </summary>
	IReadOnlyList<string> LabelsOfArea(string areaId);

	/// <summary>
	///     The device <paramref name="entityId"/> belongs to, or <c>null</c> for a group helper, a template entity
	///     and an unknown id alike.
	/// </summary>
	/// <remarks>Two entities are the same hardware when they share a device id; a group helper has none.</remarks>
	string? DeviceOf(string entityId);

	AreaFloor? FloorOf(string areaId);
}
