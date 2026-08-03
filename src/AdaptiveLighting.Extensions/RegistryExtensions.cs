using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Extensions;

/// <summary>The <see cref="IHaRegistry"/> questions the engine asks, each in one expression.</summary>
public static class RegistryExtensions
{
	/// <summary>Every area id the registry knows.</summary>
	public static IReadOnlyList<string> AreaIds(this IHaRegistry registry) =>
		[.. registry.Areas.Select(area => area.Id).OfType<string>()];

	/// <summary>Whether an area with id <paramref name="areaId"/> exists.</summary>
	public static bool AreaExists(this IHaRegistry registry, string areaId) => registry.GetArea(areaId) is not null;

	/// <summary>
	///     The display name for <paramref name="areaId"/>, or <c>null</c> when the area is unknown or unnamed. An area
	///     id is a slug, so this is the only thing that turns <c>kjeller_bad</c> back into "Kjeller - Bad".
	/// </summary>
	public static string? AreaNameOf(this IHaRegistry registry, string areaId) =>
		registry.GetArea(areaId)?.Name is { Length: > 0 } name ? name : null;

	/// <summary>The entity ids assigned to <paramref name="areaId"/>, distinct and ordinal. Empty when the area is unknown.</summary>
	public static IReadOnlyList<string> EntityIdsInArea(this IHaRegistry registry, string areaId) =>
		registry.GetArea(areaId) is { } area
			? [.. area.Entities.Select(entity => entity.EntityId).Distinct(StringComparer.Ordinal)]
			: [];

	/// <summary>
	///     The floor <paramref name="areaId"/> sits on. Floors are optional in Home Assistant, so <c>null</c> is an
	///     ordinary answer.
	/// </summary>
	public static Floor? FloorOf(this IHaRegistry registry, string areaId) => registry.GetArea(areaId)?.Floor;

	/// <summary>The labels on <paramref name="entityId"/>, both ids and names, in one list. Empty when it has none.</summary>
	public static IReadOnlyList<string> LabelsOf(this IHaRegistry registry, string entityId) =>
		registry.GetEntityRegistration(entityId)?.Labels is { } labels
			? [.. labels.SelectMany(label => new[] { label.Id, label.Name }).OfType<string>()]
			: [];

	/// <summary>Whether <paramref name="entityId"/> carries <paramref name="label"/> (id or name, ordinal-ignore-case).</summary>
	public static bool HasLabel(this IHaRegistry registry, string entityId, string label) =>
		registry.LabelsOf(entityId).Contains(label, StringComparer.OrdinalIgnoreCase);

	/// <summary>
	///     The id of the device <paramref name="entityId"/> belongs to, or <c>null</c> when it belongs to none.
	/// </summary>
	/// <remarks>
	///     How duplicate-hardware checks are decided. A group helper and a template entity have no device, so null is
	///     ordinary, and a group is never mistaken for a duplicate of the entities inside it.
	/// </remarks>
	public static string? DeviceOf(this IHaRegistry registry, string entityId) =>
		registry.GetEntityRegistration(entityId)?.Device?.Id is { Length: > 0 } device ? device : null;

	/// <summary>The entity ids in <paramref name="areaId"/> that are in <paramref name="domain"/>.</summary>
	public static IReadOnlyList<string> EntityIdsInAreaByDomain(this IHaRegistry registry, string areaId, string domain) =>
		[.. registry.EntityIdsInArea(areaId).Where(id => id.HasDomain(domain))];
}
