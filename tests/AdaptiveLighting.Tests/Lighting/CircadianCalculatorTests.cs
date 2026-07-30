using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The circadian table: which period is active, what it targets, how the caps bite, and how the blend runs.
/// </summary>
/// <remarks>
///     A pure function of (periods, sun times, instant), so these tests need no fakes and no scheduler at all —
///     the instant is simply an argument. That is the whole point of the delegate the calculator takes.
/// </remarks>
[TestClass]
public sealed class CircadianCalculatorTests
{
	private static readonly List<TimePeriodConfig> Table =
	[
		new() { Name = "day", Start = "07:00", BrightnessPct = 90, ColorTempKelvin = 4500 },
		new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
		new() { Name = "night", Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200 }
	];

	private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 1, 15, hour, minute, 0, TimeSpan.Zero);

	private static CircadianCalculator Stepped(
		IReadOnlyList<TimePeriodConfig>? periods = null,
		SunTimes? sun = null,
		IReadOnlyList<RoomLevelOverride>? levels = null) =>
		new(periods ?? Table, new GlobalConfig { SmoothTransitions = false }, () => sun ?? SunTimes.Unknown, levels);

	/// <summary>
	///     What is left of the clamp once a period's configurable floor and ceiling are gone: the physical bound.
	///     A lamp cannot be set to 140 % or to -5, and that is not a preference anybody may configure away.
	/// </summary>
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

		// Polar night: there is no sunrise to anchor to. The fixed period must still cover the whole day.
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

		// The unparseable period is known at construction — before any evaluation, before any subscriber — so it
		// is read off DroppedPeriods rather than the event.
		CollectionAssert.Contains(
			calc.DroppedPeriods.ToList(),
			new DroppedPeriod("broken", "half past tea", PeriodDropReason.Unparseable),
			"an unparseable Start is surfaced up front, so a vanished period is not a silent hole");

		// The sun-anchored 'dawn' cannot be placed with unknown sun times: that surfaces on evaluation, via the event.
		calc.GetTarget(At(12));
		CollectionAssert.Contains(
			raised,
			new DroppedPeriod("dawn", "sunrise", PeriodDropReason.Unresolvable),
			"a sun-anchored period with no sun data is surfaced once it is evaluated");

		// Dedupe: a whole day of ticks against the same unresolvable boundary must not surface it again.
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
		new(Table, new GlobalConfig { SmoothTransitions = true, BlendMinutes = blendMinutes }, () => SunTimes.Unknown, levels);

	[TestMethod]
	public void Halfway_Through_The_Blend_The_Target_Is_Halfway_Between_The_Periods()
	{
		// 18:15 is 15 of 30 blend minutes past the evening boundary: halfway from day (90/4500) to evening (70/2700).
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
		var levels = new List<RoomLevelOverride> { new() { Period = "evening", BrightnessPct = 40 } };

		var target = Stepped(levels: levels).GetTarget(At(20))!;

		Assert.AreEqual(40d, target.BrightnessPct);
		Assert.AreEqual(2700, target.ColorTempKelvin,
			"the two values are independent: a room that only wants to be dimmer still follows the schedule's warmth");
		Assert.AreEqual(RoomLevelSource.Brightness, target.FromRoom);
	}

	[TestMethod]
	public void A_Room_Replacing_Only_Colour_Keeps_Inheriting_The_Schedules_Brightness()
	{
		var levels = new List<RoomLevelOverride> { new() { Period = "evening", ColorTempKelvin = 4000 } };

		var target = Stepped(levels: levels).GetTarget(At(20))!;

		Assert.AreEqual(70d, target.BrightnessPct, "a workshop that cannot use 2700 K has said nothing about brightness");
		Assert.AreEqual(4000, target.ColorTempKelvin);
		Assert.AreEqual(RoomLevelSource.ColorTemp, target.FromRoom);
	}

	[TestMethod]
	public void A_Room_Replacing_Both_Reports_Both()
	{
		var levels = new List<RoomLevelOverride> { new() { Period = "evening", BrightnessPct = 40, ColorTempKelvin = 4000 } };

		var target = Stepped(levels: levels).GetTarget(At(20))!;

		Assert.AreEqual(40d, target.BrightnessPct);
		Assert.AreEqual(4000, target.ColorTempKelvin);
		Assert.AreEqual(RoomLevelSource.Brightness | RoomLevelSource.ColorTemp, target.FromRoom);
	}

	/// <summary>Keyed by name, so the room's levels follow the period they were written about — and match it as every other period lookup does, ignoring case.</summary>
	[TestMethod]
	public void A_Rooms_Levels_Match_Their_Period_By_Name_Ignoring_Case()
	{
		var levels = new List<RoomLevelOverride> { new() { Period = "EVENING", BrightnessPct = 40 } };

		Assert.AreEqual(40d, Stepped(levels: levels).GetTarget(At(20))!.BrightnessPct);
	}

	[TestMethod]
	public void A_Rooms_Levels_Naming_No_Period_Change_Nothing_And_Cost_Nothing()
	{
		// Almost always a period that has been renamed. The validator reports it; here it is simply never matched.
		var levels = new List<RoomLevelOverride> { new() { Period = "kveld", BrightnessPct = 40 } };

		var target = Stepped(levels: levels).GetTarget(At(20))!;

		Assert.AreEqual(70d, target.BrightnessPct, "the schedule's, exactly as a room with no levels at all gets");
		Assert.AreEqual(RoomLevelSource.None, target.FromRoom);
	}

	[TestMethod]
	public void The_First_Of_Two_Rows_For_One_Period_Wins()
	{
		var levels = new List<RoomLevelOverride>
		{
			new() { Period = "evening", BrightnessPct = 40 },
			new() { Period = "evening", BrightnessPct = 90 }
		};

		Assert.AreEqual(40d, Stepped(levels: levels).GetTarget(At(20))!.BrightnessPct,
			"first wins, matching the warning the validator raises about it");
	}

	/// <summary>An empty row must not shadow a later row that actually says something.</summary>
	[TestMethod]
	public void An_Empty_Row_Does_Not_Shadow_A_Later_Row_For_The_Same_Period()
	{
		var levels = new List<RoomLevelOverride>
		{
			new() { Period = "evening" },
			new() { Period = "evening", BrightnessPct = 40 }
		};

		Assert.AreEqual(40d, Stepped(levels: levels).GetTarget(At(20))!.BrightnessPct);
	}

	// ===================== the period's caps still bind a room =====================




	// ===================== blending across a boundary a room owns one side of =====================

	/// <summary>
	///     <b>The part most likely to be quietly wrong.</b> What is interpolated must be the room's own two
	///     endpoints, not the house's.
	/// </summary>
	/// <remarks>
	///     Blending the house's endpoints and replacing the result afterwards would put a step exactly where the
	///     blend exists to remove one: the room would run the house's level right up to the boundary and then jump.
	/// </remarks>
	[TestMethod]
	public void A_Blend_Into_An_Overridden_Period_Arrives_At_The_Rooms_Level_Not_The_Houses()
	{
		// 18:15 is halfway through the 30-minute blend from day (90) into evening, which this room runs at 40.
		var levels = new List<RoomLevelOverride> { new() { Period = "evening", BrightnessPct = 40 } };

		var target = Blended(levels: levels).GetTarget(At(18, 15))!;

		Assert.AreEqual("evening", target.PeriodName);
		Assert.AreEqual(65, target.BrightnessPct, 0.001, "halfway from day's 90 to this room's 40");
		Assert.AreNotEqual(80d, target.BrightnessPct, "80 is the house's blend — reaching it means the room was applied too late");
	}

	/// <summary>The other side of the same boundary: the period being left is read through the room too.</summary>
	[TestMethod]
	public void A_Blend_Out_Of_An_Overridden_Period_Departs_From_The_Rooms_Level()
	{
		// Day is this room's 30; evening is the house's 70. At 18:15 the blend is half of the way between them.
		var levels = new List<RoomLevelOverride> { new() { Period = "day", BrightnessPct = 30 } };

		var target = Blended(levels: levels).GetTarget(At(18, 15))!;

		Assert.AreEqual("evening", target.PeriodName);
		Assert.AreEqual(50, target.BrightnessPct, 0.001, "halfway from this room's 30 to evening's 70");
		Assert.AreEqual(RoomLevelSource.None, target.FromRoom,
			"the flag describes the period being arrived at, which this room does not override");
	}

	/// <summary>Independence survives the blend: an overridden brightness must not drag the colour off the house's curve.</summary>
	[TestMethod]
	public void A_Blend_Interpolates_The_Two_Values_Independently()
	{
		var levels = new List<RoomLevelOverride> { new() { Period = "evening", BrightnessPct = 40 } };

		var target = Blended(levels: levels).GetTarget(At(18, 15))!;

		Assert.AreEqual(3600, target.ColorTempKelvin, "halfway from 4500 to 2700, exactly as the house blends it");
	}

	[TestMethod]
	public void A_Blend_Across_Midnight_Departs_From_The_Rooms_Wrapped_Level()
	{
		// 07:15 arrives at day from the wrapped night period, which this room runs at 5 rather than 15.
		var levels = new List<RoomLevelOverride> { new() { Period = "night", BrightnessPct = 5 } };

		Assert.AreEqual(47.5, Blended(levels: levels).GetTarget(At(7, 15))!.BrightnessPct, 0.001,
			"halfway from this room's night of 5 to day's 90; the house's would be 52.5");
	}


	// ===================== the sleep clamp reaches the room's night, not the house's =====================

	/// <summary>
	///     A room that runs the night dimmer than the house means it at 03:00 too. The sleep clamp reads its
	///     ceiling off this, so a version that returned the house's night would hand the room a ceiling it had
	///     already said was too bright.
	/// </summary>
	[TestMethod]
	public void GetPeriodTarget_Reaches_The_Rooms_Levels_For_That_Period()
	{
		var levels = new List<RoomLevelOverride> { new() { Period = "night", BrightnessPct = 8 } };

		var night = Stepped(levels: levels).GetPeriodTarget("night")!;

		Assert.AreEqual(8d, night.BrightnessPct);
		Assert.AreEqual(RoomLevelSource.Brightness, night.FromRoom);
	}

	// ===================== ActivePeriodName =====================

	[TestMethod]
	public void ActivePeriodName_NamesThePeriodActiveAtTheInstant()
	{
		var calc = Stepped();

		Assert.AreEqual("day", calc.ActivePeriodName(At(12)));
		Assert.AreEqual("evening", calc.ActivePeriodName(At(20)));
		Assert.AreEqual("night", calc.ActivePeriodName(At(23)));
	}

	[TestMethod]
	public void ActivePeriodName_WrapsBeforeTheFirstBoundary()
	{
		Assert.AreEqual("night", Stepped().ActivePeriodName(At(3)),
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

		Assert.IsNull(calc.ActivePeriodName(At(12)), "no boundary can be placed, so nothing is active");
		Assert.IsNull(calc.GetTarget(At(12)), "and the target is null too — the caller must command nothing");
	}

	// ===================== the period override (Home Assistant decides) =====================

	/// <summary>A calculator following a dropdown rather than the clock. Blending on, so the step is visible.</summary>
	private static CircadianCalculator Following(
		Func<string?> periodOverride,
		IReadOnlyList<RoomLevelOverride>? levels = null) =>
		new(Table, new GlobalConfig { SmoothTransitions = true, BlendMinutes = 30 },
			() => SunTimes.Unknown, levels, periodOverride);

	[TestMethod]
	public void Override_NamesThePeriod_WhateverTheClockSays()
	{
		CircadianCalculator calc = Following(() => "night");

		Assert.AreEqual("night", calc.ActivePeriodName(At(12)), "noon, and the house has selected night");
		Assert.AreEqual("night", calc.GetTarget(At(12))!.PeriodName);
		Assert.AreEqual(15d, calc.GetTarget(At(12))!.BrightnessPct);
		Assert.AreEqual(2200, calc.GetTarget(At(12))!.ColorTempKelvin);
	}

	/// <summary>
	///     The name and the levels come from one resolution, so a card and a lamp cannot disagree about which
	///     period is in force.
	/// </summary>
	[TestMethod]
	public void Override_MakesActivePeriodNameAndGetTargetAgree()
	{
		string? selected = "day";
		CircadianCalculator calc = Following(() => selected);

		Assert.AreEqual(calc.ActivePeriodName(At(23)), calc.GetTarget(At(23))!.PeriodName);

		selected = "evening";

		Assert.AreEqual("evening", calc.ActivePeriodName(At(23)));
		Assert.AreEqual("evening", calc.GetTarget(At(23))!.PeriodName);
	}

	[TestMethod]
	public void Override_ReturningNull_LeavesTheScheduleInCharge()
	{
		CircadianCalculator calc = Following(() => null);

		Assert.AreEqual("day", calc.ActivePeriodName(At(12)), "an unreadable or unmapped select is not an opinion");
		Assert.AreEqual("night", calc.ActivePeriodName(At(23)));
	}

	/// <summary>
	///     A mapping the validator refuses and the reader reports falls back rather than taking the room dark. Both
	///     halves are already said out loud; commanding nothing on top of that would cost a room over a typo.
	/// </summary>
	[TestMethod]
	public void Override_NamingNoConfiguredPeriod_FallsBackToTheSchedule()
	{
		CircadianCalculator calc = Following(() => "middag");

		Assert.AreEqual("day", calc.ActivePeriodName(At(12)));
		Assert.AreEqual("day", calc.GetTarget(At(12))!.PeriodName);
	}

	[TestMethod]
	public void Override_MatchesThePeriodNameCaseInsensitively()
	{
		Assert.AreEqual("night", Following(() => "NIGHT").ActivePeriodName(At(12)),
			"period names are matched case-insensitively everywhere else in the engine");
	}

	/// <summary>
	///     The room's own levels still apply — <c>LevelsOf</c> is inside <c>GetPeriodTarget</c>, which is the whole
	///     reason the override routes through it rather than reading the period's raw values.
	/// </summary>
	[TestMethod]
	public void Override_StillAppliesTheRoomsOwnLevels()
	{
		List<RoomLevelOverride> levels = [new() { Period = "night", BrightnessPct = 8 }];

		LightTarget target = Following(() => "night", levels).GetTarget(At(12))!;

		Assert.AreEqual(8d, target.BrightnessPct, "the room runs the night at 8 %, selected or scheduled");
		Assert.AreEqual(RoomLevelSource.Brightness, target.FromRoom);
		Assert.AreEqual(2200, target.ColorTempKelvin, "and inherits the half it did not replace");
	}

	/// <summary>
	///     <b>The step is intended.</b> A selected period began the instant somebody moved the dropdown, so there is
	///     no boundary time to interpolate away from; a synthetic one would be the engine easing toward a period
	///     already in force. Pinned so nobody "fixes" it into a blend later.
	/// </summary>
	[TestMethod]
	public void Override_IsAStep_NotABlend()
	{
		CircadianCalculator calc = Following(() => "night");

		Assert.AreEqual(15d, calc.GetTarget(At(18, 1))!.BrightnessPct,
			"one minute into what would have been the evening blend, the selected night is already whole");
		Assert.AreEqual(15d, calc.GetTarget(At(18, 15))!.BrightnessPct);

		LightTarget blended = new CircadianCalculator(
			Table, new GlobalConfig { SmoothTransitions = true, BlendMinutes = 30 }, () => SunTimes.Unknown)
			.GetTarget(At(18, 15))!;

		Assert.AreNotEqual(15d, blended.BrightnessPct,
			"the same calculator without an override does blend, so the step is the override's doing and not the table's");
	}

	/// <summary>
	///     The sleep clamp asks for a period <i>by name</i>, and a caller that named one must get the one it named —
	///     otherwise a house that selected "day" would have its night clamp quietly hand back the day's levels.
	/// </summary>
	[TestMethod]
	public void Override_DoesNotReachGetPeriodTarget()
	{
		CircadianCalculator calc = Following(() => "day");

		LightTarget night = calc.GetPeriodTarget("night")!;

		Assert.AreEqual("night", night.PeriodName);
		Assert.AreEqual(15d, night.BrightnessPct, "the clamp reaches for the night rules, whatever is selected");
	}
}
