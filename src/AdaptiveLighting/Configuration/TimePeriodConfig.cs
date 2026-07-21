using System.Globalization;

namespace AdaptiveLighting.Configuration;

/// <summary>
///     One entry in the house-wide circadian table: the target the lights hold from <see cref="Start"/>
///     until the next period begins.
/// </summary>
public class TimePeriodConfig
{
	/// <summary>Free-form name — <c>morning</c>, <c>day</c>, <c>evening</c>, <c>night</c>. Reported in logs and snapshots.</summary>
	public string Name { get; set; } = "";

	/// <summary>
	///     When this period starts, set the husmodus select to this option value (09 §2.3). <c>null</c> leaves the
	///     mode unchanged. A deliberately new key, not a repurposed <c>Mode</c>: a stale <c>Mode:</c> in an old file
	///     binds to nothing and the period quietly becomes a plain period, rather than silently flipping semantics.
	/// </summary>
	public string? SetsMode { get; set; }

	/// <summary>
	///     When this period begins. Either a clock time (<c>06:30</c>) or a sun event with an optional
	///     offset (<c>sunrise</c>, <c>sunset-01:00</c>, <c>sunrise+00:45</c>). See <see cref="PeriodStart.TryParse"/>.
	/// </summary>
	public string Start { get; set; } = "";

	/// <summary>Target brightness for this period.</summary>
	public double BrightnessPct { get; set; } = 80;

	/// <summary>Target colour temperature for this period.</summary>
	public int ColorTempKelvin { get; set; } = 3500;

	/// <summary>Ceiling applied to every command while this period is active. This is the rule that stops 100% at 03:00.</summary>
	public double? MaxBrightnessPct { get; set; }

	/// <summary>Floor applied to every command while this period is active, including the pre-off dim.</summary>
	public double? MinBrightnessPct { get; set; }
}

/// <summary>A sun event a period boundary can be anchored to.</summary>
public enum SunEvent
{
	/// <summary>The boundary is a fixed clock time, not a sun event.</summary>
	None,

	/// <summary>Anchored to sunrise.</summary>
	Sunrise,

	/// <summary>Anchored to sunset.</summary>
	Sunset
}

/// <summary>
///     A parsed <see cref="TimePeriodConfig.Start"/>. Either a fixed clock time or a sun event plus an offset;
///     resolving the latter to a wall time needs the day's sun times, which is why parsing and resolution are split.
/// </summary>
/// <param name="FixedTime">The clock time, when <paramref name="SunEvent"/> is <see cref="SunEvent.None"/>.</param>
/// <param name="SunEvent">The anchoring sun event, if any.</param>
/// <param name="Offset">Offset from the sun event; may be negative.</param>
public sealed record PeriodStart(TimeOnly? FixedTime, SunEvent SunEvent, TimeSpan Offset)
{
	private const string SunriseToken = "sunrise";
	private const string SunsetToken = "sunset";

	/// <summary>
	///     Parses <c>"06:30"</c>, <c>"sunrise"</c>, <c>"sunset-01:00"</c> and friends. Culture-invariant, so a
	///     YAML file behaves identically on a Norwegian and an English host.
	/// </summary>
	/// <returns><c>true</c> when <paramref name="text"/> is a valid boundary.</returns>
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
///     The day's sun times, as far as the engine needs them. Supplied by the caller rather than read here,
///     so period resolution stays a pure function.
/// </summary>
/// <param name="Sunrise">Local time of sunrise, or <c>null</c> when unknown (polar night, sun entity missing).</param>
/// <param name="Sunset">Local time of sunset, or <c>null</c> when unknown.</param>
public sealed record SunTimes(TimeOnly? Sunrise, TimeOnly? Sunset)
{
	/// <summary>Sun times that resolve nothing. Sun-anchored periods are skipped when this is all that is known.</summary>
	public static readonly SunTimes Unknown = new(null, null);
}
