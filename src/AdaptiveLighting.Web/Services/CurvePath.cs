using System.Text;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     The SVG path for a response curve, so a setting shaped like a line can be drawn as one.
/// </summary>
/// <remarks>
///     <para>
///         Some settings are quantities and read perfectly as words. A shaping exponent is not one of them:
///         "1 is an even rise, higher holds the lights back until it is properly bright, lower lifts the room as
///         soon as the light outside starts climbing" is three clauses describing a shape anyone recognises on
///         sight. So the sentence keeps the number and puts the shape beside it.
///     </para>
///     <para>
///         Pure, and here rather than in the component, for the usual reason: geometry generated inside markup
///         is geometry nothing can assert about, and an off-by-one in the y axis produces a curve that is
///         upside down but perfectly plausible.
///     </para>
/// </remarks>
public static class CurvePath
{
	/// <summary>
	///     A power curve <c>y = x^exponent</c> across a box, in SVG coordinates.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The y axis is flipped on the way out, because SVG counts downward and a reader does not: without
	///         it, a gentle early rise would be drawn as a late one and the picture would say the opposite of
	///         the number beside it.
	///     </para>
	///     <para>
	///         The exponent is clamped to a sane band. A curve is a hint at a glance, and an exponent of 40
	///         renders as an indistinguishable right angle — the shape stops carrying information long before
	///         the arithmetic stops working.
	///     </para>
	/// </remarks>
	/// <param name="exponent">The shape. 1 is a straight rise; above 1 holds back and then climbs; below 1 lifts early.</param>
	/// <param name="width">The box's width in user units.</param>
	/// <param name="height">The box's height in user units.</param>
	/// <param name="samples">How many points to draw. More is smoother; twelve is enough at glyph size.</param>
	/// <returns>An SVG <c>d</c> attribute, starting with a move and continuing with lines.</returns>
	/// <exception cref="ArgumentOutOfRangeException">
	///     <paramref name="samples"/> is less than two — a curve needs at least its two ends.
	/// </exception>
	public static string Power(double exponent, double width = 40, double height = 20, int samples = 12)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(samples, 2);

		double shape = Math.Clamp(exponent, 0.1, 10);
		StringBuilder path = new();

		for (int step = 0; step < samples; step++)
		{
			double along = (double)step / (samples - 1);
			double lift = Math.Pow(along, shape);

			double x = along * width;
			double y = height - (lift * height);

			path.Append(step == 0 ? 'M' : 'L')
				.Append(x.ToString("0.##", CultureInfo.InvariantCulture))
				.Append(' ')
				.Append(y.ToString("0.##", CultureInfo.InvariantCulture))
				.Append(step == samples - 1 ? string.Empty : " ");
		}

		return path.ToString();
	}

	/// <summary>
	///     The curve in words, for a screen reader and for the sentence's plain-text form.
	/// </summary>
	/// <param name="exponent">The shape.</param>
	public static string Describe(double exponent) => exponent switch
	{
		< 0.95 => "a curve that lifts early, then flattens",
		> 1.05 => "a curve that holds back, then climbs steeply",
		_ => "a straight, even rise"
	};
}
