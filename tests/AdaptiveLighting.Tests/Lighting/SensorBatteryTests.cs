using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Tests.Common;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>A motion sensor's battery: found on the sensor's own device, and reported low on the room's snapshot.</summary>
[TestClass]
public sealed class SensorBatteryTests
{
	private const string Motion = AreaTestBuilder.Motion;
	private const string LowFlag = "binary_sensor.area_motion_battery_low";
	private const string Level = "sensor.area_motion_battery";

	[TestMethod]
	public void A_Devices_Low_Flag_Is_Found_And_Decides_Over_Its_Level()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["area"] = ["light.area", Motion];
		registry.Devices[Motion] = "sensor_device";
		registry.Devices[LowFlag] = "sensor_device";
		registry.Devices[Level] = "sensor_device";
		ha.SetState("light.area", "off");
		ha.SetState(Motion, "off", new() { ["device_class"] = "motion" });
		ha.SetState(LowFlag, "on", new() { ["device_class"] = "battery" });
		ha.SetState(Level, "80", new() { ["device_class"] = "battery" });

		Assert.IsTrue(Resolve(ha, registry, out ResolvedArea? area));
		CollectionAssert.AreEqual(new[] { new MotionBattery(Motion, LowFlag, Level) }, area!.MotionBatteries.ToArray());

		AreaFixture room = Build(area.MotionBatteries, states =>
		{
			states.SetState(LowFlag, "on");
			states.SetState(Level, "80");
		});

		CollectionAssert.AreEqual(new[] { new SensorBattery(Motion, 80) }, LowOnLastSnapshot(room),
			"the device says low, so 80 % is low for this sensor");

		room.Ha.Trigger(LowFlag, "off");

		Assert.AreEqual(0, LowOnLastSnapshot(room).Length, "the flag clearing clears the warning");
	}

	[TestMethod]
	public void A_Level_At_Twenty_Percent_Is_Low()
	{
		AreaFixture room = Build([new MotionBattery(Motion, null, Level)], ha => ha.SetState(Level, "50"));

		room.Ha.Trigger(Level, "20");

		CollectionAssert.AreEqual(new[] { new SensorBattery(Motion, 20) }, LowOnLastSnapshot(room));
	}

	[TestMethod]
	public void A_Level_At_Twenty_One_Percent_Is_Not_Low()
	{
		AreaFixture room = Build([new MotionBattery(Motion, null, Level)], ha => ha.SetState(Level, "21"));

		Assert.AreEqual(0, LowOnLastSnapshot(room).Length);
	}

	[TestMethod]
	public void A_Sensor_With_No_Battery_Entity_Has_No_Warning()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["area"] = ["light.area", Motion];
		registry.Devices[Motion] = "sensor_device";
		ha.SetState("light.area", "off");
		ha.SetState(Motion, "off", new() { ["device_class"] = "motion" });

		Assert.IsTrue(Resolve(ha, registry, out ResolvedArea? area));
		Assert.AreEqual(0, area!.MotionBatteries.Count);

		AreaFixture room = Build(area.MotionBatteries, seed: null);

		Assert.AreEqual(0, LowOnLastSnapshot(room).Length);
	}

	[TestMethod]
	public void A_Battery_Added_To_The_Sensor_Later_Is_Seen_Without_A_Save()
	{
		IReadOnlyList<MotionBattery> onDevice = [];
		AreaFixture room = new AreaTestBuilder().FindBatteries(() => onDevice).Build();
		Dictionary<string, object> battery = new() { ["device_class"] = "battery" };

		// Reported before the registry names its device: nothing to show yet, and the next report asks again.
		room.Ha.Trigger(Level, "15", battery);
		Assert.AreEqual(0, LowOnLastSnapshot(room).Length);

		onDevice = [new MotionBattery(Motion, null, Level)];
		room.Ha.Trigger(Level, "15", battery);

		CollectionAssert.AreEqual(new[] { new SensorBattery(Motion, 15) }, LowOnLastSnapshot(room),
			"the battery Home Assistant added is on the room's warning without a save or restart");

		room.Ha.Trigger(Level, "60", battery);
		Assert.AreEqual(0, LowOnLastSnapshot(room).Length, "and it is followed from then on");
	}

	private static bool Resolve(FakeHaContext ha, FakeAreaRegistry registry, out ResolvedArea? area) =>
		new AreaEntityResolver(ha, registry, new GlobalConfig(), NullLogger.Instance)
			.TryResolve(new AreaConfig { Name = "Area", AreaId = "area" }, new AreaSettings(), out area, out _);

	private static AreaFixture Build(IReadOnlyList<MotionBattery> batteries, Action<FakeHaContext>? seed) =>
		new AreaTestBuilder()
			.Seed(seed)
			.Shape(area => area with { MotionBatteries = batteries })
			.Build();

	private static SensorBattery[] LowOnLastSnapshot(AreaFixture room) =>
		[.. room.Publisher.Snapshots[^1].LowBatteries ?? []];
}
