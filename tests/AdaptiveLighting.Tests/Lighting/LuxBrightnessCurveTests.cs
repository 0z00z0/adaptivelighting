using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The daylight brightness adjustment: brighter outdoors raises the level the schedule asked for.
/// </summary>
/// <remarks>
///     The maths is pure, so it is tested as maths — no scheduler, no Home Assistant, no controller. The one
///     property worth more than the rest is the last section: a document that has not switched this on must be
///     handed back the very target it came in with, because two live houses run this engine and none of them
///     asked for the feature.
/// </remarks>
[TestClass]
public sealed class LuxBrightnessCurveTests
{
	/// <summary>Anchors a decade either side of 1 000 lx, so the log midpoint is a round number to assert on.</summary>
	private static AreaSettings Curve(
		bool enabled = true,
		double startLux = 100,
		double fullLux = 10000,
		double maxPct = 100,
		double gamma = 1.0) =>
		new()
		{
			LuxBrightnessEnabled = enabled,
			LuxBrightnessStartLux = startLux,
			LuxBrightnessFullLux = fullLux,
			LuxBrightnessMaxPct = maxPct,
			LuxBrightnessGamma = gamma
		};

	/// <summary>A target as the circadian calculator would hand one over, caps included.</summary>
	private static LightTarget Target(double brightnessPct, double? floor = null, double? cap = null) =>
		new("evening", brightnessPct, 2700, floor, cap);

	private static LuxBrightnessCurve For(AreaSettings settings, double? lux) => new(settings, () => lux);

	// ===================== the two anchors =====================

	[TestMethod]
	public void Below_The_Start_Anchor_The_Schedule_Is_Untouched()
	{
		Assert.AreEqual(40, LuxBrightnessCurve.Raise(40, 50, Curve()));
	}

	[TestMethod]
	public void The_Start_Anchor_Itself_Is_Untouched()
	{
		Assert.AreEqual(40, LuxBrightnessCurve.Raise(40, 100, Curve()),
			"the anchor is where the adjustment begins, so at it there is nothing yet to add");
	}

	[TestMethod]
	public void At_The_Full_Anchor_The_Maximum_Applies()
	{
		Assert.AreEqual(90, LuxBrightnessCurve.Raise(40, 10000, Curve(maxPct: 90)));
	}

	[TestMethod]
	public void Above_The_Full_Anchor_The_Maximum_Still_Applies()
	{
		Assert.AreEqual(90, LuxBrightnessCurve.Raise(40, 250000, Curve(maxPct: 90)),
			"direct sun is a decade past the anchor and must not extrapolate past the ceiling");
	}

	// ===================== the log interpolation =====================

	/// <summary>
	///     The whole reason for the feature's shape. With anchors at 100 and 10 000 lx the halfway point is the
	///     geometric mean, 1 000 lx — broad daylight, and where a room genuinely wants half the adjustment. A
	///     linear map puts 1 000 lx at 9 % of the range instead, which is the failure this is guarding against.
	/// </summary>
	[TestMethod]
	public void The_Midpoint_Of_The_Log_Range_Is_Halfway_Up_The_Curve()
	{
		Assert.AreEqual(70, LuxBrightnessCurve.Raise(40, 1000, Curve()), 1e-9,
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
		// 0.5^2 = 0.25 of the headroom, against 0.5 for a straight line.
		Assert.AreEqual(55, LuxBrightnessCurve.Raise(40, 1000, Curve(gamma: 2)), 1e-9);
	}

	[TestMethod]
	public void A_Gamma_Below_One_Lifts_The_Room_As_Soon_As_The_Light_Climbs()
	{
		// 0.5^0.5 ≈ 0.7071 of the headroom.
		Assert.AreEqual(82.426, LuxBrightnessCurve.Raise(40, 1000, Curve(gamma: 0.5)), 0.001);
	}

	[TestMethod]
	public void The_Anchors_Are_Unmoved_By_Any_Gamma()
	{
		Assert.AreEqual(40, LuxBrightnessCurve.Raise(40, 100, Curve(gamma: 4)));
		Assert.AreEqual(100, LuxBrightnessCurve.Raise(40, 10000, Curve(gamma: 4)));
		Assert.AreEqual(40, LuxBrightnessCurve.Raise(40, 100, Curve(gamma: 0.25)));
		Assert.AreEqual(100, LuxBrightnessCurve.Raise(40, 10000, Curve(gamma: 0.25)));
	}

	/// <summary>
	///     <c>Math.Pow(0, 0)</c> is 1, so a zero exponent taken at face value would command the full daylight level
	///     in pitch darkness. The validator refuses it; this is the belt to that pair of braces.
	/// </summary>
	[TestMethod]
	public void A_Zero_Gamma_Does_Not_Turn_Darkness_Into_Full_Daylight()
	{
		Assert.AreEqual(40, LuxBrightnessCurve.Raise(40, 50, Curve(gamma: 0)));
		Assert.AreEqual(40, LuxBrightnessCurve.Raise(40, 100, Curve(gamma: 0)));
		Assert.AreEqual(70, LuxBrightnessCurve.Raise(40, 1000, Curve(gamma: 0)), 1e-9,
			"an unusable exponent falls back to the straight line, not to nonsense");
	}

	[TestMethod]
	public void A_Negative_Or_Non_Finite_Gamma_Falls_Back_To_The_Straight_Line()
	{
		Assert.AreEqual(70, LuxBrightnessCurve.Raise(40, 1000, Curve(gamma: -2)), 1e-9);
		Assert.AreEqual(70, LuxBrightnessCurve.Raise(40, 1000, Curve(gamma: double.NaN)), 1e-9);
	}

	// ===================== readings that are not numbers =====================

	/// <summary>Sensors report 0 — and log10(0) is negative infinity, which is not a brightness.</summary>
	[TestMethod]
	public void Zero_Lux_Is_Safe_And_Leaves_The_Schedule_Alone()
	{
		double raised = LuxBrightnessCurve.Raise(40, 0, Curve());

		Assert.AreEqual(40, raised);
		Assert.IsTrue(double.IsFinite(raised));
	}

	[TestMethod]
	public void A_Negative_Reading_Is_Safe_And_Leaves_The_Schedule_Alone()
	{
		double raised = LuxBrightnessCurve.Raise(40, -12, Curve());

		Assert.AreEqual(40, raised);
		Assert.IsTrue(double.IsFinite(raised));
	}

	/// <summary>An infinity is not a bright day, it is a broken sensor, and it is treated as one.</summary>
	[TestMethod]
	public void A_NaN_Or_Infinite_Reading_Never_Reaches_A_Light()
	{
		Assert.AreEqual(40d, LuxBrightnessCurve.Raise(40, double.NaN, Curve()));
		Assert.AreEqual(40d, LuxBrightnessCurve.Raise(40, double.PositiveInfinity, Curve()));
		Assert.AreEqual(40d, LuxBrightnessCurve.Raise(40, double.NegativeInfinity, Curve()));
	}

	[TestMethod]
	public void No_Sensor_At_All_Falls_Back_To_Schedule_Only()
	{
		Assert.AreEqual(40, LuxBrightnessCurve.Raise(40, null, Curve()));
		Assert.AreEqual(Target(40), For(Curve(), lux: null).Apply(Target(40)),
			"a room with no reading is a room the schedule alone drives — not a room that fails");
	}

	// ===================== anchors that make no sense =====================

	[TestMethod]
	public void Inverted_Anchors_Make_The_Curve_Inert_Rather_Than_Wild()
	{
		Assert.AreEqual(40, LuxBrightnessCurve.Raise(40, 5000, Curve(startLux: 10000, fullLux: 100)));
	}

	[TestMethod]
	public void Equal_Anchors_Do_Not_Divide_By_Zero()
	{
		double raised = LuxBrightnessCurve.Raise(40, 5000, Curve(startLux: 1000, fullLux: 1000));

		Assert.AreEqual(40, raised);
		Assert.IsTrue(double.IsFinite(raised));
	}

	/// <summary>
	///     Two anchors can differ while their logarithms do not: <c>log10(100)</c> and
	///     <c>log10(100.00000000000003)</c> are the same double, so the span the interpolation divides by is zero
	///     and so is the numerator. The validator has no reason to object to either number — full is genuinely
	///     above start — and 0/0 is a NaN <see cref="LuxBrightnessCurve.Position"/> promises never to return.
	///     <see cref="LuxBrightnessCurve.Raise"/> survived it on its own finiteness check; anything drawing the
	///     curve did not.
	/// </summary>
	[TestMethod]
	public void Anchors_Whose_Logarithms_Are_Indistinguishable_Are_Inert_Rather_Than_NaN()
	{
		AreaSettings settings = Curve(startLux: 100, fullLux: 100.00000000000003);

		double position = LuxBrightnessCurve.Position(100.00000000000001, settings);

		Assert.IsTrue(double.IsFinite(position), "a position that is not a number is not a position");
		Assert.AreEqual(0, position, "a curve with nothing to interpolate across is inert, as every other degenerate anchor is");
		Assert.AreEqual(40, LuxBrightnessCurve.Raise(40, 100.00000000000001, settings));
	}

	[TestMethod]
	public void A_Start_Anchor_At_Or_Below_Zero_Has_No_Logarithm_And_Is_Inert()
	{
		Assert.AreEqual(40, LuxBrightnessCurve.Raise(40, 5000, Curve(startLux: 0)));
		Assert.AreEqual(40, LuxBrightnessCurve.Raise(40, 5000, Curve(startLux: -100)));
	}

	// ===================== it raises, it never lowers =====================

	[TestMethod]
	public void A_Ceiling_Below_The_Schedule_Leaves_The_Schedule_Alone()
	{
		Assert.AreEqual(70, LuxBrightnessCurve.Raise(70, 10000, Curve(maxPct: 20)),
			"a bright afternoon must not dim a room the schedule wanted at 70");
	}

	[TestMethod]
	public void The_Result_Never_Falls_Below_The_Schedule_At_Any_Reading()
	{
		foreach (double lux in new double[] { 0, 1, 99, 100, 101, 1000, 9999, 10000, 100000 })
			Assert.IsTrue(LuxBrightnessCurve.Raise(55, lux, Curve(maxPct: 80)) >= 55, $"at {lux} lx");
	}

	// ===================== the period keeps the last word =====================

	/// <summary>
	///     The rule that makes the feature safe to switch on: a night period capped at 30 % stays capped at 30 %,
	///     whatever the sky is doing. The adjustment proposes; the period disposes.
	/// </summary>
	[TestMethod]
	public void The_Periods_Cap_Beats_A_Bright_Reading()
	{
		LightTarget capped = For(Curve(), lux: 20000).Apply(Target(15, cap: 30));

		Assert.AreEqual(30d, capped.BrightnessPct);
		Assert.AreEqual(30d, capped.MaxBrightnessPct, "and the cap itself travels on, untouched");
	}

	[TestMethod]
	public void The_Periods_Floor_Still_Binds()
	{
		LightTarget floored = For(Curve(maxPct: 10), lux: 20000).Apply(Target(60, floor: 50));

		Assert.AreEqual(60d, floored.BrightnessPct, "a ceiling below the floor cannot pull the room under it");
		Assert.AreEqual(50d, floored.MinBrightnessPct);
	}

	[TestMethod]
	public void The_Result_Stays_Inside_The_Physical_Range()
	{
		LightTarget raised = For(Curve(maxPct: 100), lux: 1000000).Apply(Target(90));

		Assert.IsTrue(raised.BrightnessPct is >= 0 and <= 100);
		Assert.AreEqual(100, raised.BrightnessPct);
	}

	[TestMethod]
	public void Everything_Else_About_The_Target_Survives_The_Adjustment()
	{
		LightTarget raised = For(Curve(), lux: 1000).Apply(Target(40, floor: 10, cap: 95));

		Assert.AreEqual("evening", raised.PeriodName);
		Assert.AreEqual(2700, raised.ColorTempKelvin);
		Assert.AreEqual(10d, raised.MinBrightnessPct);
		Assert.AreEqual(95d, raised.MaxBrightnessPct);
		Assert.AreEqual(70, raised.BrightnessPct, 1e-9);
	}

	// ===================== off means off =====================

	/// <summary>
	///     The test that protects the houses already running this. Off does not mean "a curve that adds zero": the
	///     very same target instance comes back, so there is no arithmetic on the path at all and no rounding to
	///     argue about.
	/// </summary>
	[TestMethod]
	public void A_Disabled_Curve_Returns_The_Target_Untouched()
	{
		LightTarget scheduled = Target(40, floor: 10, cap: 95);
		LightTarget result = For(Curve(enabled: false), lux: 50000).Apply(scheduled);

		Assert.AreSame(scheduled, result, "not an equal target — the same one, having gone nowhere near the maths");
	}

	[TestMethod]
	public void A_Disabled_Curve_Ignores_Even_A_Nonsensical_One()
	{
		AreaSettings settings = Curve(enabled: false, startLux: -5, fullLux: -10, maxPct: 500, gamma: 0);
		LightTarget scheduled = Target(40);

		Assert.AreSame(scheduled, For(settings, lux: double.NaN).Apply(scheduled));
	}

	// ===================== per-room inheritance =====================

	[TestMethod]
	public void A_Room_That_States_Nothing_Inherits_The_Whole_Curve()
	{
		AreaSettings effective = new AreaConfig().Effective(Curve(startLux: 200, fullLux: 20000, maxPct: 85, gamma: 1.5));

		Assert.IsTrue(effective.LuxBrightnessEnabled);
		Assert.AreEqual(200d, effective.LuxBrightnessStartLux);
		Assert.AreEqual(20000d, effective.LuxBrightnessFullLux);
		Assert.AreEqual(85d, effective.LuxBrightnessMaxPct);
		Assert.AreEqual(1.5, effective.LuxBrightnessGamma);
	}

	[TestMethod]
	public void A_Room_Overrides_Only_What_It_States()
	{
		AreaConfig room = new() { LuxBrightnessMaxPct = 60 };

		AreaSettings effective = room.Effective(Curve(startLux: 200, maxPct: 100));

		Assert.AreEqual(60d, effective.LuxBrightnessMaxPct, "the room's own ceiling");
		Assert.AreEqual(200d, effective.LuxBrightnessStartLux, "and the house's anchors, untouched");
	}

	/// <summary>
	///     The reason the switch is its own <c>bool</c> rather than "leave the numbers blank to mean off": a
	///     bedroom has to be able to refuse a house-wide setting, and it must be able to do so without also
	///     throwing away the curve it would use if it changed its mind.
	/// </summary>
	[TestMethod]
	public void A_Room_Can_Switch_It_Off_While_The_House_Leaves_It_On()
	{
		AreaConfig bedroom = new() { LuxBrightnessEnabled = false };

		AreaSettings effective = bedroom.Effective(Curve(enabled: true, startLux: 200));

		Assert.IsFalse(effective.LuxBrightnessEnabled);
		Assert.AreEqual(200d, effective.LuxBrightnessStartLux, "and the inherited curve survives the refusal");
	}

	[TestMethod]
	public void A_Room_Can_Switch_It_On_While_The_House_Leaves_It_Off()
	{
		AreaSettings effective = new AreaConfig { LuxBrightnessEnabled = true }.Effective(Curve(enabled: false));

		Assert.IsTrue(effective.LuxBrightnessEnabled);
	}

	/// <summary>A fresh <see cref="AreaSettings"/> is the shape every pre-existing document binds to: off, and valid.</summary>
	[TestMethod]
	public void The_Defaults_Are_Off_And_Sane()
	{
		AreaSettings fresh = new();

		Assert.IsFalse(fresh.LuxBrightnessEnabled, "two live houses run this engine and neither asked for it");
		Assert.IsTrue(fresh.LuxBrightnessStartLux > 0);
		Assert.IsTrue(fresh.LuxBrightnessFullLux > fresh.LuxBrightnessStartLux);
		Assert.IsTrue(fresh.LuxBrightnessMaxPct is >= 0 and <= 100);
		Assert.IsTrue(fresh.LuxBrightnessGamma > 0);
	}
}
