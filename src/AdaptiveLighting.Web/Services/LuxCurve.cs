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

	/// <summary>The engine's curve across the whole axis, as an SVG <c>d</c> in the plot's own user units.</summary>
	public static string Path(AreaSettings settings, double baseBrightnessPct, double axisMaxLux, int samples = 112)
	{
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentOutOfRangeException.ThrowIfLessThan(samples, 2);

		StringBuilder path = new();

		// Every y comes from the engine's own Raise, so the drawn line cannot differ from the applied rule.
		for (int step = 0; step < samples; step++)
		{
			double fraction = (double)step / (samples - 1);
			double raised = LuxBrightnessCurve.Raise(baseBrightnessPct, LuxAt(fraction, axisMaxLux), settings);

			path.Append(step == 0 ? 'M' : 'L')
				.Append(Num(X(fraction)))
				.Append(' ')
				.Append(Num(Y(raised)));

			if (step != samples - 1)
				path.Append(' ');
		}

		return path.ToString();
	}

	/// <summary>The foot of the curve: the reading at which brightening starts, at the period's own level.</summary>
	public static CurvePoint StartHandle(AreaSettings settings, double baseBrightnessPct, double axisMaxLux)
	{
		ArgumentNullException.ThrowIfNull(settings);

		return new CurvePoint(X(FractionOf(settings.LuxBrightnessStartLux, axisMaxLux)), Y(baseBrightnessPct));
	}

	/// <summary>The head of the curve: the reading at which the room is as bright as it goes.</summary>
	public static CurvePoint FullHandle(AreaSettings settings, double axisMaxLux)
	{
		ArgumentNullException.ThrowIfNull(settings);

		return new CurvePoint(X(FractionOf(settings.LuxBrightnessFullLux, axisMaxLux)), Y(settings.LuxBrightnessMaxPct));
	}

	/// <summary>The shaping handle: halfway up the span, standing on the curve itself.</summary>
	public static CurvePoint ShapeHandle(AreaSettings settings, double baseBrightnessPct, double axisMaxLux)
	{
		ArgumentNullException.ThrowIfNull(settings);

		double lux = ShapeLux(settings);

		return new CurvePoint(
			X(FractionOf(lux, axisMaxLux)),
			Y(LuxBrightnessCurve.Raise(baseBrightnessPct, lux, settings)));
	}

	public static double ShapeLux(AreaSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);

		double start = Math.Max(settings.LuxBrightnessStartLux, AxisMinLux);
		double full = Math.Max(settings.LuxBrightnessFullLux, start * 10);

		return Math.Pow(10, Math.Log10(start) + (ShapeFraction * (Math.Log10(full) - Math.Log10(start))));
	}

	public static bool HasHeadroom(AreaSettings settings, double baseBrightnessPct)
	{
		ArgumentNullException.ThrowIfNull(settings);

		return settings.LuxBrightnessMaxPct - baseBrightnessPct > 0.5;
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
	public static string SurfaceStyle() => string.Concat(
		"left:", Num(PlotLeft / ViewWidth * 100), "%;",
		"top:", Num(PlotTop / ViewHeight * 100), "%;",
		"width:", Num(PlotWidth / ViewWidth * 100), "%;",
		"height:", Num(PlotHeight / ViewHeight * 100), "%;");

	// Invariant: under nb-NO a bare double renders 7,4 and the browser reads no length at all.
	public static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

	public static string Lux(double lux) => TokenFormat.Number(Math.Round(lux), "lx");
}
