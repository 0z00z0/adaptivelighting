using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The area picker's label: the lighting entities a room there would actually get, never registry rows.</summary>
[TestClass]
public sealed class AreaOptionTests
{
	[TestMethod]
	public void The_Label_Names_The_Area_And_What_A_Room_There_Would_Get()
	{
		var option = new AreaOption("stue", "Stue", LightCount: 1, MotionCount: 1, LuxCount: 1);

		Assert.AreEqual("Stue (stue) — 1 light, 1 motion, 1 light-level", option.Label);
	}

	[TestMethod]
	public void The_Label_Does_Not_Repeat_A_Name_That_Is_Already_The_Slug()
	{
		var option = new AreaOption("tilbygg", "tilbygg", LightCount: 2, MotionCount: 0, LuxCount: 0);

		Assert.AreEqual("tilbygg — 2 lights, 0 motion, 0 light-level", option.Label,
			"'tilbygg (tilbygg)' tells a household nothing twice");
	}

	[TestMethod]
	public void An_Area_That_Resolves_No_Lights_Says_So_And_Is_Flagged()
	{
		var option = new AreaOption("bod", "Bod", LightCount: 0, MotionCount: 3, LuxCount: 1);

		StringAssert.Contains(option.Label, "0 lights");
		Assert.IsFalse(option.HasLights, "an area here would fail, and the picker must be able to show that before a save");
	}

	[TestMethod]
	public void One_Light_Is_Singular_And_Two_Are_Not()
	{
		Assert.AreEqual("1 light, 0 motion, 0 light-level", new AreaOption("a", "a", 1, 0, 0).Counts);
		Assert.AreEqual("2 lights, 0 motion, 0 light-level", new AreaOption("a", "a", 2, 0, 0).Counts);
	}

	[TestMethod]
	public void More_Than_One_Lux_Sensor_Is_Flagged_As_Ambiguous()
	{
		Assert.IsTrue(new AreaOption("stue", "Stue", 1, 1, 2).LuxIsAmbiguous,
			"discovery refuses to guess between two lux sensors, so the picker can say so before the area is refused");
		Assert.IsFalse(new AreaOption("stue", "Stue", 1, 1, 1).LuxIsAmbiguous);
		Assert.IsFalse(new AreaOption("stue", "Stue", 1, 1, 0).LuxIsAmbiguous, "no lux sensor is a legitimate area, not an ambiguity");
	}
}
