using Adaptive.Engine;

namespace Adaptive.Settings;

/// <summary>Resolving a period out of the schedule.</summary>
/// <remarks>Cannot live in <c>AdaptiveLighting.Extensions</c>: that package is host-agnostic and knows no configuration type.</remarks>
public static class PeriodExtensions
{
	/// <summary>The period whose id is <paramref name="key"/>, or <c>null</c> when none is. Never by name.</summary>
	/// <remarks>The match the engine resolves by, so a page asking it can never badge a period the engine leaves unresolved.</remarks>
	public static TimePeriodConfig? ByKey(this IEnumerable<TimePeriodConfig> periods, string? key)
	{
		ArgumentNullException.ThrowIfNull(periods);

		return key?.Trim() is { Length: > 0 } wanted
			? periods.FirstOrDefault(period => period.Key.SameName(wanted))
			: null;
	}
}
