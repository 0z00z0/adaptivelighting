using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     What a room runs instead of the schedule, as the levels card reads and writes it.
/// </summary>
/// <remarks>
///     The two failures worth guarding are both silent. A table that writes a row for every period it draws pins
///     values nobody chose and the schedule stops reaching the room; a table that drops a row for a period that
///     has been renamed throws the room's levels away at the moment they most look like a mistake.
/// </remarks>
[TestClass]
public sealed class RoomLevelsTests
{
	private static List<TimePeriodConfig> Day() =>
	[
		new TimePeriodConfig { Name = "morgen", Start = "06:30", BrightnessPct = 60, ColorTempKelvin = 2700 },
		new TimePeriodConfig { Name = "dag", Start = "09:00", BrightnessPct = 100, ColorTempKelvin = 4500 },
		new TimePeriodConfig { Name = "kveld", Start = "sunset", BrightnessPct = 70, ColorTempKelvin = 2700 },
		new TimePeriodConfig { Name = "natt", Start = "23:00", BrightnessPct = 30, ColorTempKelvin = 2200, MaxBrightnessPct = 20 }
	];

	private static AreaConfig Room() => new() { AreaId = "kontor" };

	/// <summary>A room that says nothing runs the schedule, and every row says so.</summary>
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

	/// <summary>
	///     The two values are independent: overriding one leaves the other following the schedule.
	/// </summary>
	/// <remarks>
	///     The coupling this rules out is the one the schema's own remarks name — filling both in because the
	///     type has two fields, after which a schedule edit silently stops reaching half the room.
	/// </remarks>
	[TestMethod]
	public void Setting_One_Value_Leaves_The_Other_Following_The_Schedule()
	{
		AreaConfig room = Room();
		RoomLevels.SetBrightness(room, "dag", 45);

		RoomLevelRow row = RoomLevels.Rows(Day(), room).Single(r => r.Period == "dag");

		Assert.AreEqual(45, row.BrightnessPct);
		Assert.AreEqual(LevelSource.Room, row.Brightness);
		Assert.AreEqual(4500, row.ColorTempKelvin, "the warmth is still the house's");
		Assert.AreEqual(LevelSource.Schedule, row.Colour);
		Assert.IsNull(room.Levels.Single().ColorTempKelvin, "and it is stored as null, not as today's number");
	}

	/// <summary>Drawing four rows must not write four rows.</summary>
	[TestMethod]
	public void Reading_The_Table_Writes_Nothing()
	{
		AreaConfig room = Room();

		RoomLevels.Rows(Day(), room);
		RoomLevels.Orphans(Day(), room);

		Assert.AreEqual(0, room.Levels.Count);
	}

	/// <summary>
	///     Clearing the last value on a row removes the row, rather than leaving one that says nothing.
	/// </summary>
	/// <remarks>
	///     A row left behind would be counted as an override by everything that reads the list, so the room would
	///     report itself as disagreeing with a schedule it now follows exactly.
	/// </remarks>
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

	/// <summary>Clearing returns the row to the schedule rather than pinning today's number.</summary>
	[TestMethod]
	public void Clearing_Returns_The_Row_To_Whatever_The_Schedule_Says_Next()
	{
		AreaConfig room = Room();
		RoomLevels.SetBrightness(room, "morgen", 20);
		RoomLevels.SetBrightness(room, "morgen", null);

		List<TimePeriodConfig> moved = Day();
		moved[0].BrightnessPct = 85;

		RoomLevelRow row = RoomLevels.Rows(moved, room).Single(r => r.Period == "morgen");

		Assert.AreEqual(85, row.BrightnessPct, "a cleared row follows the schedule's later edits");
		Assert.AreEqual(LevelSource.Schedule, row.Brightness);
	}

	/// <summary>
	///     A room may pin the value the schedule already has, and that is a decision the table must keep showing.
	/// </summary>
	/// <remarks>
	///     Provenance is read off the schema's <c>null</c>, never guessed by comparing numbers: pinning 70 % while
	///     the schedule also says 70 % is a choice taken precisely so a later change to the schedule leaves this
	///     room alone.
	/// </remarks>
	[TestMethod]
	public void A_Value_Equal_To_The_Schedules_Is_Still_The_Rooms_Own()
	{
		AreaConfig room = Room();
		RoomLevels.SetBrightness(room, "kveld", 70);

		RoomLevelRow row = RoomLevels.Rows(Day(), room).Single(r => r.Period == "kveld");

		Assert.AreEqual(LevelSource.Room, row.Brightness);
		Assert.IsTrue(row.IsOwn);
	}

	/// <summary>A period the room names but the schedule no longer has is reported, not dropped.</summary>
	[TestMethod]
	public void A_Renamed_Period_Leaves_An_Orphan_That_Is_Shown_And_Named()
	{
		AreaConfig room = Room();
		RoomLevels.SetBrightness(room, "kveldstemning", 25);

		List<TimePeriodConfig> day = Day();

		Assert.IsFalse(RoomLevels.Rows(day, room).Any(row => row.Period == "kveldstemning"));

		RoomLevelOrphan orphan = RoomLevels.Orphans(day, room).Single();

		Assert.AreEqual("kveldstemning", orphan.Period);
		Assert.AreEqual(25, orphan.BrightnessPct);
		Assert.IsTrue(orphan.Says.Contains("25", StringComparison.Ordinal), "it says what removing it would throw away");
	}

	/// <summary>And it can be removed, so an override that survived a rename is not a trap.</summary>
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
	///     Matching follows the engine's own precedent for a period name, which is case-insensitive.
	/// </summary>
	/// <remarks>
	///     Ordinal matching would make a period recased in the schedule orphan every room's levels for it, while
	///     the engine went on applying them — a surface disagreeing with the engine about which rows are live.
	/// </remarks>
	[TestMethod]
	public void A_Recased_Period_Name_Still_Finds_The_Rooms_Row()
	{
		AreaConfig room = Room();
		RoomLevels.SetBrightness(room, "Kveld", 40);

		Assert.AreEqual(LevelSource.Room, RoomLevels.Rows(Day(), room).Single(r => r.Period == "kveld").Brightness);
		Assert.AreEqual(0, RoomLevels.Orphans(Day(), room).Count);
	}

	/// <summary>Editing the same period twice edits one row rather than adding another.</summary>
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

	/// <summary>
	///     A row says when the period's own limits would hold the room somewhere else.
	/// </summary>
	/// <remarks>
	///     The period's caps still apply on top of a room's replacement, so a row naming 60 % under a night period
	///     capped at 20 % would name a level the room never reaches.
	/// </remarks>
	[TestMethod]
	public void A_Row_Says_When_The_Periods_Own_Cap_Holds_It_Lower()
	{
		AreaConfig room = Room();
		RoomLevels.SetBrightness(room, "natt", 60);

		RoomLevelRow capped = RoomLevels.Rows(Day(), room).Single(r => r.Period == "natt");

		Assert.IsNotNull(capped.Limit);
		Assert.IsTrue(capped.Limit!.Contains("20", StringComparison.Ordinal), "and names the ceiling that holds it");

		RoomLevels.SetBrightness(room, "natt", 10);

		Assert.IsNull(RoomLevels.Rows(Day(), room).Single(r => r.Period == "natt").Limit,
			"a level under the cap is not held by it, so nothing is said");
	}

	/// <summary>A floor is reported the same way, and only when it bites.</summary>
	[TestMethod]
	public void A_Row_Says_When_The_Periods_Own_Floor_Lifts_It()
	{
		List<TimePeriodConfig> day = Day();
		day[0].MinBrightnessPct = 40;

		AreaConfig room = Room();
		RoomLevels.SetBrightness(room, "morgen", 5);

		Assert.IsTrue(RoomLevels.Rows(day, room).Single(r => r.Period == "morgen").Limit!.Contains("40", StringComparison.Ordinal));
	}

	/// <summary>An empty row already in the file is neither counted nor reported as an orphan.</summary>
	[TestMethod]
	public void A_Row_That_Says_Nothing_Is_Ignored_Wherever_It_Came_From()
	{
		AreaConfig room = Room();
		room.Levels.Add(new RoomLevelOverride { Period = "spøkelse" });

		Assert.AreEqual(0, RoomLevels.OwnCount(Day(), room));
		Assert.AreEqual(0, RoomLevels.Orphans(Day(), room).Count);
	}

	/// <summary>
	///     The count is of periods on screen, so it can never disagree with the marks in the table.
	/// </summary>
	/// <remarks>
	///     Counting the stored rows would include one kept for a period that no longer exists, and the card would
	///     read "1 of 4 periods are this room's own" over four rows with no mark on any of them.
	/// </remarks>
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

	/// <summary>A hand-edited file with two rows for one period reads the first, stably.</summary>
	[TestMethod]
	public void Two_Rows_For_One_Period_Read_As_The_First()
	{
		AreaConfig room = Room();
		room.Levels.Add(new RoomLevelOverride { Period = "dag", BrightnessPct = 10 });
		room.Levels.Add(new RoomLevelOverride { Period = "dag", BrightnessPct = 90 });

		Assert.AreEqual(10, RoomLevels.Rows(Day(), room).Single(r => r.Period == "dag").BrightnessPct);
	}

	/// <summary>
	///     A row carrying values but no period name reaches the surface, and is named for what it is.
	/// </summary>
	/// <remarks>
	///     Normalisation drops a row that says nothing; a row that says something under no name survives it. Shown
	///     as its blank name it would be a remove button beside an empty space.
	/// </remarks>
	[TestMethod]
	public void A_Row_With_No_Period_Name_Is_Reported_And_Called_Something()
	{
		AreaConfig room = Room();
		room.Levels.Add(new RoomLevelOverride { Period = "", BrightnessPct = 15 });

		RoomLevelOrphan orphan = RoomLevels.Orphans(Day(), room).Single();

		Assert.AreEqual("", orphan.Period, "the key it is removed by is still its own");
		Assert.IsTrue(orphan.Name.Length > 0, "but it is not drawn as a blank");
	}

	/// <summary>A schedule with no periods has nothing to draw, and no room disagrees with it.</summary>
	[TestMethod]
	public void An_Empty_Schedule_Draws_No_Rows()
	{
		Assert.AreEqual(0, RoomLevels.Rows([], Room()).Count);
		Assert.AreEqual(0, RoomLevels.Orphans([], null).Count);
		Assert.AreEqual(0, RoomLevels.OwnCount([], null));
	}
}
