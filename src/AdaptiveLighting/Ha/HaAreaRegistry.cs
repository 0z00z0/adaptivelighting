using AdaptiveLighting.Abstractions;

namespace AdaptiveLighting.Ha;

/// <summary>
///     The real <see cref="IAreaRegistry"/>: a thin projection of <see cref="IHaRegistry"/> onto the six
///     questions the engine actually asks.
/// </summary>
/// <remarks>
///     Reads the registry live rather than snapshotting it. Resolution happens once at startup, so a snapshot
///     would buy nothing and would only be one more thing that could go stale.
/// </remarks>
public sealed class HaAreaRegistry : IAreaRegistry
{
	private readonly IHaRegistry _registry;

	/// <summary>Wraps the live registry.</summary>
	public HaAreaRegistry(IHaRegistry registry) => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

	/// <inheritdoc/>
	public IReadOnlyList<string> AreaIds => _registry.AreaIds();

	/// <inheritdoc/>
	public bool AreaExists(string areaId) => _registry.AreaExists(areaId);

	/// <inheritdoc/>
	public string? NameOf(string areaId) => _registry.AreaNameOf(areaId);

	/// <inheritdoc/>
	public IReadOnlyList<string> EntitiesInArea(string areaId) => _registry.EntityIdsInArea(areaId);

	/// <inheritdoc/>
	public IReadOnlyList<string> LabelsOf(string entityId) => _registry.LabelsOf(entityId);

	/// <inheritdoc/>
	public string? DeviceOf(string entityId) => _registry.DeviceOf(entityId);

	/// <inheritdoc/>
	/// <remarks>
	///     A floor with no id cannot be grouped on — the group key is the id — so it is read as no floor at all
	///     rather than as an anonymous one every floorless area would then share.
	/// </remarks>
	public AreaFloor? FloorOf(string areaId) =>
		_registry.FloorOf(areaId) is { Id: { Length: > 0 } id } floor
			? new AreaFloor(id, floor.Name is { Length: > 0 } name ? name : id, floor.Level)
			: null;
}
