namespace AdaptiveLighting.Lamplight;

/// <summary>
/// Turns a room's lamp reading into the <c>--lampc</c> and <c>--lvl</c> custom properties the shared
/// <c>lamp-glow</c> rule reads. Takes the values a <c>RoomLamp</c> carries rather than that type itself, so
/// this package builds standalone; the wiring at the call site is one line once <c>RoomLamp</c> exists.
/// </summary>
public static class LampPaint
{
	// The six-step Kelvin ramp the theme tokens define: --k2200 through --k4500.
	private static readonly int[] Steps = [2200, 2700, 3000, 3200, 4000, 4500];

	/// <summary>The inline style carrying --lampc and --lvl, or null when the room is off.</summary>
	public static string? Style(bool isLit, int? brightnessPct, int? kelvin)
	{
		if (!isLit || brightnessPct is not { } pct || kelvin is not { } k)
			return null;

		int step = NearestStep(k);
		double level = 0.3 + (0.7 * Math.Clamp(pct, 0, 100) / 100);

		return $"--lampc:var(--k{step.ToString(System.Globalization.CultureInfo.InvariantCulture)});--lvl:{InvariantNumber.Format(level, 3)}";
	}

	private static int NearestStep(int kelvin)
	{
		int nearest = Steps[0];
		int best = Math.Abs(kelvin - nearest);

		foreach (int step in Steps)
		{
			int diff = Math.Abs(kelvin - step);

			if (diff < best)
			{
				best = diff;
				nearest = step;
			}
		}

		return nearest;
	}
}
