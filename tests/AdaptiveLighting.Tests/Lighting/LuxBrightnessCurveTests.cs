using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The daylight curve: the light outside sets the brightness for the periods that ask it to.</summary>
[TestClass]
public sealed class LuxBrightnessCurveTests
{
	/// <summary>Anchors a decade either side of 1 000 lx, so the log midpoint is a round number to assert on.</summary>
	private static AreaSettings Curve(
		double startLux = 100,
		double fullLux = 10000,
		double minPct = 40,
		double maxPct = 100,
		double gamma = 1.0) =>
		new()
		{
			LuxBrightnessStartLux = startLux,
			LuxBrightnessFullLux = fullLux,
			LuxBrightnessMinPct = minPct,
			LuxBrightnessMaxPct = maxPct,
			LuxBrightnessGamma = gamma
		};

	private static LightTarget Target(double brightnessPct, bool onTheCurve = true) =>
		new("evening", brightnessPct, 2700) { UsesDaylightCurve = onTheCurve };

	private static LuxBrightnessCurve For(AreaSettings settings, double? lux) => new(settings, () => lux);

	// ===================== the two anchors =====================

	[TestMethod]
	public void Below_The_Start_Anchor_The_Dark_End_Applies()
	{
		Assert.AreEqual(40, LuxBrightnessCurve.Brightness(50, Curve()));
	}

	[TestMethod]
	public void The_Start_Anchor_Itself_Is_The_Dark_End()
	{
		Assert.AreEqual(40, LuxBrightnessCurve.Brightness(100, Curve()),
			"the anchor is where the climb begins, so at it there is nothing yet to add");
	}

	[TestMethod]
	public void At_The_Full_Anchor_The_Bright_End_Applies()
	{
		Assert.AreEqual(90, LuxBrightnessCurve.Brightness(10000, Curve(maxPct: 90)));
	}

	[TestMethod]
	public void Above_The_Full_Anchor_The_Bright_End_Still_Applies()
	{
		Assert.AreEqual(90, LuxBrightnessCurve.Brightness(250000, Curve(maxPct: 90)),
			"direct sun is a decade past the anchor and must not extrapolate past the top");
	}

	// ===================== the log interpolation =====================

	[TestMethod]
	public void The_Midpoint_Of_The_Log_Range_Is_Halfway_Up_The_Curve()
	{
		Assert.AreEqual(70, LuxBrightnessCurve.Brightness(1000, Curve()), 1e-9,
			"log10(1000) is exactly halfway between log10(100) and log10(10000)");

		double linear = 40 + (((1000d - 100) / (10000 - 100)) * 60);
		Assert.AreEqual(45.45, linear, 0.01, "and this is what linear would have produced — the level it is not");
	}

	[TestMethod]
	public void Each_Decade_Gets_An_Equal_Share_Of_The_Curve()
	{
		AreaSettings settings = Curve(startLux: 1, fullLux: 1000);

		Assert.AreEqual(1d / 3, LuxBrightnessCurve.Position(10, settings), 1e-9);
		Assert.AreEqual(2d / 3, LuxBrightnessCurve.Position(100, settings), 1e-9);
		Assert.AreEqual(1, LuxBrightnessCurve.Position(1000, settings), 1e-9);
	}

	// ===================== the shaping exponent =====================

	[TestMethod]
	public void A_Gamma_Above_One_Holds_The_Level_Back_Until_It_Is_Properly_Bright()
	{
		// 0.5^2 = 0.25 of the span, against 0.5 for a straight line.
		Assert.AreEqual(55, LuxBrightnessCurve.Brightness(1000, Curve(gamma: 2)), 1e-9);
	}

	[TestMethod]
	public void A_Gamma_Below_One_Lifts_The_Room_As_Soon_As_The_Light_Climbs()
	{
		// 0.5^0.5 ≈ 0.7071 of the span.
		Assert.AreEqual(82.426, LuxBrightnessCurve.Brightness(1000, Curve(gamma: 0.5)), 0.001);
	}

	[TestMethod]
	public void The_Anchors_Are_Unmoved_By_Any_Gamma()
	{
		Assert.AreEqual(40, LuxBrightnessCurve.Brightness(100, Curve(gamma: 4)));
		Assert.AreEqual(100, LuxBrightnessCurve.Brightness(10000, Curve(gamma: 4)));
		Assert.AreEqual(40, LuxBrightnessCurve.Brightness(100, Curve(gamma: 0.25)));
		Assert.AreEqual(100, LuxBrightnessCurve.Brightness(10000, Curve(gamma: 0.25)));
	}

	// Math.Pow(0, 0) is 1, so a zero exponent at face value commands full daylight in pitch darkness.
	[TestMethod]
	public void A_Zero_Gamma_Does_Not_Turn_Darkness_Into_Full_Daylight()
	{
		Assert.AreEqual(40, LuxBrightnessCurve.Brightness(50, Curve(gamma: 0)));
		Assert.AreEqual(40, LuxBrightnessCurve.Brightness(100, Curve(gamma: 0)));
		Assert.AreEqual(70, LuxBrightnessCurve.Brightness(1000, Curve(gamma: 0)), 1e-9,
			"an unusable exponent falls back to the straight line, not to nonsense");
	}

	[TestMethod]
	public void A_Negative_Or_Non_Finite_Gamma_Falls_Back_To_The_Straight_Line()
	{
		Assert.AreEqual(70, LuxBrightnessCurve.Brightness(1000, Curve(gamma: -2)), 1e-9);
		Assert.AreEqual(70, LuxBrightnessCurve.Brightness(1000, Curve(gamma: double.NaN)), 1e-9);
	}

	// ===================== readings that are not numbers =====================

	// Sensors do report 0, and log10(0) is negative infinity.
	[TestMethod]
	public void Zero_Lux_Is_Safe_And_Gives_The_Dark_End()
	{
		double brightness = LuxBrightnessCurve.Brightness(0, Curve());

		Assert.AreEqual(40, brightness);
		Assert.IsTrue(double.IsFinite(brightness));
	}

	[TestMethod]
	public void A_Negative_Reading_Is_Safe_And_Gives_The_Dark_End()
	{
		double brightness = LuxBrightnessCurve.Brightness(-12, Curve());

		Assert.AreEqual(40, brightness);
		Assert.IsTrue(double.IsFinite(brightness));
	}

	[TestMethod]
	public void A_NaN_Or_Infinite_Reading_Never_Reaches_A_Light()
	{
		Assert.AreEqual(40d, LuxBrightnessCurve.Brightness(double.NaN, Curve()));
		Assert.AreEqual(40d, LuxBrightnessCurve.Brightness(double.PositiveInfinity, Curve()));
		Assert.AreEqual(40d, LuxBrightnessCurve.Brightness(double.NegativeInfinity, Curve()));
	}

	[TestMethod]
	public void No_Sensor_At_All_Holds_The_Dark_End()
	{
		Assert.AreEqual(40, LuxBrightnessCurve.Brightness(null, Curve()));
		Assert.AreEqual(40, For(Curve(), lux: null).Apply(Target(15)).BrightnessPct,
			"a room with no reading sits at the curve's dark end — it does not fail, and it does not take the period's own number");
	}

	// ===================== anchors that make no sense =====================

	[TestMethod]
	public void Inverted_Anchors_Make_The_Curve_Inert_Rather_Than_Wild()
	{
		Assert.AreEqual(40, LuxBrightnessCurve.Brightness(5000, Curve(startLux: 10000, fullLux: 100)));
	}

	[TestMethod]
	public void Equal_Anchors_Do_Not_Divide_By_Zero()
	{
		double brightness = LuxBrightnessCurve.Brightness(5000, Curve(startLux: 1000, fullLux: 1000));

		Assert.AreEqual(40, brightness);
		Assert.IsTrue(double.IsFinite(brightness));
	}

	// Two anchors can differ while their logarithms do not, so the interpolation divides 0 by 0, which the validator cannot catch.
	[TestMethod]
	public void Anchors_Whose_Logarithms_Are_Indistinguishable_Are_Inert_Rather_Than_NaN()
	{
		AreaSettings settings = Curve(startLux: 100, fullLux: 100.00000000000003);

		double position = LuxBrightnessCurve.Position(100.00000000000001, settings);

		Assert.IsTrue(double.IsFinite(position), "a position that is not a number is not a position");
		Assert.AreEqual(0, position, "a curve with nothing to interpolate across is inert, as every other degenerate anchor is");
		Assert.AreEqual(40, LuxBrightnessCurve.Brightness(100.00000000000001, settings));
	}

	[TestMethod]
	public void A_Start_Anchor_At_Or_Below_Zero_Has_No_Logarithm_And_Is_Inert()
	{
		Assert.AreEqual(40, LuxBrightnessCurve.Brightness(5000, Curve(startLux: 0)));
		Assert.AreEqual(40, LuxBrightnessCurve.Brightness(5000, Curve(startLux: -100)));
	}

	// ===================== both ends are free =====================

	[TestMethod]
	public void The_Schedules_Brightest_Period_Does_Not_Bound_The_Curve()
	{
		// The period asks 90 and the curve tops out at 20: the curve wins, because it replaces rather than adds.
		Assert.AreEqual(20, For(Curve(minPct: 5, maxPct: 20), lux: 10000).Apply(Target(90)).BrightnessPct);
	}

	[TestMethod]
	public void A_Bright_End_Under_The_Dark_End_Makes_The_Curve_Fall()
	{
		AreaSettings falling = Curve(minPct: 80, maxPct: 20);

		Assert.AreEqual(80, LuxBrightnessCurve.Brightness(100, falling));
		Assert.AreEqual(50, LuxBrightnessCurve.Brightness(1000, falling), 1e-9);
		Assert.AreEqual(20, LuxBrightnessCurve.Brightness(10000, falling));
	}

	[TestMethod]
	public void Every_Reading_Lands_Inside_The_Physical_Range()
	{
		foreach (double lux in new double[] { 0, 1, 99, 100, 101, 1000, 9999, 10000, 100000 })
			Assert.IsTrue(LuxBrightnessCurve.Brightness(lux, Curve(minPct: 0, maxPct: 100)) is >= 0 and <= 100, $"at {lux} lx");
	}

	[TestMethod]
	public void The_Result_Stays_Inside_The_Physical_Range()
	{
		LightTarget lit = For(Curve(maxPct: 100), lux: 1000000).Apply(Target(90));

		Assert.IsTrue(lit.BrightnessPct is >= 0 and <= 100);
		Assert.AreEqual(100, lit.BrightnessPct);
	}

	[TestMethod]
	public void Everything_Else_About_The_Target_Survives_The_Curve()
	{
		LightTarget lit = For(Curve(), lux: 1000).Apply(Target(40));

		Assert.AreEqual("evening", lit.PeriodName);
		Assert.AreEqual(2700, lit.ColorTempKelvin);
		Assert.AreEqual(70, lit.BrightnessPct, 1e-9);
		Assert.IsTrue(lit.UsesDaylightCurve);
	}

	// ===================== the period decides =====================

	[TestMethod]
	public void A_Period_That_Specifies_Its_Brightness_Is_Returned_Untouched()
	{
		LightTarget scheduled = Target(40, onTheCurve: false);
		LightTarget result = For(Curve(), lux: 50000).Apply(scheduled);

		Assert.AreSame(scheduled, result, "not an equal target — the same one, having gone nowhere near the maths");
		Assert.AreEqual(40, result.BrightnessPct);
	}

	[TestMethod]
	public void A_Period_That_Specifies_Its_Brightness_Ignores_Even_A_Nonsensical_Curve()
	{
		AreaSettings settings = Curve(startLux: -5, fullLux: -10, minPct: 500, maxPct: 500, gamma: 0);
		LightTarget scheduled = Target(40, onTheCurve: false);

		Assert.AreSame(scheduled, For(settings, lux: double.NaN).Apply(scheduled));
	}

	[TestMethod]
	public void A_Period_On_The_Curve_Loses_Its_Own_Percentage_Entirely()
	{
		// The two periods differ only in what they ask for, and the curve answers both the same.
		Assert.AreEqual(
			For(Curve(), lux: 1000).Apply(Target(15)).BrightnessPct,
			For(Curve(), lux: 1000).Apply(Target(95)).BrightnessPct);
	}

	// ===================== per-room inheritance =====================

	[TestMethod]
	public void A_Room_That_States_Nothing_Inherits_The_Whole_Curve()
	{
		AreaSettings effective = new AreaConfig()
			.Effective(Curve(startLux: 200, fullLux: 20000, minPct: 25, maxPct: 85, gamma: 1.5));

		Assert.AreEqual(200d, effective.LuxBrightnessStartLux);
		Assert.AreEqual(20000d, effective.LuxBrightnessFullLux);
		Assert.AreEqual(25d, effective.LuxBrightnessMinPct);
		Assert.AreEqual(85d, effective.LuxBrightnessMaxPct);
		Assert.AreEqual(1.5, effective.LuxBrightnessGamma);
	}

	[TestMethod]
	public void A_Room_Overrides_Only_What_It_States()
	{
		AreaConfig room = new() { LuxBrightnessMaxPct = 60 };

		AreaSettings effective = room.Effective(Curve(startLux: 200, maxPct: 100));

		Assert.AreEqual(60d, effective.LuxBrightnessMaxPct, "the room's own bright end");
		Assert.AreEqual(200d, effective.LuxBrightnessStartLux, "and the house's anchors, untouched");
	}

	[TestMethod]
	public void A_Room_Can_Move_The_Dark_End_While_The_House_Leaves_It_Alone()
	{
		AreaSettings effective = new AreaConfig { LuxBrightnessMinPct = 10 }.Effective(Curve(minPct: 40, startLux: 200));

		Assert.AreEqual(10d, effective.LuxBrightnessMinPct);
		Assert.AreEqual(200d, effective.LuxBrightnessStartLux, "and the inherited curve survives the override");
	}

	// A fresh AreaSettings is what every pre-existing document binds to.
	[TestMethod]
	public void The_Defaults_Are_Sane()
	{
		AreaSettings fresh = new();

		Assert.IsTrue(fresh.LuxBrightnessStartLux > 0);
		Assert.IsTrue(fresh.LuxBrightnessFullLux > fresh.LuxBrightnessStartLux);
		Assert.IsTrue(fresh.LuxBrightnessMinPct is >= 0 and <= 100);
		Assert.IsTrue(fresh.LuxBrightnessMaxPct is >= 0 and <= 100);
		Assert.IsTrue(fresh.LuxBrightnessMaxPct > fresh.LuxBrightnessMinPct);
		Assert.IsTrue(fresh.LuxBrightnessGamma > 0);
	}

	// A target built with no curve flag is a target that specifies its own brightness, which is what a room
	// with no Levels row for a period means: TimePeriodConfig itself no longer carries the choice at all.
	[TestMethod]
	public void A_Target_With_No_Curve_Flag_Specifies_Its_Own_Brightness()
	{
		Assert.IsFalse(Target(40, onTheCurve: false).UsesDaylightCurve);
	}
}
