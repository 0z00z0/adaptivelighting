namespace AdaptiveLighting.Configuration;

/// <summary>The physical bounds and the period lookups the validation sections share.</summary>
internal static class ValidationRanges
{
	internal const double MinBrightnessPct = 0;
	internal const double MaxBrightnessPct = 100;
	internal const int MinBrightness = 0;
	internal const int MaxBrightness = RawBrightness.Max;
	internal const int MinColorTempKelvin = 1000;
	internal const int MaxColorTempKelvin = 10000;
	internal const double MinSunElevationDegrees = -90;
	internal const double MaxSunElevationDegrees = 90;

	/// <summary>The period a reference names, or <c>null</c> when nothing does.</summary>
	internal static TimePeriodConfig? PeriodWithKey(IReadOnlyList<TimePeriodConfig> periods, string? key) =>
		key is { Length: > 0 } ? periods.ByKey(key) : null;

	/// <summary>A period reference as a sentence should name it: its display name, or the raw id when it resolves to nothing.</summary>
	internal static string PeriodLabel(IReadOnlyList<TimePeriodConfig> periods, string? key) =>
		PeriodWithKey(periods, key)?.Name is { Length: > 0 } name ? name : key?.Trim() ?? "";
}
