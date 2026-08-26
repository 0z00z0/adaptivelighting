using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     Moving one period between its own brightness and the daylight curve, and what that does to the curve's dark
///     end.
/// </summary>
/// <remarks>
///     An editing rule and not an engine rule. The engine reads whatever <see cref="AreaSettings.LuxBrightnessMinPct"/>
///     holds, so a hand-written document runs with nothing seeded; the schema default is only what such a document
///     falls back to.
/// </remarks>
public static class DaylightCurveMode
{
	private const double MinPct = 0;
	private const double MaxPct = 100;

	/// <summary>Whether any period hands its brightness to the curve.</summary>
	public static bool InUse(IReadOnlyList<TimePeriodConfig> periods)
	{
		ArgumentNullException.ThrowIfNull(periods);

		return periods.Any(period => period.UseDaylightCurve);
	}

	/// <summary>The dark end a level seeds: half of it, clamped to 0–100 % and rounded to one decimal.</summary>
	public static double DarkEndFor(double brightnessPct) =>
		Math.Round(Math.Clamp(brightnessPct / 2, MinPct, MaxPct), 1, MidpointRounding.AwayFromZero);

	/// <summary>
	///     Puts one period on the curve or back on its own percentage, seeding the house's dark end as the curve
	///     comes into use.
	/// </summary>
	/// <returns>Whether the dark end was seeded.</returns>
	/// <remarks>
	///     Seeding is once per adoption: a second period joining a curve already in use leaves the dark end alone,
	///     since by then it may have been dragged. Turning the curve off everywhere and on again adopts it afresh.
	///     Only the house default is written, so a room stating its own dark end keeps it.
	/// </remarks>
	public static bool Set(
		IReadOnlyList<TimePeriodConfig> periods,
		AreaSettings defaults,
		TimePeriodConfig period,
		bool useDaylightCurve)
	{
		ArgumentNullException.ThrowIfNull(periods);
		ArgumentNullException.ThrowIfNull(defaults);
		ArgumentNullException.ThrowIfNull(period);

		// Asked before the flag moves, and of every period including this one: the curve is coming into use only
		// while nothing at all is on it.
		bool adopting = useDaylightCurve && !InUse(periods);

		period.UseDaylightCurve = useDaylightCurve;

		if (!adopting)
			return false;

		defaults.LuxBrightnessMinPct = DarkEndFor(period.BrightnessPct);

		return true;
	}
}
