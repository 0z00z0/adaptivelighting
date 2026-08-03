using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Discovery: turning an area id into concrete entity ids, and refusing to guess when it cannot.</summary>
[TestClass]
public sealed class AreaEntityResolverTests
{
	private static AreaEntityResolver Resolver(
		FakeHaContext ha,
		FakeAreaRegistry registry,
		GlobalConfig? global = null,
		ILogger? logger = null) =>
		new(ha, registry, global ?? new GlobalConfig(), logger ?? NullLogger.Instance);

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
		CollectionAssert.AreEqual(new[] { "sensor.stue_lux" }, area.LuxSensors.ToArray());
		CollectionAssert.DoesNotContain(area.Lights.ToArray(), "switch.noise", "a switch is not a light");
	}

	// ===================== the registry lists rows, not devices =====================

	// Regression: a disabled entity is still a registry row and still comes back from EntitiesInArea, with no
	// state at all. Discovery called a router's status LED room lighting.
	[TestMethod]
	public void A_Registry_Entry_With_No_State_Is_Not_A_Light()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["tilbygg"] = ["light.tilbygg_taklys", "light.router_socket_status_led", "binary_sensor.tilbygg_motion"];
		ha.SetState("light.tilbygg_taklys", "off");
		ha.SetState("binary_sensor.tilbygg_motion", "off", new() { ["device_class"] = "motion" });
		// light.router_socket_status_led has no state at all: a disabled registry row.

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

	/// <summary>A light stuck on <c>unknown</c> has never reported, so it is dropped like an unavailable one.</summary>
	[TestMethod]
	public void An_Unknown_Light_Is_Not_Discovered_Any_More_Than_An_Unavailable_One()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["tilbygg"] = ["light.wiz", "light.never_reported", "binary_sensor.m"];
		ha.SetState("light.wiz", "off");
		ha.SetState("light.never_reported", "unknown");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "tilbygg" }, new AreaSettings(), out var area, out _);

		CollectionAssert.AreEqual(new[] { "light.wiz" }, area!.Lights.ToArray(),
			"a light that has never reported is as dead for discovery as an unavailable one");
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

	// Counting a permanently unavailable sensor would make the area ambiguous and cost it its lux gate.
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
		CollectionAssert.AreEqual(new[] { "sensor.annex_illuminance" }, area!.LuxSensors.ToArray());
	}

	// The area id is the last fallback, not the first: the registry is asked before it.
	[TestMethod]
	public void An_Unnamed_Area_Falls_Back_To_The_Area_Id()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.l", "binary_sensor.m"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out var area, out _);

		Assert.AreEqual("stue", area!.Name);
	}

	// Discovery writes only an area id, so the registry name is what every snapshot and every page shows.
	[TestMethod]
	public void An_Area_Is_Named_As_Home_Assistant_Names_It()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["kjeller_bad"] = ["light.l", "binary_sensor.m"];
		registry.Names["kjeller_bad"] = "Kjeller - Bad";
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "kjeller_bad" }, new AreaSettings(), out var area, out _);

		Assert.AreEqual("Kjeller - Bad", area!.Name);
	}

	[TestMethod]
	public void A_Configured_Name_Outranks_The_Registrys()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["kjeller_bad"] = ["light.l", "binary_sensor.m"];
		registry.Names["kjeller_bad"] = "Kjeller - Bad";
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		Resolver(ha, registry)
			.TryResolve(new AreaConfig { AreaId = "kjeller_bad", Name = "Kjellerbadet" }, new AreaSettings(), out var area, out _);

		Assert.AreEqual("Kjellerbadet", area!.Name);
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

	// ===================== light groups as a real house builds them =====================
	//
	// "Prefer the group" is simple only while every group is one level deep, sits inside one area and shares no
	// bulb with its neighbour. Real houses break all three, and each break hands the same bulb to two commands.

	// Membership is transitive. A one-hop rule leaves the leaf bulbs alongside the outer group that already
	// commands them whenever the intermediate group sits in no area.
	[TestMethod]
	public void A_Nested_Group_Beats_Its_Leaf_Bulbs_Even_When_The_Inner_Group_Is_Not_In_The_Area()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.stuelys_alle", "light.stue_tak_1", "light.stue_tak_2", "binary_sensor.m"];
		ha.SetState("light.stuelys_alle", "off", new() { ["entity_id"] = new[] { "light.stue_taklys" } });
		// light.stue_taklys is the intermediate group, and it is in no area at all.
		ha.SetState("light.stue_taklys", "off", new() { ["entity_id"] = new[] { "light.stue_tak_1", "light.stue_tak_2" } });
		ha.SetState("light.stue_tak_1", "off");
		ha.SetState("light.stue_tak_2", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEqual(new[] { "light.stuelys_alle" }, area.Lights.ToArray(),
			"membership is transitive, so the outer group holds the bulbs whether or not the inner group is in the room");
	}

	// A group reaching into another area would put one room in charge of the other: they take turns setting each
	// other's brightness, and the first vacancy timeout switches the lights off on whoever is in the other room.
	[TestMethod]
	public void A_Group_Reaching_Into_Another_Area_Loses_To_The_Lights_The_Area_Owns()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.stuelys_alle", "light.stue_taklys", "light.stue_tak_1", "light.stue_tak_2", "binary_sensor.m"];
		registry.Areas["kjokken"] = ["light.kjokkenlys_alle", "light.kjokken_1", "light.kjokken_2", "binary_sensor.k"];
		ha.SetState("light.stuelys_alle", "off", new() { ["entity_id"] = new[] { "light.stue_taklys", "light.kjokkenlys_alle" } });
		ha.SetState("light.stue_taklys", "off", new() { ["entity_id"] = new[] { "light.stue_tak_1", "light.stue_tak_2" } });
		ha.SetState("light.stue_tak_1", "off");
		ha.SetState("light.stue_tak_2", "off");
		ha.SetState("light.kjokkenlys_alle", "off", new() { ["entity_id"] = new[] { "light.kjokken_1", "light.kjokken_2" } });
		ha.SetState("light.kjokken_1", "off");
		ha.SetState("light.kjokken_2", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });
		ha.SetState("binary_sensor.k", "off", new() { ["device_class"] = "motion" });

		RecordingLogger logger = new();
		Resolver(ha, registry, logger: logger).TryResolve(
			new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedArea? stue, out string? error);
		Resolver(ha, registry).TryResolve(
			new AreaConfig { AreaId = "kjokken" }, new AreaSettings(), out ResolvedArea? kjokken, out _);

		Assert.IsNotNull(stue, error);
		CollectionAssert.AreEqual(new[] { "light.stue_taklys" }, stue.Lights.ToArray(),
			"the living room still prefers a group — just the one that does not reach into the kitchen");
		CollectionAssert.AreEqual(new[] { "light.kjokkenlys_alle" }, kjokken!.Lights.ToArray(),
			"and the kitchen keeps command of its own bulbs");

		Assert.AreEqual(1, logger.Warnings.Count);
		StringAssert.Contains(logger.Warnings[0], "stue", "a rule this surprising has to name the area it changed");
		StringAssert.Contains(logger.Warnings[0], "kjokken", "and the area it was reaching into, or it is undiagnosable");
		StringAssert.Contains(logger.Warnings[0], "light.stuelys_alle");
	}

	/// <summary>The cross-area clip has a floor: a room whose only light is a reaching group keeps it.</summary>
	[TestMethod]
	public void A_Reaching_Group_Is_Kept_When_It_Is_All_The_Area_Has()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["gang"] = ["light.gang_alle", "binary_sensor.m"];
		registry.Areas["stue"] = ["light.stue_1"];
		ha.SetState("light.gang_alle", "off", new() { ["entity_id"] = new[] { "light.stue_1" } });
		ha.SetState("light.stue_1", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		RecordingLogger logger = new();
		bool ok = Resolver(ha, registry, logger: logger).TryResolve(
			new AreaConfig { AreaId = "gang" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsTrue(ok, error);
		CollectionAssert.AreEqual(new[] { "light.gang_alle" }, area!.Lights.ToArray(),
			"a room with nothing else to light keeps the group it has");
		Assert.AreEqual(1, logger.Warnings.Count, "and says so, because nothing else will");
	}

	// Two groups that share bulbs while containing neither the other. The widest wins; the narrower one is
	// traded for the bulb only it holds, because a bulb missing from its room is worse than a doubled call.
	[TestMethod]
	public void Overlapping_Sibling_Groups_Command_No_Bulb_Twice_And_Lose_None()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		// The narrower group is listed first on purpose: coverage decides this, not registry order.
		registry.Areas["gang"] =
		[
			"light.gang_vegglys_opp", "light.gang_vegglys",
			"light.bulb_a", "light.bulb_b", "light.bulb_c", "light.bulb_d", "light.wiz_rgbw",
			"binary_sensor.m"
		];
		ha.SetState("light.gang_vegglys", "off",
			new() { ["entity_id"] = new[] { "light.bulb_a", "light.bulb_b", "light.bulb_c", "light.bulb_d" } });
		ha.SetState("light.gang_vegglys_opp", "off",
			new() { ["entity_id"] = new[] { "light.bulb_b", "light.bulb_c", "light.wiz_rgbw" } });
		ha.SetState("light.bulb_a", "off");
		ha.SetState("light.bulb_b", "off");
		ha.SetState("light.bulb_c", "off");
		ha.SetState("light.bulb_d", "off");
		ha.SetState("light.wiz_rgbw", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		RecordingLogger logger = new();
		Resolver(ha, registry, logger: logger).TryResolve(
			new AreaConfig { AreaId = "gang" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEquivalent(new[] { "light.gang_vegglys", "light.wiz_rgbw" }, area.Lights.ToArray(),
			"the wider group keeps its four bulbs, and the fifth arrives on its own rather than not at all");
		CollectionAssert.DoesNotContain(area.Lights.ToArray(), "light.gang_vegglys_opp",
			"keeping both groups is three bulbs commanded twice");
		Assert.AreEqual(1, logger.Warnings.Count);
		StringAssert.Contains(logger.Warnings[0], "light.gang_vegglys_opp");
		StringAssert.Contains(logger.Warnings[0], "light.wiz_rgbw", "the bulb that changed hands has to be named");
	}

	// Home Assistant lets a household build a group that contains itself. LeavesOf walks under a visited set;
	// without one this never returns, and a resolver that hangs takes the whole house with it.
	[TestMethod]
	[Timeout(10000)]
	public void A_Light_Group_That_Contains_Itself_Terminates_And_Still_Lights_The_Room()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["gang"] = ["light.loop_a", "light.loop_b", "light.self", "light.bulb", "binary_sensor.m"];
		ha.SetState("light.loop_a", "off", new() { ["entity_id"] = new[] { "light.loop_b" } });
		ha.SetState("light.loop_b", "off", new() { ["entity_id"] = new[] { "light.loop_a" } });
		ha.SetState("light.self", "off", new() { ["entity_id"] = new[] { "light.self", "light.bulb" } });
		ha.SetState("light.bulb", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		bool ok = Resolver(ha, registry).TryResolve(
			new AreaConfig { AreaId = "gang" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsTrue(ok, error);
		CollectionAssert.AreEquivalent(new[] { "light.loop_a", "light.self" }, area!.Lights.ToArray(),
			"a two-hop loop folds into one of its halves, a self-member still commands the bulb it holds");
	}

	// The overlap rule promotes members into the room's own list, so it has to re-apply the domain filter. The
	// actuator calls light.turn_on unconditionally, and a promoted switch is a call HA rejects on every command.
	[TestMethod]
	public void A_Group_Member_Outside_The_Light_Domain_Is_Not_Promoted_On_Its_Own()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["gang"] = ["light.gang_alle", "light.gang_vegg", "binary_sensor.m"];
		ha.SetState("light.gang_alle", "off", new() { ["entity_id"] = new[] { "light.bulb_a", "light.bulb_b" } });
		ha.SetState("light.gang_vegg", "off", new() { ["entity_id"] = new[] { "light.bulb_b", "switch.relay" } });
		ha.SetState("light.bulb_a", "off");
		ha.SetState("light.bulb_b", "off");
		ha.SetState("switch.relay", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "gang" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEqual(new[] { "light.gang_alle" }, area.Lights.ToArray(),
			"the wider group keeps its bulbs, and the switch the narrower one held is not a light this engine can drive");
	}

	// ===================== the same rules, for motion and illuminance =====================
	//
	// One body of code serves all three domains. These pin that it is reached from each of them, and that each
	// domain's consequence follows: a motion group and its members fire the area two or three times per
	// movement, and an illuminance group and its members weight one instrument twice in the area's average.

	[TestMethod]
	public void A_Motion_Group_Wins_Over_Its_Members()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["kontor"] =
		[
			"light.l",
			"binary_sensor.kontor_trening_bevegelse",
			"binary_sensor.office_motion_detection_desk",
			"binary_sensor.trening_bevegelse_motion_detection"
		];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.kontor_trening_bevegelse", "off", new()
		{
			["device_class"] = "motion",
			["entity_id"] = new[] { "binary_sensor.office_motion_detection_desk", "binary_sensor.trening_bevegelse_motion_detection" }
		});
		ha.SetState("binary_sensor.office_motion_detection_desk", "off", new() { ["device_class"] = "motion" });
		ha.SetState("binary_sensor.trening_bevegelse_motion_detection", "off", new() { ["device_class"] = "motion" });

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "kontor" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEqual(new[] { "binary_sensor.kontor_trening_bevegelse" }, area.MotionSensors.ToArray(),
			"a group and its members are the same movement two or three times over");
	}

	[TestMethod]
	public void A_Nested_Motion_Group_Beats_Its_Leaf_Sensors()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["kontor"] = ["light.l", "binary_sensor.alle", "binary_sensor.pir_1", "binary_sensor.pir_2"];
		ha.SetState("binary_sensor.alle", "off", new()
		{
			["device_class"] = "motion",
			["entity_id"] = new[] { "binary_sensor.inner" }
		});
		// The intermediate group is in no area at all.
		ha.SetState("binary_sensor.inner", "off", new()
		{
			["device_class"] = "motion",
			["entity_id"] = new[] { "binary_sensor.pir_1", "binary_sensor.pir_2" }
		});
		ha.SetState("binary_sensor.pir_1", "off", new() { ["device_class"] = "motion" });
		ha.SetState("binary_sensor.pir_2", "off", new() { ["device_class"] = "motion" });
		ha.SetState("light.l", "off");

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "kontor" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEqual(new[] { "binary_sensor.alle" }, area.MotionSensors.ToArray(),
			"membership is transitive, whether or not the intermediate group sits in the room");
	}

	[TestMethod]
	public void Overlapping_Motion_Groups_Fire_Nothing_Twice_And_Lose_No_Sensor()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["gang"] = ["light.l", "binary_sensor.wide", "binary_sensor.narrow", "binary_sensor.a", "binary_sensor.b", "binary_sensor.c", "binary_sensor.lonely"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.wide", "off", new()
		{
			["device_class"] = "motion",
			["entity_id"] = new[] { "binary_sensor.a", "binary_sensor.b", "binary_sensor.c" }
		});
		ha.SetState("binary_sensor.narrow", "off", new()
		{
			["device_class"] = "motion",
			["entity_id"] = new[] { "binary_sensor.b", "binary_sensor.lonely" }
		});
		ha.SetState("binary_sensor.a", "off", new() { ["device_class"] = "motion" });
		ha.SetState("binary_sensor.b", "off", new() { ["device_class"] = "motion" });
		ha.SetState("binary_sensor.c", "off", new() { ["device_class"] = "motion" });
		ha.SetState("binary_sensor.lonely", "off", new() { ["device_class"] = "motion" });

		RecordingLogger logger = new();
		Resolver(ha, registry, logger: logger).TryResolve(
			new AreaConfig { AreaId = "gang" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEquivalent(new[] { "binary_sensor.wide", "binary_sensor.lonely" }, area.MotionSensors.ToArray(),
			"the wider group keeps its three, and the fourth is watched on its own rather than not at all");
		Assert.AreEqual(1, logger.Warnings.Count, "a rule that quietly changes what a room listens to has to say so");
		StringAssert.Contains(logger.Warnings[0], "motion", "and name which kind of group it settled");
	}

	/// <summary>A motion group reaching into another room would light this one on that one's movement.</summary>
	[TestMethod]
	public void A_Motion_Group_Reaching_Into_Another_Area_Loses_To_The_Sensors_The_Area_Owns()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.l", "binary_sensor.stue_alle", "binary_sensor.stue_pir"];
		registry.Areas["kjokken"] = ["binary_sensor.kjokken_pir"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.stue_alle", "off", new()
		{
			["device_class"] = "motion",
			["entity_id"] = new[] { "binary_sensor.stue_pir", "binary_sensor.kjokken_pir" }
		});
		ha.SetState("binary_sensor.stue_pir", "off", new() { ["device_class"] = "motion" });
		ha.SetState("binary_sensor.kjokken_pir", "off", new() { ["device_class"] = "motion" });

		RecordingLogger logger = new();
		Resolver(ha, registry, logger: logger).TryResolve(
			new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEqual(new[] { "binary_sensor.stue_pir" }, area.MotionSensors.ToArray(),
			"the living room listens to its own sensor rather than to one in the kitchen");
		StringAssert.Contains(logger.Warnings[0], "kjokken");
	}

	[TestMethod]
	[Timeout(10000)]
	public void A_Motion_Group_That_Contains_Itself_Terminates_And_Still_Watches_The_Room()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["gang"] = ["light.l", "binary_sensor.loop_a", "binary_sensor.loop_b", "binary_sensor.self", "binary_sensor.pir"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.loop_a", "off", new() { ["device_class"] = "motion", ["entity_id"] = new[] { "binary_sensor.loop_b" } });
		ha.SetState("binary_sensor.loop_b", "off", new() { ["device_class"] = "motion", ["entity_id"] = new[] { "binary_sensor.loop_a" } });
		ha.SetState("binary_sensor.self", "off", new() { ["device_class"] = "motion", ["entity_id"] = new[] { "binary_sensor.self", "binary_sensor.pir" } });
		ha.SetState("binary_sensor.pir", "off", new() { ["device_class"] = "motion" });

		bool ok = Resolver(ha, registry).TryResolve(
			new AreaConfig { AreaId = "gang" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsTrue(ok, error);
		CollectionAssert.AreEquivalent(new[] { "binary_sensor.loop_a", "binary_sensor.self" }, area!.MotionSensors.ToArray(),
			"a two-hop loop folds into one of its halves, a self-member still watches the sensor it holds");
	}

	// An illuminance group and its members are one reading under three names. This shape used to make an area
	// ambiguous and cost it its lux gate.
	[TestMethod]
	public void An_Illuminance_Group_Wins_Over_Its_Members_And_Settles_The_Room()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.l", "binary_sensor.m", "sensor.lux_alle", "sensor.lux_a", "sensor.lux_b"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });
		ha.SetState("sensor.lux_alle", "12", new()
		{
			["device_class"] = "illuminance",
			["entity_id"] = new[] { "sensor.lux_a", "sensor.lux_b" }
		});
		ha.SetState("sensor.lux_a", "10", new() { ["device_class"] = "illuminance" });
		ha.SetState("sensor.lux_b", "14", new() { ["device_class"] = "illuminance" });

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEqual(new[] { "sensor.lux_alle" }, area.LuxSensors.ToArray(),
			"three candidates were one reading; the group is the room's answer and there is nothing left to average");
	}

	/// <summary>Nesting, overlap and self-reference behave for illuminance as they do everywhere else.</summary>
	[TestMethod]
	[Timeout(10000)]
	public void Illuminance_Groups_Nest_Overlap_And_Loop_Like_Every_Other_Domain()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["nest"] = ["light.l", "binary_sensor.m", "sensor.outer", "sensor.leaf_1", "sensor.leaf_2"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });
		ha.SetState("sensor.outer", "9", new() { ["device_class"] = "illuminance", ["entity_id"] = new[] { "sensor.inner" } });
		ha.SetState("sensor.inner", "9", new() { ["device_class"] = "illuminance", ["entity_id"] = new[] { "sensor.leaf_1", "sensor.leaf_2" } });
		ha.SetState("sensor.leaf_1", "8", new() { ["device_class"] = "illuminance" });
		ha.SetState("sensor.leaf_2", "10", new() { ["device_class"] = "illuminance" });

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "nest" }, new AreaSettings(), out ResolvedArea? nested, out string? error);

		Assert.IsNotNull(nested, error);
		CollectionAssert.AreEqual(new[] { "sensor.outer" }, nested.LuxSensors.ToArray(),
			"membership is transitive here too, and the intermediate group is in no area");

		FakeHaContext loopy = new();
		FakeAreaRegistry loopyRegistry = new();
		loopyRegistry.Areas["loop"] = ["light.l", "binary_sensor.m", "sensor.a", "sensor.b"];
		loopy.SetState("light.l", "off");
		loopy.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });
		loopy.SetState("sensor.a", "5", new() { ["device_class"] = "illuminance", ["entity_id"] = new[] { "sensor.b" } });
		loopy.SetState("sensor.b", "5", new() { ["device_class"] = "illuminance", ["entity_id"] = new[] { "sensor.a" } });

		bool ok = Resolver(loopy, loopyRegistry).TryResolve(
			new AreaConfig { AreaId = "loop" }, new AreaSettings(), out ResolvedArea? looped, out string? loopError);

		Assert.IsTrue(ok, loopError);
		Assert.AreEqual(1, looped!.LuxSensors.Count, "a two-hop loop folds into one of its halves rather than hanging");
	}

	// ===================== one entity per piece of hardware =====================
	//
	// Five light entities can be one RGBW fixture, and the engine commanded all five. Groups have no device of
	// their own; every duplicate channel does, so the device id is both the signal and its own guard.

	[TestMethod]
	public void A_Group_Claims_Its_Devices_So_Loose_Entities_On_The_Same_Fixture_Drop()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["kontor"] =
		[
			"light.kontorlys_alle", "light.kontor_taklys_alle", "light.kontor_taklys_nw", "light.kontor_taklys_ww",
			"light.trening_taklys_nw", "light.trening_taklys_rl", "binary_sensor.m"
		];

		foreach (string channel in new[]
		{
			"light.kontor_taklys_alle", "light.kontor_taklys_nw", "light.kontor_taklys_ww",
			"light.trening_taklys_nw", "light.trening_taklys_rl"
		})
		{
			ha.SetState(channel, "off");
			registry.Devices[channel] = "2c97a05e";
		}

		// The group helper carries no device of its own.
		ha.SetState("light.kontorlys_alle", "off",
			new() { ["entity_id"] = new[] { "light.kontor_taklys_nw", "light.kontor_taklys_ww" } });
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		RecordingLogger logger = new();
		Resolver(ha, registry, logger: logger).TryResolve(
			new AreaConfig { AreaId = "kontor" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEqual(new[] { "light.kontorlys_alle" }, area.Lights.ToArray(),
			"one physical fixture, commanded once, through the group the owner built for it");
		Assert.AreEqual(1, logger.Warnings.Count, "and the four entities that folded into it are named");
	}

	/// <summary>Home Assistant names the parent of an RGBW fixture as the id its channels extend.</summary>
	[TestMethod]
	public void Without_A_Group_The_Parent_Entity_Wins_Its_Own_Colour_Channels()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.stue_vegglys_r", "light.stue_vegglys", "light.stue_vegglys_w", "binary_sensor.m"];

		foreach (string channel in new[] { "light.stue_vegglys", "light.stue_vegglys_r", "light.stue_vegglys_w" })
		{
			ha.SetState(channel, "off");
			registry.Devices[channel] = "aa94d1fd";
		}

		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEqual(new[] { "light.stue_vegglys" }, area.Lights.ToArray(),
			"the parent is named first and the channels extend it, whatever order the registry lists them in");
	}

	/// <summary>The rule collapses hardware, not names.</summary>
	[TestMethod]
	public void Two_Lamps_On_Two_Devices_Are_Still_Two_Lamps()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.stue_tak_1", "light.stue_tak_1_1", "light.stue_tak_2", "binary_sensor.m"];
		ha.SetState("light.stue_tak_1", "off");
		ha.SetState("light.stue_tak_1_1", "off");
		ha.SetState("light.stue_tak_2", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });
		registry.Devices["light.stue_tak_1"] = "391ff1fd";
		registry.Devices["light.stue_tak_1_1"] = "391ff1fd";
		registry.Devices["light.stue_tak_2"] = "7c02aa11";

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEquivalent(new[] { "light.stue_tak_1", "light.stue_tak_2" }, area.Lights.ToArray(),
			"one entity per device: the second lamp is a second device and keeps its place");
	}

	// Motion is exempt from the device rule. One device is one lamp, but it is a controller, not one sensor: a
	// multi-zone presence sensor exposes different zones, and collapsing them blinds the room.
	[TestMethod]
	public void Two_Motion_Zones_On_One_Controller_Are_Both_Kept()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.l", "binary_sensor.zone_near", "binary_sensor.zone_far"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.zone_near", "off", new() { ["device_class"] = "motion" });
		ha.SetState("binary_sensor.zone_far", "off", new() { ["device_class"] = "motion" });
		registry.Devices["binary_sensor.zone_near"] = "mmwave_1";
		registry.Devices["binary_sensor.zone_far"] = "mmwave_1";

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEquivalent(new[] { "binary_sensor.zone_near", "binary_sensor.zone_far" }, area.MotionSensors.ToArray(),
			"losing coverage is silent and is the very failure this change exists to end; a doubled command is not");
	}

	// Illuminance is device-deduplicated: the area averages its sensors, so one instrument exposed twice would
	// carry double weight in that mean.
	[TestMethod]
	public void Two_Illuminance_Entities_On_One_Instrument_Count_Once()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.l", "binary_sensor.m", "sensor.probe", "sensor.probe_lux", "sensor.other"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });
		ha.SetState("sensor.probe", "10", new() { ["device_class"] = "illuminance" });
		ha.SetState("sensor.probe_lux", "10", new() { ["device_class"] = "illuminance" });
		ha.SetState("sensor.other", "900", new() { ["device_class"] = "illuminance" });
		registry.Devices["sensor.probe"] = "shelly_1";
		registry.Devices["sensor.probe_lux"] = "shelly_1";
		registry.Devices["sensor.other"] = "shelly_2";

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEquivalent(new[] { "sensor.probe", "sensor.other" }, area.LuxSensors.ToArray(),
			"two instruments, two votes — the same instrument twice would be one room's opinion counted twice");
	}

	[TestMethod]
	public void A_House_With_No_Device_Information_Is_Untouched_By_The_Rule()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.a", "light.b", "binary_sensor.m"];
		ha.SetState("light.a", "off");
		ha.SetState("light.b", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEqual(new[] { "light.a", "light.b" }, area.Lights.ToArray(),
			"no device is no evidence, so nothing is folded and the registry's own order survives");
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
	// The setting is opt-in. Every document written before it exists says nothing, so null has to keep meaning
	// "manage every light found" or those files change meaning under their owners.

	// Asserted on a document with no IncludeLabel key at all, not on an explicit null: that is what an existing
	// file looks like.
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

	/// <summary>Include selects candidates and exclude then removes, so a light wearing both stays out.</summary>
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

	/// <summary>The other half of the message above: a room with no lights must not be told to go and label them.</summary>
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

	// The label applies to lights only. Filtering sensors too leaves a half-labelled house deaf: lights it may
	// drive, and nothing to tell it when.
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
		CollectionAssert.AreEqual(new[] { "sensor.lux" }, area.LuxSensors.ToArray());
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

	// The default must stay empty. The .NET configuration binder appends bound list items to a non-empty
	// default instead of replacing it, so a YAML list of three would bind to six.
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
	public void Two_Illuminance_Sensors_Are_Both_Kept_For_The_Area_To_Average()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.l", "binary_sensor.m", "sensor.lux_a", "sensor.lux_b"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });
		ha.SetState("sensor.lux_a", "5", new() { ["device_class"] = "illuminance" });
		ha.SetState("sensor.lux_b", "6", new() { ["device_class"] = "illuminance" });

		var ok = Resolver(ha, registry).TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out var ambiguous, out _);

		// Both survive and the gate averages them. Picking one would gate the room on a sensor with nothing to do
		// with its daylight; refusing once disabled 8 of 17 rooms on a live house.
		Assert.IsTrue(ok, "several lux sensors must not disable the room");
		CollectionAssert.AreEquivalent(new[] { "sensor.lux_a", "sensor.lux_b" }, ambiguous!.LuxSensors.ToArray(),
			"two plain sensors with no group and no shared device are two real instruments in one room");

		var disambiguated = Resolver(ha, registry).TryResolve(
			new AreaConfig { AreaId = "stue", LuxSensor = "sensor.lux_a" }, new AreaSettings(), out var area, out _);

		Assert.IsTrue(disambiguated);
		CollectionAssert.AreEqual(new[] { "sensor.lux_a" }, area!.LuxSensors.ToArray(),
			"an explicit sensor is the owner naming the room's reading, and is one sensor by construction");
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
		Assert.AreEqual(0, area!.LuxSensors.Count);
	}

	[TestMethod]
	public void An_Area_With_Neither_An_Area_Id_Nor_A_Light_List_Cannot_Resolve()
	{
		var ok = Resolver(new FakeHaContext(), new FakeAreaRegistry())
			.TryResolve(new AreaConfig { Name = "Nowhere" }, new AreaSettings(), out _, out var error);

		Assert.IsFalse(ok);
		StringAssert.Contains(error!, "No lights");
	}

	// ===================== ExcludeEntities: dropping one discovered entity per room =====================
	//
	// The per-room escape hatch for a sensor that sits in the room's HA area but should not drive its lighting.
	// It filters discovery only. An explicit list already overrules discovery and is not re-filtered by it.

	[TestMethod]
	public void An_Excluded_Discovered_Entity_Is_Absent_From_The_Resolved_Room()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["light.keep", "light.drop", "binary_sensor.keep", "binary_sensor.drop"];
		ha.SetState("light.keep", "off");
		ha.SetState("light.drop", "off");
		ha.SetState("binary_sensor.keep", "off", new() { ["device_class"] = "motion" });
		ha.SetState("binary_sensor.drop", "off", new() { ["device_class"] = "motion" });

		var ok = Resolver(ha, registry).TryResolve(
			new AreaConfig { AreaId = "stue", ExcludeEntities = ["light.drop", "binary_sensor.drop"] },
			new AreaSettings(), out var area, out var error);

		Assert.IsTrue(ok, error);
		CollectionAssert.AreEqual(new[] { "light.keep" }, area!.Lights.ToArray(),
			"an excluded light is not part of the room, though discovery keeps everything else");
		CollectionAssert.AreEqual(new[] { "binary_sensor.keep" }, area.MotionSensors.ToArray());
	}

	[TestMethod]
	public void An_Explicit_Lights_List_Is_Not_Filtered_By_ExcludeEntities()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["stue"] = ["binary_sensor.m"];
		ha.SetState("light.hand_picked", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });

		var ok = Resolver(ha, registry).TryResolve(
			new AreaConfig { AreaId = "stue", Lights = ["light.hand_picked"], ExcludeEntities = ["light.hand_picked"] },
			new AreaSettings(), out var area, out var error);

		Assert.IsTrue(ok, error);
		CollectionAssert.AreEqual(new[] { "light.hand_picked" }, area!.Lights.ToArray(),
			"a hand-picked light stays; ExcludeEntities filters discovery, not the owner's explicit list");
	}

	[TestMethod]
	public void Excluding_The_Only_Lux_Sensor_Leaves_The_Room_Resolving_Without_One()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["kjokken"] = ["light.l", "binary_sensor.m", "sensor.fridge_lux"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });
		ha.SetState("sensor.fridge_lux", "3", new() { ["device_class"] = "illuminance" });

		var ok = Resolver(ha, registry).TryResolve(
			new AreaConfig { AreaId = "kjokken", ExcludeEntities = ["sensor.fridge_lux"] },
			new AreaSettings(), out var area, out var error);

		Assert.IsTrue(ok, error);
		Assert.AreEqual(0, area!.LuxSensors.Count,
			"the excluded sensor is gone, and a room with no lux sensor is simply dark");
	}

	// The exclude is applied to the candidate list before the count decides.
	[TestMethod]
	public void Excluding_One_Of_Two_Lux_Sensors_Chooses_The_Other()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		registry.Areas["kjokken"] = ["light.l", "binary_sensor.m", "sensor.room_lux", "sensor.fridge_lux"];
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.m", "off", new() { ["device_class"] = "motion" });
		ha.SetState("sensor.room_lux", "40", new() { ["device_class"] = "illuminance" });
		ha.SetState("sensor.fridge_lux", "3", new() { ["device_class"] = "illuminance" });

		var ok = Resolver(ha, registry).TryResolve(
			new AreaConfig { AreaId = "kjokken", ExcludeEntities = ["sensor.fridge_lux"] },
			new AreaSettings(), out var area, out var error);

		Assert.IsTrue(ok, error);
		CollectionAssert.AreEqual(new[] { "sensor.room_lux" }, area!.LuxSensors.ToArray(),
			"the fridge probe is out of the average, which is what excluding it by id is for");
	}

	// ===================== DiscoverArea: what the configuration page is allowed to show =====================
	//
	// The area picker and the entity pickers both go through DiscoverArea. These stop the page and the engine
	// drifting apart: an entity the page offers that TryResolve would drop is a lie about what will happen.

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

	// Regression: the picker offered every entity in the instance, including ones discovery drops.
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

	// Asymmetry: DiscoverArea hands back every lux candidate, where TryResolve settles on one. The picker is
	// how a household breaks the tie.
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

	// ===================== a bulb that belongs to no room =====================

	// The gap the cross-area clip cannot close. A bulb in two rooms' groups but in no area is foreign to
	// neither, so both rooms keep their group. The two rooms settle on different ids, so the sharing shows up
	// only once each id is followed down to the bulbs it stands for.
	[TestMethod]
	public void A_Bulb_With_No_Room_Of_Its_Own_Is_Commanded_By_Both_Groups_That_Hold_It()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.stue_alle", "binary_sensor.stue_m"];
		registry.Areas["kjokken"] = ["light.kjokken_alle", "binary_sensor.kjokken_m"];
		registry.Names["stue"] = "Stue";
		registry.Names["kjokken"] = "Kjøkken";

		// The bulb over the counter between the two rooms, in both groups and in no area.
		ha.SetState("light.stue_alle", "off", new() { ["entity_id"] = new[] { "light.stue_taklys", "light.benklys" } });
		ha.SetState("light.kjokken_alle", "off", new() { ["entity_id"] = new[] { "light.kjokken_taklys", "light.benklys" } });
		ha.SetState("light.stue_taklys", "off");
		ha.SetState("light.kjokken_taklys", "off");
		ha.SetState("light.benklys", "off", new() { ["friendly_name"] = "Benklys" });
		ha.SetState("binary_sensor.stue_m", "off", new() { ["device_class"] = "motion" });
		ha.SetState("binary_sensor.kjokken_m", "off", new() { ["device_class"] = "motion" });

		RecordingLogger logger = new();
		AreaEntityResolver resolver = Resolver(ha, registry, logger: logger);

		resolver.TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedArea? stue, out string? stueError);
		resolver.TryResolve(new AreaConfig { AreaId = "kjokken" }, new AreaSettings(), out ResolvedArea? kjokken, out _);

		Assert.IsNotNull(stue, stueError);
		CollectionAssert.AreEqual(new[] { "light.stue_alle" }, stue.Lights.ToArray(), "each room keeps its own group");
		CollectionAssert.AreEqual(new[] { "light.kjokken_alle" }, kjokken!.Lights.ToArray());
		Assert.AreEqual(0, logger.Warnings.Count, "no per-area rule has anything to say — that is the whole gap");

		// What the orchestrator does at start-up: follow each room's ids down to the bulbs, then compare.
		IReadOnlyList<SuspectLight> shared = LightAudit.SharedBetweenRooms(
			[BulbsOf("Stue", stue, resolver, ha), BulbsOf("Kjøkken", kjokken, resolver, ha)],
			entityId => registry.Areas.Values.Any(area => area.Contains(entityId, StringComparer.Ordinal)));

		Assert.AreEqual(1, shared.Count, "one bulb is shared, so there is one thing to say");
		Assert.AreEqual("light.benklys", shared[0].EntityId);
		Assert.AreEqual("Benklys", shared[0].Name, "named as the household named it, not by its slug");
		StringAssert.Contains(shared[0].Reason, "Stue");
		StringAssert.Contains(shared[0].Reason, "Kjøkken");
	}

	/// <summary>The same two rooms, with the bulb filed under one of them, which the cross-area clip owns.</summary>
	[TestMethod]
	public void A_Bulb_Filed_Under_One_Of_The_Rooms_Is_Not_This_Rules_Business()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.stue_alle", "light.stue_taklys", "binary_sensor.stue_m"];
		registry.Areas["kjokken"] = ["light.kjokken_alle", "light.benklys", "binary_sensor.kjokken_m"];

		ha.SetState("light.stue_alle", "off", new() { ["entity_id"] = new[] { "light.stue_taklys", "light.benklys" } });
		ha.SetState("light.kjokken_alle", "off", new() { ["entity_id"] = new[] { "light.benklys" } });
		ha.SetState("light.stue_taklys", "off");
		ha.SetState("light.benklys", "off");
		ha.SetState("binary_sensor.stue_m", "off", new() { ["device_class"] = "motion" });
		ha.SetState("binary_sensor.kjokken_m", "off", new() { ["device_class"] = "motion" });

		AreaEntityResolver resolver = Resolver(ha, registry);

		resolver.TryResolve(new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedArea? stue, out _);
		resolver.TryResolve(new AreaConfig { AreaId = "kjokken" }, new AreaSettings(), out ResolvedArea? kjokken, out _);

		IReadOnlyList<SuspectLight> shared = LightAudit.SharedBetweenRooms(
			[BulbsOf("Stue", stue!, resolver, ha), BulbsOf("Kjøkken", kjokken!, resolver, ha)],
			entityId => registry.Areas.Values.Any(area => area.Contains(entityId, StringComparer.Ordinal)));

		Assert.AreEqual(0, shared.Count, "the kitchen's bulb is the kitchen's, and the clip already said so");
	}

	/// <summary>A room and every bulb its resolved lights stand for. Mirrors <c>LightingOrchestrator.BulbsOf</c>.</summary>
	private static RoomUnderReview BulbsOf(string room, ResolvedArea area, AreaEntityResolver resolver, FakeHaContext ha)
	{
		List<LightUnderReview> bulbs = [];
		HashSet<string> seen = new(StringComparer.Ordinal);

		foreach (string entityId in area.Lights)
			foreach (string bulb in resolver.LeavesOf(entityId))
				if (seen.Add(bulb))
					bulbs.Add(new LightUnderReview(bulb, ha.AttrString(bulb, "friendly_name") ?? bulb));

		return new RoomUnderReview(room, bulbs);
	}

	[TestMethod]
	public void DiscoverArea_Yields_Nothing_For_An_Area_The_Registry_Does_Not_Know()
	{
		var found = Resolver(new FakeHaContext(), new FakeAreaRegistry()).DiscoverArea("nowhere");

		Assert.AreEqual(0, found.Lights.Count);
		Assert.AreEqual(0, found.MotionSensors.Count);
		Assert.AreEqual(0, found.LuxSensors.Count);
	}

	/// <summary>Captures warnings: "and it warns" is half of each cross-area and overlap contract.</summary>
	private sealed class RecordingLogger : ILogger
	{
		private readonly List<string> _warnings = [];

		public IReadOnlyList<string> Warnings => _warnings;

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			ArgumentNullException.ThrowIfNull(formatter);

			if (logLevel >= LogLevel.Warning)
				_warnings.Add(formatter(state, exception));
		}
	}
}
