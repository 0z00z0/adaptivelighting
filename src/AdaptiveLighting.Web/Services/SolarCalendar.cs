namespace AdaptiveLighting.Web.Services;

/// <summary>
///     How the sun behaves relative to a chosen zenith angle on a given day.
/// </summary>
/// <remarks>
///     The hour-angle equation has no solution at the poles' seasons: the sun can stay above the chosen angle for
///     the whole day, or below it for the whole day. A plain (null, null) cannot tell those two apart, and the
///     daylight chart needs to — a white night (twilight all night) and a polar night (dark all day) draw as
///     opposite fills. This is the missing distinction, carried per call.
/// </remarks>
public enum SunState
{
	/// <summary>The sun crosses the zenith angle twice: there is a genuine sunrise and sunset (or dawn and dusk).</summary>
	RisesAndSets,

	/// <summary>
	///     The sun never dips to the zenith angle — it stays above it all day. For the official zenith that is the
	///     midnight sun; for the civil zenith it is a "white night" (twilight or brighter the whole night).
	/// </summary>
	AlwaysAbove,

	/// <summary>
	///     The sun never climbs to the zenith angle — it stays below it all day. For the official zenith that is
	///     polar night (never any daylight); for the civil zenith the sun stays deeper than civil twilight all day.
	/// </summary>
	AlwaysBelow
}

/// <summary>
///     The sun's behaviour on one day at one zenith angle: the <see cref="State"/>, and the crossing times when it
///     <see cref="SunState.RisesAndSets"/>. <see cref="Sunrise"/>/<see cref="Sunset"/> are <c>null</c> in the two
///     polar states.
/// </summary>
/// <param name="State">Whether the sun rises and sets, stays above, or stays below the zenith angle.</param>
/// <param name="Sunrise">Local time the sun crosses the angle going up (sunrise / civil dawn), or <c>null</c>.</param>
/// <param name="Sunset">Local time the sun crosses the angle going down (sunset / civil dusk), or <c>null</c>.</param>
public readonly record struct SolarDay(SunState State, TimeOnly? Sunrise, TimeOnly? Sunset)
{
	/// <summary>A day on which the sun stays above the zenith angle throughout (midnight sun / white night).</summary>
	public static SolarDay AlwaysAbove { get; } = new(SunState.AlwaysAbove, null, null);

	/// <summary>A day on which the sun stays below the zenith angle throughout (polar night / deeper than twilight).</summary>
	public static SolarDay AlwaysBelow { get; } = new(SunState.AlwaysBelow, null, null);

	/// <summary>Convenience destructuring to just the crossing times, for callers that predate the state.</summary>
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
///     <para>
///         The daylight chart needs a whole year of sun times, and <c>sun.sun</c> only ever publishes the
///         <i>next</i> events — so the chart cannot read them from Home Assistant. This is a planning aid, not a
///         control input: accuracy to a minute or two is ample, and the fractional-year → equation-of-time +
///         declination → hour-angle routine is short enough to carry without reintroducing a solar library.
///     </para>
///     <para>
///         Times come back in the host's local timezone (<see cref="TimeZoneInfo.Local"/>), which is the same
///         premise <c>LightingOrchestrator.ReadSunTimes</c> makes with <c>ToLocalTime()</c> — so the chart and the
///         engine share one timezone rather than each inventing its own.
///     </para>
/// </remarks>
public static class SolarCalendar
{
	/// <summary>The official zenith for sunrise/sunset, accounting for atmospheric refraction and the sun's radius.</summary>
	public const double OfficialZenithDegrees = 90.833;

	/// <summary>The civil-twilight zenith: the sun 6° below the horizon, the edge of usable daylight.</summary>
	public const double CivilZenithDegrees = 96.0;

	/// <summary>
	///     The sun's behaviour on <paramref name="day"/> at the given location, in local time: whether it crosses the
	///     zenith angle (with the sunrise/sunset times) or stays above or below it all day.
	/// </summary>
	/// <param name="day">The calendar day.</param>
	/// <param name="latitudeDeg">Latitude in degrees, positive north.</param>
	/// <param name="longitudeDeg">Longitude in degrees, positive east — as <c>zone.home</c> reports it.</param>
	/// <param name="zenithDegrees">
	///     The solar zenith the crossing is computed for. Defaults to <see cref="OfficialZenithDegrees"/>
	///     (sunrise/sunset); pass <see cref="CivilZenithDegrees"/> for civil dawn/dusk.
	/// </param>
	/// <returns>
	///     <see cref="SolarDay.AlwaysAbove"/> when the sun never dips to the angle (midnight sun / white night),
	///     <see cref="SolarDay.AlwaysBelow"/> when it never climbs to it (polar night), otherwise a
	///     <see cref="SunState.RisesAndSets"/> day carrying both crossing times.
	/// </returns>
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

		// No hour-angle solution means the sun never reaches the angle that day. cosHa < -1 needs an hour angle past
		// midnight (the sun stays ABOVE the angle even at its lowest → midnight sun / white night); cosHa > 1 needs
		// one short of noon (the sun stays BELOW it even at its highest → polar night). The old (null, null) collapsed
		// both into one, which is exactly what notched the twilight band near midsummer.
		if (cosHa < -1.0)
			return SolarDay.AlwaysAbove;
		if (cosHa > 1.0)
			return SolarDay.AlwaysBelow;

		double haDeg = Math.Acos(cosHa) * 180.0 / Math.PI;

		// NOAA form 720 - 4*(lon ± ha) - eqtime takes longitude positive to the EAST — exactly as zone.home
		// reports it and the param doc states — so it enters directly, with no sign flip.
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
