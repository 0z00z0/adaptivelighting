using System.Text.Json;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Hosting;
using AdaptiveLighting.Persistence;

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
				Id = "room_001",
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

	/// <summary>Where the room-history note lands: the state subfolder, named after the document's own stem.</summary>
	private string HistoryFilePath => Path.Combine(
		_directory,
		JsonNoteFile.StateFolderName,
		Path.GetFileNameWithoutExtension(ConfigPath) + RoomHistoryStore.NameSuffix);

	/// <summary>The note's <c>rooms</c> object, read straight off disk rather than through the store.</summary>
	private JsonElement RoomsInHistoryFile()
	{
		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(HistoryFilePath));

		return document.RootElement.GetProperty("rooms").Clone();
	}

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

	[TestMethod]
	public void RoomHistory_ForARoomRemovedFromTheDocument_IsPrunedOnTheNextSave()
	{
		const string LightB = "light.test_room_b";
		const string MotionB = "binary_sensor.test_room_b_motion";

		FakeHaContext ha = new();
		ha.SetState(Light, "on");
		ha.SetState(Motion, "off");
		ha.SetState(LightB, "on");
		ha.SetState(MotionB, "off");

		TestScheduler scheduler = new();
		scheduler.AdvanceTo(Start.Ticks);

		AdaptiveLightingConfig twoRooms = Document();
		twoRooms.Areas.Add(new AreaConfig
		{
			Id = "room_002",
			Name = "Room B",
			Lights = [LightB],
			MotionSensors = [MotionB],
			OverrideDurationMinutes = 60,
			OverrideUntilVacant = false
		});

		using LightingEngineHost host = new(
			new LightingConfigStore(ConfigPath, NullLogger<LightingConfigStore>.Instance),
			NullLoggerFactory.Instance);
		host.Attach(ha, new FakeHaRegistry(), scheduler);

		SaveResult withBothRooms = host.Save(twoRooms);
		Assert.AreEqual(2, host.RunningAreaCount, withBothRooms.Message);

		AdaptiveLightingConfig roomAOnly = Document();

		SaveResult withRoomBRemoved = host.Save(roomAOnly);
		Assert.AreEqual(1, host.RunningAreaCount, withRoomBRemoved.Message);

		JsonElement rooms = RoomsInHistoryFile();
		Assert.IsTrue(rooms.TryGetProperty("room_001", out _), "the room kept in the document keeps its history");
		Assert.IsFalse(rooms.TryGetProperty("room_002", out _), "the room removed from the document loses its history on the next save");
	}

	/// <summary>What <see cref="LightingEngineHost"/> prunes by: a room's area id, else its room id, so a rename is never a removal.</summary>
	[TestMethod]
	public void DocumentRoomKeys_DoNotChangeWhenOnlyARoomsNameDoes_WithOrWithoutAnArea()
	{
		AdaptiveLightingConfig before = new()
		{
			Areas = [new AreaConfig { AreaId = "room_a", Name = "Test room" }, new AreaConfig { Id = "room_001", Name = "Shed" }]
		};
		AdaptiveLightingConfig renamed = new()
		{
			Areas = [new AreaConfig { AreaId = "room_a", Name = "Test room, renamed" }, new AreaConfig { Id = "room_001", Name = "Shed, renamed" }]
		};

		HashSet<string> keysBefore = LightingOrchestrator.DocumentRoomKeys(before);

		CollectionAssert.AreEquivalent(new[] { "room_a", "room_001" }, keysBefore.ToList());
		CollectionAssert.AreEquivalent(keysBefore.ToList(), LightingOrchestrator.DocumentRoomKeys(renamed).ToList());
	}
}
