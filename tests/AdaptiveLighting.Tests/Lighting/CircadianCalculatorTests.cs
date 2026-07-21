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
		new() { Name = "night", Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200, MaxBrightnessPct = 30, MinBrightnessPct = 5 }
	];

	private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 1, 15, hour, minute, 0, TimeSpan.Zero);

	private static CircadianCalculator Stepped(IReadOnlyList<TimePeriodConfig>? periods = null, SunTimes? sun = null) =>
		new(periods ?? Table, new GlobalConfig { SmoothTransitions = false }, () => sun ?? SunTimes.Unknown);

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
	public void The_Night_Ceiling_Clamps_A_Period_That_Asks_For_Too_Much()
	{
		var greedyNight = new List<TimePeriodConfig>
		{
			new() { Name = "night", Start = "22:30", BrightnessPct = 100, ColorTempKelvin = 2200, MaxBrightnessPct = 30 }
		};

		var target = Stepped(greedyNight).GetTarget(At(23))!;

		Assert.AreEqual(30d, target.BrightnessPct, "nobody gets 100% in the face at 03:00, even if the table says so");
	}

	[TestMethod]
	public void Clamp_Honours_Both_Caps_And_The_Physical_Range()
	{
		var target = Stepped().GetTarget(At(23))!;   // night: floor 5, ceiling 30

		Assert.AreEqual(30d, target.Clamp(80));
		Assert.AreEqual(5d, target.Clamp(1), "the floor is what keeps the pre-off dim legal at night");
		Assert.AreEqual(15d, target.Clamp(15));
	}

	[TestMethod]
	public void An_Uncapped_Period_Clamps_Only_To_Zero_And_A_Hundred()
	{
		var target = Stepped().GetTarget(At(20))!;   // evening: no caps

		Assert.AreEqual(100d, target.Clamp(150));
		Assert.AreEqual(0d, target.Clamp(-10));
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

	private static CircadianCalculator Blended(int blendMinutes = 30) =>
		new(Table, new GlobalConfig { SmoothTransitions = true, BlendMinutes = blendMinutes }, () => SunTimes.Unknown);

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
	public void A_Blend_Is_Still_Held_To_The_Arriving_Periods_Caps()
	{
		// 22:45 blends from evening's 70 toward night's 15 and is only halfway there — but night's ceiling is 30.
		var target = Blended().GetTarget(At(22, 45))!;

		Assert.AreEqual("night", target.PeriodName);
		Assert.AreEqual(30d, target.BrightnessPct, "the caps come from the period the name promises");
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
		Assert.AreEqual(30d, night.MaxBrightnessPct);
	}

	[TestMethod]
	public void GetPeriodTarget_Returns_Null_For_A_Period_That_Does_Not_Exist()
	{
		Assert.IsNull(Stepped().GetPeriodTarget("siesta"));
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
}
