using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Extensions;

/// <summary>
///     The <see cref="IHaRegistry"/> questions the engine actually asks, each in one expression — the whole
///     of the engine's former <c>HaAreaRegistry</c> body, made available to everyone.
/// </summary>
public static class RegistryExtensions
{
	/// <summary>Every area id the registry knows.</summary>
	public static IReadOnlyList<string> AreaIds(this IHaRegistry registry) =>
		[.. registry.Areas.Select(area => area.Id).OfType<string>()];

	/// <summary>Whether an area with id <paramref name="areaId"/> exists.</summary>
	public static bool AreaExists(this IHaRegistry registry, string areaId) => registry.GetArea(areaId) is not null;

	/// <summary>The entity ids assigned to <paramref name="areaId"/>, distinct and ordinal. Empty when the area is unknown.</summary>
	public static IReadOnlyList<string> EntityIdsInArea(this IHaRegistry registry, string areaId) =>
		registry.GetArea(areaId) is { } area
			? [.. area.Entities.Select(entity => entity.EntityId).Distinct(StringComparer.Ordinal)]
			: [];

	/// <summary>
	///     The floor <paramref name="areaId"/> sits on, or <c>null</c> when the area is unknown or the house never
	///     put it on one. Floors are optional in Home Assistant, so <c>null</c> is an ordinary answer, not a fault.
	/// </summary>
	public static Floor? FloorOf(this IHaRegistry registry, string areaId) => registry.GetArea(areaId)?.Floor;

	/// <summary>The labels on <paramref name="entityId"/> — both label ids and names. Empty when it has none.</summary>
	public static IReadOnlyList<string> LabelsOf(this IHaRegistry registry, string entityId) =>
		registry.GetEntityRegistration(entityId)?.Labels is { } labels
			? [.. labels.SelectMany(label => new[] { label.Id, label.Name }).OfType<string>()]
			: [];

	/// <summary>Whether <paramref name="entityId"/> carries <paramref name="label"/> (id or name, ordinal-ignore-case).</summary>
	public static bool HasLabel(this IHaRegistry registry, string entityId, string label) =>
		registry.LabelsOf(entityId).Contains(label, StringComparer.OrdinalIgnoreCase);

	/// <summary>The entity ids in <paramref name="areaId"/> that are in <paramref name="domain"/>.</summary>
	public static IReadOnlyList<string> EntityIdsInAreaByDomain(this IHaRegistry registry, string areaId, string domain) =>
		[.. registry.EntityIdsInArea(areaId).Where(id => id.HasDomain(domain))];
}
