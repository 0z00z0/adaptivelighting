using AdaptiveLighting.Configuration;
using AdaptiveLighting.Hosting;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The room page's write: one area slot at a time, onto the document as it is on disk now.</summary>
// The page reads the document once and stays open, so anything written in between has to survive its next save.
// Two of those writers are the engine's own: area discovery, and the schema-migrating rewrite inside Reload.
[TestClass]
public sealed class RoomWriteTests
{
	private string _directory = "";
	private string _path = "";

	[TestInitialize]
	public void CreateTempDirectory()
	{
		_directory = Path.Combine(Path.GetTempPath(), $"lighting-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_directory);
		_path = Path.Combine(_directory, "AdaptiveLighting.yaml");
	}

	[TestCleanup]
	public void RemoveTempDirectory()
	{
		if (Directory.Exists(_directory))
			Directory.Delete(_directory, recursive: true);
	}

	private LightingEngineHost BuildHost() =>
		new(new LightingConfigStore(_path, NullLogger<LightingConfigStore>.Instance), NullLoggerFactory.Instance);

	/// <summary>A document the validator accepts, holding the two rooms every test here plays off each other.</summary>
	private static AdaptiveLightingConfig TwoRooms() => new()
	{
		ConfigName = "Adaptive lighting [test]",
		Periods = [new TimePeriodConfig { Name = "day", Start = "06:00", BrightnessPct = 80, ColorTempKelvin = 3500 }],
		Areas =
		[
			new AreaConfig { AreaId = "stue" },
			new AreaConfig { AreaId = "kjokken" }
		]
	};

	private static AreaConfig Room(AdaptiveLightingConfig document, string areaId) =>
		document.Areas.Single(area => string.Equals(area.AreaId, areaId, StringComparison.Ordinal));

	private static AreaConfig? Find(AdaptiveLightingConfig document, string areaId) =>
		document.Areas.FirstOrDefault(area => string.Equals(area.AreaId, areaId, StringComparison.Ordinal));

	/// <summary>Sets the file up and hands back a host with the room page already open on <c>stue</c>.</summary>
	private (LightingEngineHost Host, AdaptiveLightingConfig Page, RoomWriteToken Token) OpenRoomPage()
	{
		LightingEngineHost host = BuildHost();
		Assert.IsTrue(host.Save(TwoRooms()).Written, "the test document has to reach the disk first");

		AdaptiveLightingConfig page = host.Store.Load();

		return (host, page, RoomWrite.Open(page, "stue"));
	}

	/// <summary>Writes a change to a room this page is not editing, the way discovery and the migration do.</summary>
	private static void AnotherWriterChanges(LightingEngineHost host, string areaId, int vacancySeconds)
	{
		AdaptiveLightingConfig other = host.Store.Load();
		Room(other, areaId).VacancyTimeoutSeconds = vacancySeconds;

		Assert.IsTrue(host.Save(other).Written, "the other writer's own save has to succeed for the test to mean anything");
	}

	// ===================== another writer in between =====================

	/// <summary>A page open across another writer's write must not revert it.</summary>
	[TestMethod]
	public void Save_WhileAnotherWriterChangedADifferentRoom_KeepsThatWritersChange()
	{
		(LightingEngineHost host, AdaptiveLightingConfig page, RoomWriteToken token) = OpenRoomPage();

		AnotherWriterChanges(host, "kjokken", 900);

		// One setting corrected on the page, and the debounced autosave fires.
		Room(page, "stue").VacancyTimeoutSeconds = 60;
		RoomWrite.Save(host, token, Room(page, "stue"), "Stue");

		Assert.AreEqual(
			900,
			Room(host.Store.Load(), "kjokken").VacancyTimeoutSeconds,
			"a save from the room page must not revert a room the page never edited");
	}

	[TestMethod]
	public void Save_WhileAnotherWriterChangedADifferentRoom_StillWritesThisRoomsEdit()
	{
		(LightingEngineHost host, AdaptiveLightingConfig page, RoomWriteToken token) = OpenRoomPage();

		AnotherWriterChanges(host, "kjokken", 900);

		Room(page, "stue").VacancyTimeoutSeconds = 60;
		RoomWriteResult write = RoomWrite.Save(host, token, Room(page, "stue"), "Stue");

		Assert.IsTrue(write.Result.Written, "the other writer touched a different room, so this save is not a conflict");
		Assert.AreEqual(60, Room(host.Store.Load(), "stue").VacancyTimeoutSeconds);
	}

	[TestMethod]
	public void Save_WhileAnotherWriterAddedARoom_KeepsThatRoom()
	{
		(LightingEngineHost host, AdaptiveLightingConfig page, RoomWriteToken token) = OpenRoomPage();

		AdaptiveLightingConfig other = host.Store.Load();
		other.Areas.Add(new AreaConfig { AreaId = "bad" });
		Assert.IsTrue(host.Save(other).Written);

		Room(page, "stue").VacancyTimeoutSeconds = 60;
		RoomWrite.Save(host, token, Room(page, "stue"), "Stue");

		Assert.IsNotNull(Find(host.Store.Load(), "bad"), "discovery's rooms must not be reverted by a room page");
	}

	// ===================== the other direction =====================

	[TestMethod]
	public void Save_WhenThisRoomChangedUnderneath_IsRefused()
	{
		(LightingEngineHost host, AdaptiveLightingConfig page, RoomWriteToken token) = OpenRoomPage();

		AnotherWriterChanges(host, "stue", 900);

		Room(page, "stue").VacancyTimeoutSeconds = 60;
		RoomWriteResult write = RoomWrite.Save(host, token, Room(page, "stue"), "Stue");

		Assert.AreEqual(SaveStatus.Conflicted, write.Result.Status);
		Assert.IsFalse(write.Result.Written);
	}

	/// <summary>The page offers no button to press again, so a refusal that does not name the room is unactionable.</summary>
	[TestMethod]
	public void Save_WhenThisRoomChangedUnderneath_NamesTheRoom()
	{
		(LightingEngineHost host, AdaptiveLightingConfig page, RoomWriteToken token) = OpenRoomPage();

		AnotherWriterChanges(host, "stue", 900);

		Room(page, "stue").VacancyTimeoutSeconds = 60;
		RoomWriteResult write = RoomWrite.Save(host, token, Room(page, "stue"), "Stue");

		StringAssert.Contains(write.Result.Message, "Stue", StringComparison.Ordinal);
		StringAssert.Contains(write.Result.Message, "Reload", StringComparison.Ordinal);
	}

	[TestMethod]
	public void Save_WhenThisRoomChangedUnderneath_LeavesTheFileAsTheOtherWriterLeftIt()
	{
		(LightingEngineHost host, AdaptiveLightingConfig page, RoomWriteToken token) = OpenRoomPage();

		AnotherWriterChanges(host, "stue", 900);

		Room(page, "stue").VacancyTimeoutSeconds = 60;
		RoomWrite.Save(host, token, Room(page, "stue"), "Stue");

		Assert.AreEqual(900, Room(host.Store.Load(), "stue").VacancyTimeoutSeconds);
	}

	/// <summary>A refused save must leave the page able to try the next one, so the token cannot move.</summary>
	[TestMethod]
	public void Save_WhenRefused_KeepsTheTokenTheCallerHad()
	{
		(LightingEngineHost host, AdaptiveLightingConfig page, RoomWriteToken token) = OpenRoomPage();

		AnotherWriterChanges(host, "stue", 900);

		RoomWriteResult write = RoomWrite.Save(host, token, Room(page, "stue"), "Stue");

		Assert.AreEqual(token, write.Token);
	}

	// ===================== the page stays open =====================

	/// <summary>The page does not reload after saving, so its token has to follow the write or the second edit is refused.</summary>
	[TestMethod]
	public void Save_TwiceFromOneOpenPage_IsNotRefusedTheSecondTime()
	{
		(LightingEngineHost host, AdaptiveLightingConfig page, RoomWriteToken token) = OpenRoomPage();

		Room(page, "stue").VacancyTimeoutSeconds = 60;
		RoomWriteResult first = RoomWrite.Save(host, token, Room(page, "stue"), "Stue");
		Assert.IsTrue(first.Result.Written);

		Room(page, "stue").VacancyTimeoutSeconds = 90;
		RoomWriteResult second = RoomWrite.Save(host, first.Token, Room(page, "stue"), "Stue");

		Assert.IsTrue(second.Result.Written, "the page's own previous write is not a conflict");
		Assert.AreEqual(90, Room(host.Store.Load(), "stue").VacancyTimeoutSeconds);
	}

	// ===================== the destructive pair, and the area change =====================

	[TestMethod]
	public void Save_WithNoRoom_RemovesOnlyThatRoom()
	{
		(LightingEngineHost host, AdaptiveLightingConfig _, RoomWriteToken token) = OpenRoomPage();

		RoomWriteResult write = RoomWrite.Save(host, token, room: null, "Stue");

		Assert.IsTrue(write.Result.Written);

		AdaptiveLightingConfig disk = host.Store.Load();
		Assert.IsNull(Find(disk, "stue"));
		Assert.IsNotNull(Find(disk, "kjokken"));
	}

	[TestMethod]
	public void Save_WhenTheRoomTakesADifferentArea_MovesTheSlotAndLeavesTheRest()
	{
		(LightingEngineHost host, AdaptiveLightingConfig page, RoomWriteToken token) = OpenRoomPage();

		AreaConfig room = Room(page, "stue");
		room.AreaId = "gang";

		RoomWriteResult write = RoomWrite.Save(host, token, room, "Stue");

		Assert.IsTrue(write.Result.Written);
		Assert.AreEqual("gang", write.Token.AreaId, "the slot follows the area the room now names");

		AdaptiveLightingConfig disk = host.Store.Load();
		Assert.IsNull(Find(disk, "stue"));
		Assert.IsNotNull(Find(disk, "gang"));
		Assert.IsNotNull(Find(disk, "kjokken"));
	}

	/// <summary>An area change is one write, so the page has to be able to save again straight after it.</summary>
	[TestMethod]
	public void Save_AfterAnAreaChange_IsNotRefusedByTheNextSave()
	{
		(LightingEngineHost host, AdaptiveLightingConfig page, RoomWriteToken token) = OpenRoomPage();

		AreaConfig room = Room(page, "stue");
		room.AreaId = "gang";
		RoomWriteResult moved = RoomWrite.Save(host, token, room, "Stue");

		room.VacancyTimeoutSeconds = 60;
		RoomWriteResult again = RoomWrite.Save(host, moved.Token, room, "Gang");

		Assert.IsTrue(again.Result.Written);
		Assert.AreEqual(60, Room(host.Store.Load(), "gang").VacancyTimeoutSeconds);
	}

	// ===================== the pipeline underneath is unchanged =====================

	/// <summary>The scoped write goes through the same host save, so a document the engine cannot run is still refused.</summary>
	[TestMethod]
	public void Save_WithADocumentTheEngineCannotRun_IsRejectedAndWritesNothing()
	{
		(LightingEngineHost host, AdaptiveLightingConfig page, RoomWriteToken token) = OpenRoomPage();

		AreaConfig room = Room(page, "stue");
		room.VacancyTimeoutSeconds = 0;

		RoomWriteResult write = RoomWrite.Save(host, token, room, "Stue");

		Assert.AreEqual(SaveStatus.Rejected, write.Result.Status);
		Assert.IsFalse(write.Result.Validation.IsValid);
		Assert.IsNull(Room(host.Store.Load(), "stue").VacancyTimeoutSeconds, "nothing reached the disk");
	}

	/// <summary>The store normalises on the way out, so a token taken off the caller's object would go stale at once.</summary>
	[TestMethod]
	public void Save_TakesTheNextTokenFromTheFile()
	{
		(LightingEngineHost host, AdaptiveLightingConfig page, RoomWriteToken token) = OpenRoomPage();

		Room(page, "stue").VacancyTimeoutSeconds = 60;
		RoomWriteResult write = RoomWrite.Save(host, token, Room(page, "stue"), "Stue");

		Assert.AreEqual(ConfigStamp.OfArea(host.Store.Load(), "stue"), write.Token.Stamp);
	}
}
