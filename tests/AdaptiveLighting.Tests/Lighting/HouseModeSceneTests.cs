using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Tests.Common;

using Fixture = AdaptiveLighting.Tests.Common.OrchestratorFixture;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>A house-mode scene stands: the mode change that fires it does not re-aim the rooms over the top of it.</summary>
// Driven through the whole engine, because what these cover is the order of operations: the orchestrator fires
// the scene and hands the new house state to every room in the same synchronous pass.
[TestClass]
public sealed class HouseModeSceneTests
{
	private const string Motion = "binary_sensor.rom_bevegelse";
	private const string Light = "light.rom_taklys";
	private const string Select = "input_select.husmodus";
	private const string NormalOption = "Normal";
	private const string SleepOption = "Sover";
	private const string NightScene = "scene.natt";

	private static Fixture Build(string? scene)
	{
		HouseModeConfig houseMode = new()
		{
			Entity = Select,
			Authority = HouseModeAuthority.HomeAssistant,
			Options =
			[
				new() { Value = NormalOption, Kind = ModeKind.Normal },
				new() { Value = SleepOption, Kind = ModeKind.Sleep, Scene = scene }
			]
		};

		AdaptiveLightingConfig config = new()
		{
			// No lux sensor anywhere, so the room always counts as dark and the darkness gate is never the reason.
			Global = new GlobalConfig { HouseMode = houseMode, SmoothTransitions = false },
			Periods = [new TimePeriodConfig { Name = "day", Start = "07:00", BrightnessPct = 60, ColorTempKelvin = 3000 }],
			Areas = [new AreaConfig { Name = "Rom", Lights = [Light], MotionSensors = [Motion] }]
		};

		return new AreaTestBuilder()
			.States(ha =>
			{
				ha.SetState(Light, "off");
				ha.SetState(Motion, "off");
				ha.SetState(Select, NormalOption);
			})
			.StartOrchestrator(config);
	}

	/// <summary>Lights the room by movement, so the mode change lands on a room the engine believes is active.</summary>
	private static void LightByMovement(Fixture t)
	{
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoActive, t.Room.State, "arranged: the room is lit and active");
		Assert.IsTrue(t.Actuator.Last is { On: true }, "arranged: movement lit the room");

		t.Ha.Trigger(Motion, "off");
		t.Actuator.Clear();
	}

	// ExpectHouseScene must run before the room is handed the new house state, or the mode change re-aims every
	// active room over the scene it just fired.
	[TestMethod]
	public void The_Modes_Own_Scene_Is_Not_Re_Aimed_By_The_Mode_Change_That_Fired_It()
	{
		Fixture t = Build(NightScene);
		LightByMovement(t);

		t.Ha.Trigger(Select, SleepOption);

		CollectionAssert.AreEqual(new List<string> { NightScene }, t.Actuator.Scenes, "arranged: the mode's scene ran");
		Assert.AreEqual(0, t.Actuator.Applied.Count,
			"the scene is the look the house asked for; the engine must not light the room behind it");

		// Waiting the room out must not light it either: the warning dim would turn a dark room on to warn it
		// that it is about to go dark.
		t.Scheduler.AdvanceBy(TimeSpan.FromHours(2).Ticks);

		Assert.IsFalse(t.Actuator.Applied.Any(applied => applied.Command.On),
			"nothing between the scene and the room emptying may switch the lights back on");
	}

	/// <summary>A level test still running when the mode's scene fires: its return must command nothing.</summary>
	// The scene arrives after the test started, so it is held as the house scene alone and the test captured no
	// levels to put back. The scene has already reached the fixtures, so the return owes nothing — and re-firing
	// it from this one room would reach every other room that scene names.
	[TestMethod]
	public void A_Level_Test_Returning_Under_A_House_Scene_Commands_Nothing()
	{
		Fixture t = Build(NightScene);
		LightByMovement(t);

		Assert.IsNull(t.Room.TestPeriod("day"), "arranged: a level test is running, with no scene standing");

		t.Ha.Trigger(Select, SleepOption);

		CollectionAssert.AreEqual(new List<string> { NightScene }, t.Actuator.Scenes, "arranged: the scene ran mid-test");
		t.Actuator.Clear();

		t.Scheduler.AdvanceBy(TimeSpan.FromSeconds(AreaController.LevelTestSeconds).Ticks);

		Assert.AreEqual(0, t.Actuator.Applied.Count,
			"the scene is the look on these lights; the test's return must not command a level over it");
		Assert.AreEqual(0, t.Actuator.Scenes.Count,
			"and must not re-fire the house scene, which would reach every other room it names");
	}

	// The control: the same mode change, with no scene on the option, still re-aims the room. A mode that darkens
	// nothing of its own is what the room's own levels are for.
	[TestMethod]
	public void A_Mode_That_Names_No_Scene_Still_Re_Aims_The_Room()
	{
		Fixture t = Build(scene: null);
		LightByMovement(t);

		t.Ha.Trigger(Select, SleepOption);

		Assert.AreEqual(0, t.Actuator.Scenes.Count, "arranged: this mode fires no scene");
		Assert.IsTrue(t.Actuator.Last is { On: true }, "the mode change is still a command for a room nothing else holds");
	}
}
