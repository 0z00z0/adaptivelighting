using AdaptiveLighting.LastSeen;

namespace AdaptiveLighting.Tests.LastSeen;

/// <summary>
///     Which file a record is written to. The rules mirror the engine's room resolution, and the fallback is
///     load-bearing: a device class nobody predicted must still be tracked.
/// </summary>
[TestClass]
public sealed class LastSeenKindsTests
{
	private static readonly LastSeenOptions Options = new();

	[TestMethod]
	public void A_Light_Is_A_Light_By_Its_Domain_Alone()
	{
		Assert.AreEqual(LastSeenKind.Light, LastSeenKinds.Classify("light.kitchen", null, null, Options));
		Assert.AreEqual(LastSeenKind.Light, LastSeenKinds.Classify("light.group_upstairs", "nonsense", ["adaptive-motion"], Options));
	}

	[TestMethod]
	public void Motion_Presence_And_Occupancy_All_Count_As_Motion()
	{
		foreach (string deviceClass in new[] { "motion", "occupancy", "presence" })
			Assert.AreEqual(LastSeenKind.Motion, LastSeenKinds.Classify("binary_sensor.hall", deviceClass, null, Options), deviceClass);

		Assert.AreEqual(LastSeenKind.Motion, LastSeenKinds.Classify("binary_sensor.hall", "MOTION", null, Options), "device classes are matched case-insensitively");
	}

	[TestMethod]
	public void The_Motion_Label_Overrules_A_Device_Class_Nobody_Predicted()
	{
		// mmWave hardware routinely reports something else entirely, which is why the engine has this escape hatch
		// and why filing honours it: the motion file should hold what the household calls motion.
		Assert.AreEqual(LastSeenKind.Motion, LastSeenKinds.Classify("binary_sensor.mmwave", "vibration", ["adaptive-motion"], Options));
		Assert.AreEqual(LastSeenKind.Motion, LastSeenKinds.Classify("sensor.presence_score", null, ["Adaptive-Motion"], Options));
	}

	[TestMethod]
	public void Illuminance_Is_A_Sensor_With_That_Device_Class()
	{
		Assert.AreEqual(LastSeenKind.Illuminance, LastSeenKinds.Classify("sensor.hall_lux", "illuminance", null, Options));
		Assert.AreEqual(LastSeenKind.Other, LastSeenKinds.Classify("binary_sensor.hall_lux", "illuminance", null, Options), "a binary sensor is not a lux reading");
	}

	[TestMethod]
	public void Anything_Unclassifiable_Is_Filed_Rather_Than_Dropped()
	{
		Assert.AreEqual(LastSeenKind.Other, LastSeenKinds.Classify("sensor.washing_machine_power", "power", null, Options));
		Assert.AreEqual(LastSeenKind.Other, LastSeenKinds.Classify("binary_sensor.front_door", null, null, Options));
		Assert.AreEqual(LastSeenKind.Other, LastSeenKinds.Classify("automation.wake_up", null, null, Options));
		Assert.AreEqual(LastSeenKind.Other, LastSeenKinds.Classify("not an entity id", null, null, Options));
		Assert.AreEqual(LastSeenKind.Other, LastSeenKinds.Classify(null, null, null, Options));
	}

	[TestMethod]
	public void A_House_Can_Name_Its_Own_Device_Classes()
	{
		LastSeenOptions custom = new()
		{
			MotionDeviceClasses = ["radar"],
			IlluminanceDeviceClass = "light_level",
			MotionLabel = ""
		};

		Assert.AreEqual(LastSeenKind.Motion, LastSeenKinds.Classify("binary_sensor.hall", "radar", null, custom));
		Assert.AreEqual(LastSeenKind.Other, LastSeenKinds.Classify("binary_sensor.hall", "motion", null, custom));
		Assert.AreEqual(LastSeenKind.Illuminance, LastSeenKinds.Classify("sensor.hall", "light_level", null, custom));
		Assert.AreEqual(LastSeenKind.Other, LastSeenKinds.Classify("binary_sensor.hall", "vibration", ["adaptive-motion"], custom),
			"a house with no motion label configured must not match every entity that happens to carry one");
	}

	[TestMethod]
	public void Every_Bucket_Has_A_Token_And_Reads_Back()
	{
		foreach (LastSeenKind kind in LastSeenKinds.All)
			Assert.AreEqual(kind, LastSeenKinds.FromToken(kind.Token()), kind.ToString());

		Assert.AreEqual(4, LastSeenKinds.All.Count);
	}
}
