using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     How the configured list of house-mode options stands against the options the dropdown helper is offering.
/// </summary>
/// <param name="CanCompare">
///     Whether the helper actually answered with options. <c>false</c> means the question cannot be settled, never
///     that the helper is empty: an unreachable Home Assistant answers with an empty list too, and read as a
///     comparison that says every configured mode should be dropped.
/// </param>
/// <param name="Added">Option values the helper offers that the document has no entry for.</param>
/// <param name="Dropped">Option values the document carries that the helper no longer offers.</param>
public sealed record HouseModeOptionsDiff(
	bool CanCompare,
	IReadOnlyList<string> Added,
	IReadOnlyList<string> Dropped)
{
	/// <summary>Nothing to do: the two lists name the same options.</summary>
	public bool Matches => CanCompare && Added.Count == 0 && Dropped.Count == 0;

	/// <summary>There is something to take, and therefore something to offer.</summary>
	public bool Differs => CanCompare && (Added.Count > 0 || Dropped.Count > 0);
}

/// <summary>
///     Compares the house-mode options in the document against the ones its dropdown helper is offering, and says
///     in words what adopting the helper's list would do.
/// </summary>
/// <remarks>
///     Reports only. Nothing here adopts on load; the page turns the report into one deliberate control.
/// </remarks>
public static class HouseModeSync
{
	/// <summary>What the helper offers against what the document carries.</summary>
	/// <param name="mode">The configured house mode, or <c>null</c> when the document has none.</param>
	/// <param name="liveOptions">The helper's options as Home Assistant last reported them. Empty means "cannot tell".</param>
	public static HouseModeOptionsDiff Compare(HouseModeConfig? mode, IReadOnlyList<string> liveOptions)
	{
		ArgumentNullException.ThrowIfNull(liveOptions);

		List<string> live = Clean(liveOptions);

		// An empty live list cannot be told from a silent Home Assistant, so it is never a difference to act on.
		if (mode?.Entity is not { Length: > 0 } || live.Count == 0)
			return new HouseModeOptionsDiff(false, [], []);

		List<string> configured = Clean(mode.Options.Select(option => option.Value));

		// Sets, trimmed and case-insensitive: the same equality ConfigEditor's adopt uses to decide which options
		// keep their settings. Order is not part of it.
		return new HouseModeOptionsDiff(
			true,
			[.. live.Where(value => !configured.Contains(value, StringComparer.OrdinalIgnoreCase))],
			[.. configured.Where(value => !live.Contains(value, StringComparer.OrdinalIgnoreCase))]);
	}

	/// <summary>What the gap is, in a heading, or <c>null</c> when there is no gap to head.</summary>
	public static string? Title(HouseModeOptionsDiff diff)
	{
		ArgumentNullException.ThrowIfNull(diff);

		if (!diff.Differs)
			return null;

		if (diff.Added.Count == 0)
			return "This list has options the helper no longer offers.";

		return diff.Dropped.Count == 0
			? "The helper offers options this list doesn't have."
			: "The helper's options and this list have drifted apart.";
	}

	/// <summary>What taking the helper's list would do, with the options named, or <c>null</c> for a no-op.</summary>
	public static string? Summary(HouseModeOptionsDiff diff)
	{
		ArgumentNullException.ThrowIfNull(diff);

		if (!diff.Differs)
			return null;

		if (diff.Added.Count == 0)
			return $"Taking its list drops {Join(diff.Dropped)}.";

		return diff.Dropped.Count == 0
			? $"Taking its list adds {Join(diff.Added)}."
			: $"Taking its list adds {Join(diff.Added)}, and drops {Join(diff.Dropped)}.";
	}

	/// <summary>
	///     Rebuilds the mode's option list from the helper's, keeping what each surviving option was configured to
	///     mean. An option the document has never seen arrives with no meaning assigned.
	/// </summary>
	/// <param name="mode">The configured house mode to rebuild. <c>null</c> does nothing.</param>
	/// <param name="liveOptions">The helper's options. Empty does nothing, for the reason <see cref="Compare"/> gives.</param>
	/// <returns>Whether anything changed, so a caller can tell an adoption from a no-op.</returns>
	public static bool Adopt(HouseModeConfig? mode, IReadOnlyList<string> liveOptions)
	{
		ArgumentNullException.ThrowIfNull(liveOptions);

		List<string> live = Clean(liveOptions);

		if (mode is null || live.Count == 0)
			return false;

		// Read before the loop: keeping an option re-stamps its value in place, so a snapshot taken afterwards
		// compares the new list against itself and always reports nothing happened.
		List<string?> before = [.. mode.Options.Select(option => option.Value)];
		List<HouseModeOptionConfig> adopted = [];

		foreach (string value in live)
		{
			HouseModeOptionConfig? kept = mode.OptionFor(value);

			if (kept is not null)
			{
				// Re-stamped with the helper's spelling, since the two are equal under this comparison anyway.
				kept.Value = value;
				adopted.Add(kept);
			}
			else
			{
				adopted.Add(new HouseModeOptionConfig { Value = value });
			}
		}

		mode.Options = adopted;

		return !before.SequenceEqual(adopted.Select(option => option.Value), StringComparer.Ordinal);
	}

	/// <summary>Trimmed, non-blank and named once each: the shape both sides of the comparison are read in.</summary>
	/// <remarks>Public because the period-select panel reads a helper's live options in the same shape.</remarks>
	public static List<string> Clean(IEnumerable<string?> values) =>
	[
		.. values
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Select(value => value!.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)
	];

	/// <summary>"a", "a and b", "a, b and c".</summary>
	private static string Join(IReadOnlyList<string> parts) => parts.Count switch
	{
		1 => parts[0],
		2 => $"{parts[0]} and {parts[1]}",
		_ => $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}"
	};
}
