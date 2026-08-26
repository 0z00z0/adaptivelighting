using System.Text;

namespace AdaptiveLighting.Web.Services;

/// <summary>The SVG path for a response curve, so a setting shaped like a line can be drawn as one.</summary>
public static class CurvePath
{
	/// <summary>A power curve <c>y = x^exponent</c> across a box, in SVG coordinates.</summary>
	/// <param name="exponent">The shape. 1 is a straight rise; above 1 holds back and then climbs; below 1 lifts early.</param>
	/// <param name="samples">How many points to draw. More is smoother; twelve is enough at glyph size.</param>
	/// <returns>An SVG <c>d</c> attribute, starting with a move and continuing with lines.</returns>
	public static string Power(double exponent, double width = 40, double height = 20, int samples = 12)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(samples, 2);

		// Clamped: past about 10 the curve renders as a right angle and stops carrying information.
		double shape = Math.Clamp(exponent, 0.1, 10);
		StringBuilder path = new();

		for (int step = 0; step < samples; step++)
		{
			double along = (double)step / (samples - 1);
			double lift = Math.Pow(along, shape);

			// y flipped, because SVG counts downward: unflipped, an early rise draws as a late one.
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

	/// <summary>The curve in words, for a screen reader and for the sentence's plain-text form.</summary>
	/// <remarks>A curve that climbs, which is what <see cref="Power"/> draws and what a glyph beside a setting shows.</remarks>
	public static string Describe(double exponent) => exponent switch
	{
		< 0.95 => "a curve that lifts early, then flattens",
		> 1.05 => "a curve that holds back, then climbs steeply",
		_ => "a straight, even rise"
	};

	/// <summary>The curve in words, as it is actually drawn.</summary>
	/// <param name="exponent">The shape.</param>
	/// <param name="span">What the curve gains from one end to the other. Negative falls, near zero is level.</param>
	// The exponent alone cannot say this: the same shape run downhill is a curve that dims, and calling that one
	// that lifts is the picture and the sentence disagreeing.
	public static string Describe(double exponent, double span)
	{
		if (!double.IsFinite(span) || Math.Abs(span) < 0.5)
			return "a level that does not change";

		if (span > 0)
			return Describe(exponent);

		return exponent switch
		{
			< 0.95 => "a curve that dims early, then flattens",
			> 1.05 => "a curve that holds on, then dims steeply",
			_ => "a straight, even fall"
		};
	}
}
