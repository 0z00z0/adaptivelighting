using System.Reactive.Subjects;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The two per-room scenes: one run in place of switching the room on for movement, one in place of switching
///     it off when the room goes empty.
/// </summary>
/// <remarks>
///     Each replaces one transition and nothing else, so most of what is asserted here is what a scene does
///     <i>not</i> change: every auto-on gate still refuses, the leaving sweep still sweeps, a hand at the switch
///     still wins, and a room naming neither scene behaves as it always did.
/// </remarks>
[TestClass]
public sealed class RoomSceneTests
{
	private const string Motion = "binary_sensor.area_motion";
	private const string Light = "light.area";
	private const string Lux = "sensor.area_lux";
	private const string Holder = "input_boolean.meeting";
	private const string Blocker = "binary_sensor.projector";

	private const string OnMotion = "scene.area_arrival";
	private const string WhenEmpty = "scene.area_atmosphere";

	private const int VacancySeconds = 600;
	private const int PreOffSeconds = 30;
	private static readonly TimeSpan OneTick = TimeSpan.FromSeconds(60);

	private sealed record Fixture(
		TestScheduler Scheduler,
		FakeHaContext Ha,
		FakeLightActuator Actuator,
		FakeStatePublisher Publisher,
		BehaviorSubject<HouseState> House,
		AreaController Area);

	/// <summary>Builds a started area at 20:00, inside "evening", lux 5 so the darkness gate is open.</summary>
	private static Fixture Build(
		string? sceneOnMotion = null,
		string? sceneWhenEmpty = null,
		IReadOnlyList<string>? keepLitWhenOn = null,
		IReadOnlyList<string>? ignoreWhenOn = null,
		bool sleepBlocksAutoOn = false,
		bool enabled = true,
		Action<FakeHaContext>? seed = null)
	{
		TestScheduler scheduler = new();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		FakeHaContext ha = new();
		ha.SetState(Motion, "off");
		ha.SetState(Light, "off");
		ha.SetState(Lux, "5");
		seed?.Invoke(ha);

		AreaSettings settings = new()
		{
			VacancyTimeoutSeconds = VacancySeconds,
			PreOffSeconds = PreOffSeconds,
			Darkness = DarknessSource.Lux,
			OverrideDurationMinutes = 120,
			VacancyResetMinutes = 10,
			SleepBlocksAutoOn = sleepBlocksAutoOn,
			Enabled = enabled
		};

		GlobalConfig global = new() { SmoothTransitions = false, CircadianTickSeconds = 60 };

		List<TimePeriodConfig> table =
		[
			new() { Name = "day", Start = "07:00", BrightnessPct = 90, ColorTempKelvin = 4500 },
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new() { Name = "night", Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200 }
		];

		ResolvedArea area = new("Test", settings, [Light], [Motion], [Lux], [.. ignoreWhenOn ?? []])
		{
			KeepLitWhenOn = [.. keepLitWhenOn ?? []],
			SceneOnMotion = sceneOnMotion,
			SceneWhenEmpty = sceneWhenEmpty
		};

		FakeLightActuator actuator = new();
		FakeStatePublisher publisher = new();
		BehaviorSubject<HouseState> house = new(HouseState.Initial);

		AreaController controller = new(
			ha, scheduler, area, global, table,
			new CircadianCalculator(table, global, () => SunTimes.Unknown),
			actuator, publisher, house, NullLoggerFactory.Instance, areaId: "test_area");

		controller.Start();
		return new Fixture(scheduler, ha, actuator, publisher, house, controller);
	}

	private static void Advance(Fixture fixture, TimeSpan by) => fixture.Scheduler.AdvanceBy(by.Ticks);

	/// <summary>A change with no user and no parent: a wall switch acting on the light itself.</summary>
	private static Context PhysicalDevice() => new() { Id = "physical" };

	/// <summary>Lights the area through motion and forgets what did it.</summary>
	private static Fixture Lit(string? sceneOnMotion = null, string? sceneWhenEmpty = null, IReadOnlyList<string>? keepLitWhenOn = null)
	{
		Fixture t = Build(sceneOnMotion, sceneWhenEmpty, keepLitWhenOn);
		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		t.Actuator.Clear();
		return t;
	}

	// ===================== the movement scene replaces the on-command =====================

	[TestMethod]
	public void Movement_Runs_The_Scene_Instead_Of_Commanding_Brightness_And_Kelvin()
	{
		Fixture t = Build(sceneOnMotion: OnMotion);

		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		CollectionAssert.AreEqual(new[] { OnMotion }, t.Actuator.Scenes.ToArray());
		Assert.AreEqual(0, t.Actuator.Applied.Count, "the scene replaces the command; it does not follow it");
	}

	[TestMethod]
	public void A_Standing_Movement_Scene_Is_Not_Re_Aimed_By_The_Circadian_Tick()
	{
		Fixture scened = Build(sceneOnMotion: OnMotion);
		Fixture control = Build();

		foreach (Fixture t in new[] { scened, control })
		{
			t.Ha.Trigger(Motion, "on");
			t.Actuator.Clear();

			// Occupied across 22:30, where the period changes and the room would be retargeted.
			for (int step = 0; step < 40; step++)
			{
				Advance(t, TimeSpan.FromMinutes(5));
				t.Ha.Trigger(Motion, "off");
				t.Ha.Trigger(Motion, "on");
			}

			Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		}

		Assert.IsTrue(control.Actuator.Last is { On: true, BrightnessPct: 15 },
			"the control proves the tick would have retargeted the room");

		Assert.AreEqual(0, scened.Actuator.Applied.Count, "the scene is the room's look until something replaces it");
		Assert.AreEqual(0, scened.Actuator.Scenes.Count, "and it is applied once, not re-asserted on every tick");
	}

	[TestMethod]
	public void A_Standing_Movement_Scene_Is_Not_Re_Aimed_By_A_House_Mode_Change()
	{
		Fixture t = Build(sceneOnMotion: OnMotion);
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		t.House.OnNext(new HouseState(true, ModeKind.Sleep, false) { ModeValue = "Sover" });

		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	// Motion during the warning dim is movement in the room, so it runs the scene the same way the auto-on does.
	[TestMethod]
	public void Motion_Rescuing_The_Warning_Dim_Runs_The_Movement_Scene_Again()
	{
		Fixture t = Lit(sceneOnMotion: OnMotion);

		Advance(t, TimeSpan.FromSeconds(VacancySeconds));
		Assert.AreEqual(AreaState.PreOff, t.Area.State);
		t.Actuator.Clear();

		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		CollectionAssert.AreEqual(new[] { OnMotion }, t.Actuator.Scenes.ToArray());
	}

	// The movement scene alone changes nothing about going off.
	[TestMethod]
	public void A_Room_With_Only_A_Movement_Scene_Still_Dims_And_Then_Switches_Off()
	{
		Fixture t = Lit(sceneOnMotion: OnMotion);

		Advance(t, TimeSpan.FromSeconds(VacancySeconds));
		Assert.AreEqual(AreaState.PreOff, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: true }, "the warning dim still runs");

		Advance(t, TimeSpan.FromSeconds(PreOffSeconds));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false });
	}

	// ===================== a refusal is still a refusal =====================

	/// <summary>The gates that refuse auto-on, named for <see cref="Refuse"/>.</summary>
	private static IEnumerable<object[]> Refusals =>
	[
		["too bright"],
		["the master switch"],
		["an empty house"],
		["a blocker"]
	];

	private static void Refuse(Fixture t, string gate)
	{
		switch (gate)
		{
			case "too bright":
				t.Ha.SetState(Lux, "5000");
				return;

			case "the master switch":
				t.House.OnNext(new HouseState(true, ModeKind.Normal, true));
				return;

			case "an empty house":
				t.House.OnNext(new HouseState(false, ModeKind.Normal, false));
				return;

			default:
				t.Ha.SetState(Blocker, "on");
				return;
		}
	}

	[TestMethod]
	[DynamicData(nameof(Refusals))]
	public void A_Gate_That_Refuses_Auto_On_Refuses_The_Movement_Scene_Too(string gate)
	{
		Fixture t = Build(sceneOnMotion: OnMotion, ignoreWhenOn: [Blocker], seed: ha => ha.SetState(Blocker, "off"));
		Refuse(t, gate);
		t.Actuator.Clear();

		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(0, t.Actuator.Scenes.Count, $"{gate} refuses, and a scene is not a way past a refusal");
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void A_Disabled_Room_Runs_Neither_Scene()
	{
		Fixture t = Build(sceneOnMotion: OnMotion, sceneWhenEmpty: WhenEmpty, enabled: false);

		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(VacancySeconds + PreOffSeconds + 60));

		Assert.AreEqual(AreaState.Disabled, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Scenes.Count);
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void Sleep_Blocking_Auto_On_Blocks_The_Movement_Scene()
	{
		Fixture t = Build(sceneOnMotion: OnMotion, sleepBlocksAutoOn: true);
		t.House.OnNext(new HouseState(true, ModeKind.Sleep, false) { ModeValue = "Sover" });

		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Scenes.Count);
	}

	// ===================== the empty scene replaces the off =====================

	[TestMethod]
	public void The_Vacancy_Timeout_Runs_The_Empty_Scene_Instead_Of_Switching_Off()
	{
		Fixture t = Lit(sceneWhenEmpty: WhenEmpty);

		Advance(t, TimeSpan.FromSeconds(VacancySeconds));

		CollectionAssert.AreEqual(new[] { WhenEmpty }, t.Actuator.Scenes.ToArray());
		Assert.AreEqual(0, t.Actuator.Applied.Count, "the room drops to atmosphere; it is not switched off");
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
	}

	[TestMethod]
	public void The_Warning_Dim_Does_Not_Run_For_A_Room_With_An_Empty_Scene()
	{
		Fixture t = Lit(sceneWhenEmpty: WhenEmpty);

		Advance(t, TimeSpan.FromSeconds(VacancySeconds));

		Assert.AreNotEqual(AreaState.PreOff, t.Area.State, "nothing is about to go off, so there is nothing to warn about");
		Assert.AreEqual(0, t.Actuator.Applied.Count);

		Advance(t, TimeSpan.FromSeconds(PreOffSeconds + 60));
		Assert.AreEqual(0, t.Actuator.Applied.Count, "and no off lands behind the dim that never ran");
	}

	[TestMethod]
	public void The_Room_Stays_On_Its_Empty_Scene_Instead_Of_Being_Re_Aimed()
	{
		Fixture t = Lit(sceneWhenEmpty: WhenEmpty);

		Advance(t, TimeSpan.FromSeconds(VacancySeconds));
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromHours(3));

		Assert.AreEqual(0, t.Actuator.Applied.Count);
		Assert.AreEqual(0, t.Actuator.Scenes.Count);
	}

	[TestMethod]
	public void Movement_After_The_Empty_Scene_Lights_The_Room_Normally_Again()
	{
		Fixture t = Lit(sceneWhenEmpty: WhenEmpty);

		Advance(t, TimeSpan.FromSeconds(VacancySeconds));
		t.Actuator.Clear();

		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70, ColorTempKelvin: 2700 });
	}

	// An expiring override leaves the room empty, which is the same event the vacancy timeout reports.
	[TestMethod]
	public void An_Override_Expiring_Into_An_Empty_Room_Runs_The_Empty_Scene()
	{
		Fixture t = Lit(sceneWhenEmpty: WhenEmpty);
		Advance(t, TimeSpan.FromSeconds(30));

		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(121));

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		CollectionAssert.AreEqual(new[] { WhenEmpty }, t.Actuator.Scenes.ToArray());
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	// ===================== leaving the house is a different event =====================

	[TestMethod]
	public void The_Leaving_Sweep_Still_Switches_A_Room_With_An_Empty_Scene_Off()
	{
		Fixture t = Lit(sceneWhenEmpty: WhenEmpty);

		t.House.OnNext(new HouseState(false, ModeKind.Normal, false));

		Assert.AreEqual(AreaState.Away, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false }, "an atmospheric scene must not keep a room lit in an empty house");
		Assert.AreEqual(0, t.Actuator.Scenes.Count);
	}

	[TestMethod]
	public void The_Leaving_Sweep_Switches_Off_A_Room_Already_Sitting_On_Its_Empty_Scene()
	{
		Fixture t = Lit(sceneWhenEmpty: WhenEmpty);
		Advance(t, TimeSpan.FromSeconds(VacancySeconds));
		t.Actuator.Clear();

		t.House.OnNext(new HouseState(false, ModeKind.Normal, false));

		Assert.IsTrue(t.Actuator.Last is { On: false });
	}

	// ===================== a hand at the switch still wins =====================

	[TestMethod]
	public void A_Hand_Switching_A_Scened_Room_Off_Is_Still_Obeyed()
	{
		Fixture t = Lit(sceneWhenEmpty: WhenEmpty);
		Advance(t, TimeSpan.FromSeconds(VacancySeconds));

		// Clear of the scene's own echo window, or the change reads as the engine's.
		Advance(t, TimeSpan.FromMinutes(2));
		t.Actuator.Clear();

		t.Ha.Trigger(Light, "off", null, PhysicalDevice());

		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "and nothing relights the room behind them");
		Assert.AreEqual(0, t.Actuator.Scenes.Count);
	}

	// The scene's own light changes carry neither a user nor a parent, which is the detector's definition of a
	// wall switch. Without an expectation declared for them the room overrides itself the instant it scenes.
	[TestMethod]
	public void The_Scenes_Own_Light_Change_Is_Not_Read_As_A_Hand_At_The_Switch()
	{
		Fixture t = Lit(sceneWhenEmpty: WhenEmpty);

		Advance(t, TimeSpan.FromSeconds(VacancySeconds));
		t.Ha.Trigger(Light, "off", null, PhysicalDevice());

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State, "a scene may leave a light off, and that is still the engine's own work");
	}

	[TestMethod]
	public void The_Movement_Scenes_Own_Light_Change_Is_Not_Read_As_A_Hand_At_The_Switch()
	{
		Fixture t = Build(sceneOnMotion: OnMotion);

		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 120 }, PhysicalDevice());

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
	}

	// ===================== the hold blocks the empty scene as it blocks the off =====================

	[TestMethod]
	public void A_KeepLitWhenOn_Hold_Stops_The_Empty_Scene_As_It_Stops_The_Off()
	{
		Fixture t = Build(sceneWhenEmpty: WhenEmpty, keepLitWhenOn: [Holder], seed: ha => ha.SetState(Holder, "on"));
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromSeconds(VacancySeconds + PreOffSeconds + 60));

		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "the room stays as it is, lit as it was");
		Assert.AreEqual(0, t.Actuator.Scenes.Count, "the empty scene is not a way around the hold");
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	// The held-back off is whatever the room's off-transition now is, and for this room that is the scene.
	[TestMethod]
	public void The_Off_A_Hold_Refused_Settles_As_The_Empty_Scene_Once_It_Releases()
	{
		Fixture t = Build(sceneWhenEmpty: WhenEmpty, keepLitWhenOn: [Holder], seed: ha => ha.SetState(Holder, "on"));
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromSeconds(VacancySeconds + 60));
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);

		t.Ha.SetState(Holder, "off");
		Advance(t, OneTick);

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		CollectionAssert.AreEqual(new[] { WhenEmpty }, t.Actuator.Scenes.ToArray());
		Assert.AreEqual(0, t.Actuator.Applied.Count, "settling is the room's own off-transition, which here is a scene");
	}

	[TestMethod]
	public void The_Leaving_Sweep_A_Hold_Refused_Still_Settles_As_An_Off()
	{
		Fixture t = Build(sceneWhenEmpty: WhenEmpty, keepLitWhenOn: [Holder], seed: ha => ha.SetState(Holder, "on"));
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		t.House.OnNext(new HouseState(false, ModeKind.Normal, false));
		Assert.AreEqual(AreaState.Away, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count);

		t.Ha.SetState(Holder, "off");
		Advance(t, OneTick);

		Assert.IsTrue(t.Actuator.Last is { On: false }, "the sweep it refused was a sweep, and an empty house gets no atmosphere");
		Assert.AreEqual(0, t.Actuator.Scenes.Count);
	}

	// ===================== a room naming neither scene =====================

	[TestMethod]
	public void A_Room_Naming_Neither_Scene_Lights_Dims_And_Switches_Off_As_It_Always_Did()
	{
		Fixture t = Build();

		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70, ColorTempKelvin: 2700 });

		Advance(t, TimeSpan.FromSeconds(VacancySeconds));
		Assert.AreEqual(AreaState.PreOff, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 35 });

		Advance(t, TimeSpan.FromSeconds(PreOffSeconds));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false });

		Assert.AreEqual(0, t.Actuator.Scenes.Count);
	}

	// ===================== reporting =====================

	[TestMethod]
	public void The_Snapshot_Names_The_Scene_The_Room_Is_Sitting_On()
	{
		Fixture t = Lit(sceneWhenEmpty: WhenEmpty);

		Advance(t, TimeSpan.FromSeconds(VacancySeconds));

		AreaSnapshot scened = t.Publisher.Snapshots[^1];
		Assert.AreEqual(WhenEmpty, scened.SceneApplied);
		Assert.IsNull(scened.BrightnessPct, "the engine commanded no levels, so it must not report any");
		Assert.IsNull(scened.ColorTempKelvin);

		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		AreaSnapshot lit = t.Publisher.Snapshots[^1];
		Assert.IsNull(lit.SceneApplied, "the engine is aiming the lights again");
		Assert.AreEqual(70d, lit.BrightnessPct);
	}

	[TestMethod]
	public void A_Room_Naming_No_Scene_Reports_None()
	{
		Fixture t = Build();

		Assert.IsNull(t.Publisher.Snapshots[0].SceneApplied);
	}
}
