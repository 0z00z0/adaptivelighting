using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The token a page holds against the file: per area for the room page, per document for the settings editor.</summary>
/// <remarks>A per-file token on the room page would be invalidated by every write the engine makes to itself.</remarks>
[TestClass]
public sealed class ConfigStampTests
{
	private static AdaptiveLightingConfig TwoRooms() => new()
	{
		Periods = [new TimePeriodConfig { Name = "day", Start = "06:00", BrightnessPct = 80, ColorTempKelvin = 3500 }],
		Areas =
		[
			new AreaConfig { AreaId = "stue" },
			new AreaConfig { AreaId = "kjokken" }
		]
	};

	private static AreaConfig Room(AdaptiveLightingConfig document, string areaId) =>
		document.Areas.Single(area => string.Equals(area.AreaId, areaId, StringComparison.Ordinal));

	[TestMethod]
	public void OfArea_ChangesWhenThatAreaChanges()
	{
		AdaptiveLightingConfig document = TwoRooms();
		string before = ConfigStamp.OfArea(document, "stue");

		Room(document, "stue").VacancyTimeoutSeconds = 900;

		Assert.AreNotEqual(before, ConfigStamp.OfArea(document, "stue"));
	}

	/// <summary>Why the room page's token is not per file: another room changing is not this room's conflict.</summary>
	[TestMethod]
	public void OfArea_IsUnchangedWhenAnotherAreaChanges()
	{
		AdaptiveLightingConfig document = TwoRooms();
		string before = ConfigStamp.OfArea(document, "stue");

		Room(document, "kjokken").VacancyTimeoutSeconds = 900;

		Assert.AreEqual(before, ConfigStamp.OfArea(document, "stue"));
	}

	/// <summary>Discovery's own write: rooms appear and the auto-discovered flag is set, half a minute after start.</summary>
	[TestMethod]
	public void OfArea_IsUnchangedWhenDiscoveryAddsRoomsAndSetsItsFlag()
	{
		AdaptiveLightingConfig document = TwoRooms();
		string before = ConfigStamp.OfArea(document, "stue");

		document.Areas.Add(new AreaConfig { AreaId = "bad" });
		document.Global.AreasAutoDiscovered = true;
		document.Global.Persons = ["person.espen"];

		Assert.AreEqual(before, ConfigStamp.OfArea(document, "stue"));
		Assert.AreNotEqual(
			ConfigStamp.OfDocument(TwoRooms()),
			ConfigStamp.OfDocument(document),
			"a per-file token would have refused that room page's next save");
	}

	[TestMethod]
	public void OfArea_OfAnAreaTheDocumentDoesNotHave_IsTheSameForEveryMissingArea()
	{
		AdaptiveLightingConfig document = TwoRooms();

		Assert.AreEqual(ConfigStamp.OfArea(document, "gang"), ConfigStamp.OfArea(document, "loft"));
		Assert.AreEqual(ConfigStamp.OfArea(document, "gang"), ConfigStamp.OfArea(document, null));
	}

	/// <summary>An area that has gone is a change to that slot, which is what a removal has to read as.</summary>
	[TestMethod]
	public void OfArea_ChangesWhenTheAreaIsRemoved()
	{
		AdaptiveLightingConfig document = TwoRooms();
		string before = ConfigStamp.OfArea(document, "stue");

		document.Areas.Remove(Room(document, "stue"));

		Assert.AreNotEqual(before, ConfigStamp.OfArea(document, "stue"));
	}

	/// <summary>Two documents holding the same room stamp that room the same, whatever else differs.</summary>
	[TestMethod]
	public void OfArea_ReadsOnlyTheAreaItNames()
	{
		AdaptiveLightingConfig one = TwoRooms();
		AdaptiveLightingConfig two = TwoRooms();

		two.ConfigName = "somebody renamed the house";
		two.Periods.Add(new TimePeriodConfig { Name = "night", Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200 });
		Room(two, "kjokken").Lights = ["light.kjokken"];

		Assert.AreEqual(ConfigStamp.OfArea(one, "stue"), ConfigStamp.OfArea(two, "stue"));
	}

	// ===================== the settings editor's token =====================

	[TestMethod]
	public void OfDocument_ChangesWhenAnyAreaChanges()
	{
		AdaptiveLightingConfig document = TwoRooms();
		string before = ConfigStamp.OfDocument(document);

		Room(document, "kjokken").VacancyTimeoutSeconds = 900;

		Assert.AreNotEqual(before, ConfigStamp.OfDocument(document));
	}

	[TestMethod]
	public void OfDocument_ChangesWhenTheScheduleChanges()
	{
		AdaptiveLightingConfig document = TwoRooms();
		string before = ConfigStamp.OfDocument(document);

		document.Periods[0].BrightnessPct = 55;

		Assert.AreNotEqual(before, ConfigStamp.OfDocument(document));
	}

	[TestMethod]
	public void OfDocument_IsTheSameForTwoIdenticalDocuments() =>
		Assert.AreEqual(ConfigStamp.OfDocument(TwoRooms()), ConfigStamp.OfDocument(TwoRooms()));

	/// <summary>The token is the serialised text, so an empty room list and no room list are two different documents.</summary>
	// This is why PeriodsEditor writes null when the last room comes off: an empty list would show the page as
	// changed against a file that simply omits the key.
	[TestMethod]
	public void OfDocument_TellsAnEmptyRoomListApartFromNoRoomListAtAll()
	{
		AdaptiveLightingConfig emptied = TwoRooms();
		emptied.Periods[0].StartsOnMotionAreas = [];

		Assert.AreNotEqual(ConfigStamp.OfDocument(TwoRooms()), ConfigStamp.OfDocument(emptied));
	}
}
