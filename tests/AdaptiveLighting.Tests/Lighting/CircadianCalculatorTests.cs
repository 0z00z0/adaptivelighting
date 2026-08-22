using System.Collections.Concurrent;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The circadian table: which period is active, what it targets, how the caps bite, and how the blend runs.</summary>
[TestClass]
public sealed class CircadianCalculatorTests
{
	private static readonly List<TimePeriodConfig> Table =
	[
		new() { Name = "day", Start = "07:00", BrightnessPct = 90, ColorTempKelvin = 4500 },
		new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
		new() { Name = "night", Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200 }
	];

	// At() builds an instant at +00:00, so every calculator below is told the household is UTC too. Left to
	// TimeZoneInfo.Local these assertions would mean a different hour on every developer's box and a third thing
	// on CI. The conversion itself is asserted in TheScheduleIsAWallClock_NotUtc, which names a real offset.
	private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 1, 15, hour, minute, 0, TimeSpan.Zero);

	private static CircadianCalculator Stepped(
		IReadOnlyList<TimePeriodConfig>? periods = null,
		SunTimes? sun = null,
		IReadOnlyList<RoomLevelOverride>? levels = null) =>
		new(periods ?? Table, new GlobalConfig { SmoothTransitions = false }, () => sun ?? SunTimes.Unknown, levels,
			zone: TimeZoneInfo.Utc);

	/// <summary>Only the physical bound applies; there is no per-period floor or ceiling.</summary>
	[TestMethod]
	public void Clamp_Honours_The_Physical_Range_And_Nothing_Else()
	{
		var target = Stepped().GetTarget(At(23))!;

		Assert.AreEqual(100d, target.Clamp(140));
		Assert.AreEqual(0d, target.Clamp(-5));
		Assert.AreEqual(15d, target.Clamp(15), "and anything a lamp can actually do passes through untouched");
	}

	[TestMethod]
	public void The_Active_Period_Is_The_Last_Boundary_At_Or_Before_Now()
	{
		var calc = Stepped();

		Assert.AreEqual("day", calc.GetTarget(At(7))!.PeriodName, "a boundary is inclusive of its own instant");
		Assert.AreEqual("day", calc.GetTarget(At(12))!.PeriodName);
		Assert.AreEqual("evening", calc.GetTarget(At(18))!.PeriodName);
		Assert.AreEqual("evening", calc.GetTarget(At(22, 29))!.PeriodName);
		Assert.AreEqual("night", calc.GetTarget(At(22, 30))!.PeriodName);
	}

	/// <summary>A Start is a household wall clock, and the instant the scheduler hands over is not.</summary>
	/// <remarks>
	///     IScheduler.Now is at +00:00, so its TimeOfDay is UTC and night@22:30 would begin at 00:30. A fixed +02:00
	///     zone, not a named one, so this asserts the same thing on a box with no tz database.
	/// </remarks>
	[TestMethod]
	public void The_Schedule_Is_A_Wall_Clock_Not_UTC()
	{
		TimeZoneInfo plusTwo = TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");

		CircadianCalculator calc = new(
			Table, new GlobalConfig { SmoothTransitions = false }, () => SunTimes.Unknown, zone: plusTwo);

		// 21:00Z is 23:00 in the household, which is night. Read as UTC it is 21:00, which is evening.
		Assert.AreEqual("night", calc.GetTarget(At(21))!.PeriodName, "23:00 local is night, whatever the offset");
		Assert.AreEqual("night", calc.ActivePeriodId(At(21)));

		// 20:29Z is 22:29 local, the minute before the boundary; the period must not arrive early either.
		Assert.AreEqual("evening", calc.GetTarget(At(20, 29))!.PeriodName);
		Assert.AreEqual("night", calc.GetTarget(At(20, 30))!.PeriodName, "22:30 local exactly");
	}

	[TestMethod]
	public void Before_The_First_Boundary_The_Table_Wraps_To_Yesterdays_Last_Period()
	{
		var calc = Stepped();

		Assert.AreEqual("night", calc.GetTarget(At(3))!.PeriodName, "03:00 is still last night, not an undefined hole");
	}

	[TestMethod]
	public void A_Period_Reports_Its_Own_Levels()
	{
		var target = Stepped().GetTarget(At(20))!;

		Assert.AreEqual("evening", target.PeriodName);
		Assert.AreEqual(70d, target.BrightnessPct);
		Assert.AreEqual(2700, target.ColorTempKelvin);
	}




	[TestMethod]
	public void An_Empty_Table_Resolves_Nothing_Rather_Than_Guessing()
	{
		Assert.IsNull(Stepped([]).GetTarget(At(20)));
	}

	[TestMethod]
	public void A_Period_With_An_Unparseable_Start_Is_Dropped_And_The_Rest_Still_Cover_The_Day()
	{
		var table = new List<TimePeriodConfig>
		{
			new() { Name = "broken", Start = "half past tea", BrightnessPct = 1 },
			new() { Name = "day", Start = "07:00", BrightnessPct = 90 }
		};

		Assert.AreEqual("day", Stepped(table).GetTarget(At(20))!.PeriodName);
	}

	// ===================== sun-anchored boundaries =====================

	[TestMethod]
	public void A_Sun_Anchored_Boundary_Is_Placed_From_The_Days_Sun_Times()
	{
		var table = new List<TimePeriodConfig>
		{
			new() { Name = "day", Start = "sunrise", BrightnessPct = 90 },
			new() { Name = "evening", Start = "sunset-01:00", BrightnessPct = 70 }
		};
		var sun = new SunTimes(new TimeOnly(9, 15), new TimeOnly(15, 45));
		var calc = Stepped(table, sun);

		Assert.AreEqual("evening", calc.GetTarget(At(14, 50))!.PeriodName, "an hour before a 15:45 sunset is 14:45");
		Assert.AreEqual("day", calc.GetTarget(At(14, 40))!.PeriodName);
		Assert.AreEqual("day", calc.GetTarget(At(9, 15))!.PeriodName);
	}

	[TestMethod]
	public void A_Sun_Boundary_That_Cannot_Be_Placed_Is_Dropped_Not_Guessed()
	{
		var table = new List<TimePeriodConfig>
		{
			new() { Name = "day", Start = "sunrise", BrightnessPct = 90 },
			new() { Name = "night", Start = "22:30", BrightnessPct = 15 }
		};

		// Polar night: no sunrise to anchor to, so the fixed period covers the day alone.
		Assert.AreEqual("night", Stepped(table, SunTimes.Unknown).GetTarget(At(12))!.PeriodName);
	}

	[TestMethod]
	public void A_Dropped_Period_Is_Surfaced_With_Its_Reason_And_Only_Once()
	{
		var table = new List<TimePeriodConfig>
		{
			new() { Name = "broken", Start = "half past tea", BrightnessPct = 1 },
			new() { Name = "dawn", Start = "sunrise", BrightnessPct = 90 },
			new() { Name = "night", Start = "22:30", BrightnessPct = 15 }
		};

		var raised = new List<DroppedPeriod>();
		var calc = new CircadianCalculator(table, new GlobalConfig { SmoothTransitions = false }, () => SunTimes.Unknown);
		calc.PeriodDropped += raised.Add;

		// An unparseable Start is known at construction, before any subscriber exists, so it surfaces on the
		// DroppedPeriods property. An unplaceable sun anchor surfaces on evaluation, through the event.
		CollectionAssert.Contains(
			calc.DroppedPeriods.ToList(),
			new DroppedPeriod("broken", "half past tea", PeriodDropReason.Unparseable),
			"an unparseable Start is surfaced up front, so a vanished period is not a silent hole");

		calc.GetTarget(At(12));
		CollectionAssert.Contains(
			raised,
			new DroppedPeriod("dawn", "sunrise", PeriodDropReason.Unresolvable),
			"a sun-anchored period with no sun data is surfaced once it is evaluated");

		for (var i = 0; i < 1440; i++)
			calc.GetTarget(At(12));

		Assert.AreEqual(1, raised.Count(drop => drop.PeriodName == "dawn"),
			"a persistently-unresolvable period is surfaced once, not on every tick");
	}

	[TestMethod]
	public void An_All_Sun_Table_During_Polar_Night_Resolves_Nothing()
	{
		var table = new List<TimePeriodConfig> { new() { Name = "day", Start = "sunrise" } };

		Assert.IsNull(Stepped(table, SunTimes.Unknown).GetTarget(At(12)),
			"a caller that gets null must command nothing; guessing a target here would be worse than doing nothing");
	}

	// ===================== blending =====================

	private static CircadianCalculator Blended(int blendMinutes = 30, IReadOnlyList<RoomLevelOverride>? levels = null) =>
		new(Table, new GlobalConfig { SmoothTransitions = true, BlendMinutes = blendMinutes }, () => SunTimes.Unknown, levels,
			zone: TimeZoneInfo.Utc);

	[TestMethod]
	public void Halfway_Through_The_Blend_The_Target_Is_Halfway_Between_The_Periods()
	{
		// 18:15 is 15 of 30 blend minutes past the boundary: halfway from day (90/4500) to evening (70/2700).
		var target = Blended().GetTarget(At(18, 15))!;

		Assert.AreEqual("evening", target.PeriodName, "the period being arrived at is the one that names the target");
		Assert.AreEqual(80, target.BrightnessPct, 0.001);
		Assert.AreEqual(3600, target.ColorTempKelvin);
	}

	[TestMethod]
	public void At_The_Boundary_The_Blend_Still_Reads_The_Previous_Periods_Levels()
	{
		var target = Blended().GetTarget(At(18))!;

		Assert.AreEqual("evening", target.PeriodName);
		Assert.AreEqual(90, target.BrightnessPct, 0.001, "the window trails the boundary rather than straddling it");
	}

	[TestMethod]
	public void Past_The_Blend_Window_The_Period_Has_Fully_Arrived()
	{
		Assert.AreEqual(70, Blended().GetTarget(At(18, 30))!.BrightnessPct, 0.001);
		Assert.AreEqual(70, Blended().GetTarget(At(20))!.BrightnessPct, 0.001);
	}

	[TestMethod]
	public void A_Blend_Across_Midnight_Interpolates_From_The_Wrapped_Previous_Period()
	{
		// 07:00 arrives from night (15%). At 07:15 the blend is half of the way from 15 to day's 90.
		var target = Blended().GetTarget(At(7, 15))!;

		Assert.AreEqual("day", target.PeriodName);
		Assert.AreEqual(52.5, target.BrightnessPct, 0.001);
	}


	[TestMethod]
	public void Blending_Off_Steps_Cleanly_At_The_Boundary()
	{
		Assert.AreEqual(70, Stepped().GetTarget(At(18, 15))!.BrightnessPct, 0.001);
	}

	[TestMethod]
	public void Zero_BlendMinutes_Behaves_Like_Blending_Off()
	{
		Assert.AreEqual(70, Blended(0).GetTarget(At(18, 15))!.BrightnessPct, 0.001);
	}

	// ===================== the sleep-mode escape hatch =====================

	[TestMethod]
	public void GetPeriodTarget_Reaches_A_Period_By_Name_Ignoring_The_Clock()
	{
		var night = Stepped().GetPeriodTarget("night")!;

		Assert.AreEqual("night", night.PeriodName);
		Assert.AreEqual(15d, night.BrightnessPct);
	}

	[TestMethod]
	public void GetPeriodTarget_Returns_Null_For_A_Period_That_Does_Not_Exist()
	{
		Assert.IsNull(Stepped().GetPeriodTarget("siesta"));
	}

	// ===================== a room's own levels =====================

	[TestMethod]
	public void A_Room_With_No_Levels_Runs_The_Schedule_Untouched()
	{
		var target = Stepped(levels: []).GetTarget(At(20))!;

		Assert.AreEqual(70d, target.BrightnessPct);
		Assert.AreEqual(2700, target.ColorTempKelvin);
		Assert.AreEqual(RoomLevelSource.None, target.FromRoom, "which is what the overwhelming majority of rooms report");
	}

	[TestMethod]
	public void A_Room_Replacing_Only_Brightness_Keeps_Inheriting_The_Schedules_Colour()
	{
		var levels = new List<RoomLevelOverride> { new() { PeriodId = "evening", BrightnessPct = 40 } };

		var target = Stepped(levels: levels).GetTarget(At(20))!;

		Assert.AreEqual(40d, target.BrightnessPct);
		Assert.AreEqual(2700, target.ColorTempKelvin,
			"the two values are independent: a room that only wants to be dimmer still follows the schedule's warmth");
		Assert.AreEqual(RoomLevelSource.Brightness, target.FromRoom);
	}

	[TestMethod]
	public void A_Room_Replacing_Only_Colour_Keeps_Inheriting_The_Schedules_Brightness()
	{
		var levels = new List<RoomLevelOverride> { new() { PeriodId = "evening", ColorTempKelvin = 4000 } };

		var target = Stepped(levels: levels).GetTarget(At(20))!;

		Assert.AreEqual(70d, target.BrightnessPct, "a workshop that cannot use 2700 K has said nothing about brightness");
		Assert.AreEqual(4000, target.ColorTempKelvin);
		Assert.AreEqual(RoomLevelSource.ColorTemp, target.FromRoom);
	}

	[TestMethod]
	public void A_Room_Replacing_Both_Reports_Both()
	{
		var levels = new List<RoomLevelOverride> { new() { PeriodId = "evening", BrightnessPct = 40, ColorTempKelvin = 4000 } };

		var target = Stepped(levels: levels).GetTarget(At(20))!;

		Assert.AreEqual(40d, target.BrightnessPct);
		Assert.AreEqual(4000, target.ColorTempKelvin);
		Assert.AreEqual(RoomLevelSource.Brightness | RoomLevelSource.ColorTemp, target.FromRoom);
	}

	[TestMethod]
	public void A_Rooms_Levels_Match_Their_Period_By_Name_Ignoring_Case()
	{
		var levels = new List<RoomLevelOverride> { new() { PeriodId = "EVENING", BrightnessPct = 40 } };

		Assert.AreEqual(40d, Stepped(levels: levels).GetTarget(At(20))!.BrightnessPct);
	}

	[TestMethod]
	public void A_Rooms_Levels_Naming_No_Period_Change_Nothing_And_Cost_Nothing()
	{
		// Almost always a renamed period. The validator warns; the engine never matches it.
		var levels = new List<RoomLevelOverride> { new() { PeriodId = "kveld", BrightnessPct = 40 } };

		var target = Stepped(levels: levels).GetTarget(At(20))!;

		Assert.AreEqual(70d, target.BrightnessPct, "the schedule's, exactly as a room with no levels at all gets");
		Assert.AreEqual(RoomLevelSource.None, target.FromRoom);
	}

	[TestMethod]
	public void The_First_Of_Two_Rows_For_One_Period_Wins()
	{
		var levels = new List<RoomLevelOverride>
		{
			new() { PeriodId = "evening", BrightnessPct = 40 },
			new() { PeriodId = "evening", BrightnessPct = 90 }
		};

		Assert.AreEqual(40d, Stepped(levels: levels).GetTarget(At(20))!.BrightnessPct,
			"first wins, matching the warning the validator raises about it");
	}

	[TestMethod]
	public void An_Empty_Row_Does_Not_Shadow_A_Later_Row_For_The_Same_Period()
	{
		var levels = new List<RoomLevelOverride>
		{
			new() { PeriodId = "evening" },
			new() { PeriodId = "evening", BrightnessPct = 40 }
		};

		Assert.AreEqual(40d, Stepped(levels: levels).GetTarget(At(20))!.BrightnessPct);
	}

	// ===================== blending across a boundary a room owns one side of =====================

	/// <summary>The room's own endpoints are interpolated; blending the house's and replacing afterwards puts a step where the blend removes one.</summary>
	[TestMethod]
	public void A_Blend_Into_An_Overridden_Period_Arrives_At_The_Rooms_Level_Not_The_Houses()
	{
		// 18:15 is halfway through the 30-minute blend from day (90) into evening, which this room runs at 40.
		var levels = new List<RoomLevelOverride> { new() { PeriodId = "evening", BrightnessPct = 40 } };

		var target = Blended(levels: levels).GetTarget(At(18, 15))!;

		Assert.AreEqual("evening", target.PeriodName);
		Assert.AreEqual(65, target.BrightnessPct, 0.001, "halfway from day's 90 to this room's 40");
		Assert.AreNotEqual(80d, target.BrightnessPct, "80 is the house's blend — reaching it means the room was applied too late");
	}

	[TestMethod]
	public void A_Blend_Out_Of_An_Overridden_Period_Departs_From_The_Rooms_Level()
	{
		// Day is this room's 30; evening is the house's 70. At 18:15 the blend is half of the way between them.
		var levels = new List<RoomLevelOverride> { new() { PeriodId = "day", BrightnessPct = 30 } };

		var target = Blended(levels: levels).GetTarget(At(18, 15))!;

		Assert.AreEqual("evening", target.PeriodName);
		Assert.AreEqual(50, target.BrightnessPct, 0.001, "halfway from this room's 30 to evening's 70");
		Assert.AreEqual(RoomLevelSource.None, target.FromRoom,
			"the flag describes the period being arrived at, which this room does not override");
	}

	[TestMethod]
	public void A_Blend_Interpolates_The_Two_Values_Independently()
	{
		var levels = new List<RoomLevelOverride> { new() { PeriodId = "evening", BrightnessPct = 40 } };

		var target = Blended(levels: levels).GetTarget(At(18, 15))!;

		Assert.AreEqual(3600, target.ColorTempKelvin, "halfway from 4500 to 2700, exactly as the house blends it");
	}

	[TestMethod]
	public void A_Blend_Across_Midnight_Departs_From_The_Rooms_Wrapped_Level()
	{
		// 07:15 arrives at day from the wrapped night period; this room runs that night at 5, not 15.
		var levels = new List<RoomLevelOverride> { new() { PeriodId = "night", BrightnessPct = 5 } };

		Assert.AreEqual(47.5, Blended(levels: levels).GetTarget(At(7, 15))!.BrightnessPct, 0.001,
			"halfway from this room's night of 5 to day's 90; the house's would be 52.5");
	}


	// ===================== the sleep clamp reaches the room's night, not the house's =====================

	/// <summary>The sleep clamp reads its ceiling off this call, so the house's night would hand a room a brighter ceiling than it asked for.</summary>
	[TestMethod]
	public void GetPeriodTarget_Reaches_The_Rooms_Levels_For_That_Period()
	{
		var levels = new List<RoomLevelOverride> { new() { PeriodId = "night", BrightnessPct = 8 } };

		var night = Stepped(levels: levels).GetPeriodTarget("night")!;

		Assert.AreEqual(8d, night.BrightnessPct);
		Assert.AreEqual(RoomLevelSource.Brightness, night.FromRoom);
	}

	// ===================== ActivePeriodName =====================

	[TestMethod]
	public void ActivePeriodName_NamesThePeriodActiveAtTheInstant()
	{
		var calc = Stepped();

		Assert.AreEqual("day", calc.ActivePeriodId(At(12)));
		Assert.AreEqual("evening", calc.ActivePeriodId(At(20)));
		Assert.AreEqual("night", calc.ActivePeriodId(At(23)));
	}

	[TestMethod]
	public void ActivePeriodName_WrapsBeforeTheFirstBoundary()
	{
		Assert.AreEqual("night", Stepped().ActivePeriodId(At(3)),
			"03:00 is still last night, exactly as GetTarget resolves it");
	}

	[TestMethod]
	public void ActivePeriodName_IsNull_WhenNoPeriodResolves()
	{
		// An all-sun-anchored table with unknown sun times (polar night): nothing can be placed.
		var polar = new List<TimePeriodConfig>
		{
			new() { Name = "day", Start = "sunrise", BrightnessPct = 90 },
			new() { Name = "night", Start = "sunset", BrightnessPct = 10 }
		};
		var calc = Stepped(polar, SunTimes.Unknown);

		Assert.IsNull(calc.ActivePeriodId(At(12)), "no boundary can be placed, so nothing is active");
		Assert.IsNull(calc.GetTarget(At(12)), "and the target is null too — the caller must command nothing");
	}

	// ===================== the period override (Home Assistant decides) =====================

	/// <summary>A calculator following a dropdown instead of the clock, with blending on so a step is visible.</summary>
	private static CircadianCalculator Following(
		Func<string?> periodOverride,
		IReadOnlyList<RoomLevelOverride>? levels = null) =>
		new(Table, new GlobalConfig { SmoothTransitions = true, BlendMinutes = 30 },
			() => SunTimes.Unknown, levels, periodOverride, zone: TimeZoneInfo.Utc);

	[TestMethod]
	public void Override_NamesThePeriod_WhateverTheClockSays()
	{
		CircadianCalculator calc = Following(() => "night");

		Assert.AreEqual("night", calc.ActivePeriodId(At(12)), "noon, and the house has selected night");
		Assert.AreEqual("night", calc.GetTarget(At(12))!.PeriodName);
		Assert.AreEqual(15d, calc.GetTarget(At(12))!.BrightnessPct);
		Assert.AreEqual(2200, calc.GetTarget(At(12))!.ColorTempKelvin);
	}

	[TestMethod]
	public void Override_MakesActivePeriodNameAndGetTargetAgree()
	{
		string? selected = "day";
		CircadianCalculator calc = Following(() => selected);

		Assert.AreEqual(calc.ActivePeriodId(At(23)), calc.GetTarget(At(23))!.PeriodName);

		selected = "evening";

		Assert.AreEqual("evening", calc.ActivePeriodId(At(23)));
		Assert.AreEqual("evening", calc.GetTarget(At(23))!.PeriodName);
	}

	[TestMethod]
	public void Override_ReturningNull_LeavesTheScheduleInCharge()
	{
		CircadianCalculator calc = Following(() => null);

		Assert.AreEqual("day", calc.ActivePeriodId(At(12)), "an unreadable or unmapped select is not an opinion");
		Assert.AreEqual("night", calc.ActivePeriodId(At(23)));
	}

	[TestMethod]
	public void Override_NamingNoConfiguredPeriod_FallsBackToTheSchedule()
	{
		CircadianCalculator calc = Following(() => "middag");

		Assert.AreEqual("day", calc.ActivePeriodId(At(12)));
		Assert.AreEqual("day", calc.GetTarget(At(12))!.PeriodName);
	}

	[TestMethod]
	public void Override_MatchesThePeriodNameCaseInsensitively()
	{
		Assert.AreEqual("night", Following(() => "NIGHT").ActivePeriodId(At(12)),
			"period names are matched case-insensitively everywhere else in the engine");
	}

	/// <summary><c>LevelsOf</c> sits inside <c>GetPeriodTarget</c>, so the override routes through it and never through the period's raw values.</summary>
	[TestMethod]
	public void Override_StillAppliesTheRoomsOwnLevels()
	{
		List<RoomLevelOverride> levels = [new() { PeriodId = "night", BrightnessPct = 8 }];

		LightTarget target = Following(() => "night", levels).GetTarget(At(12))!;

		Assert.AreEqual(8d, target.BrightnessPct, "the room runs the night at 8 %, selected or scheduled");
		Assert.AreEqual(RoomLevelSource.Brightness, target.FromRoom);
		Assert.AreEqual(2200, target.ColorTempKelvin, "and inherits the half it did not replace");
	}

	/// <summary>The step is intended: a selected period has no boundary time to interpolate away from.</summary>
	[TestMethod]
	public void Override_IsAStep_NotABlend()
	{
		CircadianCalculator calc = Following(() => "night");

		Assert.AreEqual(15d, calc.GetTarget(At(18, 1))!.BrightnessPct,
			"one minute into what would have been the evening blend, the selected night is already whole");
		Assert.AreEqual(15d, calc.GetTarget(At(18, 15))!.BrightnessPct);

		LightTarget blended = new CircadianCalculator(
			Table, new GlobalConfig { SmoothTransitions = true, BlendMinutes = 30 }, () => SunTimes.Unknown,
			zone: TimeZoneInfo.Utc)
			.GetTarget(At(18, 15))!;

		Assert.AreNotEqual(15d, blended.BrightnessPct,
			"the same calculator without an override does blend, so the step is the override's doing and not the table's");
	}

	/// <summary>If the override reached here, a house that selected "day" would have its night clamp hand back the day's levels.</summary>
	[TestMethod]
	public void Override_DoesNotReachGetPeriodTarget()
	{
		CircadianCalculator calc = Following(() => "day");

		LightTarget night = calc.GetPeriodTarget("night")!;

		Assert.AreEqual("night", night.PeriodName);
		Assert.AreEqual(15d, night.BrightnessPct, "the clamp reaches for the night rules, whatever is selected");
	}

	// ---- a period that waits for movement ------------------------------------------------------------

	/// <summary>A calculator holding back whatever <paramref name="heldBack"/> says is still waiting.</summary>
	private static CircadianCalculator Holding(Func<string, DateOnly, bool> heldBack, GlobalConfig? global = null) =>
		new(Table, global ?? new GlobalConfig { SmoothTransitions = false }, () => SunTimes.Unknown, null, null, heldBack,
			TimeZoneInfo.Utc);

	[TestMethod]
	public void AHeldPeriod_IsLeftOutOfTheTable_SoThePreviousOneKeepsRunning()
	{
		CircadianCalculator calc = Holding((period, _) => period == "day");

		Assert.AreEqual("night", calc.ActivePeriodId(At(8)), "day@07:00 has not begun, so last night is still running");
		Assert.AreEqual(15d, calc.GetTarget(At(8))!.BrightnessPct);
	}

	[TestMethod]
	public void AHeldPeriod_ThatHasBegun_IsInForceLikeAnyOther()
	{
		CircadianCalculator calc = Holding((_, _) => false);

		Assert.AreEqual("day", calc.ActivePeriodId(At(8)));
		Assert.AreEqual(90d, calc.GetTarget(At(8))!.BrightnessPct);
	}

	[TestMethod]
	public void AHeldPeriod_IsOvertakenByTheNextPeriodsStart()
	{
		CircadianCalculator calc = Holding((period, _) => period == "day");

		Assert.AreEqual("evening", calc.ActivePeriodId(At(18, 30)),
			"evening@18:00 arrives whether or not the day ever began");
		Assert.AreEqual("night", calc.ActivePeriodId(At(23)), "and the day does not end holding it either");
	}

	[TestMethod]
	public void AHeldPeriod_IsNamedByTheScheduleEvenWhileItIsNotInForce()
	{
		CircadianCalculator calc = Holding((period, _) => period == "day");

		Assert.AreEqual("day", calc.ScheduledPeriodId(At(8)), "the clock alone would have placed the day");
		Assert.AreEqual("night", calc.ActivePeriodId(At(8)), "and what is in force is what the hold left behind");
	}

	/// <summary>The sleep clamp asks for a period by name and must still get it while it is being held back.</summary>
	[TestMethod]
	public void AHeldPeriod_IsStillReachableByName()
	{
		CircadianCalculator calc = Holding((_, _) => true);

		LightTarget night = calc.GetPeriodTarget("night")!;

		Assert.AreEqual("night", night.PeriodName);
		Assert.AreEqual(15d, night.BrightnessPct);
	}

	[TestMethod]
	public void AHeldPeriod_ThatHasBegun_StillBlendsAwayFromTheOneBeforeIt()
	{
		CircadianCalculator calc = Holding(
			(_, _) => false, new GlobalConfig { SmoothTransitions = true, BlendMinutes = 30 });

		double half = calc.GetTarget(At(7, 15))!.BrightnessPct;

		Assert.IsTrue(half is > 15 and < 90, $"halfway from night's 15 % to day's 90 %, not a step; got {half}");
	}

	/// <summary>A boundary still ahead of now belongs to the instance that began yesterday, so keying on today asks about the wrong day.</summary>
	[TestMethod]
	public void TheHoldIsAskedAboutTheDayTheInstanceWouldHaveBegunOn()
	{
		List<(string Period, DateOnly Day)> asked = [];

		CircadianCalculator calc = Holding((period, day) =>
		{
			asked.Add((period, day));
			return false;
		});

		calc.ActivePeriodId(At(8));

		Assert.AreEqual(new DateOnly(2026, 1, 15), asked.Single(row => row.Period == "day").Day,
			"day@07:00 is behind us, so the instance in question is today's");
		Assert.AreEqual(new DateOnly(2026, 1, 14), asked.Single(row => row.Period == "night").Day,
			"night@22:30 is ahead of us, so the instance in question is the one that began yesterday");
	}

	// ===================== the next boundary =====================

	private static CircadianCalculator Zoned(TimeZoneInfo zone, IReadOnlyList<TimePeriodConfig> periods) =>
		new(periods, new GlobalConfig { SmoothTransitions = false }, () => SunTimes.Unknown, zone: zone);

	/// <summary>
	///     A synthetic zone on the European rule: +01:00 standard, +02:00 from the last Sunday of March at 02:00 to
	///     the last Sunday of October at 03:00, so the two tests below hold on a box with no tz database.
	/// </summary>
	private static TimeZoneInfo EuropeanRule()
	{
		TimeZoneInfo.TransitionTime springs = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
			new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday);
		TimeZoneInfo.TransitionTime falls = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
			new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday);

		TimeZoneInfo.AdjustmentRule rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
			DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), springs, falls);

		return TimeZoneInfo.CreateCustomTimeZone("test-eu", TimeSpan.FromHours(1), "UTC+1/+2", "UTC+1", "UTC+2", [rule]);
	}

	[TestMethod]
	public void NextBoundary_IsTheFirstStartAheadOfNow()
	{
		Assert.AreEqual(new DateTimeOffset(2026, 1, 15, 18, 0, 0, TimeSpan.Zero), Stepped().NextBoundary(At(12)));
	}

	/// <summary>A boundary's own instant is behind it, or a wake would arm for the moment it has just handled.</summary>
	[TestMethod]
	public void NextBoundary_StandingOnABoundary_NamesTheOneAfterIt()
	{
		Assert.AreEqual(new DateTimeOffset(2026, 1, 15, 22, 30, 0, TimeSpan.Zero), Stepped().NextBoundary(At(18)));
	}

	[TestMethod]
	public void NextBoundary_WrapsToTheEarliestStartOfTheNextDay()
	{
		Assert.AreEqual(new DateTimeOffset(2026, 1, 16, 7, 0, 0, TimeSpan.Zero), Stepped().NextBoundary(At(23)));
	}

	[TestMethod]
	public void NextBoundary_IsNullWhenNothingCanBePlaced()
	{
		Assert.IsNull(Stepped([]).NextBoundary(At(12)));
	}

	[TestMethod]
	public void NextBoundary_SkipsAPeriodThatIsStillWaitingForMovement()
	{
		CircadianCalculator calc = Holding((period, _) => period == "day");

		Assert.AreEqual(new DateTimeOffset(2026, 1, 15, 18, 0, 0, TimeSpan.Zero), calc.NextBoundary(At(3)),
			"day@07:00 will not be crossed, so evening@18:00 is the next thing worth waking for");
	}

	[TestMethod]
	public void NextBoundary_OnTheDayTheClocksGoForward_ArrivesWhenTheGapEnds()
	{
		List<TimePeriodConfig> table = [new() { Name = "night", Start = "02:30", BrightnessPct = 15 }];

		// On 2026-03-29 the clock jumps 02:00 to 03:00, so the 02:30 this period is written at never happens.
		Assert.AreEqual(
			new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero),
			Zoned(EuropeanRule(), table).NextBoundary(new DateTimeOffset(2026, 3, 28, 23, 30, 0, TimeSpan.Zero)));
	}

	[TestMethod]
	public void NextBoundary_OnTheDayTheClocksGoBack_TakesTheStandardTimeReading()
	{
		List<TimePeriodConfig> table = [new() { Name = "night", Start = "02:30", BrightnessPct = 15 }];

		// On 2026-10-25 02:30 happens at 00:30Z on summer time and again at 01:30Z on standard time.
		Assert.AreEqual(
			new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero),
			Zoned(EuropeanRule(), table).NextBoundary(new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero)));
	}

	/// <summary>The drop record is the calculator's only mutable state, and callers reach it from more than one thread.</summary>
	/// <remarks>Writers and readers at once: an unguarded write loses a drop, an unguarded read enumerates a set another thread is resizing.</remarks>
	[TestMethod]
	public void The_Drop_Record_Holds_Up_Under_Callers_On_Several_Threads()
	{
		const int Anchors = 300;
		const int Workers = 8;
		const int Passes = 300;

		List<TimePeriodConfig> table =
		[
			.. Enumerable.Range(0, Anchors)
				.Select(i => new TimePeriodConfig { Name = $"dawn-{i}", Start = "sunrise", BrightnessPct = 50 }),
			new TimePeriodConfig { Name = "night", Start = "22:30", BrightnessPct = 15 }
		];

		CircadianCalculator calc = new(
			table, new GlobalConfig { SmoothTransitions = false }, () => SunTimes.Unknown, zone: TimeZoneInfo.Utc);

		ConcurrentQueue<DroppedPeriod> raised = new();
		ConcurrentQueue<Exception> failures = new();
		calc.PeriodDropped += raised.Enqueue;

		using Barrier lineUp = new(Workers);
		Thread[] threads = new Thread[Workers];

		for (int worker = 0; worker < Workers; worker++)
		{
			bool writes = worker % 2 == 0;

			threads[worker] = new Thread(() =>
			{
				try
				{
					lineUp.SignalAndWait();

					for (int pass = 0; pass < Passes; pass++)
						if (writes)
							calc.NextBoundary(At(12));
						else
							// Enumerated, not counted: a count is one field read and would race with nothing.
							foreach (DroppedPeriod _ in calc.DroppedPeriods)
							{
							}
				}
				catch (Exception failure)
				{
					failures.Enqueue(failure);
				}
			});

			threads[worker].Start();
		}

		foreach (Thread thread in threads)
			thread.Join();

		Assert.AreEqual(0, failures.Count, failures.FirstOrDefault()?.ToString() ?? "");
		Assert.AreEqual(Anchors, calc.DroppedPeriods.Count, "every anchor is recorded, and none of them twice");
		Assert.AreEqual(Anchors, raised.Count, "and each is reported once, not once per thread that placed it");
	}
}
