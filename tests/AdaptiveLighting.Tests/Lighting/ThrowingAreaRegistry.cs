using AdaptiveLighting.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     An area registry that answers every question with an exception.
/// </summary>
/// <remarks>
///     NetDaemon's registry throws <see cref="InvalidOperationException"/> until its first connection to Home
///     Assistant completes, and Kestrel is already serving pages in that window. Anything that reads a room's name
///     has to survive it, so there has to be something to read it from.
/// </remarks>
public sealed class ThrowingAreaRegistry : IAreaRegistry
{
	/// <inheritdoc/>
	public IReadOnlyList<string> AreaIds => throw new InvalidOperationException("The registry is not connected.");

	/// <inheritdoc/>
	public bool AreaExists(string areaId) => throw new InvalidOperationException("The registry is not connected.");

	/// <inheritdoc/>
	public string? NameOf(string areaId) => throw new InvalidOperationException("The registry is not connected.");

	/// <inheritdoc/>
	public IReadOnlyList<string> EntitiesInArea(string areaId) => throw new InvalidOperationException("The registry is not connected.");

	/// <inheritdoc/>
	public IReadOnlyList<string> LabelsOf(string entityId) => throw new InvalidOperationException("The registry is not connected.");

	/// <inheritdoc/>
	public string? DeviceOf(string entityId) => throw new InvalidOperationException("The registry is not connected.");

	/// <inheritdoc/>
	public AreaFloor? FloorOf(string areaId) => throw new InvalidOperationException("The registry is not connected.");
}
