using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The orchestrator's house-wide seam: the scene apply on entry to an Away/Guest mode (09 §3.3), driven
///     through the real <see cref="ModeMonitor"/> with a <see cref="TestScheduler"/>. The scene is applied once
///     per entry via <see cref="AdaptiveLighting.Abstractions.ILightActuator.ActivateScene"/> and
///     never re-asserted.
/// </summary>
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

	// ===================== a light two rooms both command =====================

	/// <summary>
	///     Start-up looks across the rooms once and says what it found: two rooms commanding one light, named as
	///     advice a page can render and as a single warning in the log. Once per light per run — this is a fact
	///     about the Home Assistant registry, so re-deciding it on the clock would be per-tick work for an answer
	///     that cannot have moved.
	/// </summary>
	[TestMethod]
	public void Two_Rooms_Commanding_One_Light_Are_Reported_Once_At_Start_Up()
	{
		FakeHaContext ha = new();
		ha.SetState("light.stue_taklys", "off");
		ha.SetState("light.kjokken_taklys", "off");
		ha.SetState("light.benklys", "off", new() { ["friendly_name"] = "Benklys" });
		ha.SetState("binary_sensor.stue_m", "off");
		ha.SetState("binary_sensor.kjokken_m", "off");

		// Both rooms name the shared light by hand, which is the same end state a shared group reaches: the registry
		// here knows no areas at all, so nothing in the house has a room of its own.
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

	/// <summary>A house whose rooms share nothing says nothing, which is the ordinary case.</summary>
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

	/// <summary>
	///     Captures the warnings the engine writes, because "and it says so out loud" is half of what the shared-light
	///     finding is for — the household never opens the log unless something is wrong, so the line has to be there
	///     when they do, and exactly once.
	/// </summary>
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
