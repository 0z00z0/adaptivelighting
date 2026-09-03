using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>A boundary where one side follows the daylight curve and the other states a percentage.</summary>
// The round trip through both stages, because the defect only exists between them: the calculator mixes the two
// levels and the curve then replaces the result, so either stage on its own looks correct.
[TestClass]
public sealed class CurveBlendTests
{
	// A decade either side of 1 000 lx, so the log midpoint is round: at 1 000 lx the curve holds 50 %.
	private static AreaSettings Curve() =>
		new()
		{
			LuxBrightnessStartLux = 100,
			LuxBrightnessFullLux = 10000,
			LuxBrightnessMinPct = 20,
			LuxBrightnessMaxPct = 80,
			LuxBrightnessGamma = 1.0
		};

	private const double CurveAtNoonLux = 50;

	private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 1, 15, hour, minute, 0, TimeSpan.Zero);

	private static TimePeriodConfig Period(string name, string start, double brightnessPct, bool onTheCurve) =>
		new() { Name = name, Start = start, BrightnessPct = brightnessPct, ColorTempKelvin = 2700, UseDaylightCurve = onTheCurve };

	// The stored percentages are deliberately far from the curve's 50 %: 90 on the day side, 15 on the night side.
	// A stored number that happened to agree with the curve would let the defect pass unnoticed.
	private static CircadianCalculator Blended(bool dayOnCurve, bool eveningOnCurve, bool nightOnCurve)
	{
		List<TimePeriodConfig> table =
		[
			Period("day", "07:00", 90, dayOnCurve),
			Period("evening", "18:00", 70, eveningOnCurve),
			Period("night", "22:30", 15, nightOnCurve)
		];

		return new CircadianCalculator(
			table, new GlobalConfig { SmoothTransitions = true, BlendMinutes = 30 }, () => SunTimes.Unknown,
			zone: TimeZoneInfo.Utc);
	}

	private static double RoundTrip(CircadianCalculator calculator, DateTimeOffset now, double lux = 1000)
	{
		LightTarget target = calculator.GetTarget(now)!;
		return new LuxBrightnessCurve(Curve(), () => lux).Apply(target).BrightnessPct;
	}

	// ===================== leaving the curve, the worse direction =====================

	/// <summary>Leaving a curve period, the blend starts from where the curve actually left the light.</summary>
	// The stored 90 % is the number the editor calls inert, and the light was at the curve's 50 % the instant
	// before. Starting the blend at 90 raises the level at the boundary and only then falls, which is a rise of
	// 40 points at the darkest end of the day.
	[TestMethod]
	public void Leaving_A_Curve_Period_The_Blend_Starts_At_The_Curves_Own_Level()
	{
		CircadianCalculator calculator = Blended(dayOnCurve: true, eveningOnCurve: false, nightOnCurve: false);

		Assert.AreEqual(CurveAtNoonLux, RoundTrip(calculator, At(18)), 1e-9,
			"at the boundary the light must not move at all: the curve was holding 50 %");
		Assert.AreEqual(60, RoundTrip(calculator, At(18, 15)), 1e-9, "halfway from the curve's 50 % to evening's 70 %");
		Assert.AreEqual(70, RoundTrip(calculator, At(18, 30)), 1e-9, "and the arriving period holds it alone once the blend is over");
	}

	/// <summary>The leaving endpoint is the curve at this instant's lux, not a reading taken at the boundary.</summary>
	[TestMethod]
	public void The_Leaving_Endpoint_Follows_The_Light_Outside_Through_The_Blend()
	{
		CircadianCalculator calculator = Blended(dayOnCurve: true, eveningOnCurve: false, nightOnCurve: false);

		// 100 lx is the curve's dark anchor, so the leaving end is 20 %; halfway to evening's 70 % is 45 %.
		Assert.AreEqual(45, RoundTrip(calculator, At(18, 15), lux: 100), 1e-9);
	}

	// ===================== arriving at the curve =====================

	/// <summary>Arriving at a curve period, the blend eases to the curve instead of stepping onto it.</summary>
	[TestMethod]
	public void Arriving_At_A_Curve_Period_The_Blend_Eases_Onto_The_Curve()
	{
		CircadianCalculator calculator = Blended(dayOnCurve: false, eveningOnCurve: false, nightOnCurve: true);

		Assert.AreEqual(70, RoundTrip(calculator, At(22, 30)), 1e-9, "the boundary keeps the evening's stated 70 %");
		Assert.AreEqual(60, RoundTrip(calculator, At(22, 45)), 1e-9, "halfway from 70 % to the curve's 50 %");
		Assert.AreEqual(CurveAtNoonLux, RoundTrip(calculator, At(23)), 1e-9, "and the curve holds it alone afterwards");
	}

	// ===================== the two controls =====================

	/// <summary>Where both sides run the curve there is nothing to blend: the curve's answer holds throughout.</summary>
	[TestMethod]
	public void A_Blend_Between_Two_Curve_Periods_Is_The_Curves_Answer_At_Every_Instant()
	{
		CircadianCalculator calculator = Blended(dayOnCurve: true, eveningOnCurve: true, nightOnCurve: true);

		foreach (DateTimeOffset instant in new[] { At(18), At(18, 15), At(18, 29), At(18, 30), At(20) })
			Assert.AreEqual(CurveAtNoonLux, RoundTrip(calculator, instant), 1e-9, $"at {instant:HH:mm}");
	}

	/// <summary>Where neither side runs the curve the arithmetic lands on the same numbers as before.</summary>
	[TestMethod]
	public void A_Blend_Between_Two_Stated_Periods_Is_Unchanged()
	{
		CircadianCalculator calculator = Blended(dayOnCurve: false, eveningOnCurve: false, nightOnCurve: false);

		Assert.AreEqual(90, RoundTrip(calculator, At(18)), 1e-9, "the window trails the boundary");
		Assert.AreEqual(80, RoundTrip(calculator, At(18, 15)), 1e-9, "halfway from day's 90 % to evening's 70 %");
		Assert.AreEqual(70, RoundTrip(calculator, At(18, 30)), 1e-9);
	}

	/// <summary>Colour temperature keeps blending in the calculator; the curve never touches kelvin.</summary>
	[TestMethod]
	public void Kelvin_Still_Blends_Across_A_Mixed_Boundary()
	{
		List<TimePeriodConfig> table =
		[
			new() { Name = "day", Start = "07:00", BrightnessPct = 90, ColorTempKelvin = 4500, UseDaylightCurve = true },
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 }
		];

		CircadianCalculator calculator = new(
			table, new GlobalConfig { SmoothTransitions = true, BlendMinutes = 30 }, () => SunTimes.Unknown,
			zone: TimeZoneInfo.Utc);

		LightTarget lit = new LuxBrightnessCurve(Curve(), () => 1000d).Apply(calculator.GetTarget(At(18, 15))!);

		Assert.AreEqual(3600, lit.ColorTempKelvin, "halfway from 4 500 K to 2 700 K");
		Assert.AreEqual(60, lit.BrightnessPct, 1e-9);
		Assert.AreEqual("evening", lit.PeriodName);
	}

	// ===================== outside a blend nothing moves =====================

	/// <summary>With blending off a mixed boundary is a step, and the curve still owns the curve period.</summary>
	[TestMethod]
	public void With_Blending_Off_Each_Period_Answers_Alone()
	{
		List<TimePeriodConfig> table =
		[
			Period("day", "07:00", 90, onTheCurve: true),
			Period("evening", "18:00", 70, onTheCurve: false)
		];

		CircadianCalculator calculator = new(
			table, new GlobalConfig { SmoothTransitions = false }, () => SunTimes.Unknown, zone: TimeZoneInfo.Utc);

		Assert.AreEqual(CurveAtNoonLux, RoundTrip(calculator, At(17, 59)), 1e-9);
		Assert.AreEqual(70, RoundTrip(calculator, At(18)), 1e-9);
	}

	/// <summary>The sleep clamp and the level test reach a period by name, and that answer carries no blend.</summary>
	[TestMethod]
	public void A_Period_Reached_By_Name_Carries_No_Blend()
	{
		CircadianCalculator calculator = Blended(dayOnCurve: true, eveningOnCurve: false, nightOnCurve: false);

		LightTarget night = calculator.GetPeriodTarget("night")!;
		LightTarget day = calculator.GetPeriodTarget("day")!;

		Assert.IsNull(night.Blend);
		Assert.IsNull(day.Blend);

		LuxBrightnessCurve curve = new(Curve(), () => 1000d);

		Assert.AreSame(night, curve.Apply(night), "a stated period outside a blend goes nowhere near the maths");
		Assert.AreEqual(15, curve.Apply(night).BrightnessPct, 1e-9);
		Assert.AreEqual(CurveAtNoonLux, curve.Apply(day).BrightnessPct, 1e-9, "and a curve period is the curve's answer");
	}
}
