using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The scene applied on entry to an Away or Guest mode, once per entry and never re-asserted.</summary>
[TestClass]
public sealed class LightingOrchestratorTests
{
	private const string Person = "person.a";
	private const string Select = "input_select.husmodus";

	private sealed record Fixture(TestScheduler Scheduler, FakeHaContext Ha, FakeLightActuator Actuator, LightingOrchestrator Orchestrator);

	private static Fixture Build(HouseModeConfig houseMode, string selectState)
	{
		var scheduler = new TestScheduler();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		var ha = new FakeHaContext();
		ha.SetState(Person, "home");
		ha.SetState(Select, selectState);

		var config = new AdaptiveLightingConfig
		{
			Global = new GlobalConfig { Persons = [Person], AwayDebounceMinutes = 5, HouseMode = houseMode },
			// A baseline period so the document is otherwise ordinary; no areas, so the registry is never touched.
			Periods = [new TimePeriodConfig { Name = "day", Start = "07:00" }]
		};

		var actuator = new FakeLightActuator();
		var orchestrator = new LightingOrchestrator(
			ha, new FakeHaRegistry(), scheduler, config,
			actuator, new FakeStatePublisher(), new FakeNotifier(), NullLoggerFactory.Instance);

		orchestrator.Start();
		return new Fixture(scheduler, ha, actuator, orchestrator);
	}

	private static HouseModeConfig WithScenes() => new()
	{
		Entity = Select,
		Options =
		[
			new() { Value = "Normal", Kind = ModeKind.Normal },
			new() { Value = "Borte", Kind = ModeKind.Away, Scene = "scene.borte" },
			new() { Value = "Gjester", Kind = ModeKind.Guest, Scene = "scene.gjest" }
		]
	};

	[TestMethod]
	public void SceneMode_AppliesTheSceneOnceOnEntry_AndDoesNotReassert()
	{
		var t = Build(WithScenes(), selectState: "Normal");
		Assert.AreEqual(0, t.Actuator.Scenes.Count, "the Normal baseline names no scene");

		t.Ha.Trigger(Select, "Borte");
		CollectionAssert.AreEqual(new[] { "scene.borte" }, t.Actuator.Scenes, "the away scene is applied exactly once on entry");

		// An unrelated house change (presence, while the away mode stands) must not re-assert the standing scene.
		t.Ha.Trigger(Person, "not_home");
		t.Scheduler.AdvanceBy(TimeSpan.FromMinutes(5).Ticks);

		Assert.AreEqual(1, t.Actuator.Scenes.Count, "the scene is applied once per entry, never re-asserted");
	}

	[TestMethod]
	public void SceneMode_ReappliesOnAFreshEntry()
	{
		var t = Build(WithScenes(), selectState: "Normal");

		t.Ha.Trigger(Select, "Gjester");
		t.Ha.Trigger(Select, "Normal");
		t.Ha.Trigger(Select, "Gjester");

		Assert.AreEqual(2, t.Actuator.Scenes.Count(s => s == "scene.gjest"),
			"each fresh entry to the scene mode applies the scene again");
	}

	[TestMethod]
	public void ANormalMode_AppliesNoScene()
	{
		var t = Build(WithScenes(), selectState: "Normal");

		t.Ha.Trigger(Select, "Borte");
		t.Ha.Trigger(Select, "Normal");

		Assert.AreEqual(1, t.Actuator.Scenes.Count, "leaving the scene mode for Normal applies nothing new");
		CollectionAssert.DoesNotContain(t.Actuator.Scenes, "scene.normal");
	}

	// Decided at start-up, once per light per run: the registry cannot move underneath.
	[TestMethod]
	public void Two_Rooms_Commanding_One_Light_Are_Reported_Once_At_Start_Up()
	{
		FakeHaContext ha = new();
		ha.SetState("light.stue_taklys", "off");
		ha.SetState("light.kjokken_taklys", "off");
		ha.SetState("light.benklys", "off", new() { ["friendly_name"] = "Benklys" });
		ha.SetState("binary_sensor.stue_m", "off");
		ha.SetState("binary_sensor.kjokken_m", "off");

		// Both rooms name the shared light by hand, the same end state a shared group reaches; the registry knows no
		// areas, so nothing has a room of its own.
		AdaptiveLightingConfig config = new()
		{
			Periods = [new TimePeriodConfig { Name = "day", Start = "07:00" }],
			Areas =
			[
				new AreaConfig
				{
					Name = "Stue",
					Lights = ["light.stue_taklys", "light.benklys"],
					MotionSensors = ["binary_sensor.stue_m"]
				},
				new AreaConfig
				{
					Name = "Kjøkken",
					Lights = ["light.kjokken_taklys", "light.benklys"],
					MotionSensors = ["binary_sensor.kjokken_m"]
				}
			]
		};

		RecordingLoggerFactory logs = new();
		TestScheduler scheduler = new();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		LightingOrchestrator orchestrator = new(
			ha, new FakeHaRegistry(), scheduler, config,
			new FakeLightActuator(), new FakeStatePublisher(), new FakeNotifier(), logs);

		orchestrator.Start();

		Assert.AreEqual(1, orchestrator.SharedLights.Count, "one light is shared, so there is one thing to say");
		Assert.AreEqual("light.benklys", orchestrator.SharedLights[0].EntityId);
		Assert.AreEqual("Benklys", orchestrator.SharedLights[0].Name, "named as the household named it");
		StringAssert.Contains(orchestrator.SharedLights[0].Reason, "Stue");
		StringAssert.Contains(orchestrator.SharedLights[0].Reason, "Kjøkken");

		Assert.AreEqual(1, logs.Warnings.Count(warning => warning.Contains("light.benklys", StringComparison.Ordinal)),
			"one warning per light per run, not one per room and not one per tick");
	}

	[TestMethod]
	public void Rooms_With_Their_Own_Lights_Report_No_Sharing()
	{
		FakeHaContext ha = new();
		ha.SetState("light.stue_taklys", "off");
		ha.SetState("light.kjokken_taklys", "off");
		ha.SetState("binary_sensor.stue_m", "off");
		ha.SetState("binary_sensor.kjokken_m", "off");

		AdaptiveLightingConfig config = new()
		{
			Periods = [new TimePeriodConfig { Name = "day", Start = "07:00" }],
			Areas =
			[
				new AreaConfig { Name = "Stue", Lights = ["light.stue_taklys"], MotionSensors = ["binary_sensor.stue_m"] },
				new AreaConfig { Name = "Kjøkken", Lights = ["light.kjokken_taklys"], MotionSensors = ["binary_sensor.kjokken_m"] }
			]
		};

		TestScheduler scheduler = new();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		LightingOrchestrator orchestrator = new(
			ha, new FakeHaRegistry(), scheduler, config,
			new FakeLightActuator(), new FakeStatePublisher(), new FakeNotifier(), NullLoggerFactory.Instance);

		orchestrator.Start();

		Assert.AreEqual(0, orchestrator.SharedLights.Count);
	}

	// ===================== the sun moving =====================

	private const string Sun = "sun.sun";
	private const string SunLight = "light.stue";
	private const string SunMotion = "binary_sensor.stue_m";

	/// <summary>A house whose sleep boundary is anchored to sunset, started at <paramref name="startAt"/> with the sun set to <paramref name="setting"/>.</summary>
	// Every boundary is written relative to the same instant and resolved in the same zone, so the table means the
	// same thing on a UTC build agent.
	private static Fixture SunAnchored(DateTimeOffset startAt, DateTimeOffset? setting, out FakeHaContext ha)
	{
		TimeOnly localNow = TimeOnly.FromDateTime(startAt.ToLocalTime().DateTime);

		ha = new FakeHaContext();
		ha.SetState(Person, "home");
		ha.SetState(Select, "Normal");
		ha.SetState(SunLight, "off");
		ha.SetState(SunMotion, "off");
		if (setting is { } instant)
			ha.SetState(Sun, "above_horizon", new() { ["next_setting"] = instant.ToString("O"), ["elevation"] = -3.0 });

		var config = new AdaptiveLightingConfig
		{
			Global = new GlobalConfig
			{
				Persons = [Person],
				CircadianTickSeconds = 300,
				SmoothTransitions = false,
				HouseMode = new HouseModeConfig
				{
					Entity = Select,
					Options = [new() { Value = "Normal", Kind = ModeKind.Normal }, new() { Value = "Sover", Kind = ModeKind.Sleep }]
				}
			},
			Periods =
			[
				new TimePeriodConfig { Name = "evening", Start = localNow.AddMinutes(-30).ToString("HH:mm"), BrightnessPct = 60 },
				new TimePeriodConfig { Name = "night", Start = "sunset", BrightnessPct = 10, SetsModeId = "Sover" }
			],
			// One room, so the per-area sun the areas are built with is exercised beside the house-wide one.
			Areas =
			[
				new AreaConfig
				{
					Name = "Stue",
					Lights = [SunLight],
					MotionSensors = [SunMotion],
					VacancyTimeoutSeconds = 60 * 60 * 5
				}
			]
		};

		var scheduler = new TestScheduler();
		scheduler.AdvanceTo(startAt.Ticks);

		var actuator = new FakeLightActuator();
		var orchestrator = new LightingOrchestrator(
			ha, new FakeHaRegistry(), scheduler, config,
			actuator, new FakeStatePublisher(), new FakeNotifier(), NullLoggerFactory.Instance);

		orchestrator.Start();
		return new Fixture(scheduler, ha, actuator, orchestrator);
	}

	private static int SleepCalls(FakeHaContext ha) =>
		ha.Calls.Count(c => c.Domain == "input_select" && c.Service == "select_option"
			&& c.Data?.GetType().GetProperty("option")?.GetValue(c.Data) as string == "Sover");

	/// <summary>The sun entity moving its own next setting is what re-arms every boundary anchored to it.</summary>
	[TestMethod]
	public void A_Sun_Entity_That_Moves_Its_Setting_Rearms_Before_The_Next_Tick()
	{
		DateTimeOffset startAt = new(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);
		var t = SunAnchored(startAt, startAt.AddHours(3), out FakeHaContext ha);
		ha.Trigger(SunMotion, "on");
		t.Actuator.Clear();

		ha.Trigger(Sun, "above_horizon",
			new() { ["next_setting"] = startAt.AddMinutes(2).ToString("O"), ["elevation"] = -3.5 });

		t.Scheduler.AdvanceBy(TimeSpan.FromSeconds(125).Ticks);
		Assert.AreEqual(1, SleepCalls(ha), "sunset moved to two minutes out, well inside the 300 s tick");
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 10 }, "the lit room moved with it");
	}

	/// <summary>A house whose sun entity Home Assistant does not have starts, runs, and adopts one when it appears.</summary>
	[TestMethod]
	public void A_Missing_Sun_Entity_Leaves_The_House_Running_And_Is_Adopted_When_It_Appears()
	{
		DateTimeOffset startAt = new(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);
		var t = SunAnchored(startAt, setting: null, out FakeHaContext ha);

		t.Scheduler.AdvanceBy(TimeSpan.FromMinutes(20).Ticks);
		Assert.AreEqual(0, SleepCalls(ha), "night has no sunset to be placed at, so its boundary is never crossed");

		ha.Trigger(Sun, "above_horizon",
			new() { ["next_setting"] = startAt.AddMinutes(22).ToString("O"), ["elevation"] = -3.0 });

		t.Scheduler.AdvanceBy(TimeSpan.FromSeconds(125).Ticks);
		Assert.AreEqual(1, SleepCalls(ha), "the sun the house was missing places night two minutes out");
	}

	[TestMethod]
	public void SunAnchors_MoveWithTheRisingAndTheSetting_AndNotWithTheElevation()
	{
		FakeHaContext ha = new();
		Dictionary<string, object> attributes = new()
		{
			["next_rising"] = "2026-01-16T07:41:00+00:00",
			["next_setting"] = "2026-01-15T21:12:00+00:00",
			["elevation"] = -3.0,
			["azimuth"] = 241.7
		};

		ha.SetState(Sun, "above_horizon", attributes);
		(DateTimeOffset? Rising, DateTimeOffset? Setting) before = LightingOrchestrator.SunAnchorsOf(ha.GetState(Sun));

		ha.SetState(Sun, "above_horizon", new(attributes) { ["elevation"] = -3.4, ["azimuth"] = 242.1 });
		Assert.AreEqual(before, LightingOrchestrator.SunAnchorsOf(ha.GetState(Sun)),
			"elevation and azimuth move every half minute and no boundary is anchored to either");

		ha.SetState(Sun, "above_horizon", new(attributes) { ["next_setting"] = "2026-01-15T21:13:00+00:00" });
		Assert.AreNotEqual(before, LightingOrchestrator.SunAnchorsOf(ha.GetState(Sun)),
			"a setting a minute later is every sunset-anchored boundary a minute later");

		ha.SetState(Sun, "above_horizon", new(attributes) { ["next_rising"] = "2026-01-16T07:42:00+00:00" });
		Assert.AreNotEqual(before, LightingOrchestrator.SunAnchorsOf(ha.GetState(Sun)));
	}

	[TestMethod]
	public void SunAnchors_OfAnEntityWithNothingToRead_AreBothNull()
	{
		FakeHaContext ha = new();
		ha.SetState(Sun, "unavailable");

		Assert.AreEqual((null, null), LightingOrchestrator.SunAnchorsOf(ha.GetState(Sun)));
		Assert.AreEqual((null, null), LightingOrchestrator.SunAnchorsOf(ha.GetState("sun.absent")));
	}

	/// <summary><c>sun.sun</c> republishes elevation and azimuth every half minute, and no boundary is anchored to either.</summary>
	[TestMethod]
	public void SunMoved_AnnouncesTheAnchorsMoving_AndNotTheElevation()
	{
		DateTimeOffset startAt = new(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);
		Fixture t = SunAnchored(startAt, startAt.AddHours(3), out FakeHaContext ha);
		string setting = startAt.AddHours(3).ToString("O");

		int announced = 0;
		using IDisposable subscription = t.Orchestrator.SunMoved(Sun)!.Subscribe(_ => announced++);

		// The first change after subscribing has nothing behind it to be the same as, so it always announces.
		ha.Trigger(Sun, "above_horizon", new() { ["next_setting"] = setting, ["elevation"] = -3.5 });
		int primed = announced;

		ha.Trigger(Sun, "above_horizon", new() { ["next_setting"] = setting, ["elevation"] = -4.0 });
		ha.Trigger(Sun, "above_horizon", new() { ["next_setting"] = setting, ["elevation"] = -4.5 });
		Assert.AreEqual(primed, announced, "the elevation moved twice more and the setting did not");

		ha.Trigger(Sun, "above_horizon",
			new() { ["next_setting"] = startAt.AddHours(2).ToString("O"), ["elevation"] = -4.5 });
		Assert.AreEqual(primed + 1, announced,
			"a setting an hour earlier is every sunset-anchored boundary an hour earlier");
	}

	/// <summary>A house or a room that names no sun entity watches nothing, never an entity called "".</summary>
	[TestMethod]
	public void SunMoved_WithoutASunEntityToName_IsNull()
	{
		DateTimeOffset startAt = new(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);
		Fixture t = SunAnchored(startAt, startAt.AddHours(3), out FakeHaContext _);

		Assert.IsNull(t.Orchestrator.SunMoved(""));
		Assert.IsNull(t.Orchestrator.SunMoved(null));
		Assert.IsNotNull(t.Orchestrator.SunMoved(Sun), "a named sun is watched");
	}

	/// <summary>Captures the warnings the engine writes.</summary>
	private sealed class RecordingLoggerFactory : ILoggerFactory
	{
		private readonly List<string> _warnings = [];

		public IReadOnlyList<string> Warnings => _warnings;

		public ILogger CreateLogger(string categoryName) => new Recorder(_warnings);

		public void AddProvider(ILoggerProvider provider) { }

		public void Dispose() { }

		private sealed class Recorder(List<string> warnings) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => true;

			public void Log<TState>(
				LogLevel logLevel,
				EventId eventId,
				TState state,
				Exception? exception,
				Func<TState, Exception?, string> formatter)
			{
				ArgumentNullException.ThrowIfNull(formatter);

				if (logLevel >= LogLevel.Warning)
					warnings.Add(formatter(state, exception));
			}
		}
	}
}
