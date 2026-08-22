using System.Text.RegularExpressions;

using AdaptiveLighting.LastSeen;

namespace AdaptiveLighting.Tests.LastSeen;

/// <summary>Which file a record is written to: three curated rules, then device class, then domain.</summary>
[TestClass]
public sealed class LastSeenBucketsTests
{
	private static readonly LastSeenOptions Options = new();

	// ===================== the three curated buckets, unchanged =====================

	[TestMethod]
	public void A_Light_Is_A_Light_By_Its_Domain_Alone()
	{
		Assert.AreEqual(LastSeenBuckets.Light, LastSeenBuckets.Classify("light.kitchen", null, null, Options));
		Assert.AreEqual(LastSeenBuckets.Light, LastSeenBuckets.Classify("light.group_upstairs", "nonsense", ["adaptive-motion"], Options));
	}

	[TestMethod]
	public void Motion_Presence_And_Occupancy_All_Count_As_Motion()
	{
		foreach (string deviceClass in new[] { "motion", "occupancy", "presence" })
			Assert.AreEqual(LastSeenBuckets.Motion, LastSeenBuckets.Classify("binary_sensor.hall", deviceClass, null, Options), deviceClass);

		Assert.AreEqual(LastSeenBuckets.Motion, LastSeenBuckets.Classify("binary_sensor.hall", "MOTION", null, Options), "device classes are matched case-insensitively");
	}

	[TestMethod]
	public void The_Motion_Label_Overrules_A_Device_Class_Nobody_Predicted()
	{
		// mmWave hardware routinely reports another device class, so filing honours the label: the motion file
		// holds what the household calls motion.
		Assert.AreEqual(LastSeenBuckets.Motion, LastSeenBuckets.Classify("binary_sensor.mmwave", "vibration", ["adaptive-motion"], Options));
		Assert.AreEqual(LastSeenBuckets.Motion, LastSeenBuckets.Classify("sensor.presence_score", null, ["Adaptive-Motion"], Options));
	}

	[TestMethod]
	public void Illuminance_Is_A_Sensor_With_That_Device_Class()
	{
		Assert.AreEqual(LastSeenBuckets.Illuminance, LastSeenBuckets.Classify("sensor.hall_lux", "illuminance", null, Options));
		Assert.AreNotEqual(LastSeenBuckets.Illuminance, LastSeenBuckets.Classify("binary_sensor.hall_lux", "illuminance", null, Options),
			"a binary sensor is not a lux reading, and must not turn up in the file the darkness decision is diagnosed from");
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

		Assert.AreEqual(LastSeenBuckets.Motion, LastSeenBuckets.Classify("binary_sensor.hall", "radar", null, custom));
		Assert.AreEqual(LastSeenBuckets.Illuminance, LastSeenBuckets.Classify("sensor.hall", "light_level", null, custom));

		// A renamed motion label does not rename the file: an entity declaring device_class "motion" is still filed by its class.
		Assert.AreEqual("binary_sensor_motion", LastSeenBuckets.Classify("binary_sensor.hall", "motion", null, custom));
		Assert.AreEqual("vibration", LastSeenBuckets.Classify("binary_sensor.hall", "vibration", ["adaptive-motion"], custom),
			"a house with no motion label configured must not match every entity that happens to carry one");
	}

	// ===================== the split catch-all =====================

	[TestMethod]
	public void An_Entity_With_A_Device_Class_Is_Filed_Under_That_Class()
	{
		Assert.AreEqual("power", LastSeenBuckets.Classify("sensor.washing_machine_power", "power", null, Options));
		Assert.AreEqual("temperature", LastSeenBuckets.Classify("sensor.hall_temperature", "temperature", null, Options));
		Assert.AreEqual("battery", LastSeenBuckets.Classify("sensor.remote_battery", "battery", null, Options));
		Assert.AreEqual("door", LastSeenBuckets.Classify("binary_sensor.front_door", "door", null, Options));
		Assert.AreEqual("humidity", LastSeenBuckets.Classify("sensor.bath_humidity", "HUMIDITY", null, Options), "and case does not make a second file");
	}

	[TestMethod]
	public void An_Entity_With_No_Device_Class_Is_Filed_Under_Its_Domain()
	{
		// The domain is the only other thing the id actually says, and it gives self-describing files.
		Assert.AreEqual("person", LastSeenBuckets.Classify("person.espen", null, null, Options));
		Assert.AreEqual("sun", LastSeenBuckets.Classify("sun.sun", null, null, Options));
		Assert.AreEqual("automation", LastSeenBuckets.Classify("automation.wake_up", null, null, Options));
		Assert.AreEqual("input_boolean", LastSeenBuckets.Classify("input_boolean.guest_mode", null, null, Options));
		Assert.AreEqual("script", LastSeenBuckets.Classify("script.goodnight", null, null, Options));
		Assert.AreEqual("binary_sensor", LastSeenBuckets.Classify("binary_sensor.something", null, null, Options));
		Assert.AreEqual("sensor", LastSeenBuckets.Classify("sensor.uptime", "  ", null, Options), "a blank class is no class");
	}

	[TestMethod]
	public void A_Curated_Name_Is_Reserved_For_The_Rule_That_Earned_It()
	{
		// binary_sensor device_class "light" detects light and is not a lamp; filing it under "light" would change what the light file holds.
		Assert.AreEqual("binary_sensor_light", LastSeenBuckets.Classify("binary_sensor.hall_light_detected", "light", null, Options));
		Assert.AreEqual("binary_sensor_illuminance", LastSeenBuckets.Classify("binary_sensor.hall_lux", "illuminance", null, Options));
		Assert.AreEqual("cover_light", LastSeenBuckets.Classify("cover.skylight", "light", null, Options));

		foreach (string bucket in LastSeenBuckets.Curated)
			Assert.IsTrue(LastSeenBuckets.IsCurated(bucket), bucket);

		Assert.IsFalse(LastSeenBuckets.IsCurated("binary_sensor_light"));
		Assert.IsFalse(LastSeenBuckets.IsCurated(null));
	}

	[TestMethod]
	public void Nothing_Is_Dropped_However_Strange_The_Entity_Is()
	{
		// An entity with an absent or surprising class is the one most likely to be misbehaving, so it is tracked in full.
		Assert.AreEqual(LastSeenBuckets.Unclassified, LastSeenBuckets.Classify("not an entity id", null, null, Options));
		Assert.AreEqual(LastSeenBuckets.Unclassified, LastSeenBuckets.Classify(".no_domain", null, null, Options));
		Assert.AreEqual(LastSeenBuckets.Unclassified, LastSeenBuckets.Classify("", null, null, Options));
		Assert.AreEqual(LastSeenBuckets.Unclassified, LastSeenBuckets.Classify(null, null, null, Options));

		// An id with nothing but a domain still says something, and a class always wins over the id.
		Assert.AreEqual("sensor", LastSeenBuckets.Classify("sensor.", null, null, Options));
		Assert.AreEqual("power", LastSeenBuckets.Classify("not an entity id", "power", null, Options));
	}

	[TestMethod]
	public void Two_Spellings_Of_One_Class_Are_One_Bucket()
	{
		// Most file systems this runs on are case-insensitive, so two keys differing only in case would fight over one file.
		string lower = LastSeenBuckets.Classify("sensor.a", "temperature", null, Options);

		Assert.AreEqual(lower, LastSeenBuckets.Classify("sensor.b", "Temperature", null, Options));
		Assert.AreEqual(lower, LastSeenBuckets.Classify("sensor.c", "  TEMPERATURE  ", null, Options));
		Assert.AreEqual(LastSeenBuckets.FileToken(lower), LastSeenBuckets.FileToken("Temperature"));
	}

	// ===================== the name that reaches the file system =====================

	[TestMethod]
	public void The_Curated_File_Names_Did_Not_Change()
	{
		Assert.AreEqual("illuminance", LastSeenBuckets.FileToken(LastSeenBuckets.Illuminance));
		Assert.AreEqual("motion", LastSeenBuckets.FileToken(LastSeenBuckets.Motion));
		Assert.AreEqual("light", LastSeenBuckets.FileToken(LastSeenBuckets.Light));
		Assert.AreEqual("other", LastSeenBuckets.FileToken(LastSeenBuckets.Unclassified));
	}

	[TestMethod]
	public void A_Class_That_Needs_Sanitising_Still_Gets_A_Usable_Name()
	{
		// device_class is external data that reaches the file system, so it must come out as a file name that cannot escape its directory.
		foreach (string deviceClass in new[] { "Kitchen / Ambient", @"..\..\config\secrets", "temp:ambient", "a b\tc", "battery%", "../../etc/passwd" })
		{
			string token = LastSeenBuckets.FileToken(deviceClass);

			Assert.IsTrue(Regex.IsMatch(token, "^[a-z0-9_]*-[0-9a-f]{8}$"), $"'{deviceClass}' produced '{token}'");
			Assert.AreEqual(-1, token.IndexOfAny(Path.GetInvalidFileNameChars()), token);
			Assert.IsFalse(token.Contains('.', StringComparison.Ordinal), token);
			Assert.AreEqual(token, Path.GetFileName(token), "a token must be a file name, never a path");
		}
	}

	[TestMethod]
	public void Two_Classes_That_Sanitise_Alike_Do_Not_Share_A_File()
	{
		// Dropped characters are where a collision comes from: one file holding two classes' histories is quiet data loss.
		string[] tokens =
		[
			LastSeenBuckets.FileToken("a/b"),
			LastSeenBuckets.FileToken(@"a\b"),
			LastSeenBuckets.FileToken("a-b"),
			LastSeenBuckets.FileToken("a b"),
			LastSeenBuckets.FileToken("ab")
		];

		Assert.AreEqual(tokens.Length, tokens.Distinct(StringComparer.Ordinal).Count(), string.Join(", ", tokens));
		Assert.AreEqual("ab", tokens[^1], "the one that needed nothing done to it keeps its own name");
	}

	[TestMethod]
	public void A_Class_With_Nothing_Nameable_In_It_Has_A_Defined_Home()
	{
		// Japanese for "temperature" and "humidity": nothing in either survives an ASCII allow-list.
		string temperature = LastSeenBuckets.FileToken("温度");
		string humidity = LastSeenBuckets.FileToken("湿度");

		StringAssert.StartsWith(temperature, "unnamed-");
		StringAssert.StartsWith(humidity, "unnamed-");
		Assert.AreNotEqual(temperature, humidity, "a defined home is not a shared one");
	}

	[TestMethod]
	public void A_Very_Long_Class_Is_Capped_And_Still_Unique()
	{
		string first = new('a', 300);
		string second = first + "_and_then_something_else";

		Assert.IsTrue(LastSeenBuckets.FileToken(first).Length < 64, LastSeenBuckets.FileToken(first));
		Assert.AreNotEqual(LastSeenBuckets.FileToken(first), LastSeenBuckets.FileToken(second),
			"truncation is what makes two long classes look alike, so truncation is what the fingerprint is for");
	}

	[TestMethod]
	public void The_Same_Class_Always_Gets_The_Same_File()
	{
		// The fingerprint reaches a file name, so it cannot come from a per-process hash seed.
		Assert.AreEqual("kitchenambient-ac1c86f6", LastSeenBuckets.FileToken("Kitchen / Ambient"));
		Assert.AreEqual("unnamed-3732e264", LastSeenBuckets.FileToken("温度"));
	}

	[TestMethod]
	public void A_Token_Reads_Back_As_Its_Key_Unless_It_Was_Fingerprinted()
	{
		foreach (string bucket in new[] { "illuminance", "motion", "light", "other", "temperature", "input_boolean" })
			Assert.AreEqual(bucket, LastSeenBuckets.FromToken(LastSeenBuckets.FileToken(bucket)), bucket);

		// A fingerprinted name cannot be reversed, and says so instead of inventing a key.
		Assert.AreEqual(LastSeenBuckets.Unclassified, LastSeenBuckets.FromToken("kitchenambient-ac1c86f6"));
		Assert.AreEqual(LastSeenBuckets.Unclassified, LastSeenBuckets.FromToken(null));
		Assert.AreEqual(LastSeenBuckets.Unclassified, LastSeenBuckets.FromToken("  "));
	}

	[TestMethod]
	public void Classify_Refuses_To_Guess_Without_Options()
	{
		Assert.ThrowsException<ArgumentNullException>(() => LastSeenBuckets.Classify("sensor.a", "power", null, null!));
	}
}
