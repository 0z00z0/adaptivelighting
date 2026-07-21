using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     First-run zone discovery: what a brand-new installation proposes, and what it refuses to.
/// </summary>
/// <remarks>
///     The rule under test is deliberately strict — a room needs both a light and a motion sensor — because the
///     cost of the two mistakes is not symmetric. A room this misses is one the owner adds in a moment from the
///     UI. A room it invents is lights coming on somewhere nobody asked for, which is how people learn to
///     distrust the whole system.
/// </remarks>
[TestClass]
public sealed class ZoneAutoDiscoveryTests
{
	private static ZoneEntityResolver Resolver(FakeHaContext ha, FakeAreaRegistry registry) =>
		new(ha, registry, new GlobalConfig(), NullLogger.Instance);

	/// <summary>An area qualifies only with both a light and a motion sensor; everything else is left alone.</summary>
	[TestMethod]
	public void Only_Areas_With_Both_A_Light_And_A_Motion_Sensor_Are_Proposed()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();

		// Qualifies: a light and motion.
		ha.SetState("light.gang_tak", "off");
		ha.SetState("binary_sensor.gang_motion", "off", new() { ["device_class"] = "motion" });
		registry.Areas["gang"] = ["light.gang_tak", "binary_sensor.gang_motion"];

		// Lights, but nothing to sense presence with - cannot do motion-driven lighting.
		ha.SetState("light.spisestue_tak", "off");
		registry.Areas["spisestue"] = ["light.spisestue_tak"];

		// Motion, but nothing to light.
		ha.SetState("binary_sensor.bod_motion", "off", new() { ["device_class"] = "motion" });
		registry.Areas["bod"] = ["binary_sensor.bod_motion"];

		// Neither: the kind of "area" a router or a temperature probe lives in.
		ha.SetState("sensor.teknisk_temp", "21", new() { ["device_class"] = "temperature" });
		registry.Areas["teknisk"] = ["sensor.teknisk_temp"];

		var proposed = ZoneAutoDiscovery.Propose(registry, Resolver(ha, registry));

		CollectionAssert.AreEquivalent(
			new[] { "gang" },
			proposed.Select(zone => zone.AreaId).ToArray(),
			"only the room with both a light and a motion sensor is worth proposing");
	}

	/// <summary>A proposal names the area and nothing else, so it stays true across renames.</summary>
	[TestMethod]
	public void A_Proposal_Names_Only_The_Area()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();
		ha.SetState("light.bad_tak", "off");
		ha.SetState("binary_sensor.bad_motion", "off", new() { ["device_class"] = "motion" });
		registry.Areas["bad"] = ["light.bad_tak", "binary_sensor.bad_motion"];

		var zone = ZoneAutoDiscovery.Propose(registry, Resolver(ha, registry)).Single();

		Assert.AreEqual("bad", zone.AreaId);
		Assert.IsNull(zone.Name, "the display name follows the area until somebody types one");
		Assert.IsNull(zone.Lights, "lights resolve from the area at run time, not into the document");
		Assert.IsNull(zone.MotionSensors);
	}

	/// <summary>An empty registry proposes nothing rather than failing.</summary>
	[TestMethod]
	public void Nothing_Is_Proposed_When_No_Area_Qualifies()
	{
		var ha = new FakeHaContext();
		var registry = new FakeAreaRegistry();

		Assert.AreEqual(0, ZoneAutoDiscovery.Propose(registry, Resolver(ha, registry)).Count);
	}

	/// <summary>
	///     The document a fresh install starts from must be valid and must name nothing. This is the regression
	///     test for shipping a placeholder-filled example as real configuration: every REPLACE_ME id was an entity
	///     Home Assistant did not know, so a new installation started with errors and refused to run.
	/// </summary>
	[TestMethod]
	public void The_Default_Document_Is_Valid_And_Names_Nothing()
	{
		AdaptiveLightingConfig config = AdaptiveLightingConfig.CreateDefault();

		Assert.IsTrue(ConfigValidator.Validate(config).IsValid,
			"a brand-new installation must start from a document the engine will actually run");

		Assert.AreEqual(0, config.Zones.Count, "zones are discovered, not invented");
		Assert.AreEqual(0, config.Global.Persons.Count,
			"an empty Persons list discovers every person; a placeholder would override that and block the engine");
		Assert.IsFalse(config.Global.ZonesAutoDiscovered, "so the first connected reload still looks");
		Assert.IsTrue(config.Periods.Count > 0, "the circadian curve is the one thing every house shares");

		string yaml = LightingConfigDocument.Serialize(config);
		StringAssert.DoesNotMatch(yaml, new System.Text.RegularExpressions.Regex("REPLACE_ME"),
			"the shipped default must contain no placeholder ids at all");
	}
}
