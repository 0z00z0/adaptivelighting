using System.Globalization;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The geometry of the daylight-brightness chart.
/// </summary>
/// <remarks>
///     The line is asserted against <see cref="LuxBrightnessCurve"/> itself, never against numbers written here,
///     so the chart cannot drift into a plausible second opinion.
/// </remarks>
[TestClass]
public sealed class LuxCurveTests
{
	private static AreaSettings Settings() => new()
	{
		LuxBrightnessEnabled = true,
		LuxBrightnessStartLux = 100,
		LuxBrightnessFullLux = 10_000,
		LuxBrightnessMaxPct = 100,
		LuxBrightnessGamma = 1
	};

	private static (double X, double Y)[] Points(string path) =>
	[
		.. path.Split(['M', 'L'], StringSplitOptions.RemoveEmptyEntries)
			.Select(pair => pair.Trim().Split(' '))
			.Select(pair => (
				double.Parse(pair[0], CultureInfo.InvariantCulture),
				double.Parse(pair[1], CultureInfo.InvariantCulture)))
	];

	[TestMethod]
	public void Every_Decade_Takes_The_Same_Width()
	{
		Assert.AreEqual(0.00, LuxCurve.FractionOf(1, 10_000), 1e-9);
		Assert.AreEqual(0.25, LuxCurve.FractionOf(10, 10_000), 1e-9);
		Assert.AreEqual(0.50, LuxCurve.FractionOf(100, 10_000), 1e-9);
		Assert.AreEqual(0.75, LuxCurve.FractionOf(1_000, 10_000), 1e-9);
		Assert.AreEqual(1.00, LuxCurve.FractionOf(10_000, 10_000), 1e-9);
	}

	[TestMethod]
	public void A_Position_Round_Trips_Through_The_Axis()
	{
		foreach (double lux in new[] { 2.0, 40.0, 170.0, 1_000.0, 9_999.0 })
			Assert.AreEqual(lux, LuxCurve.LuxAt(LuxCurve.FractionOf(lux, 10_000), 10_000), lux * 1e-9);
	}

	[TestMethod]
	public void Readings_Off_The_Ends_Are_Held_Inside_The_Plot()
	{
		Assert.AreEqual(0, LuxCurve.FractionOf(0, 10_000), 1e-9);
		Assert.AreEqual(0, LuxCurve.FractionOf(-5, 10_000), 1e-9);
		Assert.AreEqual(0, LuxCurve.FractionOf(double.NaN, 10_000), 1e-9);
		Assert.AreEqual(1, LuxCurve.FractionOf(500_000, 10_000), 1e-9);
	}

	[TestMethod]
	public void The_Axis_Stretches_By_Whole_Decades_And_Never_To_A_Snug_Fit()
	{
		AreaSettings settings = Settings();

		Assert.AreEqual(10_000, LuxCurve.AxisMaxLux(settings, null));
		Assert.AreEqual(10_000, LuxCurve.AxisMaxLux(settings, 170));

		settings.LuxBrightnessFullLux = 30_000;
		Assert.AreEqual(100_000, LuxCurve.AxisMaxLux(settings, null));

		Assert.AreEqual(100_000, LuxCurve.AxisMaxLux(Settings(), 65_535), "a real sensor's ceiling still fits on it");

		foreach (double axis in new[] { LuxCurve.AxisMaxLux(Settings(), null), LuxCurve.AxisMaxLux(Settings(), 65_535) })
			Assert.AreEqual(Math.Round(Math.Log10(axis)), Math.Log10(axis), 1e-9, "and the top of the axis is always a decade");
	}

	[TestMethod]
	public void The_Gridlines_Are_The_Decades_The_Axis_Covers()
	{
		CollectionAssert.AreEqual(new[] { 1.0, 10.0, 100.0, 1_000.0, 10_000.0 }, LuxCurve.Decades(10_000).ToArray());
	}

	[TestMethod]
	public void The_Line_Is_The_Engines_Own_Arithmetic()
	{
		AreaSettings settings = Settings();
		settings.LuxBrightnessGamma = 1.6;

		(double X, double Y)[] points = Points(LuxCurve.Path(settings, 40, 10_000, samples: 21));

		foreach ((double x, double y) in points)
		{
			double lux = LuxCurve.LuxAt(x / LuxCurve.PlotWidth, 10_000);

			// The path is written to three decimals, so half a thousandth of a user unit is the tightest useful bound.
			Assert.AreEqual(LuxCurve.Y(LuxBrightnessCurve.Raise(40, lux, settings)), y, 5e-4);
		}
	}

	[TestMethod]
	public void The_Line_Starts_At_The_Schedules_Level_And_Only_Climbs()
	{
		(double X, double Y)[] points = Points(LuxCurve.Path(Settings(), 40, 10_000, samples: 40));

		Assert.AreEqual(LuxCurve.Y(40), points[0].Y, 1e-6);

		for (int step = 1; step < points.Length; step++)
			Assert.IsTrue(points[step].Y <= points[step - 1].Y + 1e-9, "SVG counts downward, so climbing is decreasing y");
	}

	[TestMethod]
	public void The_Brightness_Axis_Is_Not_Upside_Down()
	{
		Assert.AreEqual(0, LuxCurve.Y(100), 1e-9);
		Assert.AreEqual(LuxCurve.PlotHeight, LuxCurve.Y(0), 1e-9);
	}

	[TestMethod]
	public void The_Handles_Stand_On_The_Values_They_Set()
	{
		AreaSettings settings = Settings();
		settings.LuxBrightnessMaxPct = 85;

		CurvePoint start = LuxCurve.StartHandle(settings, 40, 10_000);
		CurvePoint full = LuxCurve.FullHandle(settings, 10_000);

		Assert.AreEqual(LuxCurve.X(LuxCurve.FractionOf(100, 10_000)), start.X, 1e-9);
		Assert.AreEqual(LuxCurve.Y(40), start.Y, 1e-9, "the foot of the curve is the period's own level");
		Assert.AreEqual(LuxCurve.X(1), full.X, 1e-9);
		Assert.AreEqual(LuxCurve.Y(85), full.Y, 1e-9);
	}

	[TestMethod]
	public void The_Shaping_Handle_Is_On_The_Curve()
	{
		foreach (double gamma in new[] { 0.4, 1.0, 2.5 })
		{
			AreaSettings settings = Settings();
			settings.LuxBrightnessGamma = gamma;

			CurvePoint handle = LuxCurve.ShapeHandle(settings, 30, 10_000);
			double lux = LuxCurve.LuxAt(handle.X / LuxCurve.PlotWidth, 10_000);

			Assert.AreEqual(LuxCurve.Y(LuxBrightnessCurve.Raise(30, lux, settings)), handle.Y, 1e-6);
		}
	}

	/// <summary>The engine's headroom is never a signed difference, so with none every exponent draws one flat line.</summary>
	[TestMethod]
	public void There_Is_No_Shaping_Handle_Without_Headroom()
	{
		AreaSettings settings = Settings();
		settings.LuxBrightnessMaxPct = 60;

		Assert.IsTrue(LuxCurve.HasHeadroom(settings, 40));
		Assert.IsFalse(LuxCurve.HasHeadroom(settings, 60));
		Assert.IsFalse(LuxCurve.HasHeadroom(settings, 80));
	}

	[TestMethod]
	public void The_Dragged_Shape_Reads_Back_The_Exponent_That_Drew_It()
	{
		foreach (double gamma in new[] { 0.5, 1.0, 2.0, 3.5 })
		{
			double position = Math.Pow(LuxCurve.ShapeFraction, gamma);

			Assert.AreEqual(gamma, LuxCurve.GammaFor(LuxCurve.ShapeFraction, position, 0.1, 5), 1e-6);
		}
	}

	/// <summary><c>Math.Pow(0, 0)</c> is 1, so a gamma of zero hands back the full daylight ceiling in the dark.</summary>
	[TestMethod]
	public void A_Drag_To_The_Edge_Cannot_Produce_A_Zero_Exponent()
	{
		foreach (double position in new[] { 0.0, 1.0, -3.0, 12.0, double.NaN })
		{
			double gamma = LuxCurve.GammaFor(LuxCurve.ShapeFraction, position, 0.1, 5);

			Assert.IsTrue(gamma >= 0.1 && gamma <= 5, $"position {position} produced {gamma}");
		}
	}

	[TestMethod]
	public void A_Dragged_Reading_Rounds_To_The_Decades_Own_Grain()
	{
		Assert.AreEqual(3, LuxCurve.RoundLux(3.4));
		Assert.AreEqual(45, LuxCurve.RoundLux(43));
		Assert.AreEqual(950, LuxCurve.RoundLux(948));
		Assert.AreEqual(7_500, LuxCurve.RoundLux(7_431));
		Assert.AreEqual(LuxCurve.AxisMinLux, LuxCurve.RoundLux(0), "and never below the bottom of the axis");
		Assert.AreEqual(LuxCurve.AxisMinLux, LuxCurve.RoundLux(double.NaN));
	}

	/// <summary>
	///     Under nb-NO a bare double renders 7.4 as "7,4". In a path a comma is a coordinate separator; in a
	///     length it is nothing, and the drag surface silently covers the chart.
	/// </summary>
	[TestMethod]
	public void The_Geometry_Is_Written_Invariantly_Whatever_The_Machine_Speaks()
	{
		CultureInfo original = CultureInfo.CurrentCulture;

		try
		{
			CultureInfo.CurrentCulture = new CultureInfo("nb-NO");

			Assert.IsFalse(LuxCurve.Path(Settings(), 40, 10_000).Contains(',', StringComparison.Ordinal));
			Assert.IsFalse(LuxCurve.SurfaceStyle().Contains(',', StringComparison.Ordinal));
			Assert.IsFalse(LuxCurve.Num(62.5).Contains(',', StringComparison.Ordinal));
		}
		finally
		{
			CultureInfo.CurrentCulture = original;
		}
	}

	[TestMethod]
	public void The_Drag_Surface_Covers_The_Plot_And_Nothing_Else()
	{
		string style = LuxCurve.SurfaceStyle();

		Assert.IsTrue(style.Contains($"left:{LuxCurve.Num(LuxCurve.PlotLeft / LuxCurve.ViewWidth * 100)}%", StringComparison.Ordinal), style);
		Assert.IsTrue(style.Contains($"width:{LuxCurve.Num(LuxCurve.PlotWidth / LuxCurve.ViewWidth * 100)}%", StringComparison.Ordinal), style);
		Assert.IsTrue(LuxCurve.PlotLeft + LuxCurve.PlotWidth <= LuxCurve.ViewWidth);
		Assert.IsTrue(LuxCurve.PlotTop + LuxCurve.PlotHeight <= LuxCurve.ViewHeight);
	}

	[TestMethod]
	public void One_Point_Is_Not_A_Curve()
	{
		Assert.ThrowsException<ArgumentOutOfRangeException>(() => LuxCurve.Path(Settings(), 40, 10_000, samples: 1));
	}
}
