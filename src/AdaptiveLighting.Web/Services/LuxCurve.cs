using System.Text;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>A point in the chart's plot area, in the plot's own user units, with Y growing downwards as SVG does.</summary>
public sealed record CurvePoint(double X, double Y);

/// <summary>One setting the chart asks to have changed, keyed by <see cref="AreaSettings"/> property name.</summary>
public sealed record CurveEdit(string Key, double Value);

/// <summary>
///     Geometry for the daylight-brightness chart: where a lux reading sits on a logarithmic axis, what the
///     engine's curve looks like drawn across it, and what a dragged handle means.
/// </summary>
public static class LuxCurve
{
	public const double ViewWidth = 620;

	public const double ViewHeight = 300;

	/// <summary>The plot area's left edge, leaving a gutter for the percentage labels.</summary>
	public const double PlotLeft = 62;

	/// <summary>The plot area's top edge, leaving room for a handle drawn on the 100 % line.</summary>
	public const double PlotTop = 14;

	public const double PlotWidth = 542;

	// The rest of the height goes to the lux axis and its labels.
	public const double PlotHeight = 232;

	// One lux, not zero: log10(0) does not exist, and every fraction on this axis is taken from log10.
	public const double AxisMinLux = 1;

	public const double AxisMaxCeilingLux = 100_000;

	/// <summary>Where the shaping handle sits along the span between the two anchors, in log space.</summary>
	public const double ShapeFraction = 0.5;

	/// <summary>How far a handle's ink reaches from its centre, mark and focus ring together.</summary>
	// Kept in step with .luxc-handle in app.css. A narrow chart draws a larger mark and shifts .luxc-lux down to
	// match, so this figure is the one that holds where no shift applies.
	public const double HandleReach = 9;

	/// <summary>The furthest a handle's ink ever reaches, which is on the narrowest chart the stylesheet draws for.</summary>
	// r 12 plus half of the 5.5 focus ring, both from the max-width 560px block in app.css.
	public const double NarrowHandleReach = 15;

	/// <summary>How far inside the plot a handle standing at either end of the axis is drawn.</summary>
	// At least NarrowHandleReach, so the mark never crosses the plot's edge and therefore cannot reach an axis
	// label, which all sit outside it. Held only its own desktop reach in, a focused mark measured 0.1 px from
	// "0 %" at 390 px, where the stylesheet caps the axis type at its largest and the mark with it. The gutter
	// cannot widen to meet it either: "100 %" is 55 of the 62 units there.
	public const double HandleInset = 16;

	/// <summary>How far the drag surface reaches past the plot, so a handle on the boundary has target around it.</summary>
	// PlotTop, so the surface starts at the top of the drawing and can never spill out of it.
	public const double GrabMargin = PlotTop;

	/// <summary>The axis type's height in user units, as the stylesheet sets it on a chart at full width.</summary>
	// The label positions below are chosen against it: it is what decides whether a handle can cover one.
	public const double AxisTextHeight = 10;

	/// <summary>How far left of the plot the percentage labels end.</summary>
	public const double PercentLabelGap = 8;

	/// <summary>How far below the plot the lux readings are written.</summary>
	public const double LuxLabelDrop = 22;

	/// <summary>How far below the plot the word naming a decade is written.</summary>
	public const double DecadeWordDrop = 35;

	/// <summary>The top of the axis: a whole decade, wide enough for both anchors and the live reading.</summary>
	public static double AxisMaxLux(AreaSettings settings, double? reading)
	{
		ArgumentNullException.ThrowIfNull(settings);

		double highest = 10_000;

		foreach (double candidate in new[] { settings.LuxBrightnessStartLux, settings.LuxBrightnessFullLux, reading ?? 0 })
			if (double.IsFinite(candidate) && candidate > highest)
				highest = candidate;

		double decade = Math.Pow(10, Math.Ceiling(Math.Log10(highest)));

		return Math.Clamp(decade, 10, AxisMaxCeilingLux);
	}

	/// <summary>Where a reading sits across the axis, 0 at <see cref="AxisMinLux"/> to 1 at the top, clamped.</summary>
	public static double FractionOf(double lux, double axisMaxLux)
	{
		double span = Math.Log10(axisMaxLux) - Math.Log10(AxisMinLux);

		if (!double.IsFinite(span) || span <= 0 || !double.IsFinite(lux) || lux <= AxisMinLux)
			return 0;

		return Math.Clamp((Math.Log10(lux) - Math.Log10(AxisMinLux)) / span, 0, 1);
	}

	/// <summary>The reading a fraction across the axis stands for. <see cref="FractionOf"/> the other way.</summary>
	public static double LuxAt(double fraction, double axisMaxLux)
	{
		double span = Math.Log10(axisMaxLux) - Math.Log10(AxisMinLux);
		double clamped = Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1);

		return Math.Pow(10, Math.Log10(AxisMinLux) + (clamped * span));
	}

	/// <summary>The decade gridlines the axis carries: 1, 10, 100 … up to and including its top.</summary>
	public static IReadOnlyList<double> Decades(double axisMaxLux)
	{
		List<double> decades = [];

		for (double lux = AxisMinLux; lux <= axisMaxLux * 1.000001; lux *= 10)
			decades.Add(lux);

		return decades;
	}

	/// <summary>A brightness as a distance down from the plot's top edge.</summary>
	public static double Y(double brightnessPct)
	{
		double clamped = Math.Clamp(double.IsFinite(brightnessPct) ? brightnessPct : 0, 0, 100);

		return PlotHeight - (clamped / 100 * PlotHeight);
	}

	/// <summary>A fraction across the axis as a distance from the plot's left edge.</summary>
	public static double X(double fraction) => Math.Clamp(fraction, 0, 1) * PlotWidth;

	/// <summary>Where a handle standing at <paramref name="fraction"/> across the axis is drawn.</summary>
	// Held a mark's width inside the plot: drawn on the boundary, a handle at either end covers the axis label
	// underneath it. Only x is moved, because the curve is flat beyond both anchors and the mark stays on the line.
	public static double HandleX(double fraction) => Math.Clamp(X(fraction), HandleInset, PlotWidth - HandleInset);

	/// <summary>A fraction across the drag surface as a fraction across the plot, so the margin reads as the edge.</summary>
	public static double AcrossPlot(double fraction) => OffMargin(fraction, PlotWidth);

	/// <summary>The same down the plot.</summary>
	public static double DownPlot(double fraction) => OffMargin(fraction, PlotHeight);

	private static double OffMargin(double fraction, double length)
	{
		double across = Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1) * (length + (2 * GrabMargin));

		return Math.Clamp((across - GrabMargin) / length, 0, 1);
	}

	/// <summary>The engine's curve across the whole axis, as an SVG <c>d</c> in the plot's own user units.</summary>
	public static string Path(AreaSettings settings, double axisMaxLux, int samples = 112)
	{
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentOutOfRangeException.ThrowIfLessThan(samples, 2);

		StringBuilder path = new();

		// Every y comes from the engine's own Brightness, so the drawn line cannot differ from the applied rule.
		for (int step = 0; step < samples; step++)
		{
			double fraction = (double)step / (samples - 1);
			double brightness = LuxBrightnessCurve.Brightness(LuxAt(fraction, axisMaxLux), settings);

			path.Append(step == 0 ? 'M' : 'L')
				.Append(Num(X(fraction)))
				.Append(' ')
				.Append(Num(Y(brightness)));

			if (step != samples - 1)
				path.Append(' ');
		}

		return path.ToString();
	}

	/// <summary>The dark end: the reading the curve starts to climb from, and the level it holds below it.</summary>
	public static CurvePoint StartHandle(AreaSettings settings, double axisMaxLux)
	{
		ArgumentNullException.ThrowIfNull(settings);

		return new CurvePoint(
			HandleX(FractionOf(settings.LuxBrightnessStartLux, axisMaxLux)),
			Y(settings.LuxBrightnessMinPct));
	}

	/// <summary>The bright end: the reading the curve reaches its top at, and how bright that is.</summary>
	public static CurvePoint FullHandle(AreaSettings settings, double axisMaxLux)
	{
		ArgumentNullException.ThrowIfNull(settings);

		return new CurvePoint(HandleX(FractionOf(settings.LuxBrightnessFullLux, axisMaxLux)), Y(settings.LuxBrightnessMaxPct));
	}

	/// <summary>The shaping handle: halfway up the span, standing on the curve itself.</summary>
	public static CurvePoint ShapeHandle(AreaSettings settings, double axisMaxLux)
	{
		ArgumentNullException.ThrowIfNull(settings);

		double lux = ShapeLux(settings);

		return new CurvePoint(X(FractionOf(lux, axisMaxLux)), Y(LuxBrightnessCurve.Brightness(lux, settings)));
	}

	public static double ShapeLux(AreaSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);

		double start = Math.Max(settings.LuxBrightnessStartLux, AxisMinLux);
		double full = Math.Max(settings.LuxBrightnessFullLux, start * 10);

		return Math.Pow(10, Math.Log10(start) + (ShapeFraction * (Math.Log10(full) - Math.Log10(start))));
	}

	/// <summary>Whether the curve's two ends are far enough apart for a shaping handle to mean anything.</summary>
	// Either direction: a curve dragged to fall answers the same as one that rises.
	public static bool HasSpan(AreaSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);

		return Math.Abs(settings.LuxBrightnessMaxPct - settings.LuxBrightnessMinPct) > 0.5;
	}

	/// <summary>The exponent that would put the curve through a dragged point.</summary>
	public static double GammaFor(double fraction, double position, double min, double max)
	{
		if (!double.IsFinite(fraction) || !double.IsFinite(position))
			return Math.Clamp(1, min, max);

		// Both ends held off their asymptotes: a drag onto the top or bottom edge solves to gamma 0, and
		// Math.Pow(0, 0) is 1, which commands the daylight level in the dark.
		double along = Math.Clamp(fraction, 0.001, 0.999);
		double up = Math.Clamp(position, 0.002, 0.998);

		double gamma = Math.Log(up) / Math.Log(along);

		return double.IsFinite(gamma) ? Math.Clamp(gamma, min, max) : Math.Clamp(1, min, max);
	}

	/// <summary>A dragged lux value rounded to something a person would have typed.</summary>
	public static double RoundLux(double lux)
	{
		if (!double.IsFinite(lux))
			return AxisMinLux;

		// Grain follows the decade. A fixed one is wrong at both ends.
		double grain = lux switch
		{
			< 20 => 1,
			< 200 => 5,
			< 2_000 => 50,
			< 20_000 => 500,
			_ => 5_000
		};

		return Math.Max(AxisMinLux, Math.Round(lux / grain, MidpointRounding.AwayFromZero) * grain);
	}

	/// <summary>The inline style that lays the drag surface over the plot area, as percentages of the chart's box.</summary>
	// A GrabMargin wider than the plot on every side. Boundary and target on the same coordinate leaves a handle
	// resting on an axis with no pointer target beyond its centre; AcrossPlot maps the overreach back.
	public static string SurfaceStyle() => string.Concat(
		"left:", Num((PlotLeft - GrabMargin) / ViewWidth * 100), "%;",
		"top:", Num((PlotTop - GrabMargin) / ViewHeight * 100), "%;",
		"width:", Num((PlotWidth + (2 * GrabMargin)) / ViewWidth * 100), "%;",
		"height:", Num((PlotHeight + (2 * GrabMargin)) / ViewHeight * 100), "%;");

	// Invariant: under nb-NO a bare double renders 7,4 and the browser reads no length at all.
	public static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

	public static string Lux(double lux) => TokenFormat.Number(Math.Round(lux), "lx");
}
