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
	private static AreaConfig Role(string areaId)
	{
		var area = new AreaConfig { AreaId = areaId };
		AreaAutoDiscovery.ApplyRole(area);
		return area;
	}

	[TestMethod]
	public void A_Bedroom_Is_Held_To_Night_Levels_And_Never_Lights_Itself()
	{
		foreach (string areaId in new[] { "soverom", "sov_1", "soverom_samuel", "bedroom" })
		{
			AreaConfig area = Role(areaId);
			Assert.IsTrue(area.RespectSleepMode, $"{areaId} should follow sleep mode");
			Assert.IsTrue(area.SleepBlocksAutoOn, $"{areaId} should not light itself while the house sleeps");
		}
	}

	[TestMethod]
	public void A_Bathroom_Dims_At_Night_But_Still_Lights()
	{
		var area = Role("kjeller_bad");

		Assert.IsTrue(area.RespectSleepMode);
		Assert.IsNull(area.SleepBlocksAutoOn, "a 03:00 trip must still get a light, just a dim one");
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

	// Matching is per segment, so "utleieleilighet" is not read as "ute".
	[TestMethod]
	public void An_Unrecognised_Room_Is_Left_Entirely_On_The_Defaults()
	{
		foreach (string areaId in new[] { "stue", "kontor", "lab", "utleieleilighet" })
		{
			AreaConfig area = Role(areaId);
			Assert.IsNull(area.RespectSleepMode, $"{areaId} should keep following Defaults");
			Assert.IsNull(area.SkipAwaySweep, $"{areaId} should keep following Defaults");
			Assert.IsNull(area.WelcomeHome);
		}
	}

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

	// A dropdown that is not about the house classifies as all-Normal, and all-Normal is never adopted.
	[TestMethod]
	public void An_Unrelated_Dropdown_Is_Not_Adopted()
	{
		Assert.IsNull(HouseModeAutoDetect.Detect(
			WithSelect("input_select.thermostat_profile", "Eco", "Comfort", "Boost"), NullLogger.Instance));
	}

	// Qualifying takes two kinds, so an adopted select always carries a Sleep, Away or Guest option. A drying
	// cupboard's Inne/Ute qualifies, and while it stands on Ute the engine reads the house as empty.
	[TestMethod]
	public void An_Adopted_Select_Always_Carries_A_Kind_That_Changes_What_The_House_Does()
	{
		HouseModeConfig? detected = HouseModeAutoDetect.Detect(
			WithSelect("input_select.torkeskap", "Inne", "Ute"), NullLogger.Instance);

		Assert.IsNotNull(detected, "two kinds and a Normal is all it takes, whatever the dropdown is really for");
		Assert.AreEqual(ModeKind.Away, detected.OptionFor("Ute")!.Kind);
		Assert.IsTrue(
			detected.Options.Any(option => option.Kind != ModeKind.Normal),
			"an all-Normal select cannot be adopted, so adoption can never be inert");

		// And the kind is not decoration: it is what every area reads as the state of the house.
		Assert.AreEqual(
			HouseMode.Away, new HouseState(true, ModeKind.Away, false).Mode,
			"a full house standing on an adopted Away option is read as an empty one");

		// The half that does hold, and the reason adoption is still worth doing.
		Assert.IsTrue(
			detected.Options.All(option => option.Scene is null && !option.HasResetTrigger),
			"nothing adopted here can make the engine write the select or apply a scene");
	}

	[TestMethod]
	public void Two_Candidates_Means_Choosing_Neither()
	{
		var ha = WithSelect("input_select.house_state", "Home", "Away", "Sleeping");
		ha.SetState("input_select.hytta", "Hjemme", new() { ["options"] = new[] { "Hjemme", "Borte" } });

		Assert.IsNull(HouseModeAutoDetect.Detect(ha, NullLogger.Instance));
	}
}
