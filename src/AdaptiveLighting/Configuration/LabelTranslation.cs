using AdaptiveLighting.Extensions;

namespace AdaptiveLighting.Configuration;

/// <summary>Turns label names a document still stores into the label ids Home Assistant keeps through a rename.</summary>
/// <remarks>
///     Runs inside the single normalise-and-write step, so a hand-edited document is translated only when that step
///     writes it. A value nothing is named stays exactly as written: a label made later still works, and a start
///     with the registry unreadable changes nothing.
/// </remarks>
public static class LabelTranslation
{
	/// <summary>Rewrites the three label settings in place. True when at least one value changed.</summary>
	public static bool Apply(GlobalConfig global, IReadOnlyList<RegistryLabel>? knownLabels)
	{
		ArgumentNullException.ThrowIfNull(global);

		if (knownLabels is null || knownLabels.Count == 0)
			return false;

		bool changed = false;

		if (LabelMatch.IdForName(knownLabels, global.ExcludeLabel) is { } exclude)
		{
			global.ExcludeLabel = exclude;
			changed = true;
		}

		if (LabelMatch.IdForName(knownLabels, global.IncludeLabel) is { } include)
		{
			global.IncludeLabel = include;
			changed = true;
		}

		if (LabelMatch.IdForName(knownLabels, global.MotionLabel) is { } motion)
		{
			global.MotionLabel = motion;
			changed = true;
		}

		return changed;
	}

	/// <summary>The label settings that are set but are not ids the house knows, by their setting name.</summary>
	/// <remarks>What the validator warns about: each of these matches by name only, so a rename in HA stops it matching.</remarks>
	public static IEnumerable<(string Setting, string Value)> NotIds(GlobalConfig global, IReadOnlyCollection<string>? knownLabelIds)
	{
		ArgumentNullException.ThrowIfNull(global);

		if (knownLabelIds is null)
			yield break;

		foreach ((string setting, string? value) in Settings(global))
			if (value is { Length: > 0 } && !knownLabelIds.Contains(value, StringComparer.OrdinalIgnoreCase))
				yield return (setting, value);
	}

	private static IEnumerable<(string Setting, string? Value)> Settings(GlobalConfig global)
	{
		yield return (nameof(GlobalConfig.ExcludeLabel), global.ExcludeLabel);
		yield return (nameof(GlobalConfig.IncludeLabel), global.IncludeLabel);
		yield return (nameof(GlobalConfig.MotionLabel), global.MotionLabel);
	}
}
