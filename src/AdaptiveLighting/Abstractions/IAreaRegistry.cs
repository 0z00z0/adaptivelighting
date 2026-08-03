namespace AdaptiveLighting.Abstractions;

/// <summary>A floor as the engine and UI need it: identity, display name, and stacking order.</summary>
/// <param name="Id">The registry floor id.</param>
/// <param name="Name">The display name ("Ground floor", "Loftet").</param>
/// <param name="Level">HA's level number, used only for ordering. Null when the house never set one.</param>
public sealed record AreaFloor(string Id, string Name, int? Level);

/// <summary>Everything the engine needs from the Home Assistant area registry, and nothing else.</summary>
/// <remarks>
///     A seam because <c>NetDaemon.HassModel.Entities.Area</c> cannot be constructed outside its own assembly: its
///     constructors are non-public and <c>Area.Entities</c> has no setter, so a test can implement <c>IHaContext</c>
///     but never return an area with entities in it.
/// </remarks>
public interface IAreaRegistry
{
	/// <summary>Every known area id, for the "did you mean" hint on a misspelled <c>AreaId</c>.</summary>
	IReadOnlyList<string> AreaIds { get; }

	bool AreaExists(string areaId);

	/// <summary>
	///     Home Assistant's display name for <paramref name="areaId"/>, or <c>null</c> when the area is unknown or
	///     was never given one.
	/// </summary>
	/// <remarks>Read, never stored. <see cref="Engine.AreaNaming"/> is the only place the fallback order is written down.</remarks>
	string? NameOf(string areaId);

	/// <summary>
	///     The entity ids assigned to <paramref name="areaId"/>, directly or through a device. Empty when the area
	///     is unknown.
	/// </summary>
	IReadOnlyList<string> EntitiesInArea(string areaId);

	/// <summary>
	///     The registry labels on <paramref name="entityId"/>, by id and by name, since HA lets a household refer to
	///     a label either way. Empty when unlabelled or unknown.
	/// </summary>
	IReadOnlyList<string> LabelsOf(string entityId);

	/// <summary>
	///     The id of the Home Assistant device <paramref name="entityId"/> belongs to, or <c>null</c> when it
	///     belongs to none, which is what a group helper, a template entity and an unknown id all look like.
	/// </summary>
	/// <remarks>
	///     The only exact answer to "these two entities are the same piece of hardware". One four-channel fixture
	///     presents five light entities, and the engine was commanding all five. Every duplicate has a device id and
	///     every group helper has none, so the device is both the signal and its own guard.
	/// </remarks>
	string? DeviceOf(string entityId);

	AreaFloor? FloorOf(string areaId);
}
