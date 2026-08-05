namespace AdaptiveLighting.Engine;

/// <summary>Reads a household wall-clock time or date off an instant.</summary>
// IScheduler.Now is a DateTimeOffset at +00:00, so its own TimeOfDay is UTC. Every Start in the document is a
// household wall clock, so the conversion has to happen before the two are compared. Without it a 22:30 period
// began at 00:30 through a Norwegian summer, and the day rolled over at 02:00.
internal static class WallClock
{
	internal static TimeOnly TimeIn(this DateTimeOffset instant, TimeZoneInfo zone) =>
		TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

	internal static DateOnly DayIn(this DateTimeOffset instant, TimeZoneInfo zone) =>
		DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
