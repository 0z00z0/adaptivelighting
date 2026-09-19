using AdaptiveLighting.Web.Presentation;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Web;

/// <summary>What the daylight-sensor picker offers, decided on plain entity lists — no Home Assistant, no page.</summary>
[TestClass]
public sealed class DaylightSensorScopeTests
{
	private static readonly EntityOption HouseSensor = new("sensor.outdoor_lux", "Outdoor light", "Garden");
	private static readonly EntityOption OwnRoomSensor = new("sensor.stue_lux", "Living room light", "Living room");
	private static readonly EntityOption OtherRoomSensor = new("sensor.bad_lux", "Bathroom light", "Bathroom");
	private static readonly EntityOption SavedElsewhere = new("sensor.loft_lux", "Loft light", "Loft");

	[TestMethod]
	public void OffersTheHouseSensorThenTheRoomsOwnAndKeepsASavedOutsider()
	{
		IReadOnlyList<EntityOption> everywhere = [HouseSensor, OwnRoomSensor, OtherRoomSensor, SavedElsewhere];

		IReadOnlyList<EntityOption> offered = DaylightSensorScope.For(
			scoped: true,
			ownArea: [OwnRoomSensor],
			everywhere: everywhere,
			houseSensor: HouseSensor.EntityId,
			configured: SavedElsewhere.EntityId,
			nameOf: id => id);

		CollectionAssert.AreEqual(
			new[] { HouseSensor.EntityId, OwnRoomSensor.EntityId, SavedElsewhere.EntityId },
			offered.Select(option => option.EntityId).ToList(),
			"the house sensor leads, the room's own sensor follows, and a sensor saved elsewhere is kept");

		Assert.IsFalse(
			offered.Any(option => option.EntityId == OtherRoomSensor.EntityId),
			"another room's sensor is not this room's to offer");
	}
}
