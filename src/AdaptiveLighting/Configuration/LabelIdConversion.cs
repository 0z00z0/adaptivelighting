using Adaptive.Engine;

namespace AdaptiveLighting.Configuration;

/// <summary>Turns label names in a document into label ids once, at the first start that can read the registry. Lighting only.</summary>
// Never guesses: a name no label has, or several share, stays as written and the validator warns about it.
internal static class LabelIdConversion
{
	// The retired hyphenated defaults. A slug never holds a hyphen, so neither can be a label id.
	private const string RetiredExcludeDefault = "adaptive-exclude";
	private const string RetiredMotionDefault = "adaptive-motion";

	/// <summary>Mutates <paramref name="global"/> in place. <c>true</c> when anything changed.</summary>
	/// <param name="global">The document's house-wide settings.</param>
	/// <param name="knownLabels">Every label the house has; <c>null</c> while the registry cannot be read, which changes nothing.</param>
	public static bool Apply(GlobalConfig global, IReadOnlyList<RegistryLabel>? knownLabels)
	{
		ArgumentNullException.ThrowIfNull(global);

		if (knownLabels is null)
			return false;

		string exclude = Convert(global.ExcludeLabel, knownLabels, RetiredExcludeDefault) ?? string.Empty;
		string? include = Convert(global.IncludeLabel, knownLabels, retiredDefault: null);
		string motion = Convert(global.MotionLabel, knownLabels, RetiredMotionDefault) ?? string.Empty;

		bool changed = !string.Equals(exclude, global.ExcludeLabel, StringComparison.Ordinal)
			|| !string.Equals(include, global.IncludeLabel, StringComparison.Ordinal)
			|| !string.Equals(motion, global.MotionLabel, StringComparison.Ordinal);

		global.ExcludeLabel = exclude;
		global.IncludeLabel = include;
		global.MotionLabel = motion;

		return changed;
	}

	private static string? Convert(string? stored, IReadOnlyList<RegistryLabel> knownLabels, string? retiredDefault)
	{
		if (stored is not { Length: > 0 } || LabelMatch.IsKnownId(knownLabels, stored))
			return stored;

		List<RegistryLabel> named = [.. knownLabels.Where(label => string.Equals(label.Name, stored, StringComparison.OrdinalIgnoreCase))];

		if (named.Count == 1)
			return named[0].Id;

		if (named.Count == 0 && string.Equals(stored, retiredDefault, StringComparison.Ordinal))
			return string.Empty;

		return stored;
	}
}
