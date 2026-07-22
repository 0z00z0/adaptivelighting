using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

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
}
