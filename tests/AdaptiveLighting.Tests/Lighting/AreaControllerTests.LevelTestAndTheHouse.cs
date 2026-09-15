using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void A_Level_Test_Is_Refused_While_The_House_Is_Away()
	{
		var t = Build();
		t.House.OnNext(AwayHouse());

		Assert.IsNotNull(t.Area.LevelTestRefusal(), "the button carries its own reason");
		Assert.IsNotNull(t.Area.TestPeriod("night"), "and the press is refused for the same one");
		Assert.IsFalse(t.Area.IsTestingLevels);
	}

	[TestMethod]
	public void A_Running_Level_Test_Is_Dropped_When_A_Guest_Scene_Takes_The_Room()
	{
		var t = Build();
		Assert.IsNull(t.Area.TestPeriod("night"));

		t.House.OnNext(House(kind: ModeKind.Guest, modeValue: "Gjester", scene: "scene.gjest"));
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromSeconds(AreaController.LevelTestSeconds + 1));

		Assert.IsFalse(t.Area.IsTestingLevels);
		Assert.AreEqual(0, t.Actuator.Applied.Count,
			"the house scene is the newest word on these lights, and the return had nothing of the scene's to give back");
	}

	[TestMethod]
	public void A_Running_Level_Test_Is_Dropped_When_The_House_Goes_Away()
	{
		var t = Build();
		Assert.IsNull(t.Area.TestPeriod("night"), "the control: an ordinary house lets the test run");

		t.House.OnNext(AwayHouse());
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromSeconds(AreaController.LevelTestSeconds + 1));

		Assert.IsFalse(t.Area.IsTestingLevels);
		Assert.AreEqual(0, t.Actuator.Applied.Count,
			"the return would sweep the room dark, over a standing away scene, ten seconds after the house left");
	}

	[TestMethod]
	public void A_Running_Level_Test_Is_Dropped_When_The_Master_Switch_Goes_On()
	{
		var t = Build();
		Assert.IsNull(t.Area.TestPeriod("night"));

		t.House.OnNext(House(killed: true));
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromSeconds(AreaController.LevelTestSeconds + 1));

		Assert.IsFalse(t.Area.IsTestingLevels);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "nothing may command a light while the app says it is paused");
	}
}
