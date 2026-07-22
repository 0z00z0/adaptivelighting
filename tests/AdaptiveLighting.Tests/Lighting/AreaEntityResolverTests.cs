using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     Discovery: turning an area id into concrete entity ids, and refusing to guess when it cannot.
/// </summary>
[TestClass]
public sealed class AreaEntityResolverTests
{
	private static AreaEntityResolver Resolver(FakeHaContext ha, FakeAreaRegistry registry, GlobalConfig? global = null) =>
		new(ha, registry, global ?? new GlobalConfig(), NullLogger.Instance);

	// ===================== the happy path =====================

	[TestMethod]
	public void An_Area_Id_Is_Enough_To_Find_An_Areas_Entities()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.stue_tak", "light.stue_lampe", "binary_sensor.stue_motion", "sensor.stue_lux", "switch.noise"];
		ha.SetState("light.stue_tak", "off");
		ha.SetState("light.stue_lampe", "off");
		ha.SetState("binary_sensor.stue_motion", "off", new() { ["device_class"] = "motion" });
		ha.SetState("sensor.stue_lux", "10", new() { ["device_class"] = "illuminance" });

		var ok = Resolver(ha, registry).TryResolve(new AreaConfig { Name = "Stue", AreaId = "stue" }, new AreaSettings(), out var area, out _);

		Assert.IsTrue(ok);
		Assert.AreEqual(2, area!.Lights.Count);
		CollectionAssert.AreEqual(new[] { "binary_sensor.stue_motion" }, area.MotionSensors.ToArray());
		Assert.AreEqual("sensor.stue_lux", area.LuxSensor);
		CollectionAssert.DoesNotContain(area.Lights.ToArray(), "switch.noise", "a switch is not a light");
	}

	// ===================== the registry lists rows, not devices =====================

	/// <summary>
	///     Regression found on a live instance: discovery called <c>light.router_socket_status_led</c> and a water
	///     sensor's indicator LED "room lighting". They are registry rows for disabled entities — no state at
	///     all — and the engine would have flashed the router's LED on motion in the annex.
	/// </summary>
	[TestMethod]
	public void A_Registry_Entry_With_No_State_Is_Not_A_Light()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["tilbygg"] = ["light.tilbygg_taklys", "light.router_socket_status_led", "binary_sensor.tilbygg_motion"];
		ha.SetState("light.tilbygg_taklys", "off");
		ha.SetState("binary_sensor.tilbygg_motion", "off", new() { ["device_class"] = "motion" });
		// light.router_socket_status_led deliberately has no state: it is a disabled registry row.

		var ok = Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "tilbygg" }, new AreaSettings(), out var area, out _);

		Assert.IsTrue(ok);
		CollectionAssert.AreEqual(new[] { "light.tilbygg_taklys" }, area!.Lights.ToArray(),
			"a registry row with no state is not something Home Assistant can dim");
	}

	[TestMethod]
	public void An_Unavailable_Light_Is_Not_Discovered()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["tilbygg"] = ["light.wiz", "light.tuya_offline", "binary_sensor.m"];
		ha.SetState("light.wiz", "off");
		ha.SetState("light.tuya_offline", "unavailable");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "tilbygg" }, new AreaSettings(), out var area, out _);

		CollectionAssert.AreEqual(new[] { "light.wiz" }, area!.Lights.ToArray(),
			"a light the engine cannot reach is a light it cannot dim");
	}

	[TestMethod]
	public void An_Unavailable_Motion_Sensor_Is_Not_Discovered()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["a"] = ["light.l", "binary_sensor.dead", "binary_sensor.live"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.dead", "unavailable", new() { ["device_class"] = "motion" });
		ha.SetState("binary_sensor.live", "off", new() { ["device_class"] = "motion" });

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "a" }, new AreaSettings(), out var area, out _);

		CollectionAssert.AreEqual(new[] { "binary_sensor.live" }, area!.MotionSensors.ToArray());
	}

	/// <summary>
	///     one live instance's tilbygg has two illuminance sensors, one of them permanently unavailable. Counting the dead one
	///     would make the area ambiguous and cost the whole room over a sensor that reports nothing.
	/// </summary>
	[TestMethod]
	public void A_Dead_Lux_Sensor_Does_Not_Make_The_Area_Ambiguous()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["tilbygg"] = ["light.l", "binary_sensor.m", "sensor.annex_illuminance", "sensor.shelly_luminosity"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });
		ha.SetState("sensor.annex_illuminance", "0.0", new() { ["device_class"] = "illuminance" });
		ha.SetState("sensor.shelly_luminosity", "unavailable", new() { ["device_class"] = "illuminance" });

		var ok = Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "tilbygg" }, new AreaSettings(), out var area, out var error);

		Assert.IsTrue(ok, error);
		Assert.AreEqual("sensor.annex_illuminance", area!.LuxSensor);
	}

	[TestMethod]
	public void The_Area_Name_Falls_Back_To_The_Area_Id()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.l", "binary_sensor.m"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out var area, out _);

		Assert.AreEqual("stue", area!.Name);
	}

	[TestMethod]
	public void Area_Overrides_Are_Merged_Onto_The_Document_Defaults()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.l", "binary_sensor.m"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		var defaults = new AreaSettings { VacancyTimeoutSeconds = 900, WelcomeHome = true };
		Resolver(ha, registry).TryResolve(
			new AreaConfig { AreaId = "stue", VacancyTimeoutSeconds = 60 }, defaults, out var area, out _);

		Assert.AreEqual(60, area!.Settings.VacancyTimeoutSeconds, "the area's own value wins");
		Assert.IsTrue(area.Settings.WelcomeHome, "and everything it did not mention is inherited");
	}

	// ===================== discovery rules =====================

	[TestMethod]
	public void A_Light_Group_Wins_Over_Its_Members()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.stue_group", "light.bulb_a", "light.bulb_b", "binary_sensor.m"];
		ha.SetState("light.stue_group", "off", new() { ["entity_id"] = new[] { "light.bulb_a", "light.bulb_b" } });
		ha.SetState("light.bulb_a", "off");
		ha.SetState("light.bulb_b", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "occupancy" });

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out var area, out _);

		CollectionAssert.AreEqual(new[] { "light.stue_group" }, area!.Lights.ToArray(),
			"commanding a group and its members is the same bulbs twice");
	}

	[TestMethod]
	public void The_Exclude_Label_Drops_An_Entity()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.keep", "light.skip", "binary_sensor.m"];
		registry.Labels["light.skip"] = ["adaptive-exclude"];
		ha.SetState("light.keep", "off");
		ha.SetState("light.skip", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out var area, out _);

		CollectionAssert.AreEqual(new[] { "light.keep" }, area!.Lights.ToArray());
	}

	// ===================== the include label =====================
	//
	// "Only manage lights carrying this label" is strictly opt-in, and the whole point of these tests is that
	// opting out is the default nobody has to write down. Every document ever written predates the setting, so
	// anything other than "null manages everything" would change what those documents mean under their owners.

	/// <summary>
	///     Asserted on a document that has no <c>IncludeLabel</c> key at all, because that — not an explicit
	///     null — is what every existing file looks like. A default that only held for the value nobody writes
	///     would be no guarantee at all.
	/// </summary>
	[TestMethod]
	public void A_Document_That_Never_Mentions_An_Include_Label_Manages_Every_Light()
	{
		DocumentReadResult read = LightingConfigDocument.Deserialize(
			"""
			AdaptiveLighting.Configuration.AdaptiveLightingConfig:
			  Global:
			    ExcludeLabel: adaptive-exclude
			  Periods:
			    - Name: day
			      Start: "07:00"
			  Areas:
			    - Name: Stue
			      AreaId: stue
			""");

		Assert.IsNull(read.Config.Global.IncludeLabel, "saying nothing must keep meaning 'manage every light found'");

		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.labelled", "light.plain", "binary_sensor.m"];
		registry.Labels["light.labelled"] = ["adaptive"];
		ha.SetState("light.labelled", "off");
		ha.SetState("light.plain", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		Resolver(ha, registry, read.Config.Global).TryResolve(
			read.Config.Areas[0], new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEquivalent(new[] { "light.labelled", "light.plain" }, area.Lights.ToArray(),
			"an unlabelled light in a house with no include label is still the household's light");
	}

	[TestMethod]
	public void An_Include_Label_Filters_Discovery_To_The_Lights_That_Carry_It()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.blessed", "light.ignored", "binary_sensor.m"];
		registry.Labels["light.blessed"] = ["adaptive"];
		ha.SetState("light.blessed", "off");
		ha.SetState("light.ignored", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		GlobalConfig global = new() { IncludeLabel = "adaptive" };
		Resolver(ha, registry, global).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedArea? area, out _);

		CollectionAssert.AreEqual(new[] { "light.blessed" }, area!.Lights.ToArray());
	}

	/// <summary>
	///     "Never touch" must never lose an argument. Include selects candidates; exclude then removes, so a light
	///     wearing both labels stays out — the setting whose whole name is a prohibition cannot be outvoted.
	/// </summary>
	[TestMethod]
	public void The_Exclude_Label_Beats_The_Include_Label_On_The_Same_Light()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.both", "light.blessed", "binary_sensor.m"];
		registry.Labels["light.both"] = ["adaptive", "adaptive-exclude"];
		registry.Labels["light.blessed"] = ["adaptive"];
		ha.SetState("light.both", "off");
		ha.SetState("light.blessed", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		GlobalConfig global = new() { IncludeLabel = "adaptive" };
		Resolver(ha, registry, global).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedArea? area, out _);

		CollectionAssert.AreEqual(new[] { "light.blessed" }, area!.Lights.ToArray());
	}

	/// <summary>
	///     An explicit pick is the owner overruling the rules, exactly as it already overrules discovery. Both
	///     labels are rules, so neither gets a veto over a list somebody wrote out by hand.
	/// </summary>
	[TestMethod]
	public void An_Explicit_Lights_List_Bypasses_Both_Labels()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["binary_sensor.m"];
		registry.Labels["light.excluded"] = ["adaptive-exclude"];
		ha.SetState("light.unlabelled", "off");
		ha.SetState("light.excluded", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		GlobalConfig global = new() { IncludeLabel = "adaptive" };
		Resolver(ha, registry, global).TryResolve(
			new AreaConfig { AreaId = "stue", Lights = ["light.unlabelled", "light.excluded"] },
			new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEqual(new[] { "light.unlabelled", "light.excluded" }, area.Lights.ToArray(),
			"a hand-picked light is the owner's decision, and the labels are the rules it overrules");
	}

	[TestMethod]
	public void A_Room_Whose_Lights_Are_All_Filtered_Out_Is_Skipped_With_The_Label_Named()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.plain", "binary_sensor.m"];
		ha.SetState("light.plain", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		GlobalConfig global = new() { IncludeLabel = "adaptive" };
		bool ok = Resolver(ha, registry, global)
			.TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out _, out string? error);

		Assert.IsFalse(ok, "a filtered-out room is skipped, never a document error");
		StringAssert.Contains(error!, "adaptive", "the message has to name the label, or the fix is unfindable");
		StringAssert.Contains(error!, "stue");
	}

	/// <summary>
	///     The other half of the message above: a room with no lights at all must not be told to go and label
	///     them. That sends a household hunting for lights Home Assistant never put in the room.
	/// </summary>
	[TestMethod]
	public void A_Room_With_No_Lights_At_All_Is_Not_Blamed_On_The_Include_Label()
	{
		FakeAreaRegistry registry = new();
		registry.Areas["bod"] = [];

		GlobalConfig global = new() { IncludeLabel = "adaptive" };
		Resolver(new FakeHaContext(), registry, global)
			.TryResolve(new AreaConfig { AreaId = "bod" }, new AreaSettings(), out _, out string? error);

		StringAssert.Contains(error!, "No lights discovered");
		Assert.IsFalse(error!.Contains("adaptive", StringComparison.Ordinal),
			"there was nothing for the label to filter out, so the label is not the problem");
	}

	/// <summary>
	///     Lights only. Motion and lux sensors are inputs, not things the engine commands, and filtering them too
	///     would leave a half-labelled house silently deaf — lights it may drive, and no sensor to tell it when.
	/// </summary>
	[TestMethod]
	public void The_Include_Label_Does_Not_Filter_Motion_Or_Lux_Sensors()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.blessed", "binary_sensor.m", "sensor.lux"];
		registry.Labels["light.blessed"] = ["adaptive"];
		ha.SetState("light.blessed", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });
		ha.SetState("sensor.lux", "10", new() { ["device_class"] = "illuminance" });

		GlobalConfig global = new() { IncludeLabel = "adaptive" };
		Resolver(ha, registry, global).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEqual(new[] { "binary_sensor.m" }, area.MotionSensors.ToArray(),
			"an unlabelled motion sensor is still how the room knows somebody is in it");
		Assert.AreEqual("sensor.lux", area.LuxSensor);
	}

	[TestMethod]
	public void DiscoverArea_Honours_The_Include_Label_Too()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.blessed", "light.ignored"];
		registry.Labels["light.blessed"] = ["adaptive"];
		ha.SetState("light.blessed", "off");
		ha.SetState("light.ignored", "off");

		GlobalConfig global = new() { IncludeLabel = "adaptive" };
		AreaDiscovery found = Resolver(ha, registry, global).DiscoverArea("stue");

		CollectionAssert.AreEqual(new[] { "light.blessed" }, found.Lights.ToArray(),
			"the preview must show what the engine will drive, filter and all");
	}

	[TestMethod]
	public void The_Motion_Label_Rescues_A_Sensor_With_An_Odd_Device_Class()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.l", "binary_sensor.mmwave", "binary_sensor.door"];
		registry.Labels["binary_sensor.mmwave"] = ["adaptive-motion"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.mmwave", "off", new() { ["device_class"] = "sound" });
		ha.SetState("binary_sensor.door", "off", new() { ["device_class"] = "door" });

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out var area, out _);

		CollectionAssert.AreEqual(new[] { "binary_sensor.mmwave" }, area!.MotionSensors.ToArray());
		CollectionAssert.DoesNotContain(area.MotionSensors.ToArray(), "binary_sensor.door", "a door is not motion");
	}

	[TestMethod]
	public void An_Explicit_List_Replaces_Discovery_For_Its_Own_Slot_Only()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.discovered", "binary_sensor.discovered"];
		ha.SetState("light.discovered", "off");
		ha.SetState("binary_sensor.discovered", "off", new() { ["device_class"] = "motion" });
		ha.SetState("light.explicit", "off");

		Resolver(ha, registry).TryResolve(
			new AreaConfig { AreaId = "stue", Lights = ["light.explicit"] }, new AreaSettings(), out var area, out _);

		CollectionAssert.AreEqual(new[] { "light.explicit" }, area!.Lights.ToArray());
		CollectionAssert.AreEqual(new[] { "binary_sensor.discovered" }, area.MotionSensors.ToArray(),
			"replacing the lights must not switch motion discovery off too");
	}

	// ===================== MotionDeviceClasses: the config-binder trap =====================

	/// <summary>
	///     The default is empty and empty means the built-in three. The property cannot simply carry the three
	///     values, because the .NET configuration binder APPENDS bound list items to a non-empty default instead
	///     of replacing it — a YAML list of three bound to six, and no edit to the YAML could ever remove a
	///     default. These four tests pin that whole contract: leave it empty and get the defaults, set it and get
	///     exactly what you set.
	/// </summary>
	[TestMethod]
	public void An_Empty_MotionDeviceClasses_Means_The_Built_In_Defaults()
	{
		var global = new GlobalConfig();

		Assert.AreEqual(0, global.MotionDeviceClasses.Count,
			"the default must stay empty, or the binder will append the household's list to it");
		CollectionAssert.AreEqual(
			GlobalConfig.DefaultMotionDeviceClasses.ToArray(),
			global.EffectiveMotionDeviceClasses.ToArray());
	}

	[TestMethod]
	public void A_Configured_MotionDeviceClasses_Replaces_The_Defaults_Rather_Than_Adding_To_Them()
	{
		var global = new GlobalConfig { MotionDeviceClasses = ["vibration"] };

		CollectionAssert.AreEqual(new[] { "vibration" }, global.EffectiveMotionDeviceClasses.ToArray(),
			"what the YAML says is what the engine does — nothing more");
	}

	[TestMethod]
	public void Discovery_Uses_The_Built_In_Classes_When_None_Are_Configured()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.l", "binary_sensor.motion", "binary_sensor.occupancy", "binary_sensor.presence"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.motion", "off", new() { ["device_class"] = "motion" });
		ha.SetState("binary_sensor.occupancy", "off", new() { ["device_class"] = "occupancy" });
		ha.SetState("binary_sensor.presence", "off", new() { ["device_class"] = "presence" });

		Resolver(ha, registry, new GlobalConfig()).TryResolve(
			new AreaConfig { AreaId = "stue" }, new AreaSettings(), out var area, out _);

		Assert.AreEqual(3, area!.MotionSensors.Count);
	}

	[TestMethod]
	public void Discovery_Uses_Only_The_Configured_Classes_When_They_Are_Set()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.l", "binary_sensor.motion", "binary_sensor.vibration"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.motion", "off", new() { ["device_class"] = "motion" });
		ha.SetState("binary_sensor.vibration", "off", new() { ["device_class"] = "vibration" });

		var global = new GlobalConfig { MotionDeviceClasses = ["vibration"] };
		Resolver(ha, registry, global).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out var area, out _);

		CollectionAssert.AreEqual(new[] { "binary_sensor.vibration" }, area!.MotionSensors.ToArray(),
			"the built-in 'motion' must be gone, because the household replaced the list");
	}

	// ===================== failures that cost one area, not the house =====================

	[TestMethod]
	public void A_Display_Name_Used_As_An_Area_Id_Is_Rejected_With_The_Real_Ids()
	{
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = [];
		registry.Areas["kjokken"] = [];

		var ok = Resolver(new FakeHaContext(), registry)
			.TryResolve(new AreaConfig { AreaId = "Stue" }, new AreaSettings(), out _, out var error);

		Assert.IsFalse(ok);
		StringAssert.Contains(error!, "stue");
		StringAssert.Contains(error!, "kjokken");
	}

	[TestMethod]
	public void An_Area_With_No_Lights_Is_An_Area_Error()
	{
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = [];

		var ok = Resolver(new FakeHaContext(), registry)
			.TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out _, out var error);

		Assert.IsFalse(ok);
		StringAssert.Contains(error!, "No lights");
	}

	[TestMethod]
	public void An_Area_With_No_Motion_Sensors_Is_An_Area_Error()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.l"];
		ha.SetState("light.l", "off");

		var ok = Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out _, out var error);

		Assert.IsFalse(ok);
		StringAssert.Contains(error!, "No motion sensors");
	}

	[TestMethod]
	public void Two_Illuminance_Sensors_Leave_The_Area_Running_Without_One()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.l", "binary_sensor.m", "sensor.lux_a", "sensor.lux_b"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });
		ha.SetState("sensor.lux_a", "5", new() { ["device_class"] = "illuminance" });
		ha.SetState("sensor.lux_b", "6", new() { ["device_class"] = "illuminance" });

		var ok = Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out var ambiguous, out _);

		// Neither guess nor refuse. Picking one would gate the room on a sensor that may have nothing to do with
		// its daylight - a real house offered the sensor inside its fridge as a candidate. But refusing left a
		// room with two sensors worse off than a room with none, and disabled 8 of 17 rooms on that same house.
		// So the room runs and decides darkness the way a room with no sensor does: outdoor lux, or the sun.
		Assert.IsTrue(ok, "an ambiguous lux sensor must not disable the room");
		Assert.IsNull(ambiguous!.LuxSensor, "and must not silently pick one either");

		var disambiguated = Resolver(ha, registry).TryResolve(
			new AreaConfig { AreaId = "stue", LuxSensor = "sensor.lux_a" }, new AreaSettings(), out var area, out _);

		Assert.IsTrue(disambiguated);
		Assert.AreEqual("sensor.lux_a", area!.LuxSensor);
	}

	[TestMethod]
	public void No_Illuminance_Sensor_Is_Not_An_Error()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.l", "binary_sensor.m"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		var ok = Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out var area, out _);

		Assert.IsTrue(ok, "an area may legitimately gate on the sun alone");
		Assert.IsNull(area!.LuxSensor);
	}

	[TestMethod]
	public void An_Area_With_Neither_An_Area_Id_Nor_A_Light_List_Cannot_Resolve()
	{
		var ok = Resolver(new FakeHaContext(), new FakeAreaRegistry())
			.TryResolve(new AreaConfig { Name = "Nowhere" }, new AreaSettings(), out _, out var error);

		Assert.IsFalse(ok);
		StringAssert.Contains(error!, "No lights");
	}

	// ===================== DiscoverArea: what the configuration page is allowed to show =====================
	//
	// The area picker labels every area with what a room there would resolve to, and the entity pickers offer
	// the area's entities rather than the whole house. Both go through DiscoverArea, so these tests are what
	// stops the page and the engine drifting apart: if the page can be shown an entity here that TryResolve
	// would drop, the page is lying about what will happen.

	[TestMethod]
	public void DiscoverArea_Offers_Exactly_What_TryResolve_Would_Use()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.stue_tak", "light.stue_lampe", "binary_sensor.stue_motion", "sensor.stue_lux", "switch.noise"];
		ha.SetState("light.stue_tak", "off");
		ha.SetState("light.stue_lampe", "off");
		ha.SetState("binary_sensor.stue_motion", "off", new() { ["device_class"] = "motion" });
		ha.SetState("sensor.stue_lux", "10", new() { ["device_class"] = "illuminance" });
		ha.SetState("switch.noise", "off");

		var found = Resolver(ha, registry).DiscoverArea("stue");
		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out var area, out _);

		CollectionAssert.AreEquivalent(area!.Lights.ToArray(), found.Lights.ToArray(),
			"the picker must offer the lights the engine will drive, and only those");
		CollectionAssert.AreEquivalent(area.MotionSensors.ToArray(), found.MotionSensors.ToArray());
		CollectionAssert.AreEqual(new[] { "sensor.stue_lux" }, found.LuxSensors.ToArray());
	}

	/// <summary>
	///     The regression this whole change is about: the picker offered every entity in the instance, so a
	///     living-room light list offered an ESP dev board's status LED. The engine never would have — and now
	///     neither does the picker, because it asks the engine.
	/// </summary>
	[TestMethod]
	public void DiscoverArea_Does_Not_Offer_A_Ghost_The_Engine_Would_Drop()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.stue_tak", "light.esp_lab_status_led", "light.offline_bulb"];
		ha.SetState("light.stue_tak", "off");
		ha.SetState("light.offline_bulb", "unavailable");
		// light.esp_lab_status_led is a disabled registry row: no state at all.

		var found = Resolver(ha, registry).DiscoverArea("stue");

		CollectionAssert.AreEqual(new[] { "light.stue_tak" }, found.Lights.ToArray(),
			"offering an entity discovery excludes would be inviting somebody to configure a light that cannot work");
	}

	[TestMethod]
	public void DiscoverArea_Drops_Group_Members_And_The_Exclude_Label()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.stue_group", "light.bulb_a", "light.bulb_b", "light.skip"];
		registry.Labels["light.skip"] = ["adaptive-exclude"];
		ha.SetState("light.stue_group", "off", new() { ["entity_id"] = new[] { "light.bulb_a", "light.bulb_b" } });
		ha.SetState("light.bulb_a", "off");
		ha.SetState("light.bulb_b", "off");
		ha.SetState("light.skip", "off");

		var found = Resolver(ha, registry).DiscoverArea("stue");

		CollectionAssert.AreEqual(new[] { "light.stue_group" }, found.Lights.ToArray());
	}

	/// <summary>
	///     The lux picker exists to break a tie, so DiscoverArea has to hand back every candidate rather than
	///     the one TryResolve would settle on. An area with two lux sensors offers two, and refusing to guess
	///     stays the resolver's job.
	/// </summary>
	[TestMethod]
	public void DiscoverArea_Reports_Every_Lux_Candidate_Rather_Than_Choosing_One()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.l", "binary_sensor.m", "sensor.lux_a", "sensor.lux_b", "sensor.dead"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });
		ha.SetState("sensor.lux_a", "5", new() { ["device_class"] = "illuminance" });
		ha.SetState("sensor.lux_b", "6", new() { ["device_class"] = "illuminance" });
		ha.SetState("sensor.dead", "unavailable", new() { ["device_class"] = "illuminance" });

		var found = Resolver(ha, registry).DiscoverArea("stue");

		CollectionAssert.AreEquivalent(new[] { "sensor.lux_a", "sensor.lux_b" }, found.LuxSensors.ToArray(),
			"the picker is how a household breaks this tie, so it must be shown both");
	}

	[TestMethod]
	public void DiscoverArea_Honours_The_Motion_Label_And_The_Configured_Device_Classes()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["binary_sensor.mmwave", "binary_sensor.vibration", "binary_sensor.door"];
		registry.Labels["binary_sensor.mmwave"] = ["adaptive-motion"];
		ha.SetState("binary_sensor.mmwave", "off", new() { ["device_class"] = "sound" });
		ha.SetState("binary_sensor.vibration", "off", new() { ["device_class"] = "vibration" });
		ha.SetState("binary_sensor.door", "off", new() { ["device_class"] = "door" });

		var global = new GlobalConfig { MotionDeviceClasses = ["vibration"] };
		var found = Resolver(ha, registry, global).DiscoverArea("stue");

		CollectionAssert.AreEquivalent(new[] { "binary_sensor.vibration", "binary_sensor.mmwave" }, found.MotionSensors.ToArray());
		CollectionAssert.DoesNotContain(found.MotionSensors.ToArray(), "binary_sensor.door");
	}

	[TestMethod]
	public void DiscoverArea_Yields_Nothing_For_An_Area_The_Registry_Does_Not_Know()
	{
		var found = Resolver(new FakeHaContext(), new FakeAreaRegistry()).DiscoverArea("nowhere");

		Assert.AreEqual(0, found.Lights.Count);
		Assert.AreEqual(0, found.MotionSensors.Count);
		Assert.AreEqual(0, found.LuxSensors.Count);
	}
}
