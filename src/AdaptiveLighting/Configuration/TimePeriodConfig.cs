using System.Globalization;

namespace AdaptiveLighting.Configuration;

/// <summary>
///     One entry in the house-wide circadian table: the target the lights hold from <see cref="Start"/>
///     until the next period begins.
/// </summary>
public class TimePeriodConfig
{
	/// <summary>Free-form name: <c>morning</c>, <c>day</c>, <c>evening</c>, <c>night</c>. Reported in logs and snapshots.</summary>
	public string Name { get; set; } = "";

	/// <summary>
	///     When this period starts, set the house-mode select to this option value. <c>null</c> leaves the mode
	///     unchanged.
	/// </summary>
	public string? SetsMode { get; set; }

	/// <summary>
	///     When this period begins. Either a clock time (<c>06:30</c>) or a sun event with an optional
	///     offset (<c>sunrise</c>, <c>sunset-01:00</c>, <c>sunrise+00:45</c>). See <see cref="PeriodStart.TryParse"/>.
	/// </summary>
	public string Start { get; set; } = "";

	/// <summary>
	///     Let movement pull this period forward, so it begins when somebody first walks in rather than on the
	///     clock alone.
	/// </summary>
	/// <remarks>
	///     Bounded two ways, and both matter. Motion can only bring the period forward to <b>at most its own
	///     <see cref="Start"/></b>, so morning cannot fire on a 02:00 trip to the kitchen; and it fires <b>once
	///     per local day</b>, so walking back in at lunch does not restart it. Without the first bound the
	///     period table stops meaning anything; without the second, a period would re-enter all day and re-fire
	///     its <see cref="SetsMode"/> with it.
	/// </remarks>
	public bool StartsOnMotion { get; set; }

	/// <summary>
	///     Which rooms' movement may start it. Empty means any room the engine watches.
	/// </summary>
	/// <remarks>
	///     Area ids, matched the way every other area reference is. Naming the kitchen keeps a bedroom sensor at
	///     06:05 from starting the morning for the whole house.
	/// </remarks>
	public List<string> StartsOnMotionAreas { get; set; } = [];

	public double BrightnessPct { get; set; } = 80;

	public int ColorTempKelvin { get; set; } = 3500;

	// MinBrightnessPct and MaxBrightnessPct are gone. A stale one still parses, since both binders ignore what they
	// do not know, but it no longer behaves: LightingConfigDocument.RetiredKeys logs it on load.
}

/// <summary>A sun event a period boundary can be anchored to.</summary>
public enum SunEvent
{
	/// <summary>The boundary is a fixed clock time, not a sun event.</summary>
	None,

	Sunrise,

	Sunset
}

/// <summary>
///     A parsed <see cref="TimePeriodConfig.Start"/>. Either a fixed clock time or a sun event plus an offset;
///     resolving the latter needs the day's sun times, which is why parsing and resolution are split.
/// </summary>
/// <param name="FixedTime">The clock time, when <paramref name="SunEvent"/> is <see cref="SunEvent.None"/>.</param>
/// <param name="SunEvent">The anchoring sun event, if any.</param>
/// <param name="Offset">Offset from the sun event; may be negative.</param>
public sealed record PeriodStart(TimeOnly? FixedTime, SunEvent SunEvent, TimeSpan Offset)
{
	private const string SunriseToken = "sunrise";
	private const string SunsetToken = "sunset";

	/// <summary>
	///     Parses <c>"06:30"</c>, <c>"sunrise"</c>, <c>"sunset-01:00"</c> and friends. Culture-invariant, so a YAML
	///     file behaves identically on a Norwegian and an English host.
	/// </summary>
	public static bool TryParse(string? text, out PeriodStart? result)
	{
		result = null;
		if (string.IsNullOrWhiteSpace(text))
			return false;

		string trimmed = text.Trim();

		if (TimeOnly.TryParseExact(trimmed, ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly fixedTime))
		{
			result = new PeriodStart(fixedTime, SunEvent.None, TimeSpan.Zero);
			return true;
		}

		int signIndex = trimmed.IndexOfAny(['+', '-']);
		string token = (signIndex < 0 ? trimmed : trimmed[..signIndex]).Trim();

		SunEvent sunEvent = token.Equals(SunriseToken, StringComparison.OrdinalIgnoreCase) ? SunEvent.Sunrise
			: token.Equals(SunsetToken, StringComparison.OrdinalIgnoreCase) ? SunEvent.Sunset
			: SunEvent.None;

		if (sunEvent == SunEvent.None)
			return false;

		TimeSpan offset = TimeSpan.Zero;
		if (signIndex >= 0)
		{
			bool negative = trimmed[signIndex] == '-';
			string magnitude = trimmed[(signIndex + 1)..].Trim();
			if (!TimeSpan.TryParseExact(magnitude, ["hh\\:mm", "h\\:mm", "hh\\:mm\\:ss"], CultureInfo.InvariantCulture, out TimeSpan parsed))
				return false;

			offset = negative ? parsed.Negate() : parsed;
		}

		result = new PeriodStart(null, sunEvent, offset);
		return true;
	}

	/// <summary>
	///     Resolves this boundary to a time of day. Sun-anchored boundaries need <paramref name="sunTimes"/>;
	///     when the required sun time is unknown the boundary cannot be placed and <c>null</c> is returned.
	/// </summary>
	public TimeOnly? Resolve(SunTimes sunTimes)
	{
		ArgumentNullException.ThrowIfNull(sunTimes);

		if (SunEvent == SunEvent.None)
			return FixedTime;

		TimeOnly? anchor = SunEvent == SunEvent.Sunrise ? sunTimes.Sunrise : sunTimes.Sunset;
		return anchor?.Add(Offset);
	}
}

/// <summary>
///     The day's sun times. Supplied by the caller, never read here, so period resolution stays pure.
/// </summary>
/// <param name="Sunrise">Local time of sunrise, or <c>null</c> when unknown (polar night, sun entity missing).</param>
/// <param name="Sunset">Local time of sunset, or <c>null</c> when unknown.</param>
public sealed record SunTimes(TimeOnly? Sunrise, TimeOnly? Sunset)
{
	/// <summary>Sun times that resolve nothing. Sun-anchored periods are skipped when this is all that is known.</summary>
	public static readonly SunTimes Unknown = new(null, null);
}
