using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     Moving one room's period between its own brightness and the daylight curve, and what that does to the
///     room's dark end.
/// </summary>
/// <remarks>
///     An editing rule and not an engine rule. The engine reads whatever <see cref="AreaConfig.LuxBrightnessMinPct"/>
///     resolves to, so a hand-written document runs with nothing seeded; the schema default is only what such a
///     document falls back to.
/// </remarks>
public static class DaylightCurveMode
{
	private const double MinPct = 0;
	private const double MaxPct = 100;

	/// <summary>Whether this room already hands any period's brightness to the curve.</summary>
	public static bool InUse(IReadOnlyList<RoomLevelOverride> levels)
	{
		ArgumentNullException.ThrowIfNull(levels);

		return levels.Any(level => level.FollowDaylightCurve == true);
	}

	/// <summary>The dark end a level seeds: half of it, clamped to 0–100 % and rounded to one decimal.</summary>
	public static double DarkEndFor(double brightnessPct) =>
		Math.Round(Math.Clamp(brightnessPct / 2, MinPct, MaxPct), 1, MidpointRounding.AwayFromZero);

	/// <summary>
	///     Puts one period on the curve or back on its own percentage, for this room, seeding the room's own dark
	///     end as it takes up the curve for the first time.
	/// </summary>
	/// <returns>Whether the dark end was seeded.</returns>
	/// <remarks>
	///     Seeding is once per room's adoption: a second period joining a curve this room already runs leaves the
	///     dark end alone, since by then it may have been dragged. Turning the curve off on every period in this
	///     room and on again adopts it afresh. Only this room's own dark end is written, never the house default,
	///     and never while the room already states one of its own.
	/// </remarks>
	public static bool Set(
		AreaConfig room,
		RoomLevelOverride level,
		double currentBrightnessPct,
		bool followDaylightCurve)
	{
		ArgumentNullException.ThrowIfNull(room);
		ArgumentNullException.ThrowIfNull(level);

		// Asked before the flag moves, and of every row including this one: the curve is coming into use in this
		// room only while nothing of the room's is on it yet.
		bool adopting = followDaylightCurve && !InUse(room.Levels) && room.LuxBrightnessMinPct is null;

		level.FollowDaylightCurve = followDaylightCurve ? true : null;

		if (!adopting)
			return false;

		room.LuxBrightnessMinPct = DarkEndFor(currentBrightnessPct);

		return true;
	}
}
