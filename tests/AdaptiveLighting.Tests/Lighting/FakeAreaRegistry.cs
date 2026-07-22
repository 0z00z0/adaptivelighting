using AdaptiveLighting.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     An in-memory area registry: area id to entity ids, entity id to labels, and area id to floor.
/// </summary>
/// <remarks>
///     This fake is the whole reason <see cref="IAreaRegistry"/> exists. HassModel's <c>Area</c> cannot be built
///     in a test — non-public constructors, a computed <c>Entities</c> collection and an internal navigator — so
///     a resolver written against <c>IHaRegistry</c> would have been untestable by construction. The seam is one
///     interface with five members, and it buys every discovery rule below, plus the floored houses the grouping
///     rules need (<c>Floor</c> is as unconstructable as <c>Area</c>, which is why <see cref="AreaFloor"/> exists).
/// </remarks>
public sealed class FakeAreaRegistry : IAreaRegistry
{
	/// <summary>Area id to the entity ids in it.</summary>
	public Dictionary<string, List<string>> Areas { get; } = new(StringComparer.Ordinal);

	/// <summary>Entity id to its registry labels.</summary>
	public Dictionary<string, List<string>> Labels { get; } = new(StringComparer.Ordinal);

	/// <summary>Area id to the floor it sits on. An area absent from here is floorless, as most houses' are.</summary>
	public Dictionary<string, AreaFloor> Floors { get; } = new(StringComparer.Ordinal);

	/// <inheritdoc/>
	public IReadOnlyList<string> AreaIds => [.. Areas.Keys];

	/// <inheritdoc/>
	public bool AreaExists(string areaId) => Areas.ContainsKey(areaId);

	/// <inheritdoc/>
	public IReadOnlyList<string> EntitiesInArea(string areaId) => Areas.GetValueOrDefault(areaId) ?? [];

	/// <inheritdoc/>
	public IReadOnlyList<string> LabelsOf(string entityId) => Labels.GetValueOrDefault(entityId) ?? [];

	/// <inheritdoc/>
	public AreaFloor? FloorOf(string areaId) => Floors.GetValueOrDefault(areaId);
}
