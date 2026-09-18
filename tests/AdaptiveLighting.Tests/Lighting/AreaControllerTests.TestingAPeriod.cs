using System.Reactive.Concurrency;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Fixture = AdaptiveLighting.Tests.Common.AreaFixture;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	// The room page's Test button. It moves the fixtures and nothing else: no state, no timer, no hold, and the
	// return is the engine's own, scheduled here rather than in whatever browser asked for it.

	private static readonly TimeSpan TestRun = TimeSpan.FromSeconds(AreaController.LevelTestSeconds);

	[TestMethod]
	public void Testing_A_Period_Puts_That_Periods_Levels_On_The_Lights()
	{
		Fixture t = Build();

		Assert.IsNull(t.Area.LevelTestRefusal());
		Assert.IsNull(t.Area.TestPeriod("day"));

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 90, ColorTempKelvin: 4500 },
			"the day period's levels, not the evening one the clock is standing in");
	}

	[TestMethod]
	public void A_Test_Shows_The_Rooms_Own_Level_Where_It_States_One()
	{
		Fixture t = Build(levels: [new RoomLevelOverride { PeriodId = "day", BrightnessPct = 25 }]);

		t.Area.TestPeriod("day");

		Assert.IsTrue(t.Actuator.Last is { BrightnessPct: 25, ColorTempKelvin: 4500 },
			"the engine resolves the period, so a test cannot show a level the room would not actually run");
	}

	// The contract changed from "a test is no news about the room" once the countdown had to reach a page that
	// reloads or navigates back mid-test: State still holds, but the press is published so the deadline survives.
	[TestMethod]
	public void A_Test_Changes_No_State_But_Publishes_The_Countdown()
	{
		Fixture t = Build();
		int published = t.Publisher.Snapshots.Count;

		Assert.IsNull(t.Area.TestPeriod("day"));

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State, "a test moves no state the area itself owns");
		Assert.IsTrue(t.Area.IsTestingLevels);
		Assert.AreEqual(published + 1, t.Publisher.Snapshots.Count,
			"pressing Test now has to be news, or a fresh page load finds nothing to redraw the countdown from");

		AreaSnapshot report = t.Publisher.Snapshots[^1];
		Assert.AreEqual("day", report.TestingPeriodId);
		Assert.AreEqual(t.Scheduler.Now + TestRun, report.TestEndsAt);
	}

	[TestMethod]
	public void Abandoning_A_Test_Publishes_That_None_Is_Running()
	{
		// The shipped echo window plus a night fade outlasts a whole test, so a hand can only be read as one here
		// with both shortened; the same trap A_Hand_At_The_Switch_During_A_Test_... below works around.
		Fixture t = Build(s => s.NightTransitionSeconds = 0, g => g.SelfEchoWindowSeconds = 0);
		t.Area.TestPeriod("day");

		// A moment past the (zeroed) echo window, or the trigger below lands at the same instant the expectation
		// expires and still reads as the test's own command.
		Advance(t, TimeSpan.FromSeconds(1));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());

		AreaSnapshot report = t.Publisher.Snapshots[^1];
		Assert.IsNull(report.TestingPeriodId, "the test that hand at the switch abandoned must not still be reported");
		Assert.IsNull(report.TestEndsAt);
	}

	/// <summary>The trap: a command with no expectation declared ahead of it is read as a hand at the switch.</summary>
	[TestMethod]
	public void A_Test_Declares_An_Expectation_For_Every_Light_And_So_Starts_No_Manual_Hold()
	{
		Fixture t = Build(seed: ha => ha.SetState(SecondLight, "off"), lights: [Light, SecondLight]);

		t.Area.TestPeriod("day");

		CollectionAssert.AreEquivalent(
			new[] { Light, SecondLight },
			t.Actuator.Applied.ConvertAll(applied => applied.EntityId),
			"a light commanded without its own expectation would report back as a person");

		// Home Assistant reporting each light the test just commanded, with the context a bulb reports for itself.
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 229 }, PhysicalDevice());
		t.Ha.Trigger(SecondLight, "on", new() { ["brightness"] = 229 }, PhysicalDevice());

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State,
			"the room must not fall into the very hold somebody is on the page configuring");
	}

	[TestMethod]
	public void The_Return_Starts_No_Manual_Hold_Either()
	{
		Fixture t = Build();
		t.Area.TestPeriod("day");
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 229 }, PhysicalDevice());

		Advance(t, TestRun);
		t.Ha.Trigger(Light, "off", null, PhysicalDevice());

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State,
			"the room went dark because the engine sent it there, not because anybody hit a switch");
	}

	[TestMethod]
	public void A_Test_Hands_An_Empty_Room_Back_To_Dark_When_Its_Time_Is_Up()
	{
		Fixture t = Build();
		t.Area.TestPeriod("day");
		t.Actuator.Clear();

		Advance(t, TestRun - TimeSpan.FromSeconds(1));
		Assert.AreEqual(0, t.Actuator.Applied.Count, "the room is still showing the setting");

		Advance(t, TimeSpan.FromSeconds(1));
		Assert.IsTrue(t.Actuator.Last is { On: false }, "a room that should be off goes off");
		Assert.IsFalse(t.Area.IsTestingLevels);
	}

	[TestMethod]
	public void A_Test_In_A_Lit_Room_Returns_It_To_The_Levels_It_Was_Holding()
	{
		Fixture t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		t.Area.TestPeriod("day");
		Assert.IsTrue(t.Actuator.Last is { BrightnessPct: 90 });

		Advance(t, TestRun);

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70, ColorTempKelvin: 2700 },
			"resolved at the instant the test ends, so movement or a boundary during the test is honoured");
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
	}

	[TestMethod]
	public void A_Test_Does_Not_Restart_The_Vacancy_Countdown()
	{
		Fixture t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Motion, "off");

		Advance(t, TimeSpan.FromMinutes(5));
		t.Area.TestPeriod("night");
		Advance(t, TestRun);

		// 9 min 59 s after the movement that armed the ten-minute timeout.
		Advance(t, TimeSpan.FromSeconds(599) - TimeSpan.FromMinutes(5) - TestRun);
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);

		Advance(t, TimeSpan.FromSeconds(1));
		Assert.AreEqual(AreaState.PreOff, t.Area.State, "the vacancy timeout ran on its own clock throughout");
	}

	/// <summary>A second press while one is running: the room must end up owed exactly one return.</summary>
	[TestMethod]
	public void A_Second_Test_Moves_The_Test_And_Leaves_One_Return_Outstanding()
	{
		Fixture t = Build();

		t.Area.TestPeriod("day");
		Advance(t, TimeSpan.FromSeconds(3));

		t.Area.TestPeriod("night");
		Assert.IsTrue(t.Actuator.Last is { BrightnessPct: 15, ColorTempKelvin: 2200 });
		t.Actuator.Clear();

		// The first press's time is up, and nothing happens: its return went with it.
		Advance(t, TestRun - TimeSpan.FromSeconds(3));
		Assert.AreEqual(0, t.Actuator.Applied.Count);
		Assert.IsTrue(t.Area.IsTestingLevels);

		Advance(t, TimeSpan.FromSeconds(3));
		Assert.AreEqual(1, t.Actuator.Applied.Count, "one return, the test's length from the newest press");
		Assert.IsTrue(t.Actuator.Last is { On: false });
	}

	/// <summary>A save rebuilds every controller, and the replacement's first tick is CircadianTickSeconds away.</summary>
	[TestMethod]
	public void Rebuilding_The_Engine_Ends_A_Running_Test_At_Once()
	{
		Fixture t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Area.TestPeriod("day");
		t.Actuator.Clear();

		t.Area.Dispose();

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70 },
			"a discarded controller must not leave the room stranded on test levels");
	}

	[TestMethod]
	public void The_Return_Re_Fires_A_Standing_Scene_Rather_Than_Levels()
	{
		Fixture t = Build(sceneOnMotion: "scene.kveld");
		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(1, t.Actuator.Scenes.Count);

		t.Area.TestPeriod("day");
		Advance(t, TestRun);

		Assert.AreEqual(2, t.Actuator.Scenes.Count,
			"the room's look is that scene, and no level command describes it");
	}

	[TestMethod]
	public void A_Test_Is_Refused_While_The_Master_Switch_Is_On()
	{
		Fixture t = Build();
		t.House.OnNext(House(killed: true));

		Assert.IsNotNull(t.Area.LevelTestRefusal());
		Assert.IsNotNull(t.Area.TestPeriod("day"));
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void A_Test_Is_Refused_In_A_Room_Whose_Automatic_Lighting_Is_Switched_Off()
	{
		Fixture t = Build(s => s.Enabled = false);

		Assert.IsNotNull(t.Area.LevelTestRefusal());
		Assert.IsNotNull(t.Area.TestPeriod("day"));
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void A_Period_The_Schedule_No_Longer_Has_Commands_Nothing()
	{
		Fixture t = Build();

		Assert.IsNotNull(t.Area.TestPeriod("brunch"));
		Assert.AreEqual(0, t.Actuator.Applied.Count);
		Assert.IsFalse(t.Area.IsTestingLevels);
	}
}
