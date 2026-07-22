using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     A do-nothing <see cref="IHaRegistry"/>: every collection is empty and every lookup is <c>null</c>.
/// </summary>
/// <remarks>
///     The orchestrator only touches the registry while resolving an area to its entities. Tests that use explicit
///     light/motion lists — or no areas at all — never reach it, so an empty registry is enough to construct the
///     orchestrator without pulling in HassModel's <c>Area</c>/<c>EntityRegistration</c> types, whose constructors
///     are not public and cannot be built in a test.
/// </remarks>
public sealed class FakeHaRegistry : IHaRegistry
{
	/// <inheritdoc/>
	public IReadOnlyCollection<EntityRegistration> Entities => [];

	/// <inheritdoc/>
	public IReadOnlyCollection<Device> Devices => [];

	/// <inheritdoc/>
	public IReadOnlyCollection<Area> Areas => [];

	/// <inheritdoc/>
	public IReadOnlyCollection<Floor> Floors => [];

	/// <inheritdoc/>
	public IReadOnlyCollection<Label> Labels => [];

	/// <inheritdoc/>
	public EntityRegistration? GetEntityRegistration(string entityId) => null;

	/// <inheritdoc/>
	public Device? GetDevice(string deviceId) => null;

	/// <inheritdoc/>
	public Area? GetArea(string areaId) => null;

	/// <inheritdoc/>
	public Floor? GetFloor(string floorId) => null;

	/// <inheritdoc/>
	public Label? GetLabel(string labelId) => null;
}
