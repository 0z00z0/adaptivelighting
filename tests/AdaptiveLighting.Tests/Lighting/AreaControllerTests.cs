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
///     Every arrow in the area state machine of 02-architecture.md §5, driven through fakes and a
///     <see cref="TestScheduler"/>.
/// </summary>
/// <remarks>
///     Nothing here touches wall-clock time: the scheduler is the controller's only clock, so a vacancy timeout
///     is an <see cref="TestScheduler.AdvanceBy(long)"/> away and the answer is the same on every machine at
///     every hour. The literal entity ids are fixtures — the engine itself never names an entity.
/// </remarks>
[TestClass]
public sealed class AreaControllerTests
{
	private const string Motion = "binary_sensor.area_motion";
	private const string Light = "light.area";
	private const string Lux = "sensor.area_lux";
	private const string Blocker = "binary_sensor.projector";

	/// <summary>Everything a test needs to drive one area and read what it did.</summary>
	private sealed record Fixture(
		TestScheduler Scheduler,
		FakeHaContext Ha,
		FakeLightActuator Actuator,
		FakeStatePublisher Publisher,
		BehaviorSubject<HouseState> House,
		AreaController Area);

	/// <summary>A house-state snapshot, spelled out so the call sites read as English rather than raw fields.</summary>
	private static HouseState House(bool home = true, ModeKind kind = ModeKind.Normal, bool killed = false, string? modeValue = null, string? scene = null) =>
		new(home, kind, killed) { ModeValue = modeValue, ActiveScene = scene };

	/// <summary>The cabin's real helper shape: Normal, Borte (away), Sover (sleep, no ClampPeriod).</summary>
	private static HouseModeConfig SoverMode() => new()
	{
		Entity = "input_select.husmodus",
		Options =
		[
			new() { Value = "Normal", Kind = ModeKind.Normal },
			new() { Value = "Borte", Kind = ModeKind.Away },
			new() { Value = "Sover", Kind = ModeKind.Sleep }
		]
	};

	/// <summary>
	///     Builds a started area at 20:00 — inside "evening", so the area is dark and its target is stable
	///     across the whole test rather than drifting under it.
	/// </summary>
	private static Fixture Build(
		Action<AreaSettings>? tweak = null,
		Action<GlobalConfig>? tweakGlobal = null,
		IReadOnlyList<string>? ignoreWhenOn = null,
		Action<FakeHaContext>? seed = null,
		IReadOnlyList<TimePeriodConfig>? periods = null)
	{
		var scheduler = new TestScheduler();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		var ha = new FakeHaContext();
		ha.SetState(Motion, "off");
		ha.SetState(Light, "off");
		ha.SetState(Lux, "5");

		// Before Start(), so a test can hand the controller a world that already exists — which is the whole
		// point of start-up adoption.
		seed?.Invoke(ha);

		var settings = new AreaSettings
		{
			VacancyTimeoutSeconds = 600,
			PreOffSeconds = 30,
			Darkness = DarknessSource.Lux,
			OverrideDurationMinutes = 120,
			VacancyResetMinutes = 10
		};
		tweak?.Invoke(settings);

		var global = new GlobalConfig { SmoothTransitions = false, CircadianTickSeconds = 60 };
		tweakGlobal?.Invoke(global);

		var table = periods ?? new List<TimePeriodConfig>
		{
			new() { Name = "day", Start = "07:00", BrightnessPct = 90, ColorTempKelvin = 4500 },
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new() { Name = "night", Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200, MaxBrightnessPct = 30 }
		};

		var area = new ResolvedArea("Test", settings, [Light], [Motion], Lux, ignoreWhenOn ?? []);
		var actuator = new FakeLightActuator();
		var publisher = new FakeStatePublisher();
		var house = new BehaviorSubject<HouseState>(HouseState.Initial);

		var controller = new AreaController(
			ha, scheduler, area, global, table,
			new CircadianCalculator(table, global, () => SunTimes.Unknown),
			actuator, publisher, house, NullLoggerFactory.Instance, areaId: "test_area");

		controller.Start();
		return new Fixture(scheduler, ha, actuator, publisher, house, controller);
	}

	private static void Advance(Fixture fixture, TimeSpan by) => fixture.Scheduler.AdvanceBy(by.Ticks);

	/// <summary>
	///     Builds an area whose light is already on before the engine starts — the state of the world after any
	///     restart of a host that had lit a room.
	/// </summary>
	private static Fixture BuildAlreadyLit(Action<AreaSettings>? tweak = null, string lux = "5") =>
		Build(tweak, seed: ha =>
		{
			ha.SetState(Light, "on", new() { ["brightness"] = 178.5 });
			ha.SetState(Lux, lux);
		});

	/// <summary>A change with no user and no parent: a wall switch or dimmer acting on the light itself.</summary>
	private static Context PhysicalDevice() => new() { Id = "physical" };

	// ===================== AutoVacant -> AutoActive =====================

	[TestMethod]
	public void Motion_When_Dark_Turns_The_Area_On_At_The_Periods_Levels()
	{
		var t = Build();

		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70, ColorTempKelvin: 2700 });
	}

	[TestMethod]
	public void Motion_When_Not_Dark_Is_Logged_But_Not_Acted_On()
	{
		var t = Build();
		t.Ha.SetState(Lux, "500");

		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void Auto_On_Is_Blocked_While_An_IgnoreWhenOn_Entity_Is_On()
	{
		var t = Build(s => s.Darkness = DarknessSource.Always, ignoreWhenOn: [Blocker]);
		t.Ha.SetState(Blocker, "on");

		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count);

		t.Ha.SetState(Blocker, "off");
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
	}

	// ===================== AutoActive -> PreOff -> AutoVacant =====================

	[TestMethod]
	public void Vacancy_Dims_To_PreOff_And_Then_Turns_Off()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(9));
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "the vacancy timeout must not fire early");

		Advance(t, TimeSpan.FromMinutes(1));
		Assert.AreEqual(AreaState.PreOff, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 35 }, "PreOff dims to half the period's brightness");

		Advance(t, TimeSpan.FromSeconds(30));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false });
	}

	[TestMethod]
	public void Motion_During_The_PreOff_Grace_Restores_The_Area()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromMinutes(10));
		t.Actuator.Clear();

		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70 }, "the full levels come back, not the dim");

		Advance(t, TimeSpan.FromSeconds(60));
		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "the grace timer must have been cancelled, not merely outrun");
	}

	[TestMethod]
	public void Motion_Restarts_The_Vacancy_Timer()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromMinutes(9));

		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromMinutes(9));
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);

		Advance(t, TimeSpan.FromMinutes(1));
		Assert.AreEqual(AreaState.PreOff, t.Area.State, "restarting is not cancelling: the timer still fires eventually");
	}

	// ===================== -> OverriddenOn =====================

	[TestMethod]
	public void Manual_On_Overrides_And_The_Engine_Backs_Off_Until_Expiry()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();
		Advance(t, TimeSpan.FromSeconds(30));

		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);

		Advance(t, TimeSpan.FromMinutes(30));
		Assert.AreEqual(0, t.Actuator.Applied.Count, "the human's levels are sacred until the override expires");
	}

	[TestMethod]
	public void Override_Expiring_While_Vacant_Turns_The_Area_Off()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(121));

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false });
	}

	[TestMethod]
	public void Override_Expiring_While_Occupied_Resumes_Control_Instead()
	{
		var t = Build(s => s.OverrideDurationMinutes = 5);
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(2));
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromMinutes(4));

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70 });
	}

	[TestMethod]
	public void Motion_While_Overridden_Extends_Nothing()
	{
		var t = Build(s => s.OverrideDurationMinutes = 5);
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(4));
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "motion must not push the manual levels around");

		Advance(t, TimeSpan.FromMinutes(1));
		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "motion did not extend the override past its five minutes");
	}

	[TestMethod]
	public void Manual_On_From_AutoVacant_Also_Overrides()
	{
		var t = Build();

		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());

		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);
	}

	// ===================== -> SuppressedOff =====================

	[TestMethod]
	public void Manual_Off_Suppresses_The_Area_And_Motion_Respects_It()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");

		t.Ha.Trigger(Light, "off", null, PhysicalDevice());
		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State);

		t.Actuator.Clear();
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State, "the human turned these lights off; motion does not undo that");
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void Manual_Off_During_PreOff_Also_Suppresses()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromMinutes(10));
		Assert.AreEqual(AreaState.PreOff, t.Area.State);

		Advance(t, TimeSpan.FromSeconds(20));
		t.Ha.Trigger(Light, "off", null, PhysicalDevice());

		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State);

		t.Actuator.Clear();
		Advance(t, TimeSpan.FromSeconds(30));
		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State, "the pre-off timer must not fire into a suppressed area");
	}

	/// <summary>
	///     The reset is exactly VacancyResetMinutes of no motion. This is the regression test for a reset that
	///     was additionally gated on an occupancy check — which, since motion restarts the timer anyway, only
	///     ever stretched the reset out to the vacancy timeout and made the configured value a lie.
	/// </summary>
	[TestMethod]
	public void Suppression_Lifts_After_VacancyResetMinutes_Of_No_Motion()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Light, "off", null, PhysicalDevice());

		Advance(t, TimeSpan.FromMinutes(9));
		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State);

		Advance(t, TimeSpan.FromMinutes(1));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State,
			"ten vacant minutes is ten vacant minutes — not ten plus the vacancy timeout");
	}

	[TestMethod]
	public void Motion_Restarts_The_Suppression_Reset_Timer()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Light, "off", null, PhysicalDevice());

		Advance(t, TimeSpan.FromMinutes(9));
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromMinutes(9));
		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State);

		Advance(t, TimeSpan.FromMinutes(2));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
	}

	// ===================== self-echo =====================

	[TestMethod]
	public void Our_Own_Echo_Is_Not_Read_As_A_Human()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");

		Advance(t, TimeSpan.FromSeconds(1));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 178 }, new Context { Id = "echo", UserId = "nd-user" });

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
	}

	/// <summary>
	///     The night-fade bug. A long fade emits attribute changes for its whole duration; with a fixed echo
	///     window the engine read the tail of its own fade as a human at the dimmer and overrode itself on
	///     every night retarget. The window must be SelfEchoWindowSeconds + TransitionSeconds.
	/// </summary>
	[TestMethod]
	public void An_Echo_From_The_Middle_Of_A_Long_Fade_Is_Still_Ours()
	{
		var t = Build(s =>
		{
			s.NightTransitionSeconds = 30;
			s.Darkness = DarknessSource.Always;
		});
		t.Ha.Trigger(Motion, "on");

		// 20 s in: past the 8 s echo window, well inside the 30 s fade we ourselves commanded.
		Advance(t, TimeSpan.FromSeconds(20));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 150 }, new Context { Id = "echo", UserId = "nd-user" });

		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "the engine must not override itself mid-fade");
	}

	[TestMethod]
	public void An_Echo_After_The_Window_And_The_Fade_Have_Both_Passed_Is_A_Human()
	{
		var t = Build(s =>
		{
			s.NightTransitionSeconds = 30;
			s.Darkness = DarknessSource.Always;
		});
		t.Ha.Trigger(Motion, "on");

		Advance(t, TimeSpan.FromSeconds(45));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 150 }, PhysicalDevice());

		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State, "the window must close eventually, or nothing is ever an override");
	}

	// ===================== automations =====================

	[TestMethod]
	public void An_Automation_Counts_As_Manual_By_Default()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");

		t.Ha.Trigger(Light, "off", null, new Context { Id = "x", UserId = "u", ParentId = "automation" });

		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State);
	}

	[TestMethod]
	public void An_Automation_Is_Ignored_When_TreatAutomationsAsManual_Is_False()
	{
		var t = Build(tweakGlobal: g => g.TreatAutomationsAsManual = false);
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));

		t.Ha.Trigger(Light, "off", null, new Context { Id = "x", UserId = "u", ParentId = "automation" });

		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "the knob must actually do something");
	}

	// ===================== kill switch =====================

	[TestMethod]
	public void The_Kill_Switch_Muzzles_The_Engine_And_Releases_Cleanly()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		t.House.OnNext(House(killed: true));
		Assert.AreEqual(AreaState.Disabled, t.Area.State);

		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(0, t.Actuator.Applied.Count, "a disabled engine sends nothing");

		t.House.OnNext(House());
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);

		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
	}

	[TestMethod]
	public void The_Kill_Switch_Is_Entered_From_Any_State()
	{
		var overridden = Build();
		overridden.Ha.Trigger(Motion, "on");
		Advance(overridden, TimeSpan.FromSeconds(30));   // clear the echo window of our own turn_on first
		overridden.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		Assert.AreEqual(AreaState.OverriddenOn, overridden.Area.State);
		overridden.House.OnNext(House(killed: true));
		Assert.AreEqual(AreaState.Disabled, overridden.Area.State);

		var suppressed = Build();
		suppressed.Ha.Trigger(Motion, "on");
		suppressed.Ha.Trigger(Light, "off", null, PhysicalDevice());
		Assert.AreEqual(AreaState.SuppressedOff, suppressed.Area.State);
		suppressed.House.OnNext(House(killed: true));
		Assert.AreEqual(AreaState.Disabled, suppressed.Area.State);

		var preOff = Build();
		preOff.Ha.Trigger(Motion, "on");
		Advance(preOff, TimeSpan.FromMinutes(10));
		Assert.AreEqual(AreaState.PreOff, preOff.Area.State);
		preOff.Actuator.Clear();
		preOff.House.OnNext(House(killed: true));
		Assert.AreEqual(AreaState.Disabled, preOff.Area.State);

		// The pre-off grace had 30 s left. A disabled engine must not spend them turning the lights off.
		Advance(preOff, TimeSpan.FromMinutes(1));
		Assert.AreEqual(0, preOff.Actuator.Applied.Count);
	}

	[TestMethod]
	public void A_Disabled_Area_Never_Commands_Anything()
	{
		var t = Build(s => s.Enabled = false);

		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void Re_Enabling_Into_An_Empty_House_Lands_In_Away_Not_AutoVacant()
	{
		var t = Build();
		t.House.OnNext(House(killed: true));
		Assert.AreEqual(AreaState.Disabled, t.Area.State);

		t.House.OnNext(House(home: false));

		Assert.AreEqual(AreaState.Away, t.Area.State);
	}

	// ===================== away =====================

	[TestMethod]
	public void Everyone_Leaving_Sweeps_The_Area_Off_And_Motion_Then_Does_Nothing()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		t.House.OnNext(House(home: false));

		Assert.AreEqual(AreaState.Away, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false });

		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(AreaState.Away, t.Area.State);
	}

	[TestMethod]
	public void An_Area_With_SkipAwaySweep_Goes_Away_Without_Being_Swept()
	{
		var t = Build(s => s.SkipAwaySweep = true);
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		t.House.OnNext(House(home: false));

		Assert.AreEqual(AreaState.Away, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "outdoor and security lights opt out of the sweep");
	}

	[TestMethod]
	public void The_Sweep_Beats_An_Override()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));   // clear the echo window of our own turn_on first
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);
		t.Actuator.Clear();

		t.House.OnNext(House(home: false));

		Assert.AreEqual(AreaState.Away, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false }, "nobody is in the house to enjoy those levels");
	}

	[TestMethod]
	public void The_Sweep_Reaches_A_Suppressed_Area_Too()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Light, "off", null, PhysicalDevice());

		t.House.OnNext(House(home: false));

		Assert.AreEqual(AreaState.Away, t.Area.State);
	}

	// ===================== welcome home =====================

	[TestMethod]
	public void First_Arrival_Lights_A_WelcomeHome_Area_When_It_Is_Dark()
	{
		var t = Build(s => s.WelcomeHome = true);
		t.House.OnNext(House(home: false));
		t.Actuator.Clear();

		t.House.OnNext(House());

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70 });
	}

	[TestMethod]
	public void First_Arrival_Leaves_An_Ordinary_Area_Dark()
	{
		var t = Build();
		t.House.OnNext(House(home: false));
		t.Actuator.Clear();

		t.House.OnNext(House());

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void A_WelcomeHome_Area_Stays_Dark_When_It_Is_Not_Dark()
	{
		var t = Build(s => s.WelcomeHome = true);
		t.Ha.SetState(Lux, "500");
		t.House.OnNext(House(home: false));
		t.Actuator.Clear();

		t.House.OnNext(House());

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	// ===================== sleep mode =====================

	[TestMethod]
	public void SleepBlocksAutoOn_Stops_The_Area_Lighting_At_All()
	{
		var t = Build(s => s.SleepBlocksAutoOn = true);
		t.House.OnNext(House(kind: ModeKind.Sleep));

		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void RespectSleepMode_Clamps_The_Evening_Target_To_The_Night_Ceiling()
	{
		// Sover is Sleep-kind with no ClampPeriod; SleepClampPeriodFor falls back to the period named "night" (09 §4.1).
		var t = Build(s => s.RespectSleepMode = true, g => g.HouseMode = SoverMode());
		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 30 },
			"20:00 says 70%, but a sleeping house gets night's 30% ceiling whatever the clock says");
	}

	[TestMethod]
	public void Sleep_Mode_Turning_On_Retargets_An_Active_Area()
	{
		var t = Build(s => s.RespectSleepMode = true, g => g.HouseMode = SoverMode());
		t.Ha.Trigger(Motion, "on");
		Assert.IsTrue(t.Actuator.Last is { BrightnessPct: 70 });
		t.Actuator.Clear();

		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 30 });
	}

	// ===================== circadian tick =====================

	[TestMethod]
	public void The_Tick_Retargets_An_Active_Area_When_The_Period_Changes()
	{
		// A vacancy timeout long enough that the area is still AutoActive when the night boundary passes.
		var t = Build(s => s.VacancyTimeoutSeconds = 60 * 60 * 5);
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(151));   // 20:00 -> 22:31, into night

		Assert.IsTrue(t.Actuator.Applied.Any(a => a.Command is { On: true, BrightnessPct: 15 }));
		Assert.IsTrue(t.Actuator.Applied.Count < 5, "a retarget is one command, not one per tick");
	}

	[TestMethod]
	public void A_Tick_That_Changes_Nothing_Sends_Nothing()
	{
		var t = Build(s => s.VacancyTimeoutSeconds = 60 * 60 * 5);
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(10));

		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void The_Tick_Never_Retargets_An_Overridden_Area()
	{
		var t = Build(s =>
		{
			s.VacancyTimeoutSeconds = 60 * 60 * 5;
			s.OverrideDurationMinutes = 60 * 24;
		});
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(151));   // across the night boundary

		Assert.AreEqual(0, t.Actuator.Applied.Count, "the human's levels are sacred until the override expires");
	}

	// ===================== observability and lifetime =====================

	[TestMethod]
	public void Every_Transition_Is_Published()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromMinutes(10));
		Advance(t, TimeSpan.FromSeconds(30));

		var states = t.Publisher.Snapshots.Select(s => s.State).ToList();

		Assert.IsTrue(t.Publisher.Snapshots.Any(s => s.Reason == TransitionReason.Startup));
		CollectionAssert.Contains(states, AreaState.AutoActive);
		CollectionAssert.Contains(states, AreaState.PreOff);
		CollectionAssert.Contains(states, AreaState.AutoVacant);
		Assert.IsTrue(t.Publisher.Snapshots.Any(s => s.PeriodName == "evening"), "a snapshot names the period it acted under");
		Assert.AreEqual(2026, t.Publisher.Snapshots[^1].Timestamp.Year, "the snapshot clock is the scheduler, not the wall");
	}

	/// <summary>
	///     The startup snapshot is read by a person on the dashboard, and it must not dress defaults up as
	///     facts: darkness is evaluated, the period is named, and everything the engine cannot know yet —
	///     commands, motion, deadlines — is null rather than a confident-looking zero.
	/// </summary>
	[TestMethod]
	public void The_Startup_Snapshot_Claims_Only_What_It_Evaluated()
	{
		var dark = Build();
		var opening = dark.Publisher.Snapshots.Single();

		Assert.AreEqual(TransitionReason.Startup, opening.Reason);
		Assert.AreEqual(true, opening.IsDark, "lux 5 is dark and the startup snapshot must have looked");
		Assert.AreEqual("evening", opening.PeriodName, "20:00 is inside the evening period whether or not anything was commanded");
		Assert.IsNull(opening.BrightnessPct);
		Assert.IsNull(opening.LastCommandAt, "no command has been sent, which is not the same as 'lights off'");
		Assert.IsNull(opening.LastMotionAt);
		Assert.IsNull(opening.NextChangeAt);
	}

	[TestMethod]
	public void The_Startup_Snapshot_Reads_The_Actual_Sensor_Not_A_Default()
	{
		var scheduler = new TestScheduler();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		var ha = new FakeHaContext();
		ha.SetState(Motion, "off");
		ha.SetState(Light, "off");
		ha.SetState(Lux, "500");

		var settings = new AreaSettings { Darkness = DarknessSource.Lux };
		var global = new GlobalConfig { SmoothTransitions = false, CircadianTickSeconds = 60 };
		var periods = new List<TimePeriodConfig>
		{
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 }
		};

		var publisher = new FakeStatePublisher();
		var controller = new AreaController(
			ha, scheduler, new ResolvedArea("Test", settings, [Light], [Motion], Lux, []), global, periods,
			new CircadianCalculator(periods, global, () => SunTimes.Unknown),
			new FakeLightActuator(), publisher, new BehaviorSubject<HouseState>(HouseState.Initial),
			NullLoggerFactory.Instance);

		controller.Start();

		Assert.AreEqual(false, publisher.Snapshots.Single().IsDark,
			"500 lux is not dark, and the opening snapshot must say so rather than echo a default");
	}

	[TestMethod]
	public void An_Area_With_No_Lux_Sensor_Reads_The_House_Wide_Outdoor_Lux()
	{
		var scheduler = new TestScheduler();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		const string Outdoor = "sensor.ute_lux";
		var ha = new FakeHaContext();
		ha.SetState(Motion, "off");
		ha.SetState(Light, "off");
		ha.SetState(Outdoor, "5");   // dark outside

		var settings = new AreaSettings { Darkness = DarknessSource.Lux, LuxThreshold = 40 };
		var global = new GlobalConfig { SmoothTransitions = false, CircadianTickSeconds = 60, OutdoorLuxSensor = Outdoor };
		var periods = new List<TimePeriodConfig>
		{
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 }
		};

		var publisher = new FakeStatePublisher();
		var controller = new AreaController(
			ha, scheduler, new ResolvedArea("Test", settings, [Light], [Motion], null, []), global, periods,
			new CircadianCalculator(periods, global, () => SunTimes.Unknown),
			new FakeLightActuator(), publisher, new BehaviorSubject<HouseState>(HouseState.Initial),
			NullLoggerFactory.Instance);

		controller.Start();

		Assert.AreEqual(true, publisher.Snapshots.Single().IsDark,
			"the area has no lux sensor, so it reads the house-wide outdoor sensor: 5 lux is dark");
	}

	// ===================== deadlines and republishing =====================

	[TestMethod]
	public void A_Snapshot_Carries_The_Deadline_Its_State_Is_Waiting_On()
	{
		var start = new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);
		var t = Build();

		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(start + TimeSpan.FromSeconds(600), t.Publisher.Snapshots[^1].NextChangeAt,
			"an active area knows when it will start dimming");

		Advance(t, TimeSpan.FromMinutes(10));
		var preOff = t.Publisher.Snapshots[^1];
		Assert.AreEqual(AreaState.PreOff, preOff.State);
		Assert.AreEqual(start + TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(30), preOff.NextChangeAt,
			"the dim warning names the moment the lights go out");

		Advance(t, TimeSpan.FromSeconds(30));
		var vacant = t.Publisher.Snapshots[^1];
		Assert.AreEqual(AreaState.AutoVacant, vacant.State);
		Assert.IsNull(vacant.NextChangeAt, "a resting area is waiting on motion, not on a clock");
		Assert.IsNull(vacant.BrightnessPct, "the standing command is now 'off'");
		Assert.IsNotNull(vacant.LastCommandAt, "…but it is a dated command, not an absence of one");
	}

	/// <summary>
	///     Motion in an active area moves the vacancy deadline without a state change. A snapshot that
	///     carries a deadline must be re-issued when the deadline moves, or every consumer holds a stale
	///     countdown — this was the dashboard bug where a card could sit frozen for half an hour.
	/// </summary>
	[TestMethod]
	public void Motion_While_Active_Republishes_With_The_Deadline_Moved()
	{
		var start = new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);
		var t = Build();
		t.Ha.Trigger(Motion, "on");

		Advance(t, TimeSpan.FromMinutes(5));
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		var republished = t.Publisher.Snapshots[^1];
		Assert.AreEqual(AreaState.AutoActive, republished.State);
		Assert.AreEqual(TransitionReason.Motion, republished.Reason);
		Assert.AreEqual(start + TimeSpan.FromMinutes(5), republished.LastMotionAt);
		Assert.AreEqual(start + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(600), republished.NextChangeAt);
		Assert.AreEqual(70, republished.BrightnessPct,
			"a republish keeps the standing command's levels — the lights did not change, only the clock did");
	}

	[TestMethod]
	public void An_Override_Publishes_Its_Expiry_And_A_Suppression_Its_Reset()
	{
		var start = new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);

		var overridden = Build();
		overridden.Ha.Trigger(Motion, "on");
		Advance(overridden, TimeSpan.FromSeconds(30));
		overridden.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());

		Assert.AreEqual(
			start + TimeSpan.FromSeconds(30) + TimeSpan.FromMinutes(120),
			overridden.Publisher.Snapshots[^1].NextChangeAt,
			"the override snapshot names the moment automatic control returns");

		var suppressed = Build();
		suppressed.Ha.Trigger(Motion, "on");
		suppressed.Ha.Trigger(Light, "off", null, PhysicalDevice());

		Assert.AreEqual(
			start + TimeSpan.FromMinutes(10),
			suppressed.Publisher.Snapshots[^1].NextChangeAt,
			"the suppression snapshot names the moment motion starts counting again");

		// Motion during the suppression restarts the reset clock, and the restarted clock is republished.
		Advance(suppressed, TimeSpan.FromMinutes(9));
		suppressed.Ha.Trigger(Motion, "off");
		suppressed.Ha.Trigger(Motion, "on");

		var moved = suppressed.Publisher.Snapshots[^1];
		Assert.AreEqual(AreaState.SuppressedOff, moved.State);
		Assert.AreEqual(start + TimeSpan.FromMinutes(9) + TimeSpan.FromMinutes(10), moved.NextChangeAt);
	}

	[TestMethod]
	public void Disabling_The_Area_Clears_The_Published_Deadline()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");

		t.House.OnNext(House(killed: true));

		var disabled = t.Publisher.Snapshots[^1];
		Assert.AreEqual(AreaState.Disabled, disabled.State);
		Assert.IsNull(disabled.NextChangeAt, "a muzzled engine has no scheduled next move to promise");
		Assert.IsNull(disabled.NextChangeFrom, "…and no countdown span either — the pair lives and dies together");
	}

	/// <summary>
	///     A countdown has two ends. <see cref="AreaSnapshot.NextChangeAt"/> alone renders a deadline; a
	///     progress bar also needs the instant the timer was armed, and deriving that client-side from any
	///     other timestamp would be a guess — <see cref="AreaSnapshot.Timestamp"/> moves on republishes that
	///     re-arm nothing.
	/// </summary>
	[TestMethod]
	public void A_Snapshot_Carries_Both_Ends_Of_Its_Countdown()
	{
		var start = new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);
		var t = Build();

		t.Ha.Trigger(Motion, "on");
		var active = t.Publisher.Snapshots[^1];
		Assert.AreEqual(start, active.NextChangeFrom, "the vacancy countdown began the moment it was armed");
		Assert.AreEqual(start + TimeSpan.FromSeconds(600), active.NextChangeAt);

		// Motion re-arms the vacancy timer: both ends move together, and the re-arm republishes.
		Advance(t, TimeSpan.FromMinutes(5));
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		var rearmed = t.Publisher.Snapshots[^1];
		Assert.AreEqual(start + TimeSpan.FromMinutes(5), rearmed.NextChangeFrom);
		Assert.AreEqual(start + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(600), rearmed.NextChangeAt);

		// The pre-off warning is a new, shorter countdown, not the tail of the old one.
		Advance(t, TimeSpan.FromMinutes(10));
		var preOff = t.Publisher.Snapshots[^1];
		Assert.AreEqual(AreaState.PreOff, preOff.State);
		Assert.AreEqual(start + TimeSpan.FromMinutes(15), preOff.NextChangeFrom);
		Assert.AreEqual(start + TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(30), preOff.NextChangeAt);
	}

	[TestMethod]
	public void The_Countdown_Span_Is_Cleared_When_Nothing_Is_Scheduled()
	{
		var t = Build();

		Assert.IsNull(t.Publisher.Snapshots.Single().NextChangeFrom,
			"a dark, unlit area starts with nothing armed, so there is no span to claim");

		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(600 + 30));

		var vacant = t.Publisher.Snapshots[^1];
		Assert.AreEqual(AreaState.AutoVacant, vacant.State);
		Assert.IsNull(vacant.NextChangeAt);
		Assert.IsNull(vacant.NextChangeFrom, "an area waiting on motion has no countdown to draw");
	}

	/// <summary>
	///     Every snapshot carries the registry area id, so a reader can join live state to the document by
	///     identity rather than by a display name somebody can edit while the page is open.
	/// </summary>
	[TestMethod]
	public void Every_Snapshot_Names_The_Registry_Area_It_Came_From()
	{
		Fixture t = Build();

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Publisher.Snapshots.All(snapshot => snapshot.AreaId == "test_area"));
	}

	/// <summary>
	///     The armed instant is part of what the area is waiting on, not a date on the report — so it counts
	///     in <see cref="AreaSnapshot.HasSameMeaningAs"/>, where the as-of fields deliberately do not.
	/// </summary>
	[TestMethod]
	public void A_Moved_Countdown_Start_Is_News_And_A_Moved_Timestamp_Is_Not()
	{
		var when = new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);
		var snapshot = new AreaSnapshot(
			"Stue", AreaState.AutoActive, TransitionReason.Motion, HouseMode.Home,
			false, true, "evening", 70, 2700, when,
			when, when, when + TimeSpan.FromMinutes(10), when);

		Assert.IsTrue(snapshot.HasSameMeaningAs(snapshot with { Timestamp = when + TimeSpan.FromMinutes(1) }));
		Assert.IsFalse(snapshot.HasSameMeaningAs(snapshot with { NextChangeFrom = when + TimeSpan.FromMinutes(1) }));
	}

	// ===================== start-up adoption =====================

	/// <summary>
	///     The forever-on bug. An area the engine lit and then forgot across a restart used to start
	///     <see cref="AreaState.AutoVacant"/>, which arms no vacancy timer — so the light burned until somebody
	///     walked back into the room, which in a room nobody enters means forever.
	/// </summary>
	[TestMethod]
	public void An_Area_Found_Lit_Is_Adopted_And_Eventually_Turned_Off()
	{
		var t = BuildAlreadyLit();

		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "a lit room is the engine's problem, not nobody's");

		// The vacancy timeout is now running against a light the engine never commanded.
		Advance(t, TimeSpan.FromMinutes(10));
		Assert.AreEqual(AreaState.PreOff, t.Area.State);

		Advance(t, TimeSpan.FromSeconds(30));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false }, "the light the restart orphaned is finally out");
	}

	[TestMethod]
	public void Adoption_Commands_Absolutely_Nothing()
	{
		var t = BuildAlreadyLit();

		Assert.AreEqual(0, t.Actuator.Applied.Count,
			"somebody walking past a restart must notice nothing at all");

		// Nor may the first tick quietly 'correct' levels the engine never chose: the target is seeded at
		// adoption precisely so the tick finds nothing to do.
		Advance(t, TimeSpan.FromMinutes(5));
		Assert.AreEqual(0, t.Actuator.Applied.Count, "adoption takes charge of the lights, not of their levels");
	}

	/// <summary>
	///     Darkness gates auto-on, not adoption. They answer different questions — "should I light this?" versus
	///     "these are lit, whose are they?" — and answering the second with the first would leave a lamp burning
	///     through a bright afternoon, which is the same bug in daylight.
	/// </summary>
	[TestMethod]
	public void A_Lit_Area_Is_Adopted_Even_When_It_Is_Too_Bright_To_Have_Been_Lit()
	{
		var t = BuildAlreadyLit(lux: "500");

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		Assert.AreEqual(false, t.Publisher.Snapshots[^1].IsDark, "it is not dark, and the snapshot says so");
		Assert.AreEqual(0, t.Actuator.Applied.Count);

		Advance(t, TimeSpan.FromMinutes(11));
		Assert.IsTrue(t.Actuator.Applied.Any(a => a.Command is { On: false }),
			"no light is left burning because the engine forgot it, daylight included");
	}

	[TestMethod]
	public void An_Adopted_Area_Says_It_Was_Adopted_And_Claims_No_Levels()
	{
		var t = BuildAlreadyLit();
		var opening = t.Publisher.Snapshots.Single();

		Assert.AreEqual(TransitionReason.AdoptedAtStartup, opening.Reason);
		Assert.AreEqual(AreaState.AutoActive, opening.State);
		Assert.IsNull(opening.BrightnessPct, "the engine did not choose these levels and must not claim them");
		Assert.IsNull(opening.LastCommandAt, "…and it has not commanded this area at all");
		Assert.IsNotNull(opening.NextChangeAt, "but it has armed the timeout that ends the burning");
	}

	[TestMethod]
	public void An_Area_Found_Dark_Is_Not_Adopted()
	{
		var t = Build();

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.AreEqual(TransitionReason.Startup, t.Publisher.Snapshots.Single().Reason);
		Assert.IsNull(t.Publisher.Snapshots.Single().NextChangeAt, "nothing to wait for: nothing is on");
	}

	[TestMethod]
	public void A_Muzzled_Engine_Adopts_Nothing()
	{
		var t = BuildAlreadyLit(s => s.Enabled = false);

		// Start() declines to adopt, and the house subscription then lands the area in Disabled — where a lit
		// room is somebody else's business, which is exactly what a kill switch is for.
		Assert.AreEqual(AreaState.Disabled, t.Area.State);

		Advance(t, TimeSpan.FromMinutes(15));
		Assert.AreEqual(0, t.Actuator.Applied.Count,
			"arming a timer that ends in a command is a command deferred, and a disabled engine makes none");
	}

	// ===================== periodic evaluation =====================

	/// <summary>
	///     Dusk. Lux crossing the threshold is the moment a vacant area becomes eligible to light, and it is
	///     exactly a moment with no transition and no deadline — so nothing but the tick can notice it.
	/// </summary>
	[TestMethod]
	public void A_Vacant_Area_Publishes_Once_When_Darkness_Changes_Under_It()
	{
		var t = Build(s => s.Darkness = DarknessSource.Lux);
		t.Ha.SetState(Lux, "500");

		// One tick to notice it got bright, then quiet again.
		Advance(t, TimeSpan.FromMinutes(1));
		Assert.AreEqual(false, t.Publisher.Snapshots[^1].IsDark);

		var afterBright = t.Publisher.Snapshots.Count;
		Advance(t, TimeSpan.FromMinutes(20));
		Assert.AreEqual(afterBright, t.Publisher.Snapshots.Count, "an area whose world is not moving stays quiet");

		// Dusk: the sensor falls below the threshold with nobody in the room.
		t.Ha.SetState(Lux, "5");
		Advance(t, TimeSpan.FromMinutes(1));

		Assert.AreEqual(afterBright + 1, t.Publisher.Snapshots.Count,
			"dusk in an empty room is news, and it is published exactly once");
		Assert.AreEqual(true, t.Publisher.Snapshots[^1].IsDark);
		Assert.AreEqual(AreaState.AutoVacant, t.Publisher.Snapshots[^1].State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "noticing dusk is not a reason to light an empty room");
	}

	/// <summary>
	///     One real transition is announced once. A republish that carries no new news — motion in an overridden
	///     area records occupancy but moves neither the state, the levels, nor the override deadline — resolves to
	///     a snapshot identical to the last one published, and the identical-consecutive guard must swallow it.
	///     This is the regression test for the owner seeing a single transition log its line twice.
	/// </summary>
	[TestMethod]
	public void A_Repeated_Identical_Snapshot_Is_Published_Only_Once()
	{
		var t = Build(s => s.OverrideDurationMinutes = 120);
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));   // past the echo window, so the manual touch is read as a human
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);

		t.Publisher.Snapshots.Clear();

		// Motion while overridden: the deadline is untouched, the state and levels are untouched, only the
		// last-motion instant moves — and that is deliberately not part of a snapshot's meaning.
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(0, t.Publisher.Snapshots.Count,
			"a republish saying the very same thing is suppressed, so one transition is published exactly once");
	}

	/// <summary>
	///     The trap in diffing a record: value equality would compare the timestamps too, so every tick would
	///     differ and the suppression would suppress nothing — a fixed-rate heartbeat wearing a diff's clothes.
	/// </summary>
	[TestMethod]
	public void A_Quiet_Area_Publishes_Nothing_However_Long_It_Ticks()
	{
		var t = Build();
		var afterStartup = t.Publisher.Snapshots.Count;

		Advance(t, TimeSpan.FromHours(2));

		Assert.AreEqual(afterStartup, t.Publisher.Snapshots.Count,
			"two hours of ticks over an unchanging area is two hours of silence");
	}

	[TestMethod]
	public void A_Tick_Publishes_When_The_House_Mode_Changes_Under_A_Resting_Area()
	{
		var t = Build();
		t.Publisher.Snapshots.Clear();

		// Sleep mode does not transition an AutoVacant area, so only the tick's diff can carry the news.
		t.House.OnNext(House(kind: ModeKind.Sleep));
		Advance(t, TimeSpan.FromMinutes(1));

		Assert.IsTrue(t.Publisher.Snapshots.Any(s => s.Mode == HouseMode.Sleep),
			"the card must not keep saying 'somebody home' at a sleeping house");
	}

	[TestMethod]
	public void A_Disposed_Controller_Goes_Quiet()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");

		t.Area.Dispose();
		t.Actuator.Clear();
		Advance(t, TimeSpan.FromMinutes(30));

		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	// ===================== house-mode sleep (09 §3.4) =====================

	[TestMethod]
	public void Sleep_NonRespectingArea_FollowsThePlainTable()
	{
		// A sleeping house, but this area does not respect sleep: it follows the one shared table, unclamped.
		var t = Build(tweakGlobal: g => g.HouseMode = SoverMode());
		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70 },
			"an area that does not respect sleep follows the shared evening period, unclamped");
	}

	[TestMethod]
	public void Sleep_RespectingArea_ClampsViaAnExplicitClampPeriod()
	{
		// The Sover option names its own clamp period explicitly, which beats the 'night' fallback.
		var mode = SoverMode();
		mode.OptionFor("Sover")!.ClampPeriod = "dim";
		var periods = new List<TimePeriodConfig>
		{
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new() { Name = "dim", Start = "22:00", BrightnessPct = 5, ColorTempKelvin = 2000, MaxBrightnessPct = 8 },
			new() { Name = "night", Start = "23:00", BrightnessPct = 15, ColorTempKelvin = 2200, MaxBrightnessPct = 30 }
		};
		var t = Build(s => s.RespectSleepMode = true, g => g.HouseMode = mode, periods: periods);
		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 8 },
			"the explicit ClampPeriod 'dim' (ceiling 8) drives the clamp, not the 'night' fallback");
	}

	[TestMethod]
	public void Sleep_RespectingArea_WithNoResolvableClamp_LeavesTheTargetAlone()
	{
		// Sover has no ClampPeriod, and there is no 'night' period nor one that SetsMode Sover — nothing resolves.
		var periods = new List<TimePeriodConfig>
		{
			new() { Name = "day", Start = "07:00", BrightnessPct = 90, ColorTempKelvin = 4500 },
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 }
		};
		var t = Build(s => s.RespectSleepMode = true, g => g.HouseMode = SoverMode(), periods: periods);
		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70 },
			"with no clamp period resolving, the respecting area is left on the plain evening target");
	}

	// ===================== away-kind mode =====================

	[TestMethod]
	public void AwayKind_SweepsImmediately_UnlessTheAreaOptsOut()
	{
		var swept = Build(tweakGlobal: g => g.HouseMode = SoverMode());
		swept.Ha.Trigger(Motion, "on");
		swept.Actuator.Clear();

		swept.House.OnNext(House(kind: ModeKind.Away, modeValue: "Borte"));

		Assert.AreEqual(AreaState.Away, swept.Area.State);
		Assert.IsTrue(swept.Actuator.Last is { On: false }, "an away-kind Borte sweeps a full house at once");

		var optedOut = Build(s => s.SkipAwaySweep = true, g => g.HouseMode = SoverMode());
		optedOut.Ha.Trigger(Motion, "on");
		optedOut.Actuator.Clear();

		optedOut.House.OnNext(House(kind: ModeKind.Away, modeValue: "Borte"));

		Assert.AreEqual(AreaState.Away, optedOut.Area.State);
		Assert.AreEqual(0, optedOut.Actuator.Applied.Count, "a SkipAwaySweep area is left alone");
	}

	[TestMethod]
	public void AwayKind_MotionIgnored()
	{
		var t = Build(tweakGlobal: g => g.HouseMode = SoverMode());
		t.House.OnNext(House(kind: ModeKind.Away, modeValue: "Borte"));
		t.Actuator.Clear();

		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.Away, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "motion is ignored while the house is away");
	}

	[TestMethod]
	public void Away_WithAScene_SkipsTheSweep()
	{
		var mode = SoverMode();
		mode.OptionFor("Borte")!.Scene = "scene.borte";
		var t = Build(tweakGlobal: g => g.HouseMode = mode);
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		t.House.OnNext(House(kind: ModeKind.Away, modeValue: "Borte", scene: "scene.borte"));

		Assert.AreEqual(AreaState.Away, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "an away scene is the look; the area publishes but sweeps nothing");
	}

	[TestMethod]
	public void PresenceAway_UnaffectedByModeModel()
	{
		// No HouseMode configured: raw presence still drives Away exactly as before.
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		t.House.OnNext(House(home: false));

		Assert.AreEqual(AreaState.Away, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false });
	}

	[TestMethod]
	public void Migration_LiveCabin_NoHouseMode_UsesBaseline()
	{
		// No HouseMode, nobody asleep, no mode selected → the baseline evening period drives, unchanged.
		var t = Build(s => s.RespectSleepMode = true);
		t.House.OnNext(House(modeValue: null));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70 },
			"with no mode selected and nobody asleep, the baseline evening period drives, exactly as today");
	}

	// ===================== guest scene hold (09 §3.4) =====================

	private static HouseModeConfig GuestSceneMode()
	{
		var mode = SoverMode();
		mode.Options.Add(new HouseModeOptionConfig { Value = "Gjester", Kind = ModeKind.Guest, Scene = "scene.gjest" });
		return mode;
	}

	[TestMethod]
	public void Guest_WithAScene_HoldsTheArea_AndIgnoresMotionForCommanding()
	{
		var t = Build(tweakGlobal: g => g.HouseMode = GuestSceneMode());
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		t.House.OnNext(House(kind: ModeKind.Guest, modeValue: "Gjester", scene: "scene.gjest"));

		Assert.AreEqual(AreaState.SceneHold, t.Area.State, "a guest scene holds the area");
		Assert.AreEqual(0, t.Actuator.Applied.Count, "the scene is the look; the area commands nothing");

		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(AreaState.SceneHold, t.Area.State, "motion is recorded but does not command out of the hold");
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void Guest_SceneResetToNormal_ExitsSceneHoldToAutoVacant()
	{
		var t = Build(tweakGlobal: g => g.HouseMode = GuestSceneMode());
		t.House.OnNext(House(kind: ModeKind.Guest, modeValue: "Gjester", scene: "scene.gjest"));
		Assert.AreEqual(AreaState.SceneHold, t.Area.State);

		t.House.OnNext(House());   // back to Normal / Home, no scene

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State, "resetting to Normal releases the hold");
	}

	[TestMethod]
	public void FromAway_IntoAGuestScene_EntersSceneHold_WithoutWelcomeHome()
	{
		var t = Build(tweak: s => s.WelcomeHome = true, tweakGlobal: g => g.HouseMode = GuestSceneMode());

		// Everyone leaves: the area goes Away.
		t.House.OnNext(House(home: false));
		Assert.AreEqual(AreaState.Away, t.Area.State);
		t.Actuator.Clear();

		// A guest scene is selected while the area is still Away. The scene-hold check runs before the was-Away
		// recovery, so this must land in SceneHold rather than fire the welcome-home ApplyTarget that would clobber it.
		t.House.OnNext(House(kind: ModeKind.Guest, modeValue: "Gjester", scene: "scene.gjest"));

		Assert.AreEqual(AreaState.SceneHold, t.Area.State, "a scene mode entered from Away lands in SceneHold");
		Assert.AreEqual(0, t.Actuator.Applied.Count, "and commands nothing — the scene is the look");
	}

	[TestMethod]
	public void Guest_WithoutAScene_DoesNotEnterSceneHold()
	{
		var mode = SoverMode();
		mode.Options.Add(new HouseModeOptionConfig { Value = "Gjester", Kind = ModeKind.Guest });   // no scene
		var t = Build(tweakGlobal: g => g.HouseMode = mode);
		t.Ha.Trigger(Motion, "on");

		t.House.OnNext(House(kind: ModeKind.Guest, modeValue: "Gjester"));

		Assert.AreNotEqual(AreaState.SceneHold, t.Area.State, "a guest mode with no scene does not hold the area");
		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "it stays on the baseline instead");
	}
}
