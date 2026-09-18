using System.Text.Json;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Hosting;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

using NetDaemon.HassModel;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>A settings save rebuilds every room; each rebuilt room keeps its history and a hold made at the switch.</summary>
// Driven through the host's real save path and read back from the event the room publishes, as the pages read it.
[TestClass]
public sealed class CarryOverOnSaveTests
{
	private const string Light = "light.test_room";
	private const string Motion = "binary_sensor.test_room_motion";

	private static readonly DateTimeOffset Start = new(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);

	private string _directory = "";

	[TestInitialize]
	public void CreateTempDirectory()
	{
		_directory = Path.Combine(Path.GetTempPath(), $"carry-over-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_directory);
	}

	[TestCleanup]
	public void RemoveTempDirectory()
	{
		if (Directory.Exists(_directory))
			Directory.Delete(_directory, recursive: true);
	}

	private sealed record House(LightingEngineHost Host, FakeHaContext Ha, TestScheduler Scheduler) : IDisposable
	{
		public void Advance(TimeSpan by) => Scheduler.AdvanceBy(by.Ticks);

		public void Dispose() => Host.Dispose();
	}

	private House Running(string lightState, int overrideMinutes = 60)
	{
		FakeHaContext ha = new();
		ha.SetState(Light, lightState);
		ha.SetState(Motion, "off");

		TestScheduler scheduler = new();
		scheduler.AdvanceTo(Start.Ticks);

		LightingEngineHost host = new(
			new LightingConfigStore(Path.Combine(_directory, "AdaptiveLighting.yaml"), NullLogger<LightingConfigStore>.Instance),
			NullLoggerFactory.Instance);
		host.Attach(ha, new FakeHaRegistry(), scheduler);

		SaveResult result = host.Save(Document(overrideMinutes));
		Assert.AreEqual(1, host.RunningAreaCount, result.Message);

		return new House(host, ha, scheduler);
	}

	private static AdaptiveLightingConfig Document(int overrideMinutes) => new()
	{
		ConfigName = "Adaptive lighting [test]",
		Periods = [new TimePeriodConfig { Id = "day", Name = "day", Start = "06:00", BrightnessPct = 80, ColorTempKelvin = 3500 }],
		Areas =
		[
			new AreaConfig
			{
				Name = "Test room",
				Lights = [Light],
				MotionSensors = [Motion],
				OverrideDurationMinutes = overrideMinutes,
				OverrideUntilVacant = false
			}
		]
	};

	private static Context AtTheSwitch() => new() { Id = "physical" };

	/// <summary>The room's newest published report, as the pages receive it.</summary>
	private static JsonElement Latest(FakeHaContext ha) =>
		JsonSerializer.SerializeToElement(ha.SentEvents.Last(sent => sent.Type == "adaptive_lighting_area").Data);

	private static string? Field(JsonElement report, string name) =>
		report.GetProperty(name) is { ValueKind: not JsonValueKind.Null } value ? value.ToString() : null;

	private static int LightOnCommands(FakeHaContext ha) =>
		ha.Calls.Count(call => call.Domain == "light" && call.Service == "turn_on");

	[TestMethod]
	public void ASave_KeepsLastMovementAndWhoLastChangedTheLights()
	{
		using House house = Running(lightState: "on");

		house.Ha.Trigger(Motion, "on");
		house.Advance(TimeSpan.FromMinutes(1));
		house.Ha.Trigger(Light, "off", null, AtTheSwitch());
		house.Advance(TimeSpan.FromMinutes(1));

		JsonElement before = Latest(house.Ha);
		Assert.IsNotNull(Field(before, "last_motion_at"));
		Assert.IsNotNull(Field(before, "changed_at"));
		Assert.IsNotNull(Field(before, "changed_by"));

		house.Host.Save(Document(overrideMinutes: 45));

		JsonElement after = Latest(house.Ha);
		Assert.AreEqual(Field(before, "last_motion_at"), Field(after, "last_motion_at"), "last movement survives a save");
		Assert.AreEqual(Field(before, "changed_at"), Field(after, "changed_at"), "so does when the lights were last changed");
		Assert.AreEqual(Field(before, "changed_by"), Field(after, "changed_by"), "and who changed them");
	}

	[TestMethod]
	public void ARoomSwitchedOffByHand_StaysOffAfterASave_AndMovementDoesNotLightIt()
	{
		using House house = Running(lightState: "on");

		house.Ha.Trigger(Light, "off", null, AtTheSwitch());
		Assert.AreEqual(nameof(AreaState.SuppressedOff), Field(Latest(house.Ha), "state"));

		house.Host.Save(Document(overrideMinutes: 45));
		Assert.AreEqual(nameof(AreaState.SuppressedOff), Field(Latest(house.Ha), "state"), "the save must not undo the hand at the switch");

		int commandsBefore = LightOnCommands(house.Ha);
		house.Ha.Trigger(Motion, "on");

		Assert.AreEqual(nameof(AreaState.SuppressedOff), Field(Latest(house.Ha), "state"));
		Assert.AreEqual(commandsBefore, LightOnCommands(house.Ha), "movement right after a save lit a room somebody had switched off");
	}

	[TestMethod]
	public void ALevelTestRunningAtASave_KeepsItsCountdown_AndADarkRoomGoesBackToDarkAtTheEnd()
	{
		using House house = Running(lightState: "off");

		Assert.IsNull(house.Host.RunningAreas[0].TestPeriod("day"));
		house.Ha.SetState(Light, "on");
		string? endsAt = Field(Latest(house.Ha), "test_ends_at");
		Assert.IsNotNull(endsAt);

		house.Advance(TimeSpan.FromSeconds(2));
		int calls = house.Ha.Calls.Count;
		house.Host.Save(Document(overrideMinutes: 45));

		JsonElement after = Latest(house.Ha);
		Assert.AreEqual("day", Field(after, "testing_period_id"), "the level row is still testing after the save");
		Assert.AreEqual(endsAt, Field(after, "test_ends_at"), "on the same countdown");
		Assert.AreEqual(nameof(AreaState.AutoVacant), Field(after, "state"), "a room lit only by the test is not taken as lit");
		Assert.AreEqual(calls, house.Ha.Calls.Count, "the save sends the lights nowhere");

		house.Advance(TimeSpan.FromSeconds(3));
		Assert.IsTrue(house.Ha.Calls.Skip(calls).Any(call => call.Domain == "light" && call.Service == "turn_off"),
			"the room goes back to dark at the test's own deadline");
		Assert.IsNull(Field(Latest(house.Ha), "testing_period_id"));
	}

	[TestMethod]
	public void AHoldCarriedOverASave_CountsFromWhenItStarted_UnderTheNewLength()
	{
		using House house = Running(lightState: "off", overrideMinutes: 60);

		DateTimeOffset heldAt = house.Scheduler.Now;
		house.Ha.Trigger(Light, "on", null, AtTheSwitch());
		Assert.AreEqual(nameof(AreaState.OverriddenOn), Field(Latest(house.Ha), "state"));

		house.Advance(TimeSpan.FromMinutes(20));
		house.Host.Save(Document(overrideMinutes: 30));

		JsonElement after = Latest(house.Ha);
		Assert.AreEqual(nameof(AreaState.OverriddenOn), Field(after, "state"));
		Assert.AreEqual(heldAt + TimeSpan.FromMinutes(30), after.GetProperty("next_change_at").GetDateTimeOffset(),
			"thirty minutes from the touch, neither the old hour nor thirty minutes from the save");

		house.Advance(TimeSpan.FromMinutes(9));
		Assert.AreEqual(nameof(AreaState.OverriddenOn), Field(Latest(house.Ha), "state"));

		house.Advance(TimeSpan.FromMinutes(2));
		Assert.AreNotEqual(nameof(AreaState.OverriddenOn), Field(Latest(house.Ha), "state"), "the hold ends at its own deadline");
	}
}
