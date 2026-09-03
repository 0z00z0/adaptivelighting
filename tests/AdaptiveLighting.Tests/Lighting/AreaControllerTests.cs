using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Every arrow in the area state machine, driven through fakes and a <see cref="TestScheduler"/>.</summary>
/// <remarks>The scheduler is the controller's only clock. No test here reads wall-clock time.</remarks>
[TestClass]
public sealed class AreaControllerTests
{
	private const string Motion = "binary_sensor.area_motion";
	private const string Light = "light.area";
	private const string SecondLight = "light.area_second";
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

	/// <summary>Hands the test the period-boundary callback, so it can be called back by hand as Home Assistant's own thread would.</summary>
	private sealed class BoundaryCapturingScheduler : IScheduler
	{
		private readonly IScheduler _inner;

		public BoundaryCapturingScheduler(IScheduler inner) => _inner = inner;

		public DateTimeOffset Now => _inner.Now;

		public Action? Boundary { get; private set; }

		public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action) =>
			_inner.Schedule(state, action);

		public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action) =>
			_inner.Schedule(state, dueTime, action);

		public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
		{
			Boundary = () => action(this, state);

			return _inner.Schedule(state, dueTime, action);
		}
	}

	/// <summary>A sun the test moves by hand, and the announcement the orchestrator would make when it does.</summary>
	private sealed class MovableSun
	{
		private readonly Subject<Unit> _moved = new();

		public SunTimes Times { get; private set; } = SunTimes.Unknown;

		public IObservable<Unit> Moved => _moved;

		/// <summary>Moves the sun without announcing it, as an unread sun entity leaves it.</summary>
		public void SetQuietly(TimeOnly? sunrise, TimeOnly? sunset) => Times = new SunTimes(sunrise, sunset);

		public void MoveTo(TimeOnly? sunrise, TimeOnly? sunset)
		{
			SetQuietly(sunrise, sunset);
			_moved.OnNext(Unit.Default);
		}
	}

	/// <summary>A house-state snapshot with everything a call site does not mention defaulted.</summary>
	private static HouseState House(
		bool home = true,
		ModeKind kind = ModeKind.Normal,
		bool killed = false,
		string? modeValue = null,
		string? scene = null,
		ForcedMode? forced = null) =>
		new(home, kind, killed) { ModeValue = modeValue, ActiveScene = scene, Forced = forced };

	/// <summary>An <c>input_boolean</c> left on, pinning the Away option over the select.</summary>
	private static ForcedMode ForcedAway(string entityId = "input_boolean.occupancy") =>
		new(ModeKind.Away, "Borte", ModeForceSource.WhileEntityOn, entityId, "on");

	/// <summary>Normal, Borte (away) and Sover (sleep, carrying no ClampPeriodId).</summary>
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

	/// <summary>Builds a started area at 20:00, inside "evening", so its target holds for the length of a test.</summary>
	private static Fixture Build(
		Action<AreaSettings>? tweak = null,
		Action<GlobalConfig>? tweakGlobal = null,
		IReadOnlyList<string>? ignoreWhenOn = null,
		Action<FakeHaContext>? seed = null,
		IReadOnlyList<TimePeriodConfig>? periods = null,
		IReadOnlyList<RoomLevelOverride>? levels = null,
		bool openHouse = true,
		MovableSun? sun = null,
		bool watchSun = true,
		Func<IScheduler, IScheduler>? wrapScheduler = null,
		bool withMotionSensor = true,
		string? sceneOnMotion = null,
		IReadOnlyList<string>? lights = null)
	{
		var scheduler = new TestScheduler();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		var ha = new FakeHaContext();
		ha.SetState(Motion, "off");
		ha.SetState(Light, "off");
		ha.SetState(Lux, "5");

		// Before Start(), so a test can hand the controller a world that already exists.
		seed?.Invoke(ha);

		var settings = new AreaSettings
		{
			VacancyTimeoutSeconds = 600,
			PreOffSeconds = 30,
			Darkness = DarknessSource.Lux,
			OverrideDurationMinutes = 120,

			// Pinned, unlike the shipped default: most of these tests are about the fixed hold's clock, and one
			// that arms the vacancy timeout instead would be measuring a different rule under the same name.
			OverrideUntilVacant = false,
			VacancyResetMinutes = 10
		};
		tweak?.Invoke(settings);

		var global = new GlobalConfig { SmoothTransitions = false, CircadianTickSeconds = 60 };
		tweakGlobal?.Invoke(global);

		var table = periods ?? new List<TimePeriodConfig>
		{
			new() { Name = "day", Start = "07:00", BrightnessPct = 90, ColorTempKelvin = 4500 },
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new() { Name = "night", Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200 }
		};

		var area = new ResolvedArea(
			"Test", settings, lights ?? [Light], withMotionSensor ? [Motion] : [], [Lux], ignoreWhenOn ?? [])
		{
			SceneOnMotion = sceneOnMotion
		};
		var actuator = new FakeLightActuator();
		var publisher = new FakeStatePublisher();
		var house = new BehaviorSubject<HouseState>(HouseState.Initial);

		var controller = new AreaController(
			ha, wrapScheduler?.Invoke(scheduler) ?? scheduler, area, global, table,
			new CircadianCalculator(table, global, () => sun?.Times ?? SunTimes.Unknown, levels, zone: TimeZoneInfo.Utc),
			actuator, publisher, house, NullLoggerFactory.Instance, areaId: "test_area",
			sunMoved: watchSun ? sun?.Moved : null);

		controller.Start();

		// The orchestrator publishes the opening house state straight after starting the areas, so the first push is a change.
		if (openHouse)
			house.OnNext(House());

		return new Fixture(scheduler, ha, actuator, publisher, house, controller);
	}

	private static void Advance(Fixture fixture, TimeSpan by) => fixture.Scheduler.AdvanceBy(by.Ticks);

	/// <summary>Builds an area whose light is already on before the engine starts.</summary>
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
		t.Ha.SetState(Lux, "5000");

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
	public void Motion_Under_A_Fixed_Hold_Extends_Nothing()
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
	public void Motion_Under_A_Movement_Led_Hold_Restarts_It()
	{
		var t = Build(s =>
		{
			s.OverrideUntilVacant = true;
			s.VacancyTimeoutSeconds = 300;
		});

		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(4));
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(0, t.Actuator.Applied.Count, "motion must not push the manual levels around");

		Advance(t, TimeSpan.FromMinutes(4));
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State,
			"the five minutes restarted at the movement, so the manual level still stands");

		Advance(t, TimeSpan.FromMinutes(1));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State, "the room has now been motion-free for its whole timeout");
		Assert.IsTrue(t.Actuator.Last is { On: false }, "an empty room settles off, the same as any other vacancy");
	}

	// The number is left in the document while the hold follows movement, so a stale one must not be able to end
	// the hold early or hold it open.
	[TestMethod]
	public void A_Movement_Led_Hold_Ignores_The_Fixed_Duration()
	{
		var t = Build(s =>
		{
			s.OverrideUntilVacant = true;
			s.VacancyTimeoutSeconds = 600;
			s.OverrideDurationMinutes = 1;
		});

		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());

		Advance(t, TimeSpan.FromMinutes(5));
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State, "one minute has passed five times over and nothing ended");

		Advance(t, TimeSpan.FromMinutes(5));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State, "the ten-minute vacancy timeout is what ended it");
	}

	[TestMethod]
	public void A_Movement_Led_Hold_Publishes_The_Vacancy_Timeout_As_Its_Expiry()
	{
		var start = new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);

		var t = Build(s =>
		{
			s.OverrideUntilVacant = true;
			s.VacancyTimeoutSeconds = 300;
		});

		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());

		Assert.AreEqual(
			start + TimeSpan.FromSeconds(30) + TimeSpan.FromMinutes(5),
			t.Publisher.Snapshots[^1].NextChangeAt,
			"the snapshot names the moment the quiet would run out, not a fixed duration");

		Advance(t, TimeSpan.FromMinutes(2));
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(
			start + TimeSpan.FromSeconds(30) + TimeSpan.FromMinutes(2) + TimeSpan.FromMinutes(5),
			t.Publisher.Snapshots[^1].NextChangeAt,
			"a deadline that moved has to be republished, or the page counts down to a moment nothing happens at");
	}

	// ===================== A room with no motion sensor =====================

	[TestMethod]
	public void A_Room_With_No_Motion_Sensor_Is_Still_Published()
	{
		Fixture t = Build(withMotionSensor: false);

		Assert.IsTrue(t.Publisher.Snapshots.Count > 0, "the room must reach the interface, not vanish from it");
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
	}

	// The two failure modes a hold judged on movement can have where no movement is ever reported: off almost at
	// once because the room reads as instantly vacant, and never off because it never does.
	[TestMethod]
	public void A_Movement_Led_Hold_With_No_Motion_Sensor_Runs_The_Whole_Vacancy_Timeout()
	{
		Fixture t = Build(
			s =>
			{
				s.OverrideUntilVacant = true;
				s.VacancyTimeoutSeconds = 300;
			},
			withMotionSensor: false);

		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(1));
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State,
			"a room that never reports movement must not read as instantly vacant");
		Assert.AreEqual(0, t.Actuator.Applied.Count);

		Advance(t, TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(59));
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State, "the hold must not end before the vacancy timeout");

		Advance(t, TimeSpan.FromSeconds(1));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State, "with nothing to restart it the hold ends on its own");
		Assert.IsTrue(t.Actuator.Last is { On: false }, "and the room settles off, the same as any other empty room");
	}

	[TestMethod]
	public void A_Fixed_Hold_With_No_Motion_Sensor_Ends_On_Its_Own_Clock()
	{
		Fixture t = Build(s => s.OverrideDurationMinutes = 5, withMotionSensor: false);

		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		Advance(t, TimeSpan.FromMinutes(4));
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);

		Advance(t, TimeSpan.FromMinutes(1));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false });
	}

	// The vacancy timer is what a sensor-less room runs on once it is adopted rather than held, and that is the
	// path the pre-off warning hangs off.
	[TestMethod]
	public void A_Lit_Room_With_No_Motion_Sensor_Is_Adopted_Then_Dimmed_And_Switched_Off()
	{
		Fixture t = Build(
			seed: ha => ha.SetState(Light, "on", new() { ["brightness"] = 178.5 }),
			withMotionSensor: false);

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(10));
		Assert.AreEqual(AreaState.PreOff, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 35 });

		Advance(t, TimeSpan.FromSeconds(30));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false });
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

	// The echo window must be SelfEchoWindowSeconds + TransitionSeconds. A fixed one reads the tail of the
	// engine's own night fade as a human at the dimmer.
	[TestMethod]
	public void An_Echo_From_The_Middle_Of_A_Long_Fade_Is_Still_Ours()
	{
		var t = Build(s =>
		{
			s.NightTransitionSeconds = 30;
			s.Darkness = DarknessSource.Always;
		});
		t.Ha.Trigger(Motion, "on");

		// 20 s in: past the 8 s echo window, well inside the 30 s fade the engine itself commanded.
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

	// ===================== a radio is not a hand =====================
	//
	// Home Assistant writes 'unavailable' with a context carrying neither a user nor a parent, the same shape a
	// wall switch reports. Both ends of the change have to read on or off before it counts as a person.

	[TestMethod]
	public void A_Light_Dropping_Off_The_Network_Is_Not_A_Human_Switching_It_Off()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);

		// Past the echo window, so nothing here is mistaken for the tail of the engine's own command.
		Advance(t, TimeSpan.FromSeconds(30));
		t.Ha.Trigger(Light, "unavailable", null, PhysicalDevice());

		Assert.AreEqual(AreaState.AutoActive, t.Area.State,
			"a bulb losing its radio is not a person at the switch, and must not suppress the room");
	}

	[TestMethod]
	public void A_Light_Coming_Back_From_Unavailable_Is_Not_A_Human_Switching_It_On()
	{
		var t = Build();
		Advance(t, TimeSpan.FromSeconds(30));

		t.Ha.Trigger(Light, "unavailable", null, PhysicalDevice());
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State,
			"a device reconnecting is indistinguishable from a hand at the switch, and the safe reading is neither");
	}

	[TestMethod]
	public void A_Lights_Very_First_Report_Is_Not_An_Override()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));

		t.Ha.Trigger(Light, "unknown", null, PhysicalDevice());

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
	}

	/// <summary>The guard must not swallow the thing it sits in front of.</summary>
	[TestMethod]
	public void A_Real_Off_Is_Still_Read_As_A_Human()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));

		t.Ha.Trigger(Light, "off", null, PhysicalDevice());

		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State);
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

	// Muzzle-then-release must land where stop-then-start does; AutoVacant arms no vacancy timeout.
	[TestMethod]
	public void Releasing_The_Kill_Switch_Adopts_A_Room_Left_Lit()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Ha.SetState(Light, "on", new() { ["brightness"] = 178 });

		t.House.OnNext(House(killed: true));
		Assert.AreEqual(AreaState.Disabled, t.Area.State);

		t.Actuator.Clear();
		t.House.OnNext(House());

		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "a lit room is the engine's again the moment it is allowed to act");
		Assert.AreEqual(0, t.Actuator.Applied.Count, "adoption observes; it does not command");
		Assert.IsNotNull(t.Publisher.Snapshots[^1].NextChangeAt, "and it arms the timeout that ends the burning");

		// The vacancy timeout, then the pre-off grace.
		Advance(t, TimeSpan.FromSeconds(601));
		Advance(t, TimeSpan.FromSeconds(31));

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsFalse(t.Actuator.Last!.On, "the room does not burn on for ever because the engine was muzzled once");
	}

	[TestMethod]
	public void Releasing_The_Kill_Switch_Over_A_Dark_Room_Changes_Nothing()
	{
		var t = Build();
		t.House.OnNext(House(killed: true));
		t.Actuator.Clear();

		t.House.OnNext(House());

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
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
		t.Ha.SetState(Lux, "5000");
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
	public void RespectSleepMode_Holds_The_Evening_Target_To_The_Night_Level()
	{
		// Sover is Sleep-kind with no ClampPeriodId, so the clamp falls back to the period named "night".
		var t = Build(s => s.RespectSleepMode = true, g => g.HouseMode = SoverMode());
		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 15 },
			"20:00 says 70%, but a sleeping house is held to night's own 15% whatever the clock says");
	}

	[TestMethod]
	public void Sleep_Mode_Turning_On_Retargets_An_Active_Area()
	{
		var t = Build(s => s.RespectSleepMode = true, g => g.HouseMode = SoverMode());
		t.Ha.Trigger(Motion, "on");
		Assert.IsTrue(t.Actuator.Last is { BrightnessPct: 70 });
		t.Actuator.Clear();

		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 15 },
			"the sleeping house is held to the night period's own level");
	}

	// ===================== what the snapshot says about auto-on =====================

	// Two of the refusals leave the area in AutoVacant, the same state as one waiting for someone to walk in,
	// so the snapshot has to carry the gate as well.
	[TestMethod]
	public void A_Sleeping_House_Is_Published_As_The_Reason_Auto_On_Would_Refuse()
	{
		Fixture t = Build(s => s.SleepBlocksAutoOn = true);

		t.House.OnNext(House(kind: ModeKind.Sleep));
		Advance(t, TimeSpan.FromMinutes(1));

		AreaSnapshot latest = t.Publisher.Snapshots[^1];

		Assert.AreEqual(AreaState.AutoVacant, latest.State, "the refusal is invisible in the state, which is the point");
		Assert.AreEqual(true, latest.IsDark, "and invisible in the darkness verdict too");
		Assert.AreEqual(AutoOnBlock.Sleep, latest.AutoOnBlockedBy);
		Assert.IsNull(latest.AutoOnBlockingEntity, "no entity is holding this one off");
	}

	[TestMethod]
	public void A_Blocking_Entity_Is_Published_By_Name()
	{
		Fixture t = Build(ignoreWhenOn: [Blocker]);

		t.Ha.SetState(Lux, "5000");
		Advance(t, TimeSpan.FromMinutes(1));

		t.Ha.SetState(Blocker, "on");
		t.Ha.SetState(Lux, "5");
		Advance(t, TimeSpan.FromMinutes(1));

		AreaSnapshot dusk = t.Publisher.Snapshots[^1];

		Assert.AreEqual(AreaState.AutoVacant, dusk.State);
		Assert.AreEqual(true, dusk.IsDark, "dusk: the verdict just flipped, which is why this report exists");
		Assert.AreEqual(AutoOnBlock.EntityOn, dusk.AutoOnBlockedBy);
		Assert.AreEqual(Blocker, dusk.AutoOnBlockingEntity);
	}

	[TestMethod]
	public void An_Area_With_Nothing_In_The_Way_Publishes_An_Open_Gate()
	{
		Fixture t = Build();

		Assert.AreEqual(AutoOnBlock.None, t.Publisher.Snapshots[0].AutoOnBlockedBy);
	}

	// The snapshot reads the gate the engine consults, not a second copy of its rules.
	[TestMethod]
	public void What_The_Snapshot_Reports_Is_What_Motion_Actually_Does()
	{
		Fixture t = Build(s => s.SleepBlocksAutoOn = true);
		t.House.OnNext(House(kind: ModeKind.Sleep));
		Advance(t, TimeSpan.FromMinutes(1));

		Assert.AreNotEqual(AutoOnBlock.None, t.Publisher.Snapshots[^1].AutoOnBlockedBy);

		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State, "which is exactly what the report promised");
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	// ===================== a room's own levels =====================

	[TestMethod]
	public void Motion_Lights_The_Area_At_The_Rooms_Own_Level_Where_It_Has_One()
	{
		// The fixture's clock stands at 20:00, in evening, which the house runs at 70 % / 2700 K.
		Fixture t = Build(levels: [new RoomLevelOverride { PeriodId = "evening", BrightnessPct = 25 }]);

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 25, ColorTempKelvin: 2700 },
			"the room's brightness, and the schedule's colour it never said anything about");
	}

	[TestMethod]
	public void The_Snapshot_Says_Which_Levels_This_Room_Names_For_Itself()
	{
		Fixture t = Build(levels: [new RoomLevelOverride { PeriodId = "evening", BrightnessPct = 25 }]);

		AreaSnapshot latest = t.Publisher.Snapshots[^1];

		Assert.AreEqual("evening", latest.PeriodName);
		Assert.AreEqual(RoomLevelSource.Brightness, latest.LevelsFromRoom,
			"a statement about the period, so it holds before the engine has commanded anything");
	}

	[TestMethod]
	public void A_Room_With_No_Levels_Publishes_None_Rather_Than_Nothing()
	{
		Fixture t = Build();

		Assert.AreEqual(RoomLevelSource.None, t.Publisher.Snapshots[^1].LevelsFromRoom,
			"null is reserved for a build that predates the field; a running engine always has an answer");
	}

	[TestMethod]
	public void The_Snapshot_Flag_Follows_The_Period_Across_A_Boundary()
	{
		Fixture t = Build(levels: [new RoomLevelOverride { PeriodId = "evening", BrightnessPct = 25 }]);

		Assert.AreEqual(RoomLevelSource.Brightness, t.Publisher.Snapshots[^1].LevelsFromRoom);

		// 20:00 + 2h35m is 22:35, five minutes into night, which this room does not override.
		Advance(t, TimeSpan.FromMinutes(155));

		AreaSnapshot latest = t.Publisher.Snapshots[^1];

		Assert.AreEqual("night", latest.PeriodName);
		Assert.AreEqual(RoomLevelSource.None, latest.LevelsFromRoom);
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

	/// <summary>The boundary is its own wake-up, so the tick does not decide how late the levels change.</summary>
	/// <remarks>Measured at a 300 s tick, boundaries landed up to four minutes late, so this asserts on the seconds either side of 20:03.</remarks>
	[TestMethod]
	public void The_Period_Arrives_At_The_Boundary_Not_At_The_Next_Tick()
	{
		var periods = new List<TimePeriodConfig>
		{
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new() { Name = "night", Start = "20:03", BrightnessPct = 15, ColorTempKelvin = 2200 }
		};
		var t = Build(s => s.VacancyTimeoutSeconds = 60 * 60 * 5, g => g.CircadianTickSeconds = 300, periods: periods);
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromSeconds(170));   // 20:00 -> 20:02:50
		Assert.AreEqual(0, t.Actuator.Applied.Count, "night@20:03 has not come round yet");

		Advance(t, TimeSpan.FromSeconds(15));    // -> 20:03:05
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 15 },
			"the boundary itself, two minutes before the 300 s tick would have reached it");
	}

	/// <summary>A table with one fixed boundary and one anchored to sunset, lit and quiet at 20:00.</summary>
	private static Fixture SunAnchored(MovableSun sun, bool watchSun = true)
	{
		var periods = new List<TimePeriodConfig>
		{
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new() { Name = "night", Start = "sunset", BrightnessPct = 15, ColorTempKelvin = 2200 }
		};
		var t = Build(
			s => s.VacancyTimeoutSeconds = 60 * 60 * 5,
			g => g.CircadianTickSeconds = 300,
			periods: periods,
			sun: sun,
			watchSun: watchSun);

		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();
		return t;
	}

	[TestMethod]
	public void A_Sun_Time_That_Moves_Rearms_The_Boundary_Without_Waiting_For_A_Tick()
	{
		var sun = new MovableSun();
		sun.SetQuietly(sunrise: new TimeOnly(8, 0), sunset: new TimeOnly(23, 0));
		var t = SunAnchored(sun);

		sun.MoveTo(sunrise: new TimeOnly(8, 0), sunset: new TimeOnly(20, 2));

		Advance(t, TimeSpan.FromSeconds(125));   // 20:00 -> 20:02:05
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 15 },
			"night now begins at 20:02, three minutes before the 300 s tick would have looked");
	}

	/// <summary>A sun time can move backwards as easily as forwards, and a boundary it has just taken past still counts.</summary>
	/// <remarks>The next boundary is only ever the first start ahead of now, so one that moved into the past would be armed straight over.</remarks>
	[TestMethod]
	public void A_Sun_Time_That_Moves_Behind_Us_Is_Acted_On_At_Once()
	{
		var sun = new MovableSun();
		sun.SetQuietly(sunrise: new TimeOnly(8, 0), sunset: new TimeOnly(22, 0));
		var t = SunAnchored(sun);

		sun.MoveTo(sunrise: new TimeOnly(8, 0), sunset: new TimeOnly(19, 30));

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 15 },
			"night began half an hour ago, so the room is already owed its levels");
	}

	/// <summary>Without the announcement the tick is what re-arms, which is the behaviour of a house with no sun.</summary>
	[TestMethod]
	public void An_Unwatched_Sun_Waits_For_The_Tick()
	{
		var sun = new MovableSun();
		sun.SetQuietly(sunrise: new TimeOnly(8, 0), sunset: new TimeOnly(23, 0));
		var t = SunAnchored(sun, watchSun: false);

		sun.MoveTo(sunrise: new TimeOnly(8, 0), sunset: new TimeOnly(20, 2));

		Advance(t, TimeSpan.FromSeconds(125));   // 20:00 -> 20:02:05
		Assert.AreEqual(0, t.Actuator.Applied.Count, "the boundary armed at 23:00 has not moved");

		Advance(t, TimeSpan.FromSeconds(180));   // -> 20:05:05, the first tick
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 15 }, "the tick is the safety net and still catches it");
	}

	/// <summary>A sun that stops resolving takes its own boundaries out of the table and nothing else.</summary>
	[TestMethod]
	public void A_Sun_That_Becomes_Unreadable_Leaves_The_Area_Running()
	{
		var sun = new MovableSun();
		sun.SetQuietly(sunrise: new TimeOnly(8, 0), sunset: new TimeOnly(20, 2));
		var t = SunAnchored(sun);

		sun.MoveTo(sunrise: null, sunset: null);

		Advance(t, TimeSpan.FromSeconds(600));   // past both the moved boundary and two ticks
		Assert.AreEqual(0, t.Actuator.Applied.Count, "night cannot be placed, so the area stays on evening");
		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "the area is still running the state machine");
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
		ha.SetState(Lux, "5000");

		var settings = new AreaSettings { Darkness = DarknessSource.Lux };
		var global = new GlobalConfig { SmoothTransitions = false, CircadianTickSeconds = 60 };
		var periods = new List<TimePeriodConfig>
		{
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 }
		};

		var publisher = new FakeStatePublisher();
		var controller = new AreaController(
			ha, scheduler, new ResolvedArea("Test", settings, [Light], [Motion], [Lux], []), global, periods,
			new CircadianCalculator(periods, global, () => SunTimes.Unknown, zone: TimeZoneInfo.Utc),
			new FakeLightActuator(), publisher, new BehaviorSubject<HouseState>(HouseState.Initial),
			NullLoggerFactory.Instance);

		controller.Start();

		Assert.AreEqual(false, publisher.Snapshots.Single().IsDark,
			"5000 lux is not dark, and the opening snapshot must say so rather than echo a default");
	}

	/// <summary>Following the house's outdoor lux sensor is opt-in per room.</summary>
	/// <remarks>Asserted on a bright reading: a dark one cannot tell the two rules apart, since a room with no reading counts as dark.</remarks>
	[TestMethod]
	public void A_Room_That_Follows_The_Outdoor_Sensor_Is_Gated_By_It()
	{
		AreaSnapshot opted = SensorlessRoom(outdoorLux: "5000", followOutdoorLux: true);

		Assert.AreEqual(false, opted.IsDark,
			"the room asked to follow the outdoor sensor, and outdoors it is broad daylight");
	}

	[TestMethod]
	public void A_Room_That_Did_Not_Ask_Ignores_The_Outdoor_Sensor_And_Counts_As_Dark()
	{
		AreaSnapshot silent = SensorlessRoom(outdoorLux: "5000", followOutdoorLux: false);

		Assert.AreEqual(true, silent.IsDark,
			"no lux sensor and no opt-in means no reading, and a gate with nothing to read refuses nothing");
	}

	/// <summary>Starts a room that resolved no lux sensor of its own and hands back its opening report.</summary>
	private static AreaSnapshot SensorlessRoom(string outdoorLux, bool followOutdoorLux)
	{
		var scheduler = new TestScheduler();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		const string Outdoor = "sensor.ute_lux";
		var ha = new FakeHaContext();
		ha.SetState(Motion, "off");
		ha.SetState(Light, "off");
		ha.SetState(Outdoor, outdoorLux);

		var settings = new AreaSettings { Darkness = DarknessSource.Lux, LuxThreshold = 1000 };
		var global = new GlobalConfig { SmoothTransitions = false, CircadianTickSeconds = 60, OutdoorLuxSensor = Outdoor };
		var periods = new List<TimePeriodConfig>
		{
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 }
		};

		var publisher = new FakeStatePublisher();
		var controller = new AreaController(
			ha, scheduler,
			new ResolvedArea("Test", settings, [Light], [Motion], [], [], followOutdoorLux),
			global, periods,
			new CircadianCalculator(periods, global, () => SunTimes.Unknown, zone: TimeZoneInfo.Utc),
			new FakeLightActuator(), publisher, new BehaviorSubject<HouseState>(HouseState.Initial),
			NullLoggerFactory.Instance);

		controller.Start();
		return publisher.Snapshots.Single();
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

	// Motion in an active area moves the vacancy deadline without a state change.
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

	// NextChangeFrom cannot be derived client-side: Timestamp moves on republishes that re-arm nothing.
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

	/// <summary>The area id is what joins live state to the document; the display name is editable.</summary>
	[TestMethod]
	public void Every_Snapshot_Names_The_Registry_Area_It_Came_From()
	{
		Fixture t = Build();

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Publisher.Snapshots.All(snapshot => snapshot.AreaId == "test_area"));
	}

	// NextChangeFrom counts in HasSameMeaningAs; the as-of fields, Timestamp among them, do not.
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

		// The target is seeded at adoption so the first tick finds nothing to correct.
		Advance(t, TimeSpan.FromMinutes(5));
		Assert.AreEqual(0, t.Actuator.Applied.Count, "adoption takes charge of the lights, not of their levels");
	}

	/// <summary>Darkness gates auto-on, not adoption.</summary>
	[TestMethod]
	public void A_Lit_Area_Is_Adopted_Even_When_It_Is_Too_Bright_To_Have_Been_Lit()
	{
		var t = BuildAlreadyLit(lux: "5000");

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

		// Start() declines to adopt, then the house subscription lands the area in Disabled.
		Assert.AreEqual(AreaState.Disabled, t.Area.State);

		Advance(t, TimeSpan.FromMinutes(15));
		Assert.AreEqual(0, t.Actuator.Applied.Count,
			"arming a timer that ends in a command is a command deferred, and a disabled engine makes none");
	}

	// ===================== periodic evaluation =====================

	// Dusk moves no state and arms no deadline, so only the tick can notice it.
	[TestMethod]
	public void A_Vacant_Area_Publishes_Once_When_Darkness_Changes_Under_It()
	{
		var t = Build(s => s.Darkness = DarknessSource.Lux);
		t.Ha.SetState(Lux, "5000");

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

	// The identical-consecutive guard has to swallow a republish that resolves to the snapshot already published.
	[TestMethod]
	public void A_Repeated_Identical_Snapshot_Is_Published_Only_Once()
	{
		var t = Build(s => s.OverrideDurationMinutes = 120);
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));   // past the echo window, so the manual touch is read as a human
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);

		t.Publisher.Snapshots.Clear();

		// Motion while overridden moves only the last-motion instant, which is not part of a snapshot's meaning.
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(0, t.Publisher.Snapshots.Count,
			"a republish saying the very same thing is suppressed, so one transition is published exactly once");
	}

	// Record value equality would compare the timestamps too, so every tick would differ and suppress nothing.
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

	/// <summary>A save replaces every controller; the one thrown away must not still aim lights at the table it was built from.</summary>
	[TestMethod]
	public void A_Boundary_Already_In_Flight_Commands_Nothing_Once_The_Controller_Is_Disposed()
	{
		BoundaryCapturingScheduler? captured = null;
		var t = Build(wrapScheduler: inner => captured = new BoundaryCapturingScheduler(inner));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { BrightnessPct: 70 }, "the area has to be lit at the evening levels first");

		t.Area.Dispose();
		t.Actuator.Clear();
		t.Publisher.Snapshots.Clear();

		// Past 22:30, so the night period is what this boundary would retarget the area to.
		Advance(t, TimeSpan.FromHours(3));

		captured!.Boundary!();

		Assert.AreEqual(0, t.Actuator.Applied.Count, "a discarded controller commanded the lights");
		Assert.AreEqual(0, t.Publisher.Snapshots.Count, "a discarded controller reported over its replacement");
	}

	// ===================== house-mode sleep =====================

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
		mode.OptionFor("Sover")!.ClampPeriodId = "dim";
		var periods = new List<TimePeriodConfig>
		{
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new() { Name = "dim", Start = "22:00", BrightnessPct = 5, ColorTempKelvin = 2000 },
			new() { Name = "night", Start = "23:00", BrightnessPct = 15, ColorTempKelvin = 2200 }
		};
		var t = Build(s => s.RespectSleepMode = true, g => g.HouseMode = mode, periods: periods);
		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 5 },
			"the explicit ClampPeriodId 'dim' (its own 5 %) drives the clamp, not the 'night' fallback");
	}

	[TestMethod]
	public void Sleep_RespectingArea_WithNoResolvableClamp_LeavesTheTargetAlone()
	{
		// Sover has no ClampPeriodId, and there is no 'night' period nor one that SetsModeId Sover, so nothing resolves.
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

	// The sleep clamp reads this room's night level, not the house's. It is the one place a room's level is a
	// ceiling instead of a target.
	[TestMethod]
	public void Sleep_RespectingArea_ClampsToItsOwnNightLevelRatherThanTheHouses()
	{
		var periods = new List<TimePeriodConfig>
		{
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new() { Name = "night", Start = "23:00", BrightnessPct = 15, ColorTempKelvin = 2200 }
		};
		var t = Build(
			s => s.RespectSleepMode = true,
			g => g.HouseMode = SoverMode(),
			periods: periods,
			levels: [new RoomLevelOverride { PeriodId = "night", BrightnessPct = 4 }]);

		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 4 },
			"the clamp period's level is this room's 4, so 4 is the ceiling — the house's 15 never applies here");
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
		// No HouseMode configured, so raw presence drives Away.
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		t.House.OnNext(House(home: false));

		Assert.AreEqual(AreaState.Away, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false });
	}

	// ---- a forced mode is never a presence departure -------------------------------------------

	private static AreaSnapshot LastReport(Fixture fixture) => fixture.Publisher.Snapshots[^1];

	[TestMethod]
	public void ForcedAwayMode_IsNotReportedAsAPresenceDeparture()
	{
		Fixture t = Build(tweakGlobal: g => g.HouseMode = SoverMode());
		t.Ha.Trigger(Motion, "on");

		t.House.OnNext(House(kind: ModeKind.Away, modeValue: "Borte", forced: ForcedAway()));

		AreaSnapshot report = LastReport(t);

		Assert.AreEqual(AreaState.Away, report.State, "the mode still sweeps the room — that part was never wrong");
		Assert.AreNotEqual(TransitionReason.EveryoneLeft, report.Reason,
			"nobody left; the mode did this, and saying otherwise cost an hour hunting a presence fault");
		Assert.AreEqual(TransitionReason.HouseModeChanged, report.Reason);
	}

	[TestMethod]
	public void ForcedAwayMode_ReportsWhoIsHomeAndWhatIsForcingIt()
	{
		Fixture t = Build(tweakGlobal: g => g.HouseMode = SoverMode());
		t.House.OnNext(House(kind: ModeKind.Away, modeValue: "Borte", forced: ForcedAway()));

		AreaSnapshot report = LastReport(t);

		Assert.AreEqual(true, report.IsAnyoneHome,
			"presence said the house was full throughout — the room must not be able to claim otherwise");
		Assert.AreEqual(ModeForceSource.WhileEntityOn, report.Forced!.Source);
		Assert.AreEqual("input_boolean.occupancy", report.Forced.EntityId);
		Assert.AreEqual("Away mode is forced while input_boolean.occupancy is on.", report.Forced.Describe());
	}

	[TestMethod]
	public void PresenceDeparture_StillReportsEveryoneLeft()
	{
		Fixture t = Build();
		t.Ha.Trigger(Motion, "on");

		t.House.OnNext(House(home: false));

		AreaSnapshot report = LastReport(t);

		Assert.AreEqual(TransitionReason.EveryoneLeft, report.Reason,
			"an empty house is still an empty house — only the mode's route was ever mislabelled");
		Assert.AreEqual(false, report.IsAnyoneHome);
		Assert.IsNull(report.Forced, "presence leaving is nobody forcing anything");
	}

	[TestMethod]
	public void AwayModeReleasing_IsAModeChange_NotAnArrival()
	{
		Fixture t = Build(tweakGlobal: g => g.HouseMode = SoverMode());
		t.House.OnNext(House(kind: ModeKind.Away, modeValue: "Borte", forced: ForcedAway()));
		Assert.AreEqual(AreaState.Away, t.Area.State);

		// The boolean goes off. Nobody arrived; the mode let go.
		t.House.OnNext(House(modeValue: "Normal"));

		AreaSnapshot report = LastReport(t);

		Assert.AreNotEqual(TransitionReason.FirstPersonArrived, report.Reason,
			"claiming an arrival nobody made is the same invented cause in the other direction");
		Assert.AreEqual(TransitionReason.HouseModeChanged, report.Reason);
	}

	// ---- the mode an area found when it started is not a mode change ---------------------------

	/// <summary>A lit room, so the area adopts and is in the one state the opening mode retargets.</summary>
	private static Fixture BuildLitAndUnopened() =>
		Build(
			tweakGlobal: g => g.HouseMode = SoverMode(),
			seed: ha => ha.SetState(Light, "on", new() { ["brightness"] = 178.5 }),
			openHouse: false);

	[TestMethod]
	public void TheModeFoundAtStartUp_IsReportedAsStartUp_NotAsAModeChange()
	{
		Fixture t = BuildLitAndUnopened();

		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		Assert.AreEqual(TransitionReason.Startup, LastReport(t).Reason,
			"the select never moved; the engine started and read it");
	}

	[TestMethod]
	public void AModeChangeAfterStartUp_IsStillAModeChange()
	{
		Fixture t = BuildLitAndUnopened();
		t.House.OnNext(House(modeValue: "Normal"));

		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		Assert.AreEqual(TransitionReason.HouseModeChanged, LastReport(t).Reason);
	}

	[TestMethod]
	public void AnAwayModeFoundAtStartUp_StillSweepsTheRoom_AndStillNamesWhatIsForcingIt()
	{
		Fixture t = Build(tweakGlobal: g => g.HouseMode = SoverMode(), openHouse: false);

		t.House.OnNext(House(kind: ModeKind.Away, modeValue: "Borte", forced: ForcedAway()));

		AreaSnapshot report = LastReport(t);

		Assert.AreEqual(AreaState.Away, report.State, "the sweep was never the part that was wrong");
		Assert.AreEqual(TransitionReason.Startup, report.Reason);
		Assert.AreEqual("input_boolean.occupancy", report.Forced?.EntityId,
			"a mode forced at start-up must still be able to say what is holding it");
	}

	[TestMethod]
	public void FirstArrival_AfterAPresenceDeparture_StillReportsAnArrival()
	{
		Fixture t = Build();
		t.House.OnNext(House(home: false));
		Assert.AreEqual(AreaState.Away, t.Area.State);

		t.House.OnNext(House(home: true));

		Assert.AreEqual(TransitionReason.FirstPersonArrived, LastReport(t).Reason,
			"somebody genuinely walked in, and that is what an arrival is");
	}

	// Asserted on the tick, not on the house change: an area already Away short-circuits out of OnHouseChanged
	// without publishing. The correction only lands because IsAnyoneHome counts in HasSameMeaningAs.
	[TestMethod]
	public void ComingHomeToAForcedAwayMode_IsCorrectedOnTheNextTick()
	{
		Fixture t = Build(tweakGlobal: g => g.HouseMode = SoverMode());
		t.House.OnNext(House(home: false, kind: ModeKind.Away, modeValue: "Borte", forced: ForcedAway()));

		Assert.AreEqual(false, LastReport(t).IsAnyoneHome);

		// The boolean is still on, so the mode does not move, but the house fills up again.
		t.House.OnNext(House(home: true, kind: ModeKind.Away, modeValue: "Borte", forced: ForcedAway()));
		Advance(t, TimeSpan.FromSeconds(60));

		Assert.AreEqual(true, LastReport(t).IsAnyoneHome,
			"the report that said the house was empty while somebody stood in it is the one that had to move");
	}

	[TestMethod]
	public void Migration_LiveCabin_NoHouseMode_UsesBaseline()
	{
		// No HouseMode, nobody asleep, no mode selected: the baseline evening period drives.
		var t = Build(s => s.RespectSleepMode = true);
		t.House.OnNext(House(modeValue: null));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70 },
			"with no mode selected and nobody asleep, the baseline evening period drives, exactly as today");
	}

	// ===================== guest scene hold =====================

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

		// The scene-hold check runs before the was-Away recovery, so a scene selected while Away lands in
		// SceneHold instead of firing the welcome-home ApplyTarget.
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

	// ===================== movement the engine turned down =====================
	//
	// Movement into a blocked room publishes a report, and the report is bounded: the comparison is on the
	// refusing gate, so the count over any interval follows gate changes, never footfall.

	[TestMethod]
	public void Forty_Walks_Under_One_Unchanged_Block_Produce_One_Report()
	{
		Fixture t = Build();
		t.Ha.SetState(Lux, "5000");
		t.Publisher.Snapshots.Clear();

		for (int walk = 0; walk < 40; walk++)
		{
			t.Ha.Trigger(Motion, "off");
			t.Ha.Trigger(Motion, "on");
		}

		AreaSnapshot[] declined = [.. t.Publisher.Snapshots.Where(s => s.Reason == TransitionReason.Motion)];

		Assert.AreEqual(1, declined.Length, "one refusal reported, not forty — the reason never changed");
		Assert.AreEqual(AutoOnBlock.NotDark, declined[0].AutoOnBlockedBy);
		Assert.AreEqual(AreaState.AutoVacant, declined[0].State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "and still no light, which is the point of the row");
	}

	// AutoOnBlockedBy is kept out of HasSameMeaningAs, so a drifting lux reading cannot republish every area.
	[TestMethod]
	public void A_Drifting_Reading_Under_One_Unchanged_Reason_Adds_No_Row()
	{
		Fixture t = Build();
		t.Ha.SetState(Lux, "5000");
		t.Ha.Trigger(Motion, "on");
		t.Publisher.Snapshots.Clear();

		foreach (string reading in new[] { "5100", "5300", "4900", "6000" })
		{
			t.Ha.SetState(Lux, reading);
			t.Ha.Trigger(Motion, "off");
			t.Ha.Trigger(Motion, "on");
		}

		Assert.AreEqual(0, t.Publisher.Snapshots.Count(s => s.Reason == TransitionReason.Motion),
			"four different readings, one unchanged verdict, nothing new to say");
	}

	// The bound must not swallow a second spell: blocked, then lit, then blocked by the same gate is two reports.
	[TestMethod]
	public void A_Changed_Reason_Reports_Again_And_So_Does_A_Block_That_Returns()
	{
		Fixture t = Build(ignoreWhenOn: [Blocker]);
		t.Ha.SetState(Lux, "5000");
		t.Publisher.Snapshots.Clear();

		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Motion, "off");

		// Same room, different gate: the television goes on and outranks the darkness verdict.
		t.Ha.SetState(Blocker, "on");
		t.Ha.Trigger(Motion, "on");

		AutoOnBlock?[] reasons = [.. t.Publisher.Snapshots
			.Where(s => s.Reason == TransitionReason.Motion)
			.Select(s => s.AutoOnBlockedBy)];

		CollectionAssert.AreEqual(new AutoOnBlock?[] { AutoOnBlock.NotDark, AutoOnBlock.EntityOn }, reasons,
			"two different refusals are two rows, and the second names the entity rather than the sensor");

		Assert.AreEqual(Blocker, t.Publisher.Snapshots.Last(s => s.Reason == TransitionReason.Motion).AutoOnBlockingEntity);

		// Everything clears, the room lights, and then the same block returns.
		t.Ha.SetState(Blocker, "off");
		t.Ha.SetState(Lux, "5");
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "nothing is in the way now, so the room lights");

		t.Ha.SetState(Lux, "5000");
		Advance(t, TimeSpan.FromMinutes(11));   // vacancy, pre-off, back to AutoVacant
		t.Publisher.Snapshots.Clear();
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(1, t.Publisher.Snapshots.Count(s => s.Reason == TransitionReason.Motion),
			"the room lit in between, so the refusal that came back is news again rather than a repeat");
	}

	/// <summary>Every gate that can refuse movement produces a report naming itself.</summary>
	[TestMethod]
	public void Every_Refusal_Names_Itself_On_A_Declined_Movement()
	{
		Assert.AreEqual(AutoOnBlock.NotDark, FirstDeclined(Build(), t => t.Ha.SetState(Lux, "5000")));

		Assert.AreEqual(AutoOnBlock.Disabled, FirstDeclined(Build(s => s.Enabled = false), _ => { }));

		Assert.AreEqual(AutoOnBlock.KillSwitch, FirstDeclined(Build(), t => t.House.OnNext(House(killed: true))));

		Assert.AreEqual(AutoOnBlock.Away, FirstDeclined(Build(), t => t.House.OnNext(House(home: false))));

		Assert.AreEqual(AutoOnBlock.Sleep,
			FirstDeclined(Build(s => s.SleepBlocksAutoOn = true), t => t.House.OnNext(House(kind: ModeKind.Sleep))));

		Assert.AreEqual(AutoOnBlock.EntityOn,
			FirstDeclined(Build(ignoreWhenOn: [Blocker]), t => t.Ha.SetState(Blocker, "on")));

		Assert.AreEqual(AutoOnBlock.SceneHold,
			FirstDeclined(Build(), t => t.House.OnNext(House(kind: ModeKind.Guest, modeValue: "Gjester", scene: "scene.gjest"))),
			"the fourth silent refusal: a guest scene holds the room, and saying 'not dark enough' would be a lie");
	}

	/// <summary>Arranges a block, walks through the room once, and hands back the gate the report named.</summary>
	private static AutoOnBlock? FirstDeclined(Fixture fixture, Action<Fixture> block)
	{
		block(fixture);
		fixture.Publisher.Snapshots.Clear();
		fixture.Ha.Trigger(Motion, "on");

		return fixture.Publisher.Snapshots.Single(s => s.Reason == TransitionReason.Motion).AutoOnBlockedBy;
	}

	// ===================== testing a period on the real lights =====================
	//
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

	[TestMethod]
	public void A_Test_Changes_Nothing_The_Area_Decides_On()
	{
		Fixture t = Build();
		int published = t.Publisher.Snapshots.Count;

		t.Area.TestPeriod("day");

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsTrue(t.Area.IsTestingLevels);
		Assert.AreEqual(published, t.Publisher.Snapshots.Count, "a test is no news about the room");
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
			"resolved at the instant the test ends, so movement or a boundary in those ten seconds is honoured");
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
	}

	[TestMethod]
	public void A_Test_Does_Not_Restart_The_Vacancy_Countdown()
	{
		Fixture t = Build();
		t.Ha.Trigger(Motion, "on");

		Advance(t, TimeSpan.FromMinutes(5));
		t.Area.TestPeriod("night");
		Advance(t, TestRun);

		// 9 min 59 s after the movement that armed the ten-minute timeout.
		Advance(t, TimeSpan.FromSeconds(289));
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
		Advance(t, TimeSpan.FromSeconds(6));

		t.Area.TestPeriod("night");
		Assert.IsTrue(t.Actuator.Last is { BrightnessPct: 15, ColorTempKelvin: 2200 });
		t.Actuator.Clear();

		// The first press's ten seconds are up, and nothing happens: its return went with it.
		Advance(t, TimeSpan.FromSeconds(4));
		Assert.AreEqual(0, t.Actuator.Applied.Count);
		Assert.IsTrue(t.Area.IsTestingLevels);

		Advance(t, TimeSpan.FromSeconds(6));
		Assert.AreEqual(1, t.Actuator.Applied.Count, "one return, ten seconds from the newest press");
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
	public void A_Test_Is_Refused_While_Somebody_Elses_Levels_Hold_The_Room()
	{
		Fixture t = Build();
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);

		t.Actuator.Clear();

		Assert.IsNotNull(t.Area.TestPeriod("day"), "there is no way back to levels the engine never chose");
		Assert.AreEqual(0, t.Actuator.Applied.Count);
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State, "and the hold itself is left standing");
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
