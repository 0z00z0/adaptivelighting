using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     What a first start configures for itself: the role a room's name implies, and the dropdown that is
///     obviously the house mode.
/// </summary>
[TestClass]
public sealed class AutoConfigureTests
{
	private static ZoneConfig Role(string areaId)
	{
		var zone = new ZoneConfig { AreaId = areaId };
		ZoneAutoDiscovery.ApplyRole(zone);
		return zone;
	}

	[TestMethod]
	public void A_Bedroom_Is_Held_To_Night_Levels_And_Never_Lights_Itself()
	{
		foreach (string area in new[] { "soverom", "sov_1", "soverom_samuel", "bedroom" })
		{
			var zone = Role(area);
			Assert.IsTrue(zone.RespectSleepMode, $"{area} should follow sleep mode");
			Assert.IsTrue(zone.SleepBlocksAutoOn, $"{area} should not light itself while the house sleeps");
		}
	}

	[TestMethod]
	public void A_Bathroom_Dims_At_Night_But_Still_Lights()
	{
		var zone = Role("kjeller_bad");

		Assert.IsTrue(zone.RespectSleepMode);
		Assert.IsNull(zone.SleepBlocksAutoOn, "a 03:00 trip must still get a light, just a dim one");
	}

	[TestMethod]
	public void A_Hallway_Meets_You_At_The_Door()
	{
		Assert.IsTrue(Role("gang").WelcomeHome);
		Assert.IsTrue(Role("kjellergang").WelcomeHome, "a compound segment still reads as a hallway");
		Assert.IsTrue(Role("inngang_ute").WelcomeHome);
	}

	[TestMethod]
	public void Outdoor_Rooms_Are_Left_On_When_The_House_Empties()
	{
		foreach (string area in new[] { "terrasse", "garasje", "inngang_ute" })
			Assert.IsTrue(Role(area).SkipAwaySweep, $"{area} is wanted precisely when nobody is home");
	}

	/// <summary>Segment matching, so a rental flat is not mistaken for the outdoors.</summary>
	[TestMethod]
	public void An_Unrecognised_Room_Is_Left_Entirely_On_The_Defaults()
	{
		foreach (string area in new[] { "stue", "kontor", "lab", "utleieleilighet" })
		{
			var zone = Role(area);
			Assert.IsNull(zone.RespectSleepMode, $"{area} should keep following Defaults");
			Assert.IsNull(zone.SkipAwaySweep, $"{area} should keep following Defaults");
			Assert.IsNull(zone.WelcomeHome);
		}
	}

	// ===================== house mode =====================

	private static FakeHaContext WithSelect(string entityId, params string[] options)
	{
		var ha = new FakeHaContext();
		ha.SetState(entityId, options.FirstOrDefault() ?? "", new() { ["options"] = options });
		return ha;
	}

	[TestMethod]
	public void The_Obvious_House_Mode_Dropdown_Is_Adopted_And_Classified()
	{
		var ha = WithSelect("input_select.house_state", "Home", "Away", "Sleeping", "Guests");

		var detected = HouseModeAutoDetect.Detect(ha, NullLogger.Instance);

		Assert.IsNotNull(detected);
		Assert.AreEqual("input_select.house_state", detected.Entity);
		Assert.AreEqual(ModeKind.Normal, detected.OptionFor("Home")!.Kind);
		Assert.AreEqual(ModeKind.Away, detected.OptionFor("Away")!.Kind);
		Assert.AreEqual(ModeKind.Sleep, detected.OptionFor("Sleeping")!.Kind);
		Assert.AreEqual(ModeKind.Guest, detected.OptionFor("Guests")!.Kind);

		// Read-only on adoption: nothing here can make the engine write the select.
		Assert.IsTrue(detected.Options.All(option => option.Scene is null), "no scene is invented");
		Assert.IsTrue(detected.Options.All(option => !option.HasResetTrigger), "and no reset trigger");
	}

	[TestMethod]
	public void Norwegian_Options_Are_Understood()
	{
		var detected = HouseModeAutoDetect.Detect(
			WithSelect("input_select.husmodus", "Hjemme", "Borte", "Sover", "Gjester"), NullLogger.Instance);

		Assert.IsNotNull(detected);
		Assert.AreEqual(ModeKind.Away, detected.OptionFor("Borte")!.Kind);
		Assert.AreEqual(ModeKind.Sleep, detected.OptionFor("Sover")!.Kind);
		Assert.AreEqual(ModeKind.Guest, detected.OptionFor("Gjester")!.Kind);
	}

	/// <summary>A dropdown that is not about the house classifies as all-Normal and is ignored.</summary>
	[TestMethod]
	public void An_Unrelated_Dropdown_Is_Not_Adopted()
	{
		Assert.IsNull(HouseModeAutoDetect.Detect(
			WithSelect("input_select.thermostat_profile", "Eco", "Comfort", "Boost"), NullLogger.Instance));
	}

	/// <summary>Two plausible candidates is a choice for the household, not for this.</summary>
	[TestMethod]
	public void Two_Candidates_Means_Choosing_Neither()
	{
		var ha = WithSelect("input_select.house_state", "Home", "Away", "Sleeping");
		ha.SetState("input_select.hytta", "Hjemme", new() { ["options"] = new[] { "Hjemme", "Borte" } });

		Assert.IsNull(HouseModeAutoDetect.Detect(ha, NullLogger.Instance));
	}
}
