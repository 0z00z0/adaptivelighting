using AdaptiveLighting.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     An in-memory area registry: area id to entity ids, entity id to labels, and area id to floor.
/// </summary>
/// <remarks>
///     HassModel's Area and Floor cannot be constructed in a test, so the resolver binds this seam and not
///     IHaRegistry. AreaFloor exists for the same reason.
/// </remarks>
public sealed class FakeAreaRegistry : IAreaRegistry
{
	public Dictionary<string, List<string>> Areas { get; } = new(StringComparer.Ordinal);

	public Dictionary<string, List<string>> Labels { get; } = new(StringComparer.Ordinal);

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

	public IReadOnlyList<string> LabelsOf(string entityId) => Labels.GetValueOrDefault(entityId) ?? [];

	public string? DeviceOf(string entityId) => Devices.GetValueOrDefault(entityId);

	public AreaFloor? FloorOf(string areaId) => Floors.GetValueOrDefault(areaId);
}
