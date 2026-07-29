using System.Text;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>A point in the chart's plot area, in the plot's own user units.</summary>
/// <param name="X">Distance from the plot's left edge.</param>
/// <param name="Y">Distance from the plot's top edge — SVG's direction, so 0 is the top of the scale.</param>
public sealed record CurvePoint(double X, double Y);

/// <summary>
///     One setting the chart asks to have changed.
/// </summary>
/// <remarks>
///     The chart writes nothing itself, exactly as <c>SentenceView</c> writes nothing: the value leaves through a
///     callback and the page puts it through its own debounced save, so a drag and a typed number reach the file
///     by the one path. A handle can carry two settings — the top of the curve is both an illuminance and a
///     brightness — so an edit is one key at a time and a drag may raise two.
/// </remarks>
/// <param name="Key">The <see cref="AreaSettings"/> property name, the same key the settings rows carry.</param>
/// <param name="Value">The new value, in the unit that setting's row shows.</param>
public sealed record CurveEdit(string Key, double Value);

/// <summary>
///     The geometry of the daylight-brightness chart: where a lux reading sits on a logarithmic axis, what the
///     engine's curve looks like drawn across it, and what a dragged handle means.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why the x axis is logarithmic.</b> The same reason the engine interpolates that way — illuminance
///         spans orders of magnitude and perception follows the logarithm, so a linear axis would spend nine
///         tenths of its width on the top decade and squash the range a room actually moves through into a
///         sliver. See <see cref="LuxBrightnessCurve"/>, whose argument this axis is the picture of.
///     </para>
///     <para>
///         <b>The curve drawn is the engine's own.</b> Every y comes from <see cref="LuxBrightnessCurve.Raise"/>
///         rather than from a re-derivation here, so the line cannot be a plausible curve that differs from the
///         one the house applies. That is the whole value of drawing it: a chart that disagrees with the engine
///         teaches the wrong thing with more authority than no chart at all.
///     </para>
///     <para>
///         Pure, and separate from the component, for this repo's usual reason: geometry generated inside markup
///         is geometry nothing can assert about, and an inverted axis draws a perfectly plausible picture of the
///         opposite rule.
///     </para>
/// </remarks>
public static class LuxCurve
{
	/// <summary>The chart's viewBox width, in user units.</summary>
	public const double ViewWidth = 620;

	/// <summary>The chart's viewBox height.</summary>
	public const double ViewHeight = 300;

	/// <summary>
	///     The plot area's left edge — the gutter the percentage labels sit in.
	/// </summary>
	/// <remarks>
	///     Wide enough for "100 %" at the size the labels are drawn on a phone, which is nearly twice their
	///     desktop size in user units because the whole chart is a third as wide there. Measured: at a narrower
	///     gutter the top label ran off the left of the viewBox and simply vanished, on the one screen size where
	///     the axis needs the most help.
	/// </remarks>
	public const double PlotLeft = 62;

	/// <summary>The plot area's top edge, leaving room for a handle drawn on the 100 % line.</summary>
	public const double PlotTop = 14;

	/// <summary>The plot area's width.</summary>
	public const double PlotWidth = 542;

	/// <summary>The plot area's height — the rest goes to the lux axis and its labels.</summary>
	public const double PlotHeight = 232;

	/// <summary>
	///     The darkest reading the axis starts at.
	/// </summary>
	/// <remarks>
	///     One lux, not zero: <c>log10(0)</c> does not exist, and one lux is already the bottom of the useful
	///     range — a shaded outdoor sensor reads 1–3 lx at night, which is the decade this axis has to open on.
	/// </remarks>
	public const double AxisMinLux = 1;

	/// <summary>
	///     The brightest the axis will ever stretch to: one whole decade above the most a lux setting takes.
	/// </summary>
	/// <remarks>
	///     Not <see cref="RoomSettings.MaxLux"/> itself, deliberately. A 16-bit sensor's 65 535 lx is not a
	///     decade, so an axis topped there would give its last decade four fifths of the plot and the four below
	///     it a fifth between them — which is the equal-width property this axis exists for, silently broken on
	///     exactly the houses whose anchors are high enough to notice. A round decade above it costs a few per
	///     cent of the plot mapping to values the settings clamp, at the far right edge, where nothing is aimed.
	/// </remarks>
	public const double AxisMaxCeilingLux = 100_000;

	/// <summary>Where the shaping handle is placed along the span between the two anchors.</summary>
	/// <remarks>
	///     Halfway in <i>log</i> space, which is where the exponent's effect is largest and therefore where a
	///     drag has the most resolution. It is also the point the engine's own worked example names: with anchors
	///     at 100 and 10 000 lx, halfway is 1 000 lx.
	/// </remarks>
	public const double ShapeFraction = 0.5;

	/// <summary>
	///     The top of the axis: the decade that contains everything the chart has to show.
	/// </summary>
	/// <remarks>
	///     Ten thousand lux by default — a bright overcast day, and the value the schema ships as the full-
	///     brightness anchor — stretching by whole decades when an anchor or a live reading sits above it. Whole
	///     decades rather than a snug fit so the gridlines stay at round numbers a person recognises, and so the
	///     axis does not visibly rescale while a handle is being dragged near its top.
	/// </remarks>
	/// <param name="settings">The room's curve, for its two anchors.</param>
	/// <param name="reading">The room's live reading, or <c>null</c> when it has no sensor.</param>
	/// <exception cref="ArgumentNullException"><paramref name="settings"/> is <c>null</c>.</exception>
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

	/// <summary>
	///     Where a reading sits across the axis, from 0 at <see cref="AxisMinLux"/> to 1 at the top.
	/// </summary>
	/// <param name="lux">The reading.</param>
	/// <param name="axisMaxLux">The top of the axis, from <see cref="AxisMaxLux"/>.</param>
	/// <returns>A fraction, clamped to 0–1 so nothing is ever drawn outside the plot.</returns>
	public static double FractionOf(double lux, double axisMaxLux)
	{
		double span = Math.Log10(axisMaxLux) - Math.Log10(AxisMinLux);

		if (!double.IsFinite(span) || span <= 0 || !double.IsFinite(lux) || lux <= AxisMinLux)
			return 0;

		return Math.Clamp((Math.Log10(lux) - Math.Log10(AxisMinLux)) / span, 0, 1);
	}

	/// <summary>The reading a fraction across the axis stands for — <see cref="FractionOf"/> the other way.</summary>
	/// <param name="fraction">How far across the axis, 0–1.</param>
	/// <param name="axisMaxLux">The top of the axis.</param>
	public static double LuxAt(double fraction, double axisMaxLux)
	{
		double span = Math.Log10(axisMaxLux) - Math.Log10(AxisMinLux);
		double clamped = Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1);

		return Math.Pow(10, Math.Log10(AxisMinLux) + (clamped * span));
	}

	/// <summary>The decade gridlines the axis carries: 1, 10, 100 … up to and including its top.</summary>
	/// <param name="axisMaxLux">The top of the axis.</param>
	public static IReadOnlyList<double> Decades(double axisMaxLux)
	{
		List<double> decades = [];

		for (double lux = AxisMinLux; lux <= axisMaxLux * 1.000001; lux *= 10)
			decades.Add(lux);

		return decades;
	}

	/// <summary>A brightness as a distance down from the plot's top edge.</summary>
	/// <param name="brightnessPct">The brightness, 0–100.</param>
	public static double Y(double brightnessPct)
	{
		double clamped = Math.Clamp(double.IsFinite(brightnessPct) ? brightnessPct : 0, 0, 100);

		return PlotHeight - (clamped / 100 * PlotHeight);
	}

	/// <summary>A fraction across the axis as a distance from the plot's left edge.</summary>
	/// <param name="fraction">How far across, 0–1.</param>
	public static double X(double fraction) => Math.Clamp(fraction, 0, 1) * PlotWidth;

	/// <summary>
	///     The engine's curve across the whole axis, as an SVG <c>d</c> in the plot's own user units.
	/// </summary>
	/// <remarks>
	///     Sampled evenly in <i>screen</i> space rather than in lux, so the sample spacing is what the eye sees
	///     and the bottom decade is drawn at the same fidelity as the top. Every sample is
	///     <see cref="LuxBrightnessCurve.Raise"/>'s answer for that reading.
	/// </remarks>
	/// <param name="settings">The room's curve.</param>
	/// <param name="baseBrightnessPct">What the period asks for — the level the curve rises from.</param>
	/// <param name="axisMaxLux">The top of the axis.</param>
	/// <param name="samples">How many points to draw. One per five user units is smooth at this size.</param>
	/// <exception cref="ArgumentNullException"><paramref name="settings"/> is <c>null</c>.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="samples"/> is less than two.</exception>
	public static string Path(AreaSettings settings, double baseBrightnessPct, double axisMaxLux, int samples = 112)
	{
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentOutOfRangeException.ThrowIfLessThan(samples, 2);

		StringBuilder path = new();

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

	/// <summary>Where the foot of the curve sits: the reading at which brightening starts, at the period's own level.</summary>
	/// <param name="settings">The room's curve.</param>
	/// <param name="baseBrightnessPct">What the period asks for.</param>
	/// <param name="axisMaxLux">The top of the axis.</param>
	/// <exception cref="ArgumentNullException"><paramref name="settings"/> is <c>null</c>.</exception>
	public static CurvePoint StartHandle(AreaSettings settings, double baseBrightnessPct, double axisMaxLux)
	{
		ArgumentNullException.ThrowIfNull(settings);

		return new CurvePoint(X(FractionOf(settings.LuxBrightnessStartLux, axisMaxLux)), Y(baseBrightnessPct));
	}

	/// <summary>Where the head of the curve sits: the reading at which the room is as bright as it goes.</summary>
	/// <param name="settings">The room's curve.</param>
	/// <param name="axisMaxLux">The top of the axis.</param>
	/// <exception cref="ArgumentNullException"><paramref name="settings"/> is <c>null</c>.</exception>
	public static CurvePoint FullHandle(AreaSettings settings, double axisMaxLux)
	{
		ArgumentNullException.ThrowIfNull(settings);

		return new CurvePoint(X(FractionOf(settings.LuxBrightnessFullLux, axisMaxLux)), Y(settings.LuxBrightnessMaxPct));
	}

	/// <summary>
	///     Where the shaping handle sits: halfway up the span, on the curve itself.
	/// </summary>
	/// <remarks>
	///     On the curve rather than beside it, so the thing being dragged is visibly the line that will move. It
	///     is placed by the same <see cref="LuxBrightnessCurve.Raise"/> every other point is, which is what keeps
	///     it on the line at every exponent instead of only near 1.
	/// </remarks>
	/// <param name="settings">The room's curve.</param>
	/// <param name="baseBrightnessPct">What the period asks for.</param>
	/// <param name="axisMaxLux">The top of the axis.</param>
	/// <exception cref="ArgumentNullException"><paramref name="settings"/> is <c>null</c>.</exception>
	public static CurvePoint ShapeHandle(AreaSettings settings, double baseBrightnessPct, double axisMaxLux)
	{
		ArgumentNullException.ThrowIfNull(settings);

		double lux = ShapeLux(settings);

		return new CurvePoint(
			X(FractionOf(lux, axisMaxLux)),
			Y(LuxBrightnessCurve.Raise(baseBrightnessPct, lux, settings)));
	}

	/// <summary>The reading the shaping handle stands on: <see cref="ShapeFraction"/> of the way between the anchors, in log space.</summary>
	/// <param name="settings">The room's curve.</param>
	/// <exception cref="ArgumentNullException"><paramref name="settings"/> is <c>null</c>.</exception>
	public static double ShapeLux(AreaSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);

		double start = Math.Max(settings.LuxBrightnessStartLux, AxisMinLux);
		double full = Math.Max(settings.LuxBrightnessFullLux, start * 10);

		return Math.Pow(10, Math.Log10(start) + (ShapeFraction * (Math.Log10(full) - Math.Log10(start))));
	}

	/// <summary>
	///     Whether the shaping handle means anything at all.
	/// </summary>
	/// <remarks>
	///     A ceiling at or below what the period already asks for leaves no headroom, so every exponent draws the
	///     same flat line — see <see cref="LuxBrightnessCurve.Raise"/>, whose headroom is never a signed
	///     difference. A handle that cannot move the picture is worse than no handle, so it is not drawn.
	/// </remarks>
	/// <param name="settings">The room's curve.</param>
	/// <param name="baseBrightnessPct">What the period asks for.</param>
	/// <exception cref="ArgumentNullException"><paramref name="settings"/> is <c>null</c>.</exception>
	public static bool HasHeadroom(AreaSettings settings, double baseBrightnessPct)
	{
		ArgumentNullException.ThrowIfNull(settings);

		return settings.LuxBrightnessMaxPct - baseBrightnessPct > 0.5;
	}

	/// <summary>
	///     The exponent that would put the curve through a dragged point.
	/// </summary>
	/// <remarks>
	///     From the engine's own shaping, <c>position = fraction ^ gamma</c>, read backwards. Both ends are held
	///     off their asymptotes before the logarithms are taken: a drag onto the very top or bottom of the plot
	///     otherwise asks for an exponent of zero or infinity, and zero is the value
	///     <see cref="LuxBrightnessCurve"/> documents as the trap — <c>Math.Pow(0, 0)</c> is 1, which would
	///     command the daylight level in the dark.
	/// </remarks>
	/// <param name="fraction">Where along the span between the anchors the handle sits, 0–1 exclusive.</param>
	/// <param name="position">How far up the headroom the drag put it, 0–1.</param>
	/// <param name="min">The lowest exponent the setting takes.</param>
	/// <param name="max">The highest.</param>
	public static double GammaFor(double fraction, double position, double min, double max)
	{
		if (!double.IsFinite(fraction) || !double.IsFinite(position))
			return Math.Clamp(1, min, max);

		double along = Math.Clamp(fraction, 0.001, 0.999);
		double up = Math.Clamp(position, 0.002, 0.998);

		double gamma = Math.Log(up) / Math.Log(along);

		return double.IsFinite(gamma) ? Math.Clamp(gamma, min, max) : Math.Clamp(1, min, max);
	}

	/// <summary>
	///     A dragged lux value rounded to something a person would have typed.
	/// </summary>
	/// <remarks>
	///     The grain follows the decade, because a fixed one is wrong at both ends: rounding to 50 lx makes the
	///     bottom decade unreachable, and rounding to 1 lx leaves an anchor reading "7 431 lx" that nobody chose
	///     and nobody can reproduce.
	/// </remarks>
	/// <param name="lux">The value the drag landed on.</param>
	public static double RoundLux(double lux)
	{
		if (!double.IsFinite(lux))
			return AxisMinLux;

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

	/// <summary>
	///     The inline style that lays the drag surface exactly over the plot area.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Percentages of the chart's own box, which is the same box the viewBox maps onto — the SVG keeps its
	///         aspect ratio, so one source of geometry serves both the drawing and the thing a finger touches.
	///         Written here rather than in the stylesheet so a change to the plot's margins moves both at once.
	///     </para>
	///     <para>
	///         Invariant, like every other number this UI writes into an attribute: under <c>nb-NO</c> a bare
	///         <c>double</c> renders <c>7,4%</c>, which no browser reads as a length, and the surface would
	///         silently cover the whole chart.
	///     </para>
	/// </remarks>
	public static string SurfaceStyle() => string.Concat(
		"left:", Num(PlotLeft / ViewWidth * 100), "%;",
		"top:", Num(PlotTop / ViewHeight * 100), "%;",
		"width:", Num(PlotWidth / ViewWidth * 100), "%;",
		"height:", Num(PlotHeight / ViewHeight * 100), "%;");

	/// <summary>A number bound for an SVG attribute: invariant, and no more precision than a pixel needs.</summary>
	/// <param name="value">The number.</param>
	public static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

	/// <summary>A lux value written the way the rest of the UI writes one.</summary>
	/// <param name="lux">The reading.</param>
	public static string Lux(double lux) => TokenFormat.Number(Math.Round(lux), "lx");
}
