using Adaptive.Engine;

namespace AdaptiveLighting.Configuration;

/// <summary>Turns label names in a document into label ids once, at the start that first converts it to readable ids. Lighting only.</summary>
// Never guesses: a name no label has, or several share, stays as written and the validator warns about it.
// The marker is a period counter of zero: the readable-id conversion and every save raise it, and nothing lowers it.
internal static class LabelIdConversion
{
	// The known-id check runs first, so a house that has a label with one of these ids keeps it.
	private static readonly string[] RetiredExcludeDefaults = ["adaptive-exclude", "adaptive_exclude"];
	private static readonly string[] RetiredMotionDefaults = ["adaptive-motion", "adaptive_motion"];

	/// <summary>Mutates <paramref name="global"/> in place. <c>true</c> when anything changed.</summary>
	/// <param name="global">The document's house-wide settings, read before the readable-id conversion raises its counter.</param>
	/// <param name="knownLabels">Every label the house has; <c>null</c> while the registry cannot be read, which changes nothing.</param>
	public static bool Apply(GlobalConfig global, IReadOnlyList<RegistryLabel>? knownLabels)
	{
		ArgumentNullException.ThrowIfNull(global);

		if (knownLabels is null || global.IdCounters.Periods > 0)
			return false;

		string exclude = Convert(global.ExcludeLabel, knownLabels, RetiredExcludeDefaults) ?? string.Empty;
		string? include = Convert(global.IncludeLabel, knownLabels, []);
		string motion = Convert(global.MotionLabel, knownLabels, RetiredMotionDefaults) ?? string.Empty;

		bool changed = !string.Equals(exclude, global.ExcludeLabel, StringComparison.Ordinal)
			|| !string.Equals(include, global.IncludeLabel, StringComparison.Ordinal)
			|| !string.Equals(motion, global.MotionLabel, StringComparison.Ordinal);

		global.ExcludeLabel = exclude;
		global.IncludeLabel = include;
		global.MotionLabel = motion;

		return changed;
	}

	private static string? Convert(string? stored, IReadOnlyList<RegistryLabel> knownLabels, string[] retiredDefaults)
	{
		if (stored is not { Length: > 0 })
			return stored;

		// The label's own spelling: the picker and the room page compare ids ordinally.
		if (knownLabels.FirstOrDefault(label => string.Equals(label.Id, stored, StringComparison.OrdinalIgnoreCase)) is { } known)
			return known.Id;

		List<RegistryLabel> named = [.. knownLabels.Where(label => string.Equals(label.Name, stored, StringComparison.OrdinalIgnoreCase))];

		if (named.Count == 1)
			return named[0].Id;

		if (named.Count == 0 && retiredDefaults.Contains(stored, StringComparer.Ordinal))
			return string.Empty;

		return stored;
	}
}
