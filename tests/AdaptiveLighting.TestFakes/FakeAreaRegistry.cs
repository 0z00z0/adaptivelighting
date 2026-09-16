using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Extensions;

namespace AdaptiveLighting.TestFakes;

/// <summary>An in-memory area registry: area id to entity ids, entity id to labels, and area id to floor.</summary>
/// <remarks>HassModel's Area and Floor cannot be constructed in a test, so the resolver binds this seam and not IHaRegistry.</remarks>
public sealed class FakeAreaRegistry : IAreaRegistry
{
	public Dictionary<string, List<string>> Areas { get; } = new(StringComparer.Ordinal);

	public Dictionary<string, List<string>> Labels { get; } = new(StringComparer.Ordinal);

	/// <summary>Area id to the labels on the area itself, as opposed to on its entities.</summary>
	public Dictionary<string, List<string>> AreaLabels { get; } = new(StringComparer.Ordinal);

	/// <summary>Label id to its display name. A label absent from here is named after its own id.</summary>
	/// <remarks>What a rename looks like: the id stays in <see cref="Labels"/> and the name here changes.</remarks>
	public Dictionary<string, string> LabelNames { get; } = new(StringComparer.Ordinal);

	/// <summary>Label ids the house has that nothing carries, so a test can hold a label after unlabelling it.</summary>
	public List<string> SpareLabelIds { get; } = [];

	/// <summary>Entity id to its device. Absent means no device, which is what a group helper looks like.</summary>
	public Dictionary<string, string> Devices { get; } = new(StringComparer.Ordinal);

	/// <summary>Area id to its floor. An area absent from here is floorless.</summary>
	public Dictionary<string, AreaFloor> Floors { get; } = new(StringComparer.Ordinal);

	/// <summary>Area id to the display name HA shows. Absent means the registry cannot answer.</summary>
	public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);

	public IReadOnlyList<string> AreaIds => [.. Areas.Keys];

	public bool AreaExists(string areaId) => Areas.ContainsKey(areaId);

	public string? NameOf(string areaId) => Names.GetValueOrDefault(areaId);

	public IReadOnlyList<string> EntitiesInArea(string areaId) => Areas.GetValueOrDefault(areaId) ?? [];

	public IReadOnlyList<RegistryLabel> KnownLabels =>
	[
		.. Labels.Values
			.Concat(AreaLabels.Values)
			.SelectMany(ids => ids)
			.Concat(LabelNames.Keys)
			.Concat(SpareLabelIds)
			.Distinct(StringComparer.Ordinal)
			.Select(Pair)
	];

	public IReadOnlyList<RegistryLabel> LabelsOf(string entityId) => Pairs(Labels.GetValueOrDefault(entityId));

	public IReadOnlyList<RegistryLabel> LabelsOfArea(string areaId) => Pairs(AreaLabels.GetValueOrDefault(areaId));

	public string? DeviceOf(string entityId) => Devices.GetValueOrDefault(entityId);

	public AreaFloor? FloorOf(string areaId) => Floors.GetValueOrDefault(areaId);

	private RegistryLabel Pair(string labelId) => new(labelId, LabelNames.GetValueOrDefault(labelId) ?? labelId);

	private IReadOnlyList<RegistryLabel> Pairs(List<string>? labelIds) => labelIds is null ? [] : [.. labelIds.Select(Pair)];
}
