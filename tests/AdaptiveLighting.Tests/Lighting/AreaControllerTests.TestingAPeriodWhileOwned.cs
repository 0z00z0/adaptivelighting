using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	// The same rule as the return above, with a second owner. Where the engine chose the levels it is asked for
	// them again; where a person did, they cannot be derived, so they are read before the test and put back after
	// it. A room with no movement sensor is in OverriddenOn the whole time it is lit, which is precisely when
	// somebody standing in it wants to see what a period looks like.

	// 204 of 255 is 80 %, far enough from every period in the table to identify the command that carries it.
	private static Dictionary<string, object> HandSetLevels() =>
		new() { ["brightness"] = 204.0, ["color_temp_kelvin"] = 3000.0 };

	/// <summary>Has Home Assistant report the day period's levels, as it would once a test has commanded them.</summary>
	// The fake actuator does not write back, so without this a capture taken at the wrong moment reads the
	// person's levels anyway and the test cannot fail. 229.5 of 255 is 90 %.
	private static void ReportTestLevels(Fixture t) =>
		t.Ha.SetState(Light, "on", new() { ["brightness"] = 229.5, ["color_temp_kelvin"] = 4500.0 });

	/// <summary>A room with no movement sensor, lit the only way such a room ever is: by hand.</summary>
	private static Fixture BuildHeldByHand(Action<AreaSettings>? tweak = null, Action<GlobalConfig>? tweakGlobal = null)
	{
		Fixture t = Build(tweak, tweakGlobal, withMotionSensor: false);
		t.Ha.Trigger(Light, "on", HandSetLevels(), PhysicalDevice());

		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State, "the hold this whole section is about");
		t.Actuator.Clear();

		return t;
	}

	private static void AssertLevels(LightCommand? command, double brightnessPct, int? kelvin, string because)
	{
		Assert.IsNotNull(command, because);
		Assert.IsTrue(command.On, because);
		Assert.AreEqual(brightnessPct, command.BrightnessPct ?? double.NaN, 0.01, because);
		Assert.AreEqual(kelvin, command.ColorTempKelvin, because);
	}

	[TestMethod]
	public void A_Test_During_A_Manual_Hold_Gives_The_Person_Their_Own_Levels_Back()
	{
		Fixture t = BuildHeldByHand();

		Assert.IsNull(t.Area.LevelTestRefusal(), "the room the button exists for is the one that used to refuse it");
		Assert.IsNull(t.Area.TestPeriod("day"));
		AssertLevels(t.Actuator.Last, 90, 4500, "the day period's levels reach the real lights");

		ReportTestLevels(t);
		Advance(t, TestRun);

		AssertLevels(t.Actuator.Last, 80, 3000, "read off the fixtures before the test and put back after it");
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State, "and the hold itself is left standing");
	}

	// Issue #24: a colour-channel-only fixture (no colour temperature) has no kelvin for CaptureLights to read
	// back, so its hand-set colour has to travel through LightCommand.Channels instead.
	private static Dictionary<string, object> HandSetColour() =>
		new() { ["brightness"] = 204.0, ["rgb_color"] = new[] { 255, 120, 40 } };

	/// <summary>A room with no movement sensor, its one fixture RGB-only and lit by hand at a hand-set colour.</summary>
	private static Fixture BuildHeldByHandWithColour()
	{
		Fixture t = Build(s => s.ColorControl = ColorControl.EqualChannels, withMotionSensor: false);
		t.Ha.Trigger(Light, "on", HandSetColour(), PhysicalDevice());

		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State, "the hold this test is about");
		t.Actuator.Clear();

		return t;
	}

	/// <summary>The colour-channel counterpart of the test above: a hand-set colour on an RGB-only fixture must
	/// survive a period test too, rather than coming back as the period's neutral equal-channel white.</summary>
	[TestMethod]
	public void A_Test_During_A_Manual_Hold_Gives_The_Persons_Colour_Back_Too()
	{
		Fixture t = BuildHeldByHandWithColour();

		Assert.IsNull(t.Area.TestPeriod("day"));
		Assert.IsTrue(t.Actuator.Last is { EqualChannels: true }, "the test itself shows the period's own equal-channel white");

		Advance(t, TestRun);

		Assert.IsTrue(t.Actuator.Last is { On: true, Channels: [255, 120, 40] },
			"read off the fixture before the test and put back after it, the same as brightness and kelvin");
	}

	/// <summary>The sharpest constraint: the person's hold expires when it would have had nobody pressed.</summary>
	[TestMethod]
	public void A_Test_Leaves_The_Manual_Holds_Own_Countdown_Exactly_Where_It_Was()
	{
		Fixture t = BuildHeldByHand();

		Advance(t, TimeSpan.FromMinutes(5));
		t.Area.TestPeriod("day");
		Advance(t, TestRun);

		// 1 h 59 min 59 s after the hand that armed the two-hour hold.
		Advance(t, TimeSpan.FromMinutes(114) + TimeSpan.FromSeconds(49));
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State, "one second short of the two hours the hand bought");

		Advance(t, TimeSpan.FromSeconds(1));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State,
			"the hold ran on its own clock throughout: neither stretched nor cut short");
	}

	/// <summary>The trap: a return with no expectation declared ahead of it reads as a person at the switch.</summary>
	[TestMethod]
	public void Neither_The_Test_Nor_The_Return_Starts_A_Fresh_Hold_Over_The_Standing_One()
	{
		// A shipped echo window plus a night fade outlasts the whole test, so the test command's own expectation
		// would cover the return as well and this would assert nothing about the return's.
		Fixture t = BuildHeldByHand(
			s => s.NightTransitionSeconds = 0,
			g => g.SelfEchoWindowSeconds = 3);

		t.Area.TestPeriod("day");

		// Home Assistant reporting the light the test just commanded, with the context a bulb reports for itself.
		Advance(t, TimeSpan.FromSeconds(1));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 229.0 }, PhysicalDevice());
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);

		// And the same for the return, nine seconds after that expectation has expired.
		Advance(t, TimeSpan.FromSeconds(9));
		t.Ha.Trigger(Light, "on", HandSetLevels(), PhysicalDevice());

		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State,
			"the return is the engine's own work, so the room must not read it as a person");

		// Still 1 h 59 min 59 s after the hand, because neither command was one.
		Advance(t, TimeSpan.FromMinutes(119) + TimeSpan.FromSeconds(49));
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);

		Advance(t, TimeSpan.FromSeconds(1));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State,
			"a hold restarted by the engine's own return would still have most of two hours to run");
	}

	[TestMethod]
	public void A_Test_While_A_House_Scene_Holds_The_Room_Ends_With_The_Scene_Still_Showing()
	{
		Fixture t = Build(tweakGlobal: g => g.HouseMode = GuestSceneMode());
		t.Ha.SetState(Light, "on", HandSetLevels());
		t.House.OnNext(House(kind: ModeKind.Guest, modeValue: "Gjester", scene: "scene.gjest"));

		Assert.AreEqual(AreaState.SceneHold, t.Area.State);
		t.Actuator.Clear();

		Assert.IsNull(t.Area.TestPeriod("day"));
		Advance(t, TestRun);

		AssertLevels(t.Actuator.Last, 80, 3000, "the scene's own look, read off the fixtures and put back");
		Assert.AreEqual(0, t.Actuator.Scenes.Count,
			"re-firing the house scene would reach every other room it names");
		Assert.AreEqual(AreaState.SceneHold, t.Area.State);
	}

	/// <summary>A second press must not read the first test's levels as if they were somebody's.</summary>
	[TestMethod]
	public void A_Second_Test_During_A_Hold_Still_Owes_The_Person_Their_Levels_And_Owes_Them_Once()
	{
		Fixture t = BuildHeldByHand();

		t.Area.TestPeriod("day");
		ReportTestLevels(t);

		Advance(t, TimeSpan.FromSeconds(6));
		t.Area.TestPeriod("night");
		t.Actuator.Clear();

		// The first press's ten seconds are up, and nothing happens: its return went with it.
		Advance(t, TimeSpan.FromSeconds(4));
		Assert.AreEqual(0, t.Actuator.Applied.Count);

		Advance(t, TimeSpan.FromSeconds(6));
		Assert.AreEqual(1, t.Actuator.Applied.Count, "one return, ten seconds from the newest press");
		AssertLevels(t.Actuator.Last, 80, 3000, "the person's levels, not the levels the first test was showing");
	}

	[TestMethod]
	public void Rebuilding_The_Engine_Hands_A_Held_Room_Back_To_The_Person_At_Once()
	{
		Fixture t = BuildHeldByHand();
		t.Area.TestPeriod("day");
		ReportTestLevels(t);
		t.Actuator.Clear();

		t.Area.Dispose();

		Assert.AreEqual(1, t.Actuator.Applied.Count);
		AssertLevels(t.Actuator.Last, 80, 3000, "a discarded controller must not strand a hold on test levels");

		Advance(t, TestRun);
		Assert.AreEqual(1, t.Actuator.Applied.Count, "and the capture cannot go out a second time");
	}

	/// <summary>A hand at the switch mid-test is the newest word on those levels, so the return is dropped.</summary>
	[TestMethod]
	public void A_Hand_At_The_Switch_During_A_Test_Drops_The_Return_Rather_Than_Landing_On_Top_Of_It()
	{
		// The shipped echo window plus a night fade outlasts a whole test, so a hand can only be read as one here
		// with both shortened.
		Fixture t = BuildHeldByHand(
			s => s.NightTransitionSeconds = 0,
			g => g.SelfEchoWindowSeconds = 0);

		t.Area.TestPeriod("day");

		Advance(t, TimeSpan.FromSeconds(4));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 25.0 }, PhysicalDevice());
		t.Actuator.Clear();

		Advance(t, TestRun);

		Assert.AreEqual(0, t.Actuator.Applied.Count,
			"a ten-second-old capture must not overwrite what the person has just set");
		Assert.IsFalse(t.Area.IsTestingLevels);
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);
	}
}
