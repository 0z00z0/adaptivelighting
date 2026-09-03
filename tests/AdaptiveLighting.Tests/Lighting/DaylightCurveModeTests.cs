using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The editing rule that seeds a room's curve dark end from the level of the period that first claims it, in that room.</summary>
[TestClass]
public sealed class DaylightCurveModeTests
{
	private static AreaConfig Room() => new() { AreaId = "kontor" };

	private static RoomLevelOverride Row(AreaConfig room, string periodId, double? brightnessPct = null)
	{
		RoomLevelOverride row = new() { PeriodId = periodId, BrightnessPct = brightnessPct };
		room.Levels.Add(row);
		return row;
	}

	[TestMethod]
	public void FirstPeriodOnTheCurveSeedsHalfItsOwnLevel()
	{
		AreaConfig night = Room();
		RoomLevelOverride nightRow = Row(night, "night", 15);

		Assert.AreNotEqual(7.5d, night.LuxBrightnessMinPct);
		Assert.IsTrue(DaylightCurveMode.Set(night, nightRow, 15, true));
		Assert.AreEqual(7.5d, night.LuxBrightnessMinPct);

		AreaConfig day = Room();
		RoomLevelOverride dayRow = Row(day, "day", 90);

		Assert.AreNotEqual(45d, day.LuxBrightnessMinPct);
		Assert.IsTrue(DaylightCurveMode.Set(day, dayRow, 90, true));
		Assert.AreEqual(45d, day.LuxBrightnessMinPct);
	}

	[TestMethod]
	public void SecondPeriodOnTheCurveLeavesTheDarkEndAlone()
	{
		AreaConfig room = Room();
		RoomLevelOverride nightRow = Row(room, "night", 15);
		RoomLevelOverride dayRow = Row(room, "day", 90);

		DaylightCurveMode.Set(room, nightRow, 15, true);
		Assert.AreEqual(7.5d, room.LuxBrightnessMinPct);

		Assert.IsFalse(DaylightCurveMode.Set(room, dayRow, 90, true));
		Assert.AreEqual(7.5d, room.LuxBrightnessMinPct);
		Assert.AreNotEqual(45d, room.LuxBrightnessMinPct);
	}

	[TestMethod]
	public void AHandDraggedDarkEndSurvivesTheSecondPeriodJoining()
	{
		AreaConfig room = Room();
		RoomLevelOverride nightRow = Row(room, "night", 15);
		RoomLevelOverride dayRow = Row(room, "day", 90);

		DaylightCurveMode.Set(room, nightRow, 15, true);
		room.LuxBrightnessMinPct = 22;

		Assert.IsFalse(DaylightCurveMode.Set(room, dayRow, 90, true));
		Assert.AreEqual(22d, room.LuxBrightnessMinPct);
	}

	[TestMethod]
	public void ReclaimingTheCurveOnAPeriodAlreadyOnItSeedsNothing()
	{
		AreaConfig room = Room();
		RoomLevelOverride nightRow = Row(room, "night", 15);

		DaylightCurveMode.Set(room, nightRow, 15, true);
		room.LuxBrightnessMinPct = 22;

		Assert.IsFalse(DaylightCurveMode.Set(room, nightRow, 15, true));
		Assert.AreEqual(22d, room.LuxBrightnessMinPct);
	}

	[TestMethod]
	public void AnotherRoomsDarkEndIsNeverTouched()
	{
		AreaConfig stated = new() { AreaId = "kitchen", LuxBrightnessMinPct = 30 };
		AreaConfig silent = new() { AreaId = "hall" };

		RoomLevelOverride statedRow = Row(stated, "night", 15);
		RoomLevelOverride silentRow = Row(silent, "night", 15);

		Assert.IsFalse(DaylightCurveMode.Set(stated, statedRow, 15, true), "the room already states its own dark end");
		Assert.AreEqual(30d, stated.LuxBrightnessMinPct);

		Assert.IsTrue(DaylightCurveMode.Set(silent, silentRow, 15, true));
		Assert.AreEqual(7.5d, silent.LuxBrightnessMinPct);
	}

	/// <summary>The seed is a value the room now states for itself, same as a hand edit — leaving the curve does not withdraw it, and readopting does not overwrite it again.</summary>
	[TestMethod]
	public void LeavingTheCurveEverywhereInTheRoomDoesNotClearTheSeededDarkEnd()
	{
		AreaConfig room = Room();
		RoomLevelOverride nightRow = Row(room, "night", 15);
		RoomLevelOverride dayRow = Row(room, "day", 90);

		DaylightCurveMode.Set(room, nightRow, 15, true);
		Assert.AreEqual(7.5d, room.LuxBrightnessMinPct);

		Assert.IsFalse(DaylightCurveMode.Set(room, nightRow, 15, false));
		Assert.IsFalse(DaylightCurveMode.InUse(room.Levels));
		Assert.AreEqual(7.5d, room.LuxBrightnessMinPct, "the seed is now this room's own value, not a curve-only figure");

		Assert.IsFalse(DaylightCurveMode.Set(room, dayRow, 90, true), "the room already states a dark end of its own");
		Assert.AreEqual(7.5d, room.LuxBrightnessMinPct);
	}

	[TestMethod]
	public void ARoomAlreadyRunningTheCurveIsLeftAsItIs()
	{
		AreaConfig room = new() { AreaId = "kitchen", LuxBrightnessMinPct = 18 };
		RoomLevelOverride nightRow = Row(room, "night", 15);
		nightRow.FollowDaylightCurve = true;
		RoomLevelOverride dayRow = Row(room, "day", 90);

		Assert.IsFalse(DaylightCurveMode.Set(room, dayRow, 90, true));
		Assert.AreEqual(18d, room.LuxBrightnessMinPct);
	}

	[TestMethod]
	public void LeavingTheCurveSeedsNothing()
	{
		AreaConfig room = Room();
		RoomLevelOverride nightRow = Row(room, "night", 15);

		DaylightCurveMode.Set(room, nightRow, 15, true);
		room.LuxBrightnessMinPct = 22;

		Assert.IsFalse(DaylightCurveMode.Set(room, nightRow, 15, false));
		Assert.IsFalse(nightRow.FollowDaylightCurve == true);
		Assert.AreEqual(22d, room.LuxBrightnessMinPct);
	}

	[TestMethod]
	public void TheEndsOfTheRangeAreClamped()
	{
		AreaConfig dark = Room();
		Assert.IsTrue(DaylightCurveMode.Set(dark, Row(dark, "night", 0), 0, true));
		Assert.AreEqual(0d, dark.LuxBrightnessMinPct);

		AreaConfig full = Room();
		Assert.IsTrue(DaylightCurveMode.Set(full, Row(full, "night", 100), 100, true));
		Assert.AreEqual(50d, full.LuxBrightnessMinPct);

		// A document edited past the validator's range still seeds inside it.
		AreaConfig over = Room();
		Assert.IsTrue(DaylightCurveMode.Set(over, Row(over, "night", 400), 400, true));
		Assert.AreEqual(100d, over.LuxBrightnessMinPct);

		AreaConfig under = Room();
		Assert.IsTrue(DaylightCurveMode.Set(under, Row(under, "night", -40), -40, true));
		Assert.AreEqual(0d, under.LuxBrightnessMinPct);
	}

	[TestMethod]
	public void TheSeededValueCarriesOneDecimal()
	{
		Assert.AreEqual(7.5d, DaylightCurveMode.DarkEndFor(15));
		Assert.AreEqual(12.3d, DaylightCurveMode.DarkEndFor(24.66));
		Assert.AreEqual(0.1d, DaylightCurveMode.DarkEndFor(0.11));
	}

	[TestMethod]
	public void EverySeededValuePassesValidation()
	{
		foreach (double percent in new[] { 0d, 15d, 50d, 90d, 100d })
		{
			AreaConfig room = Room();
			RoomLevelOverride row = Row(room, "night", percent);

			DaylightCurveMode.Set(room, row, percent, true);

			Assert.IsTrue(
				room.LuxBrightnessMinPct is >= 0 and <= 100,
				$"{percent} seeded {room.LuxBrightnessMinPct}");
			Assert.IsTrue(new AreaSettings().LuxBrightnessMaxPct > room.LuxBrightnessMinPct);
		}
	}
}
