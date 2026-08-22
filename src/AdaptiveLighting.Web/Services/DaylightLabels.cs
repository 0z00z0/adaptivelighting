namespace AdaptiveLighting.Web.Services;

/// <summary>
///     The daylight chart's vertical geometry, and the one judgement inside it: where the right-edge period
///     labels sit once two boundaries are too close to give each a line of its own.
/// </summary>
/// <remarks>
///     Every number here derives from <see cref="MaxLabelUnits"/>, which is the cap <c>app.css</c> holds chart
///     type to. Raising it there alone brings the collisions back.
/// </remarks>
public static class DaylightLabels
{
	/// <summary>The largest chart type ever renders, in SVG user units.</summary>
	public const double MaxLabelUnits = 15;

	/// <summary>
	///     How far a label's box reaches either side of its baseline, as a fraction of the type size. Measured
	///     off rendered charts at caps 16 to 24; everything below is spaced from them.
	/// </summary>
	public const double LabelAscent = 0.77;

	/// <inheritdoc cref="LabelAscent"/>
	public const double LabelDescent = 0.42;

	public const int PlotHeight = 240;

	/// <summary>The band the right-edge period labels may occupy, inside the plot.</summary>
	public const double LabelCeiling = 6;

	/// <inheritdoc cref="LabelCeiling"/>
	public const double LabelFloor = 234;

	/// <summary>Baseline to baseline, so a label clears the box either side of it at the largest type it gets.</summary>
	public static double MinGap => ((LabelAscent + LabelDescent) * MaxLabelUnits) + 5;

	/// <summary>
	///     The month row's baseline: below the plot, never over the foot of the night band, where it would share
	///     the corner with the last period's label. Clears a period label sitting on the floor, plus two units.
	/// </summary>
	public static int MonthBaseline =>
		(int)Math.Ceiling(LabelFloor + ((LabelAscent + LabelDescent) * MaxLabelUnits) + 2);

	/// <summary>The whole drawing: the plot, plus the month gutter and its descenders under it.</summary>
	public static int ChartHeight => (int)Math.Ceiling(MonthBaseline + (LabelDescent * MaxLabelUnits) + 2);

	/// <summary>
	///     <paramref name="labels"/> ordered down the plot with every neighbouring pair at least
	///     <see cref="MinGap"/> apart, or spread evenly when no arrangement can manage that.
	/// </summary>
	/// <remarks>
	///     The boundary lines stay where they are; only the labels move. Two passes: pushing down alone meets
	///     <see cref="LabelFloor"/> and clamps there, which closes the gap it has just opened.
	/// </remarks>
	public static IReadOnlyList<(string Label, double LabelY)> Spread(IEnumerable<(string Label, double LabelY)> labels)
	{
		ArgumentNullException.ThrowIfNull(labels);

		List<(string Label, double LabelY)> ordered = [.. labels.OrderBy(label => label.LabelY)];

		if (ordered.Count < 2)
			return ordered;

		// More labels than the plot is tall: nothing clears, so spread them evenly. An even comb reads as a list,
		// where the passes below would stack them all against one end.
		if ((ordered.Count - 1) * MinGap > LabelFloor - LabelCeiling)
		{
			double step = (LabelFloor - LabelCeiling) / (ordered.Count - 1);

			for (int i = 0; i < ordered.Count; i++)
				ordered[i] = (ordered[i].Label, LabelCeiling + (i * step));

			return ordered;
		}

		Push(ordered);

		ordered[^1] = (ordered[^1].Label, Math.Min(ordered[^1].LabelY, LabelFloor));

		for (int i = ordered.Count - 2; i >= 0; i--)
			ordered[i] = (ordered[i].Label, Math.Min(ordered[i].LabelY, ordered[i + 1].LabelY - MinGap));

		// Pulling up can breach the ceiling, and the run is known to fit, so pushing it back down cannot now
		// breach the floor.
		ordered[0] = (ordered[0].Label, Math.Max(ordered[0].LabelY, LabelCeiling));
		Push(ordered);

		return ordered;

		static void Push(List<(string Label, double LabelY)> ordered)
		{
			for (int i = 1; i < ordered.Count; i++)
				ordered[i] = (ordered[i].Label, Math.Max(ordered[i].LabelY, ordered[i - 1].LabelY + MinGap));
		}
	}
}
