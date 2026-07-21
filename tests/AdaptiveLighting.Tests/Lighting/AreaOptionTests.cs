using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The area picker's label: what a zone on this area would actually get.
/// </summary>
/// <remarks>
///     This label used to be the registry's entity count, and it was worse than nothing. On a live instance it read
///     "Stue (stue) — 517 entities" where Home Assistant's own <c>area_entities('stue')</c> says 164 — the gap
///     being disabled registry rows — and neither number had anything to do with lighting. A big reassuring
///     number on an area that resolves to no lights at all is the exact shape of a bug somebody only finds in
///     the dark, so what is counted here is what discovery resolves, and nothing else.
/// </remarks>
[TestClass]
public sealed class AreaOptionTests
{
	[TestMethod]
	public void The_Label_Names_The_Area_And_What_A_Zone_On_It_Would_Get()
	{
		var option = new AreaOption("stue", "Stue", LightCount: 1, MotionCount: 1, LuxCount: 1);

		Assert.AreEqual("Stue (stue) — 1 light, 1 motion, 1 lux", option.Label);
	}

	[TestMethod]
	public void The_Label_Does_Not_Repeat_A_Name_That_Is_Already_The_Slug()
	{
		var option = new AreaOption("tilbygg", "tilbygg", LightCount: 2, MotionCount: 0, LuxCount: 0);

		Assert.AreEqual("tilbygg — 2 lights, 0 motion, 0 lux", option.Label,
			"'tilbygg (tilbygg)' tells a household nothing twice");
	}

	/// <summary>
	///     The whole point of counting resolved entities rather than registry rows: an area that cannot run a
	///     zone has to say so on sight. The old label would have said "517 entities" here.
	/// </summary>
	[TestMethod]
	public void An_Area_That_Resolves_No_Lights_Says_So_And_Is_Flagged()
	{
		var option = new AreaOption("bod", "Bod", LightCount: 0, MotionCount: 3, LuxCount: 1);

		StringAssert.Contains(option.Label, "0 lights");
		Assert.IsFalse(option.HasLights, "a zone here would fail, and the picker must be able to show that before a save");
	}

	[TestMethod]
	public void One_Light_Is_Singular_And_Two_Are_Not()
	{
		Assert.AreEqual("1 light, 0 motion, 0 lux", new AreaOption("a", "a", 1, 0, 0).Counts);
		Assert.AreEqual("2 lights, 0 motion, 0 lux", new AreaOption("a", "a", 2, 0, 0).Counts);
	}

	[TestMethod]
	public void More_Than_One_Lux_Sensor_Is_Flagged_As_Ambiguous()
	{
		Assert.IsTrue(new AreaOption("stue", "Stue", 1, 1, 2).LuxIsAmbiguous,
			"discovery refuses to guess between two lux sensors, so the picker can say so before the zone is refused");
		Assert.IsFalse(new AreaOption("stue", "Stue", 1, 1, 1).LuxIsAmbiguous);
		Assert.IsFalse(new AreaOption("stue", "Stue", 1, 1, 0).LuxIsAmbiguous, "no lux sensor is a legitimate zone, not an ambiguity");
	}
}
