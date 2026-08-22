using AdaptiveLighting.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>An area registry that throws on every question, the way NetDaemon's does while Kestrel is already serving pages.</summary>
public sealed class ThrowingAreaRegistry : IAreaRegistry
{
	public IReadOnlyList<string> AreaIds => throw new InvalidOperationException("The registry is not connected.");

	public bool AreaExists(string areaId) => throw new InvalidOperationException("The registry is not connected.");

	public string? NameOf(string areaId) => throw new InvalidOperationException("The registry is not connected.");

	public IReadOnlyList<string> EntitiesInArea(string areaId) => throw new InvalidOperationException("The registry is not connected.");

	public IReadOnlyList<string> LabelsOf(string entityId) => throw new InvalidOperationException("The registry is not connected.");

	public IReadOnlyList<string> LabelsOfArea(string areaId) => throw new InvalidOperationException("The registry is not connected.");

	public string? DeviceOf(string entityId) => throw new InvalidOperationException("The registry is not connected.");

	public AreaFloor? FloorOf(string areaId) => throw new InvalidOperationException("The registry is not connected.");
}
