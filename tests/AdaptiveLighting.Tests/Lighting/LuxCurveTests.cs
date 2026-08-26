using System.Globalization;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The geometry of the daylight-brightness chart, asserted against <see cref="LuxBrightnessCurve"/> and never against numbers written here.</summary>
[TestClass]
public sealed class LuxCurveTests
{
	private static AreaSettings Settings() => new()
	{
		LuxBrightnessStartLux = 100,
		LuxBrightnessFullLux = 10_000,
		LuxBrightnessMinPct = 40,
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

		(double X, double Y)[] points = Points(LuxCurve.Path(settings, 10_000, samples: 21));

		foreach ((double x, double y) in points)
		{
			double lux = LuxCurve.LuxAt(x / LuxCurve.PlotWidth, 10_000);

			// The path is written to three decimals, so half a thousandth of a user unit is the tightest useful bound.
			Assert.AreEqual(LuxCurve.Y(LuxBrightnessCurve.Brightness(lux, settings)), y, 5e-4);
		}
	}

	[TestMethod]
	public void The_Line_Starts_At_The_Dark_End_And_Only_Climbs()
	{
		(double X, double Y)[] points = Points(LuxCurve.Path(Settings(), 10_000, samples: 40));

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

	/// <summary>A handle stands on its own reading, save at the ends of the axis, where it is held a mark inside.</summary>
	[TestMethod]
	public void The_Handles_Stand_On_The_Values_They_Set_Except_Where_The_Axis_Runs_Out()
	{
		AreaSettings settings = Settings();
		settings.LuxBrightnessMaxPct = 85;

		CurvePoint start = LuxCurve.StartHandle(settings, 10_000);
		CurvePoint full = LuxCurve.FullHandle(settings, 10_000);

		Assert.AreEqual(LuxCurve.X(LuxCurve.FractionOf(100, 10_000)), start.X, 1e-9);
		Assert.AreEqual(LuxCurve.Y(40), start.Y, 1e-9, "the foot of the curve is the curve's own dark end");
		Assert.AreEqual(LuxCurve.PlotWidth - LuxCurve.HandleInset, full.X, 1e-9, "the bright end is at the top of the axis, so it is held inside");
		Assert.AreEqual(LuxCurve.Y(85), full.Y, 1e-9);
	}

	/// <summary>Neither handle is bounded by the schedule: both reach every percentage a lamp can take.</summary>
	[TestMethod]
	public void Both_Ends_Reach_The_Whole_Range()
	{
		AreaSettings settings = Settings();

		foreach (double percent in new double[] { 0, 50, 100 })
		{
			settings.LuxBrightnessMinPct = percent;
			settings.LuxBrightnessMaxPct = percent;

			Assert.AreEqual(LuxCurve.Y(percent), LuxCurve.StartHandle(settings, 10_000).Y, 1e-9);
			Assert.AreEqual(LuxCurve.Y(percent), LuxCurve.FullHandle(settings, 10_000).Y, 1e-9);
		}
	}

	[TestMethod]
	public void The_Shaping_Handle_Is_On_The_Curve()
	{
		foreach (double gamma in new[] { 0.4, 1.0, 2.5 })
		{
			AreaSettings settings = Settings();
			settings.LuxBrightnessGamma = gamma;

			CurvePoint handle = LuxCurve.ShapeHandle(settings, 10_000);
			double lux = LuxCurve.LuxAt(handle.X / LuxCurve.PlotWidth, 10_000);

			Assert.AreEqual(LuxCurve.Y(LuxBrightnessCurve.Brightness(lux, settings)), handle.Y, 1e-6);
		}
	}

	/// <summary>A flat curve has nothing to shape, in either direction.</summary>
	[TestMethod]
	public void There_Is_No_Shaping_Handle_Without_A_Span()
	{
		AreaSettings settings = Settings();
		settings.LuxBrightnessMaxPct = 60;

		settings.LuxBrightnessMinPct = 40;
		Assert.IsTrue(LuxCurve.HasSpan(settings), "40 to 60 is a span to shape");

		settings.LuxBrightnessMinPct = 60;
		Assert.IsFalse(LuxCurve.HasSpan(settings), "the two ends are the same level");

		settings.LuxBrightnessMinPct = 80;
		Assert.IsTrue(LuxCurve.HasSpan(settings), "a curve that falls is still a curve to shape");
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

	/// <summary>Under nb-NO a bare double renders 7.4 as "7,4", which in a path is a coordinate separator and in a length is nothing.</summary>
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

	/// <summary>The surface overreaches the plot, so a handle sitting on the boundary has target on both sides of it.</summary>
	[TestMethod]
	public void The_Drag_Surface_Reaches_A_Margin_Past_The_Plot_On_Every_Side()
	{
		string style = LuxCurve.SurfaceStyle();

		Assert.IsTrue(LuxCurve.GrabMargin > 0, "a surface that stops where the plot stops cannot be pointed at on the edge");

		double left = LuxCurve.PlotLeft - LuxCurve.GrabMargin;
		double width = LuxCurve.PlotWidth + (2 * LuxCurve.GrabMargin);
		double height = LuxCurve.PlotHeight + (2 * LuxCurve.GrabMargin);

		Assert.IsTrue(style.Contains($"left:{LuxCurve.Num(left / LuxCurve.ViewWidth * 100)}%", StringComparison.Ordinal), style);
		Assert.IsTrue(style.Contains($"width:{LuxCurve.Num(width / LuxCurve.ViewWidth * 100)}%", StringComparison.Ordinal), style);
		Assert.IsTrue(style.Contains($"height:{LuxCurve.Num(height / LuxCurve.ViewHeight * 100)}%", StringComparison.Ordinal), style);

		// Still inside the drawing, so the target cannot swallow a control beside the chart.
		Assert.IsTrue(left >= 0);
		Assert.IsTrue(LuxCurve.PlotTop - LuxCurve.GrabMargin >= 0);
		Assert.IsTrue(left + width <= LuxCurve.ViewWidth);
		Assert.IsTrue(LuxCurve.PlotTop - LuxCurve.GrabMargin + height <= LuxCurve.ViewHeight);
	}

	[TestMethod]
	public void A_Pointer_On_The_Margin_Reads_As_The_Edge_Of_The_Plot()
	{
		double across = LuxCurve.GrabMargin / (LuxCurve.PlotWidth + (2 * LuxCurve.GrabMargin));
		double down = LuxCurve.GrabMargin / (LuxCurve.PlotHeight + (2 * LuxCurve.GrabMargin));

		Assert.AreEqual(0, LuxCurve.AcrossPlot(across), 1e-9, "the plot's left edge is a margin in from the surface's");
		Assert.AreEqual(1, LuxCurve.AcrossPlot(1 - across), 1e-9);
		Assert.AreEqual(0, LuxCurve.DownPlot(down), 1e-9);
		Assert.AreEqual(1, LuxCurve.DownPlot(1 - down), 1e-9);

		Assert.AreEqual(0, LuxCurve.AcrossPlot(0), 1e-9, "and the margin itself reads as the edge, never past it");
		Assert.AreEqual(1, LuxCurve.AcrossPlot(1), 1e-9);
		Assert.AreEqual(0, LuxCurve.DownPlot(0), 1e-9);
		Assert.AreEqual(1, LuxCurve.DownPlot(1), 1e-9);

		Assert.AreEqual(0.5, LuxCurve.AcrossPlot(0.5), 1e-9, "the middle of the surface is the middle of the plot");
		Assert.AreEqual(0.5, LuxCurve.DownPlot(0.5), 1e-9);
	}

	/// <summary>A handle standing on an extreme is drawn a mark's width inside, so it never covers its own axis label.</summary>
	[TestMethod]
	public void A_Handle_At_Either_End_Of_The_Axis_Is_Drawn_Inside_The_Plot()
	{
		AreaSettings settings = Settings();
		settings.LuxBrightnessStartLux = LuxCurve.AxisMinLux;
		settings.LuxBrightnessFullLux = 10_000;

		double left = LuxCurve.StartHandle(settings, 10_000).X;
		double right = LuxCurve.FullHandle(settings, 10_000).X;

		Assert.IsTrue(left >= LuxCurve.HandleInset, $"the dark end is drawn at {left}");
		Assert.IsTrue(right <= LuxCurve.PlotWidth - LuxCurve.HandleInset, $"the bright end is drawn at {right}");

		// Every axis label sits outside the plot, so a mark that stays inside it cannot reach one.
		Assert.IsTrue(LuxCurve.HandleInset >= LuxCurve.NarrowHandleReach,
			$"a mark reaching {LuxCurve.NarrowHandleReach} drawn {LuxCurve.HandleInset} in crosses the plot's edge");
		Assert.IsTrue(LuxCurve.PercentLabelGap > 0, "and the labels are outside it");

		// A handle away from the ends keeps standing on its own reading.
		settings.LuxBrightnessStartLux = 100;
		Assert.AreEqual(
			LuxCurve.X(LuxCurve.FractionOf(100, 10_000)),
			LuxCurve.StartHandle(settings, 10_000).X,
			1e-9);
	}

	/// <summary>Only x is nudged: a handle moved down off the 100 % line would claim a level it does not set.</summary>
	[TestMethod]
	public void A_Handle_Is_Never_Nudged_Off_The_Level_It_Sets()
	{
		AreaSettings settings = Settings();
		settings.LuxBrightnessStartLux = LuxCurve.AxisMinLux;
		settings.LuxBrightnessMinPct = 100;
		settings.LuxBrightnessMaxPct = 0;
		settings.LuxBrightnessFullLux = 10_000;

		Assert.AreEqual(LuxCurve.Y(100), LuxCurve.StartHandle(settings, 10_000).Y, 1e-9);
		Assert.AreEqual(LuxCurve.Y(0), LuxCurve.FullHandle(settings, 10_000).Y, 1e-9);
	}

	/// <summary>The lux labels clear a handle resting on the 0 % line, mark and focus ring together.</summary>
	[TestMethod]
	public void The_Lux_Labels_Sit_Clear_Of_A_Handle_Standing_On_The_Foot_Of_The_Plot()
	{
		// The label hangs from its baseline, so what a handle can cover is the drop less a line of type.
		Assert.IsTrue(LuxCurve.LuxLabelDrop - LuxCurve.AxisTextHeight >= LuxCurve.HandleReach,
			$"a label dropped {LuxCurve.LuxLabelDrop} tops out under a handle reaching {LuxCurve.HandleReach}");

		Assert.IsTrue(LuxCurve.DecadeWordDrop > LuxCurve.LuxLabelDrop + LuxCurve.AxisTextHeight,
			"the decade word sits under the reading it names, not on it");

		// The lowest ink on the chart still has to fit the drawing.
		Assert.IsTrue(LuxCurve.PlotTop + LuxCurve.PlotHeight + LuxCurve.DecadeWordDrop < LuxCurve.ViewHeight);
	}

	[TestMethod]
	public void One_Point_Is_Not_A_Curve()
	{
		Assert.ThrowsException<ArgumentOutOfRangeException>(() => LuxCurve.Path(Settings(), 10_000, samples: 1));
	}
}
