using System.Globalization;

using YamlDotNet.Serialization;

namespace AdaptiveLighting.Configuration;

/// <summary>One entry in the circadian table: the target the lights hold from <see cref="Start"/> until the next period.</summary>
public class TimePeriodConfig
{
	/// <summary>What every reference to this period names. Minted once, never shown, never changed.</summary>
	/// <remarks>Filled in on load by <see cref="StableKeyMigration"/>. Editing it by hand orphans every reference to the old value.</remarks>
	public string? Id { get; set; }

	/// <summary>Free-form name: <c>morning</c>, <c>day</c>, <c>evening</c>, <c>night</c>. Reported in logs and snapshots. Two periods may share one.</summary>
	public string Name { get; set; } = "";

	/// <summary>What this period is resolved by, everywhere.</summary>
	// Falls back to Name for the one reader that can never be migrated: NetDaemon's ConfigurationBinder, against a
	// house's app YAML, has no pre-pass and so no ids.
	[YamlIgnore]
	public string Key => Id is { Length: > 0 } id ? id.Trim() : Name.Trim();

	/// <summary>
	///     The <see cref="HouseModeOptionConfig.Id"/> the house mode is set to when this period starts; <c>null</c>
	///     leaves the mode unchanged.
	/// </summary>
	/// <remarks>
	///     A value matching no configured option is written to the select verbatim, which is how a live option that
	///     nobody has classified yet still works.
	/// </remarks>
	public string? SetsModeId { get; set; }

	/// <summary>
	///     When this period begins: a clock time (<c>06:30</c>) or a sun event with an optional offset
	///     (<c>sunrise</c>, <c>sunset-01:00</c>). See <see cref="PeriodStart.TryParse"/>.
	/// </summary>
	public string Start { get; set; } = "";

	/// <summary>
	///     This period waits for movement instead of beginning at its <see cref="Start"/>, and then begins for the
	///     whole house: levels, warmth and <see cref="SetsModeId"/> together.
	/// </summary>
	/// <remarks>
	///     Bounded three ways. Movement can only start it once its own <see cref="Start"/> has come round, so morning
	///     cannot fire on a 02:00 trip to the kitchen; it starts once per local day, so walking back in at lunch does
	///     not restart it; and the next period's own <see cref="Start"/> overtakes it, so an empty house is never
	///     stranded on last night's levels.
	/// </remarks>
	public bool StartsOnMotion { get; set; }

	/// <summary>Which rooms' movement may start it, by area id. <c>null</c> and empty both mean any room the engine watches.</summary>
	/// <remarks>
	///     Naming the kitchen keeps a bedroom sensor at 06:05 from starting the morning for the whole house. Nullable
	///     only so <c>OmitNull</c> keeps the key out of a period that names no rooms; <see cref="ConfigNormalizer"/>
	///     writes <c>null</c> and never an empty list, so the two never both occur in a saved document.
	/// </remarks>
	public List<string>? StartsOnMotionAreas { get; set; }

	/// <summary>The level this period holds, house-wide.</summary>
	// A room follows the daylight curve instead through its own Levels row for this period
	// (RoomLevelOverride.FollowDaylightCurve); this period never hands that decision away.
	public double BrightnessPct { get; set; } = 80;

	public int ColorTempKelvin { get; set; } = 3500;

	// MinBrightnessPct and MaxBrightnessPct are gone, and so is this period's own UseDaylightCurve. A stale one
	// still parses, since both binders ignore what they do not know, but it no longer behaves:
	// LightingConfigDocument.RetiredKeys logs it on load.
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
///     A parsed <see cref="TimePeriodConfig.Start"/>. Resolving a sun event needs the day's sun times, which is why
///     parsing and resolution are split.
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
	///     Resolves this boundary to a time of day. A sun-anchored boundary whose sun time is unknown cannot be
	///     placed and returns <c>null</c>.
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

/// <summary>The day's sun times, supplied by the caller so period resolution stays pure.</summary>
/// <param name="Sunrise">Local time of sunrise, or <c>null</c> when unknown (polar night, sun entity missing).</param>
/// <param name="Sunset">Local time of sunset, or <c>null</c> when unknown.</param>
public sealed record SunTimes(TimeOnly? Sunrise, TimeOnly? Sunset)
{
	/// <summary>Sun times that resolve nothing. Sun-anchored periods are skipped when this is all that is known.</summary>
	public static readonly SunTimes Unknown = new(null, null);
}
