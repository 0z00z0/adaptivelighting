using AdaptiveLighting.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     An in-memory area registry: area id to entity ids, and entity id to labels.
/// </summary>
/// <remarks>
///     This fake is the whole reason <see cref="IAreaRegistry"/> exists. HassModel's <c>Area</c> cannot be built
///     in a test — non-public constructors, a computed <c>Entities</c> collection and an internal navigator — so
///     a resolver written against <c>IHaRegistry</c> would have been untestable by construction. The seam is one
///     interface with four members, and it buys every discovery rule below.
/// </remarks>
public sealed class FakeAreaRegistry : IAreaRegistry
{
	/// <summary>Area id to the entity ids in it.</summary>
	public Dictionary<string, List<string>> Areas { get; } = new(StringComparer.Ordinal);

	/// <summary>Entity id to its registry labels.</summary>
	public Dictionary<string, List<string>> Labels { get; } = new(StringComparer.Ordinal);

	/// <inheritdoc/>
	public IReadOnlyList<string> AreaIds => [.. Areas.Keys];

	/// <inheritdoc/>
	public bool AreaExists(string areaId) => Areas.ContainsKey(areaId);

	/// <inheritdoc/>
	public IReadOnlyList<string> EntitiesInArea(string areaId) => Areas.GetValueOrDefault(areaId) ?? [];

	/// <inheritdoc/>
	public IReadOnlyList<string> LabelsOf(string entityId) => Labels.GetValueOrDefault(entityId) ?? [];
}
