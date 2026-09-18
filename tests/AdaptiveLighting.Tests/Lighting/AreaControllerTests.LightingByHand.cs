using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Fixture = AdaptiveLighting.Tests.Common.AreaFixture;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	// The room page's Light on button. It is the room lighting as movement would light it, on the engine's own
	// levels and the room's own vacancy timeout, so nothing here is a rule that exists only for this button.

	[TestMethod]
	public void Lighting_By_Hand_Lights_The_Room_At_The_Periods_Levels_And_Goes_Active()
	{
		Fixture t = Build();

		Assert.IsNull(t.Area.LightNowRefusal());
		Assert.IsNull(t.Area.LightNow());

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		AssertLevels(t.Actuator.Last, 70, 2700, "the evening period the clock is standing in, not a full-brightness answer of its own");
	}

	/// <summary>The trap: a command with no expectation declared ahead of it is read as a hand at the switch.</summary>
	[TestMethod]
	public void Lighting_By_Hand_Declares_An_Expectation_For_Every_Light_And_So_Starts_No_Manual_Hold()
	{
		Fixture t = Build(seed: ha => ha.SetState(SecondLight, "off"), lights: [Light, SecondLight]);

		t.Area.LightNow();

		CollectionAssert.AreEquivalent(
			new[] { Light, SecondLight },
			t.Actuator.Applied.ConvertAll(applied => applied.EntityId),
			"a light commanded without its own expectation would report back as a person");

		// Home Assistant reporting each light the press just commanded, with the context a bulb reports for itself.
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 178 }, PhysicalDevice());
		t.Ha.Trigger(SecondLight, "on", new() { ["brightness"] = 178 }, PhysicalDevice());

		Assert.AreEqual(AreaState.AutoActive, t.Area.State,
			"the room must not fall into a manual hold over its own command");
	}

	[TestMethod]
	public void A_Hand_Lit_Room_Goes_Off_On_The_Rooms_Own_Vacancy_Timeout()
	{
		Fixture t = Build();
		t.Area.LightNow();
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(10));
		Assert.AreEqual(AreaState.PreOff, t.Area.State, "the same ten minutes movement would have bought");

		Advance(t, TimeSpan.FromSeconds(30));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false });
	}

	[TestMethod]
	public void A_Hand_Lit_Room_Keeps_Following_The_Time_Of_Day()
	{
		// Long enough that the night boundary at 22:30 arrives while the room is still lit.
		Fixture t = Build(s => s.VacancyTimeoutSeconds = 36000);
		t.Area.LightNow();

		Advance(t, TimeSpan.FromHours(3));

		AssertLevels(t.Actuator.Last, 15, 2200, "a room lit by hand is still the engine's, so the night period reaches it");
	}

	[TestMethod]
	public void Lighting_By_Hand_Overrides_The_Gates_That_Judge_Conditions()
	{
		Fixture bright = Build(ignoreWhenOn: [Blocker], seed: ha => ha.SetState(Blocker, "on"));
		bright.Ha.SetState(Lux, "5000");

		Assert.IsNull(bright.Area.LightNow(), "too bright and a blocking entity are both what the button exists to defeat");
		Assert.AreEqual(AreaState.AutoActive, bright.Area.State);
		Assert.IsTrue(bright.Actuator.Last is { On: true });

		Fixture asleep = Build(s => s.SleepBlocksAutoOn = true);
		asleep.House.OnNext(House(kind: ModeKind.Sleep));

		Assert.IsNull(asleep.Area.LightNow(), "asking for light in a sleeping house is exactly what a person means");
		Assert.AreEqual(AreaState.AutoActive, asleep.Area.State);
	}

	[TestMethod]
	public void Lighting_By_Hand_Is_Refused_While_The_Room_Is_Not_Enabled()
	{
		Fixture t = Build(s => s.Enabled = false);

		Assert.IsNotNull(t.Area.LightNowRefusal());
		Assert.IsNotNull(t.Area.LightNow());
		Assert.AreEqual(0, t.Actuator.Applied.Count, "a room this app does not manage is not this app's to switch on");
	}

	[TestMethod]
	public void Lighting_By_Hand_Is_Refused_While_The_Master_Switch_Is_On()
	{
		Fixture t = Build();
		t.House.OnNext(House(killed: true));
		t.Actuator.Clear();

		Assert.IsNotNull(t.Area.LightNowRefusal());
		Assert.IsNotNull(t.Area.LightNow());
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void Lighting_By_Hand_Is_Refused_While_The_House_Is_Away()
	{
		Fixture t = Build();
		t.House.OnNext(AwayHouse());
		t.Actuator.Clear();

		Assert.IsNotNull(t.Area.LightNowRefusal(), "an away house is a standing instruction, not a condition");
		Assert.IsNotNull(t.Area.LightNow());
		Assert.AreEqual(AreaState.Away, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void Lighting_By_Hand_Is_Refused_While_A_Guest_Scene_Holds_The_Room()
	{
		Fixture t = Build();
		t.House.OnNext(House(kind: ModeKind.Guest, scene: "scene.dinner"));
		t.Actuator.Clear();

		Assert.IsNotNull(t.Area.LightNowRefusal());
		Assert.IsNotNull(t.Area.LightNow());
		Assert.AreEqual(AreaState.SceneHold, t.Area.State, "the scene is the room's look until the house says otherwise");
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void Lighting_By_Hand_Takes_A_Manually_Held_Room_Back_From_Its_Override()
	{
		Fixture t = BuildHeldByHand();

		Assert.IsNull(t.Area.LightNow());

		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "the press hands the room back to the engine");
		AssertLevels(t.Actuator.Last, 70, 2700, "on the engine's levels, not the ones the hand left");

		// The hold's own clock is gone with it: the vacancy timeout is what ends this room now.
		Advance(t, TimeSpan.FromMinutes(10));
		Assert.AreEqual(AreaState.PreOff, t.Area.State);
	}

	[TestMethod]
	public void Lighting_By_Hand_Lifts_A_Manual_Switch_Off()
	{
		Fixture t = Build();
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 178.5 }, PhysicalDevice());
		t.Ha.Trigger(Light, "off", null, PhysicalDevice());
		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State, "the suppression this test is about");
		t.Actuator.Clear();

		Assert.IsNull(t.Area.LightNow());

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		AssertLevels(t.Actuator.Last, 70, 2700, "asking for light outranks the switch-off it supersedes");
	}

	[TestMethod]
	public void Lighting_By_Hand_Abandons_A_Running_Level_Test()
	{
		Fixture t = Build();
		t.Area.TestPeriod("day");

		Assert.IsNull(t.Area.LightNow());
		Assert.IsFalse(t.Area.IsTestingLevels, "the press is the newest word on these levels");

		AreaSnapshot report = t.Publisher.Snapshots[^1];
		Assert.IsNull(report.TestingPeriodId, "a countdown nothing will honour must not still be reported");
		Assert.IsNull(report.TestEndsAt);
	}

	[TestMethod]
	public void Lighting_By_Hand_Reports_Itself_As_The_Reason()
	{
		Fixture t = Build();
		t.Publisher.Snapshots.Clear();

		t.Area.LightNow();

		AreaSnapshot report = t.Publisher.Snapshots[^1];
		Assert.AreEqual(TransitionReason.ManualLightOn, report.Reason);
		Assert.AreEqual(AreaState.AutoActive, report.State);
	}

	[TestMethod]
	public void Lighting_By_Hand_Uses_The_Rooms_Motion_Scene_Where_It_Names_One()
	{
		Fixture t = Build(sceneOnMotion: "scene.reading");

		Assert.IsNull(t.Area.LightNow());

		CollectionAssert.AreEqual(
			new[] { "scene.reading" },
			t.Actuator.Scenes,
			"one answer to what this room looks like when it lights");
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
	}

	/// <summary>The Light switch pressed off: the wall switch's outcome, so a person standing in the room is not relit.</summary>
	[TestMethod]
	public void Switching_Off_By_Hand_Turns_The_Lights_Off_And_Ignores_Movement()
	{
		Fixture t = Build();
		t.Area.LightNow();
		t.Actuator.Clear();

		Assert.IsNull(t.Area.LightOffRefusal());
		Assert.IsNull(t.Area.LightOff());

		Assert.IsTrue(t.Actuator.Last is { On: false }, "the press must reach the lights");
		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State);
		Assert.AreEqual(TransitionReason.ManualLightOff, t.Publisher.Snapshots[^1].Reason);

		t.Actuator.Clear();
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State, "movement straight after the press must not relight the room");
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}
}
