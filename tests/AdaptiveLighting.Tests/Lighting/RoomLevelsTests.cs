using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     What a room runs instead of the schedule, as the levels card reads and writes it.
/// </summary>
[TestClass]
public sealed class RoomLevelsTests
{
	private static List<TimePeriodConfig> Day() =>
	[
		new TimePeriodConfig { Name = "morgen", Start = "06:30", BrightnessPct = 60, ColorTempKelvin = 2700 },
		new TimePeriodConfig { Name = "dag", Start = "09:00", BrightnessPct = 100, ColorTempKelvin = 4500 },
		new TimePeriodConfig { Name = "kveld", Start = "sunset", BrightnessPct = 70, ColorTempKelvin = 2700 },
		new TimePeriodConfig { Name = "natt", Start = "23:00", BrightnessPct = 30, ColorTempKelvin = 2200 }
	];

	private static AreaConfig Room() => new() { AreaId = "kontor" };

	/// <summary>
	///     Regression: the card and <c>CircadianCalculator.LevelsOf</c> disagreed here. Both now skip empty rows
	///     before taking the first match. Reachable only on a hand-edited file; save normalises empty rows away.
	/// </summary>
	[TestMethod]
	public void A_Cleared_Row_Does_Not_Hide_The_Real_One_Below_It()
	{
		AreaConfig room = Room();
		room.Levels =
		[
			new RoomLevelOverride { PeriodId = "kveld" },
			new RoomLevelOverride { PeriodId = "kveld", BrightnessPct = 8 }
		];

		RoomLevelRow evening = RoomLevels.Rows(Day(), room).Single(row => row.PeriodId == "kveld");

		Assert.AreEqual(8, evening.BrightnessPct, "the engine runs this room at 8 %, so the card has to say 8 %");
		Assert.AreEqual(LevelSource.Room, evening.Brightness,
			"and has to call it the room's, or there is no control to undo it with");
	}

	[TestMethod]
	public void A_Room_With_No_Levels_Of_Its_Own_Shows_The_Schedule_Throughout()
	{
		IReadOnlyList<RoomLevelRow> rows = RoomLevels.Rows(Day(), Room());

		Assert.AreEqual(4, rows.Count, "every period gets a row, whether or not the room disagrees with it");

		foreach (RoomLevelRow row in rows)
		{
			Assert.AreEqual(LevelSource.Schedule, row.Brightness);
			Assert.AreEqual(LevelSource.Schedule, row.Colour);
			Assert.IsFalse(row.IsOwn);
		}

		Assert.AreEqual(100, rows[1].BrightnessPct);
		Assert.AreEqual(4500, rows[1].ColorTempKelvin);
	}

	[TestMethod]
	public void Setting_One_Value_Leaves_The_Other_Following_The_Schedule()
	{
		AreaConfig room = Room();
		RoomLevels.SetBrightness(room, "dag", 45);

		RoomLevelRow row = RoomLevels.Rows(Day(), room).Single(r => r.PeriodId == "dag");

		Assert.AreEqual(45, row.BrightnessPct);
		Assert.AreEqual(LevelSource.Room, row.Brightness);
		Assert.AreEqual(4500, row.ColorTempKelvin, "the warmth is still the house's");
		Assert.AreEqual(LevelSource.Schedule, row.Colour);
		Assert.IsNull(room.Levels.Single().ColorTempKelvin, "and it is stored as null, not as today's number");
	}

	[TestMethod]
	public void Reading_The_Table_Writes_Nothing()
	{
		AreaConfig room = Room();

		RoomLevels.Rows(Day(), room);
		RoomLevels.Orphans(Day(), room);

		Assert.AreEqual(0, room.Levels.Count);
	}

	[TestMethod]
	public void Clearing_The_Last_Value_Drops_The_Row()
	{
		AreaConfig room = Room();

		RoomLevels.SetBrightness(room, "kveld", 40);
		RoomLevels.SetColorTemp(room, "kveld", 2200);
		Assert.AreEqual(1, room.Levels.Count);

		RoomLevels.SetBrightness(room, "kveld", null);
		Assert.AreEqual(1, room.Levels.Count, "the warmth is still stated, so the row stays");

		RoomLevels.SetColorTemp(room, "kveld", null);
		Assert.AreEqual(0, room.Levels.Count);
		Assert.AreEqual(0, RoomLevels.OwnCount(Day(), room));
	}

	[TestMethod]
	public void Clearing_Returns_The_Row_To_Whatever_The_Schedule_Says_Next()
	{
		AreaConfig room = Room();
		RoomLevels.SetBrightness(room, "morgen", 20);
		RoomLevels.SetBrightness(room, "morgen", null);

		List<TimePeriodConfig> moved = Day();
		moved[0].BrightnessPct = 85;

		RoomLevelRow row = RoomLevels.Rows(moved, room).Single(r => r.PeriodId == "morgen");

		Assert.AreEqual(85, row.BrightnessPct, "a cleared row follows the schedule's later edits");
		Assert.AreEqual(LevelSource.Schedule, row.Brightness);
	}

	/// <summary>Provenance comes from the schema's null. Comparing numbers would erase a room that pins 70 % over 70 %.</summary>
	[TestMethod]
	public void A_Value_Equal_To_The_Schedules_Is_Still_The_Rooms_Own()
	{
		AreaConfig room = Room();
		RoomLevels.SetBrightness(room, "kveld", 70);

		RoomLevelRow row = RoomLevels.Rows(Day(), room).Single(r => r.PeriodId == "kveld");

		Assert.AreEqual(LevelSource.Room, row.Brightness);
		Assert.IsTrue(row.IsOwn);
	}

	[TestMethod]
	public void A_Renamed_Period_Leaves_An_Orphan_That_Is_Shown_And_Named()
	{
		AreaConfig room = Room();
		RoomLevels.SetBrightness(room, "kveldstemning", 25);

		List<TimePeriodConfig> day = Day();

		Assert.IsFalse(RoomLevels.Rows(day, room).Any(row => row.PeriodId == "kveldstemning"));

		RoomLevelOrphan orphan = RoomLevels.Orphans(day, room).Single();

		Assert.AreEqual("kveldstemning", orphan.PeriodId);
		Assert.AreEqual(25, orphan.BrightnessPct);
		Assert.IsTrue(orphan.Says.Contains("25", StringComparison.Ordinal), "it says what removing it would throw away");
	}

	[TestMethod]
	public void An_Orphan_Can_Be_Removed()
	{
		AreaConfig room = Room();
		RoomLevels.SetColorTemp(room, "gammelt navn", 3000);

		Assert.IsTrue(RoomLevels.Remove(room, "gammelt navn"));
		Assert.AreEqual(0, room.Levels.Count);
		Assert.IsFalse(RoomLevels.Remove(room, "gammelt navn"), "and says so when there was nothing to remove");
	}

	/// <summary>
	///     Period names match case-insensitively, as the engine matches them. Ordinal matching would orphan every
	///     row for a recased period while the engine went on applying it.
	/// </summary>
	[TestMethod]
	public void A_Recased_Period_Name_Still_Finds_The_Rooms_Row()
	{
		AreaConfig room = Room();
		RoomLevels.SetBrightness(room, "Kveld", 40);

		Assert.AreEqual(LevelSource.Room, RoomLevels.Rows(Day(), room).Single(r => r.PeriodId == "kveld").Brightness);
		Assert.AreEqual(0, RoomLevels.Orphans(Day(), room).Count);
	}

	[TestMethod]
	public void Two_Edits_To_One_Period_Are_One_Row()
	{
		AreaConfig room = Room();

		RoomLevels.SetBrightness(room, "dag", 40);
		RoomLevels.SetColorTemp(room, "DAG", 3000);
		RoomLevels.SetBrightness(room, "dag", 45);

		RoomLevelOverride stored = room.Levels.Single();

		Assert.AreEqual(45, stored.BrightnessPct);
		Assert.AreEqual(3000, stored.ColorTempKelvin);
	}



	[TestMethod]
	public void A_Row_That_Says_Nothing_Is_Ignored_Wherever_It_Came_From()
	{
		AreaConfig room = Room();
		room.Levels.Add(new RoomLevelOverride { PeriodId = "spøkelse" });

		Assert.AreEqual(0, RoomLevels.OwnCount(Day(), room));
		Assert.AreEqual(0, RoomLevels.Orphans(Day(), room).Count);
	}

	/// <summary>The count is of periods on screen, so it cannot disagree with the marks in the table.</summary>
	[TestMethod]
	public void An_Orphan_Is_Not_Counted_Among_The_Periods_On_Screen()
	{
		AreaConfig room = Room();
		RoomLevels.SetBrightness(room, "kveldstemning", 25);

		Assert.AreEqual(0, RoomLevels.OwnCount(Day(), room), "no row on screen carries a mark");
		Assert.AreEqual(1, RoomLevels.Orphans(Day(), room).Count, "and the orphan is counted where it belongs");

		RoomLevels.SetBrightness(room, "dag", 45);

		Assert.AreEqual(1, RoomLevels.OwnCount(Day(), room));
	}

	[TestMethod]
	public void Two_Rows_For_One_Period_Read_As_The_First()
	{
		AreaConfig room = Room();
		room.Levels.Add(new RoomLevelOverride { PeriodId = "dag", BrightnessPct = 10 });
		room.Levels.Add(new RoomLevelOverride { PeriodId = "dag", BrightnessPct = 90 });

		Assert.AreEqual(10, RoomLevels.Rows(Day(), room).Single(r => r.PeriodId == "dag").BrightnessPct);
	}

	/// <summary>Normalisation drops a row that says nothing; a row that says something under no name survives it.</summary>
	[TestMethod]
	public void A_Row_With_No_Period_Name_Is_Reported_And_Called_Something()
	{
		AreaConfig room = Room();
		room.Levels.Add(new RoomLevelOverride { PeriodId = "", BrightnessPct = 15 });

		RoomLevelOrphan orphan = RoomLevels.Orphans(Day(), room).Single();

		Assert.AreEqual("", orphan.PeriodId, "the key it is removed by is still its own");
		Assert.IsTrue(orphan.Name.Length > 0, "but it is not drawn as a blank");
	}

	[TestMethod]
	public void An_Empty_Schedule_Draws_No_Rows()
	{
		Assert.AreEqual(0, RoomLevels.Rows([], Room()).Count);
		Assert.AreEqual(0, RoomLevels.Orphans([], null).Count);
		Assert.AreEqual(0, RoomLevels.OwnCount([], null));
	}
}
