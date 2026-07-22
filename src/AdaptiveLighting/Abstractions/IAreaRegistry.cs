namespace AdaptiveLighting.Abstractions;

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
///         untested part. Four methods of registry navigation behind an interface buys that back, and follows
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
	///     The entity ids assigned to <paramref name="areaId"/>, directly or through a device. Empty when the
	///     area is unknown.
	/// </summary>
	IReadOnlyList<string> EntitiesInArea(string areaId);

	/// <summary>
	///     The registry labels on <paramref name="entityId"/>, by id and by name — HA lets a household refer to
	///     a label either way, and the engine should not care which they used. Empty when unlabelled or unknown.
	/// </summary>
	IReadOnlyList<string> LabelsOf(string entityId);
}
