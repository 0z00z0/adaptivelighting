namespace AdaptiveLighting.Abstractions;

/// <summary>A floor as the engine and UI need it: identity, display name, and stacking order.</summary>
/// <remarks>
///     A record of three scalars rather than HassModel's own <c>Floor</c>, for the same reason
///     <see cref="IAreaRegistry"/> exists at all: <c>Floor</c> cannot be constructed outside its own assembly, so
///     a test could never build a floored house. Everything the two screens group by is here and nothing else is.
/// </remarks>
/// <param name="Id">The registry floor id.</param>
/// <param name="Name">The display name ("Ground floor", "Loftet").</param>
/// <param name="Level">HA's level number, used only for ordering. Null when the house never set one.</param>
public sealed record AreaFloor(string Id, string Name, int? Level);

/// <summary>
///     Everything the engine needs from the Home Assistant area registry, and nothing else.
/// </summary>
/// <remarks>
///     <para>
///         This seam exists for one concrete reason: <c>NetDaemon.HassModel.Entities.Area</c> cannot be
///         constructed outside its own assembly. Its constructors are non-public and take an internal registry
///         navigator, and <c>Area.Entities</c> has no setter — it is computed by navigating that navigator. So a
///         test can implement <c>IHaRegistry</c> but can never return an <c>Area</c> with entities in it, which
///         would leave every discovery rule in <see cref="Engine.AreaEntityResolver"/> unverifiable.
///     </para>
///     <para>
///         Discovery is the feature the whole configuration design rests on, so it does not get to be the
///         untested part. Six members of registry navigation behind an interface buys that back, and follows
///         the same pattern as the engine's other seams.
///     </para>
/// </remarks>
public interface IAreaRegistry
{
	/// <summary>Every known area id, for the "did you mean" hint on a misspelled <c>AreaId</c>.</summary>
	IReadOnlyList<string> AreaIds { get; }

	/// <summary>Whether <paramref name="areaId"/> names a real area.</summary>
	bool AreaExists(string areaId);

	/// <summary>
	///     Home Assistant's display name for <paramref name="areaId"/> — "Kjeller - Bad" for <c>kjeller_bad</c> —
	///     or <c>null</c> when the area is unknown or was never given one.
	/// </summary>
	/// <remarks>
	///     The seam had no name accessor at all, so every surface fell back to the slug for rooms discovery had
	///     proposed. Read rather than stored: see <see cref="Engine.AreaNaming"/>, which is the only place the
	///     fallback order is written down.
	/// </remarks>
	string? NameOf(string areaId);

	/// <summary>
	///     The entity ids assigned to <paramref name="areaId"/>, directly or through a device. Empty when the
	///     area is unknown.
	/// </summary>
	IReadOnlyList<string> EntitiesInArea(string areaId);

	/// <summary>
	///     The registry labels on <paramref name="entityId"/>, by id and by name — HA lets a household refer to
	///     a label either way, and the engine should not care which they used. Empty when unlabelled or unknown.
	/// </summary>
	IReadOnlyList<string> LabelsOf(string entityId);

	/// <summary>
	///     The id of the Home Assistant <i>device</i> <paramref name="entityId"/> belongs to, or <c>null</c> when
	///     it belongs to none — which is what a group helper, a template entity and an unknown id all look like.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Here because "these two entities are the same piece of hardware" has no other exact answer. One
	///         live instance's office holds five light entities on one device — a four-channel RGBW fixture's
	///         <c>_nw</c>, <c>_ww</c> and two more channels, plus the fixture's own combined entity — and the
	///         engine was commanding all five, which is one lamp told four contradictory things and four times the
	///         service calls. Every one of those duplicates has a device id and every group helper has none, so the
	///         device is both the signal and its own guard.
	///     </para>
	///     <para>
	///         The alternative in use elsewhere is to infer channels from id suffixes (<c>LightAudit</c> does, and
	///         still earns its keep for the entities that are genuinely foreign rather than duplicated). A suffix
	///         is a naming convention; a device is a fact the registry records, so where both can answer, this one
	///         is believed.
	///     </para>
	/// </remarks>
	string? DeviceOf(string entityId);

	/// <summary>The floor <paramref name="areaId"/> sits on, or null — floors are optional in HA.</summary>
	AreaFloor? FloorOf(string areaId);
}
