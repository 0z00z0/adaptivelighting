namespace AdaptiveLighting.Web.Services;

/// <summary>
///     How the sun behaves relative to a chosen zenith angle on a given day.
/// </summary>
/// <remarks>
///     The hour-angle equation has no solution at the poles' seasons, and the two ways it fails draw as opposite
///     fills on the chart. A single "no times today" answer cannot tell them apart.
/// </remarks>
public enum SunState
{
	/// <summary>The sun crosses the zenith angle twice: a genuine sunrise and sunset, or dawn and dusk.</summary>
	RisesAndSets,

	/// <summary>
	///     The sun stays above the angle all day. Midnight sun at the official zenith; a white night at the civil
	///     one, meaning twilight or brighter all night.
	/// </summary>
	AlwaysAbove,

	/// <summary>
	///     The sun stays below the angle all day. Polar night at the official zenith; deeper than civil twilight
	///     all day at the civil one.
	/// </summary>
	AlwaysBelow
}

/// <summary>The sun's behaviour on one day at one zenith angle.</summary>
/// <param name="Sunrise">Local time the sun crosses the angle going up, or <c>null</c> in the two polar states.</param>
/// <param name="Sunset">Local time the sun crosses the angle going down, or <c>null</c> in the two polar states.</param>
public readonly record struct SolarDay(SunState State, TimeOnly? Sunrise, TimeOnly? Sunset)
{
	public static SolarDay AlwaysAbove { get; } = new(SunState.AlwaysAbove, null, null);

	public static SolarDay AlwaysBelow { get; } = new(SunState.AlwaysBelow, null, null);

	/// <summary>Destructuring to just the crossing times, for callers that predate the state.</summary>
	public void Deconstruct(out TimeOnly? sunrise, out TimeOnly? sunset)
	{
		sunrise = Sunrise;
		sunset = Sunset;
	}
}

/// <summary>
///     Sunrise and sunset for any day of the year, dependency-free, via the NOAA equations.
/// </summary>
/// <remarks>
///     <c>sun.sun</c> only publishes the next events, so a whole year cannot be read from Home Assistant. Times
///     come back in <see cref="TimeZoneInfo.Local"/>, the same premise <c>LightingOrchestrator.ReadSunTimes</c>
///     makes, so the chart and the engine share one timezone.
/// </remarks>
public static class SolarCalendar
{
	/// <summary>The official zenith for sunrise/sunset, allowing for refraction and the sun's radius.</summary>
	public const double OfficialZenithDegrees = 90.833;

	/// <summary>The civil-twilight zenith: the sun 6° below the horizon, the edge of usable daylight.</summary>
	public const double CivilZenithDegrees = 96.0;

	/// <summary>The sun's behaviour on one day at one location, in local time.</summary>
	/// <param name="latitudeDeg">Latitude in degrees, positive north.</param>
	/// <param name="longitudeDeg">Longitude in degrees, positive east, as <c>zone.home</c> reports it.</param>
	/// <param name="zenithDegrees">Defaults to sunrise/sunset; pass <see cref="CivilZenithDegrees"/> for dawn/dusk.</param>
	public static SolarDay On(DateOnly day, double latitudeDeg, double longitudeDeg, double zenithDegrees = OfficialZenithDegrees)
	{
		double gamma = 2.0 * Math.PI / 365.0 * (day.DayOfYear - 1);

		double eqTime = 229.18 * (0.000075
			+ (0.001868 * Math.Cos(gamma))
			- (0.032077 * Math.Sin(gamma))
			- (0.014615 * Math.Cos(2 * gamma))
			- (0.040849 * Math.Sin(2 * gamma)));

		double decl = 0.006918
			- (0.399912 * Math.Cos(gamma))
			+ (0.070257 * Math.Sin(gamma))
			- (0.006758 * Math.Cos(2 * gamma))
			+ (0.000907 * Math.Sin(2 * gamma))
			- (0.002697 * Math.Cos(3 * gamma))
			+ (0.00148 * Math.Sin(3 * gamma));

		double latRad = latitudeDeg * Math.PI / 180.0;
		double zenithRad = zenithDegrees * Math.PI / 180.0;

		double cosHa = (Math.Cos(zenithRad) / (Math.Cos(latRad) * Math.Cos(decl))) - (Math.Tan(latRad) * Math.Tan(decl));

		// No solution: cosHa < -1 wants an hour angle past midnight, so the sun stays above the angle even at its
		// lowest. cosHa > 1 wants one short of noon, so it stays below even at its highest. Collapsing the two
		// notches the twilight band near midsummer.
		if (cosHa < -1.0)
			return SolarDay.AlwaysAbove;
		if (cosHa > 1.0)
			return SolarDay.AlwaysBelow;

		double haDeg = Math.Acos(cosHa) * 180.0 / Math.PI;

		// The NOAA form 720 - 4*(lon ± ha) - eqtime takes longitude positive east, as zone.home reports it, so it
		// enters with no sign flip.
		double sunriseMinutesUtc = 720.0 - (4.0 * (longitudeDeg + haDeg)) - eqTime;
		double sunsetMinutesUtc = 720.0 - (4.0 * (longitudeDeg - haDeg)) - eqTime;

		return new SolarDay(SunState.RisesAndSets, ToLocal(day, sunriseMinutesUtc), ToLocal(day, sunsetMinutesUtc));
	}

	private static TimeOnly ToLocal(DateOnly day, double minutesFromUtcMidnight)
	{
		DateTime utc = new DateTime(day.Year, day.Month, day.Day, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minutesFromUtcMidnight);
		return TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local));
	}
}
