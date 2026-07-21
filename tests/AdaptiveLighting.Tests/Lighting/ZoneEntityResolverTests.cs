using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     Discovery: turning an area id into concrete entity ids, and refusing to guess when it cannot.
/// </summary>
[TestClass]
public sealed class ZoneEntityResolverTests
{
	private static ZoneEntityResolver Resolver(FakeHaContext ha, FakeAreaRegistry registry, GlobalConfig? global = null) =>
		new(ha, registry, global ?? new GlobalConfig(), NullLogger.Instance);

	// ===================== the happy path =====================

	[TestMethod]
	public void An_Area_Id_Is_Enough_To_Find_A_Zones_Entities()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.stue_tak", "light.stue_lampe", "binary_sensor.stue_motion", "sensor.stue_lux", "switch.noise"];
		ha.SetState("light.stue_tak", "off");
		ha.SetState("light.stue_lampe", "off");
		ha.SetState("binary_sensor.stue_motion", "off", new() { ["device_class"] = "motion" });
		ha.SetState("sensor.stue_lux", "10", new() { ["device_class"] = "illuminance" });

		var ok = Resolver(ha, registry).TryResolve(new ZoneConfig { Name = "Stue", AreaId = "stue" }, new ZoneSettings(), out var zone, out _);

		Assert.IsTrue(ok);
		Assert.AreEqual(2, zone!.Lights.Count);
		CollectionAssert.AreEqual(new[] { "binary_sensor.stue_motion" }, zone.MotionSensors.ToArray());
		Assert.AreEqual("sensor.stue_lux", zone.LuxSensor);
		CollectionAssert.DoesNotContain(zone.Lights.ToArray(), "switch.noise", "a switch is not a light");
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

		var ok = Resolver(ha, registry).TryResolve(new ZoneConfig { AreaId = "tilbygg" }, new ZoneSettings(), out var zone, out _);

		Assert.IsTrue(ok);
		CollectionAssert.AreEqual(new[] { "light.tilbygg_taklys" }, zone!.Lights.ToArray(),
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

		Resolver(ha, registry).TryResolve(new ZoneConfig { AreaId = "tilbygg" }, new ZoneSettings(), out var zone, out _);

		CollectionAssert.AreEqual(new[] { "light.wiz" }, zone!.Lights.ToArray(),
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

		Resolver(ha, registry).TryResolve(new ZoneConfig { AreaId = "a" }, new ZoneSettings(), out var zone, out _);

		CollectionAssert.AreEqual(new[] { "binary_sensor.live" }, zone!.MotionSensors.ToArray());
	}

	/// <summary>
	///     one live instance's tilbygg has two illuminance sensors, one of them permanently unavailable. Counting the dead one
	///     would make the area ambiguous and cost the whole zone over a sensor that reports nothing.
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

		var ok = Resolver(ha, registry).TryResolve(new ZoneConfig { AreaId = "tilbygg" }, new ZoneSettings(), out var zone, out var error);

		Assert.IsTrue(ok, error);
		Assert.AreEqual("sensor.annex_illuminance", zone!.LuxSensor);
	}

	[TestMethod]
	public void The_Zone_Name_Falls_Back_To_The_Area_Id()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.l", "binary_sensor.m"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		Resolver(ha, registry).TryResolve(new ZoneConfig { AreaId = "stue" }, new ZoneSettings(), out var zone, out _);

		Assert.AreEqual("stue", zone!.Name);
	}

	[TestMethod]
	public void Zone_Overrides_Are_Merged_Onto_The_Document_Defaults()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.l", "binary_sensor.m"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		var defaults = new ZoneSettings { VacancyTimeoutSeconds = 900, WelcomeHome = true };
		Resolver(ha, registry).TryResolve(
			new ZoneConfig { AreaId = "stue", VacancyTimeoutSeconds = 60 }, defaults, out var zone, out _);

		Assert.AreEqual(60, zone!.Settings.VacancyTimeoutSeconds, "the zone's own value wins");
		Assert.IsTrue(zone.Settings.WelcomeHome, "and everything it did not mention is inherited");
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

		Resolver(ha, registry).TryResolve(new ZoneConfig { AreaId = "stue" }, new ZoneSettings(), out var zone, out _);

		CollectionAssert.AreEqual(new[] { "light.stue_group" }, zone!.Lights.ToArray(),
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

		Resolver(ha, registry).TryResolve(new ZoneConfig { AreaId = "stue" }, new ZoneSettings(), out var zone, out _);

		CollectionAssert.AreEqual(new[] { "light.keep" }, zone!.Lights.ToArray());
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

		Resolver(ha, registry).TryResolve(new ZoneConfig { AreaId = "stue" }, new ZoneSettings(), out var zone, out _);

		CollectionAssert.AreEqual(new[] { "binary_sensor.mmwave" }, zone!.MotionSensors.ToArray());
		CollectionAssert.DoesNotContain(zone.MotionSensors.ToArray(), "binary_sensor.door", "a door is not motion");
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
			new ZoneConfig { AreaId = "stue", Lights = ["light.explicit"] }, new ZoneSettings(), out var zone, out _);

		CollectionAssert.AreEqual(new[] { "light.explicit" }, zone!.Lights.ToArray());
		CollectionAssert.AreEqual(new[] { "binary_sensor.discovered" }, zone.MotionSensors.ToArray(),
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
			new ZoneConfig { AreaId = "stue" }, new ZoneSettings(), out var zone, out _);

		Assert.AreEqual(3, zone!.MotionSensors.Count);
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
		Resolver(ha, registry, global).TryResolve(new ZoneConfig { AreaId = "stue" }, new ZoneSettings(), out var zone, out _);

		CollectionAssert.AreEqual(new[] { "binary_sensor.vibration" }, zone!.MotionSensors.ToArray(),
			"the built-in 'motion' must be gone, because the household replaced the list");
	}

	// ===================== failures that cost one zone, not the house =====================

	[TestMethod]
	public void A_Display_Name_Used_As_An_Area_Id_Is_Rejected_With_The_Real_Ids()
	{
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = [];
		registry.Areas["kjokken"] = [];

		var ok = Resolver(new FakeHaContext(), registry)
			.TryResolve(new ZoneConfig { AreaId = "Stue" }, new ZoneSettings(), out _, out var error);

		Assert.IsFalse(ok);
		StringAssert.Contains(error!, "stue");
		StringAssert.Contains(error!, "kjokken");
	}

	[TestMethod]
	public void An_Area_With_No_Lights_Is_A_Zone_Error()
	{
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = [];

		var ok = Resolver(new FakeHaContext(), registry)
			.TryResolve(new ZoneConfig { AreaId = "stue" }, new ZoneSettings(), out _, out var error);

		Assert.IsFalse(ok);
		StringAssert.Contains(error!, "No lights");
	}

	[TestMethod]
	public void An_Area_With_No_Motion_Sensors_Is_A_Zone_Error()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.l"];
		ha.SetState("light.l", "off");

		var ok = Resolver(ha, registry).TryResolve(new ZoneConfig { AreaId = "stue" }, new ZoneSettings(), out _, out var error);

		Assert.IsFalse(ok);
		StringAssert.Contains(error!, "No motion sensors");
	}

	[TestMethod]
	public void Two_Illuminance_Sensors_Must_Be_Disambiguated_Rather_Than_Guessed_Between()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.l", "binary_sensor.m", "sensor.lux_a", "sensor.lux_b"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });
		ha.SetState("sensor.lux_a", "5", new() { ["device_class"] = "illuminance" });
		ha.SetState("sensor.lux_b", "6", new() { ["device_class"] = "illuminance" });

		var ok = Resolver(ha, registry).TryResolve(new ZoneConfig { AreaId = "stue" }, new ZoneSettings(), out _, out var error);

		Assert.IsFalse(ok, "guessing here would gate the room on the wrong sensor, which nobody would ever track down");
		StringAssert.Contains(error!, "sensor.lux_a");
		StringAssert.Contains(error!, "sensor.lux_b");

		var disambiguated = Resolver(ha, registry).TryResolve(
			new ZoneConfig { AreaId = "stue", LuxSensor = "sensor.lux_a" }, new ZoneSettings(), out var zone, out _);

		Assert.IsTrue(disambiguated);
		Assert.AreEqual("sensor.lux_a", zone!.LuxSensor);
	}

	[TestMethod]
	public void No_Illuminance_Sensor_Is_Not_An_Error()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.l", "binary_sensor.m"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		var ok = Resolver(ha, registry).TryResolve(new ZoneConfig { AreaId = "stue" }, new ZoneSettings(), out var zone, out _);

		Assert.IsTrue(ok, "a zone may legitimately gate on the sun alone");
		Assert.IsNull(zone!.LuxSensor);
	}

	[TestMethod]
	public void A_Zone_With_Neither_An_Area_Nor_A_Light_List_Cannot_Resolve()
	{
		var ok = Resolver(new FakeHaContext(), new FakeAreaRegistry())
			.TryResolve(new ZoneConfig { Name = "Nowhere" }, new ZoneSettings(), out _, out var error);

		Assert.IsFalse(ok);
		StringAssert.Contains(error!, "No lights");
	}

	// ===================== DiscoverArea: what the configuration page is allowed to show =====================
	//
	// The area picker labels every area with what a zone on it would resolve to, and the entity pickers offer
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
		Resolver(ha, registry).TryResolve(new ZoneConfig { AreaId = "stue" }, new ZoneSettings(), out var zone, out _);

		CollectionAssert.AreEquivalent(zone!.Lights.ToArray(), found.Lights.ToArray(),
			"the picker must offer the lights the engine will drive, and only those");
		CollectionAssert.AreEquivalent(zone.MotionSensors.ToArray(), found.MotionSensors.ToArray());
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
