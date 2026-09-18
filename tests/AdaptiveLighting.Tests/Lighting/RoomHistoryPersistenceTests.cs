using System.Text.Json;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Hosting;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

using NetDaemon.HassModel;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Each room's history: whether it survives a restart, and what a room with no note entry shows instead.</summary>
// Driven through the host's real attach and save paths, read back from the event the room publishes, as the
// pages read it - the same approach CarryOverOnSaveTests uses for the in-memory carry-over.
[TestClass]
public sealed class RoomHistoryPersistenceTests
{
	private const string Light = "light.test_room";
	private const string Motion = "binary_sensor.test_room_motion";

	private static readonly DateTimeOffset Start = new(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);

	private string _directory = "";

	[TestInitialize]
	public void CreateTempDirectory()
	{
		_directory = Path.Combine(Path.GetTempPath(), $"room-history-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_directory);
	}

	[TestCleanup]
	public void RemoveTempDirectory()
	{
		if (Directory.Exists(_directory))
			Directory.Delete(_directory, recursive: true);
	}

	private string ConfigPath => Path.Combine(_directory, "AdaptiveLighting.yaml");

	private static AdaptiveLightingConfig Document() => new()
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
				OverrideDurationMinutes = 60,
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

	[TestMethod]
	public void History_Survives_A_Restart()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "on");
		ha.SetState(Motion, "off");

		TestScheduler scheduler = new();
		scheduler.AdvanceTo(Start.Ticks);

		LightingConfigStore store = new(ConfigPath, NullLogger<LightingConfigStore>.Instance);
		LightingEngineHost host = new(store, NullLoggerFactory.Instance);
		host.Attach(ha, new FakeHaRegistry(), scheduler);

		SaveResult result = host.Save(Document());
		Assert.AreEqual(1, host.RunningAreaCount, result.Message);

		ha.Trigger(Motion, "on");
		scheduler.AdvanceBy(TimeSpan.FromMinutes(1).Ticks);
		ha.Trigger(Light, "off", null, AtTheSwitch());
		scheduler.AdvanceBy(TimeSpan.FromMinutes(1).Ticks);

		JsonElement before = Latest(ha);
		string? motionBefore = Field(before, "last_motion_at");
		string? changedAtBefore = Field(before, "changed_at");
		string? changedByBefore = Field(before, "changed_by");
		Assert.IsNotNull(motionBefore);
		Assert.IsNotNull(changedAtBefore);
		Assert.IsNotNull(changedByBefore);

		// The shutdown write: nothing here waits for the coalesced interval.
		host.Dispose();

		FakeHaContext restartedHa = new();
		restartedHa.SetState(Light, "on");
		restartedHa.SetState(Motion, "off");

		TestScheduler restartedScheduler = new();
		restartedScheduler.AdvanceTo(Start.AddMinutes(5).Ticks);

		LightingEngineHost restarted = new(
			new LightingConfigStore(ConfigPath, NullLogger<LightingConfigStore>.Instance),
			NullLoggerFactory.Instance);

		using (restarted)
		{
			restarted.Attach(restartedHa, new FakeHaRegistry(), restartedScheduler);
			SaveResult reloaded = restarted.Reload();
			Assert.AreEqual(1, restarted.RunningAreaCount, reloaded.Message);

			JsonElement after = Latest(restartedHa);
			Assert.AreEqual(motionBefore, Field(after, "last_motion_at"), "last movement survives a restart");
			Assert.AreEqual(changedAtBefore, Field(after, "changed_at"), "so does when the lights were last changed");
			Assert.AreEqual(changedByBefore, Field(after, "changed_by"), "and who changed them");
		}
	}

	[TestMethod]
	public void ARoomWithNoHistoryNote_FallsBackToTheSensorsOwnLastChanged_Approximate()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "off");

		DateTimeOffset sensorLastChanged = Start.AddHours(-2);
		ha.SetStateReportedAt(Motion, "off", sensorLastChanged);

		TestScheduler scheduler = new();
		scheduler.AdvanceTo(Start.Ticks);

		LightingEngineHost host = new(
			new LightingConfigStore(ConfigPath, NullLogger<LightingConfigStore>.Instance),
			NullLoggerFactory.Instance);

		using (host)
		{
			host.Attach(ha, new FakeHaRegistry(), scheduler);

			// The very first save: nothing has ever run, so there is no carried history and no note on disk.
			SaveResult result = host.Save(Document());
			Assert.AreEqual(1, host.RunningAreaCount, result.Message);

			JsonElement report = Latest(ha);
			Assert.AreEqual(
				sensorLastChanged.ToString("O"),
				DateTimeOffset.Parse(Field(report, "last_motion_at")!).ToString("O"),
				"last movement falls back to the motion sensor's own last-changed");
			Assert.IsNull(Field(report, "changed_at"), "no change is known, so it stays empty");
			Assert.IsNull(Field(report, "changed_by"), "and so does who");
		}
	}
}
