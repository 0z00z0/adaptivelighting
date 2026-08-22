using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>A do-nothing <see cref="IHaRegistry"/>: every collection is empty and every lookup is <c>null</c>.</summary>
/// <remarks>Enough to construct the orchestrator without HassModel's Area and EntityRegistration, whose constructors are not public.</remarks>
public sealed class FakeHaRegistry : IHaRegistry
{
	public IReadOnlyCollection<EntityRegistration> Entities => [];

	public IReadOnlyCollection<Device> Devices => [];

	public IReadOnlyCollection<Area> Areas => [];

	public IReadOnlyCollection<Floor> Floors => [];

	public IReadOnlyCollection<Label> Labels => [];

	public EntityRegistration? GetEntityRegistration(string entityId) => null;

	public Device? GetDevice(string deviceId) => null;

	public Area? GetArea(string areaId) => null;

	public Floor? GetFloor(string floorId) => null;

	public Label? GetLabel(string labelId) => null;
}
