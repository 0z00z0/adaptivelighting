using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     How the configured list of house-mode options stands against the options the dropdown helper is actually
///     offering: whether the two can be compared at all, and what taking the helper's list would change.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="CanCompare"/> is the field that keeps this honest.</b> An unreachable Home Assistant
///         answers with an empty option list, which is indistinguishable from a helper that genuinely has no
///         options — and the two call for opposite responses. Treated as a comparison, an empty live list says
///         every configured mode should be dropped, and a settings page that offered to empty somebody's house
///         modes because the connection was down would be the worst button in this application. So an empty live
///         list is "cannot tell", it is stated as such, and no action is offered against it.
///     </para>
/// </remarks>
/// <param name="CanCompare">
///     Whether the helper actually answered with options. <c>false</c> means the question cannot be settled —
///     never that the helper is empty.
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
///     Compares the house-mode options in the document against the ones its dropdown helper is offering, and
///     says in words what adopting the helper's list would do.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a comparison exists at all.</b> Picking a helper rebuilds the mode list from it, which is right
///         and which the settings page has always done. But that only fires when the entity <i>changes</i> — so a
///         house whose helper was chosen long ago and has since gained an option had no way to ask for it, and
///         from the owner's side the feature simply did not exist. What was missing was not the rebuild; it was
///         something visible to press.
///     </para>
///     <para>
///         <b>And why it is a comparison rather than a sync.</b> Adopting on load would be a configuration that
///         rewrites itself because somebody opened a page, which is the behaviour this project has refused
///         everywhere else. So this only ever reports, and the page turns the report into one deliberate control.
///     </para>
///     <para>
///         Compared as sets, ordinal-insensitive and trimmed — the same equality <c>ConfigEditor</c>'s own adopt
///         uses to decide which options keep their settings, so the two can never disagree about whether there is
///         anything to do. Order is deliberately not part of it: the helper's order is how a dropdown is drawn,
///         not a fact about the house, and a call to action raised over it would never stop being raised.
///     </para>
/// </remarks>
public static class HouseModeSync
{
	/// <summary>
	///     What the helper offers against what the document carries.
	/// </summary>
	/// <param name="mode">The configured house mode, or <c>null</c> when the document has none.</param>
	/// <param name="liveOptions">The helper's options as Home Assistant last reported them. Empty means "cannot tell".</param>
	/// <returns>The comparison — never <c>null</c>, and never a comparison it could not make.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="liveOptions"/> is <c>null</c>.</exception>
	public static HouseModeOptionsDiff Compare(HouseModeConfig? mode, IReadOnlyList<string> liveOptions)
	{
		ArgumentNullException.ThrowIfNull(liveOptions);

		List<string> live = Clean(liveOptions);

		// No helper picked, or none of its options came back. Neither is a difference to act on: the first has
		// nothing to compare against, and the second cannot tell a silent Home Assistant from an empty helper.
		if (mode?.Entity is not { Length: > 0 } || live.Count == 0)
			return new HouseModeOptionsDiff(false, [], []);

		List<string> configured = Clean(mode.Options.Select(option => option.Value));

		return new HouseModeOptionsDiff(
			true,
			[.. live.Where(value => !configured.Contains(value, StringComparer.OrdinalIgnoreCase))],
			[.. configured.Where(value => !live.Contains(value, StringComparer.OrdinalIgnoreCase))]);
	}

	/// <summary>
	///     What the gap is, in a heading — or <c>null</c> when there is no gap to head.
	/// </summary>
	/// <remarks>
	///     Three headings rather than one, because the three cases are different news and the middle one is easy to
	///     misread. Options only this list has are usually a rename or a deletion in Home Assistant, and a heading
	///     saying the helper had "changed" would send somebody looking for a change they did not make.
	/// </remarks>
	/// <param name="diff">The comparison to head.</param>
	/// <exception cref="ArgumentNullException"><paramref name="diff"/> is <c>null</c>.</exception>
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

	/// <summary>
	///     What taking the helper's list would do, named rather than counted — or <c>null</c> when it would do
	///     nothing.
	/// </summary>
	/// <remarks>
	///     The options are named, in the manner of the re-setup warning: this button drops configuration somebody
	///     wrote by hand, and "2 options will be removed" leaves them to work out which two while deciding whether
	///     to press it.
	/// </remarks>
	/// <param name="diff">The comparison to describe.</param>
	/// <exception cref="ArgumentNullException"><paramref name="diff"/> is <c>null</c>.</exception>
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
	///     Rebuilds <paramref name="mode"/>'s option list from the helper's, keeping what each surviving option was
	///     configured to mean.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>An option the helper still offers keeps its entire configuration</b> — its kind, its scene, its
	///         clamp period, its resets. Two lists that both offer "Away" mean the same thing by it, and rebuilding
	///         from scratch would throw away somebody's work over a rename elsewhere in the list. Options the helper
	///         does not offer are dropped: they can never be selected, and leaving them would keep the section
	///         describing a mode that cannot happen.
	///     </para>
	///     <para>
	///         Nothing is guessed. An option the document has never seen arrives with no meaning assigned rather
	///         than one inferred from its wording — the adoption at first start is allowed to guess because it is
	///         reporting a discovery, whereas this is a person pressing a button, and quietly assigning meanings to
	///         their options would be a different edit from the one they asked for.
	///     </para>
	///     <para>
	///         An empty <paramref name="liveOptions"/> does nothing at all. That is the same refusal
	///         <see cref="Compare"/> makes and for the same reason: a Home Assistant that has gone quiet answers
	///         with an empty list, and emptying somebody's house modes over a dropped connection is the one outcome
	///         this must be incapable of.
	///     </para>
	/// </remarks>
	/// <param name="mode">The configured house mode to rebuild. <c>null</c> does nothing.</param>
	/// <param name="liveOptions">The helper's options as Home Assistant last reported them.</param>
	/// <returns>Whether anything changed, so a caller can tell an adoption from a no-op.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="liveOptions"/> is <c>null</c>.</exception>
	public static bool Adopt(HouseModeConfig? mode, IReadOnlyList<string> liveOptions)
	{
		ArgumentNullException.ThrowIfNull(liveOptions);

		List<string> live = Clean(liveOptions);

		if (mode is null || live.Count == 0)
			return false;

		// Read before the loop, because keeping an option re-stamps its value in place — and a comparison taken
		// afterwards would be comparing the new list against itself and always report nothing happened.
		List<string?> before = [.. mode.Options.Select(option => option.Value)];
		List<HouseModeOptionConfig> adopted = [];

		foreach (string value in live)
		{
			HouseModeOptionConfig? kept = mode.OptionFor(value);

			if (kept is not null)
			{
				// Re-stamped with the helper's spelling: the two are equal by this comparison, and the document
				// should read the way the dropdown does rather than preserve an older capitalisation of it.
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

	/// <summary>Trimmed, non-blank, and named once each — the shape both sides of the comparison are read in.</summary>
	/// <remarks>
	///     Public because the period-select panel reads a helper's live options in exactly this shape, and had grown
	///     its own nested-loop copy of it. One reading of "what the dropdown is offering" rather than two that agree
	///     until somebody changes the trimming in one of them.
	/// </remarks>
	/// <param name="values">Raw option strings as Home Assistant reported them.</param>
	/// <exception cref="ArgumentNullException"><paramref name="values"/> is <c>null</c>.</exception>
	public static List<string> Clean(IEnumerable<string?> values) =>
	[
		.. values
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Select(value => value!.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)
	];

	/// <summary>"a", "a and b", "a, b and c" — a list a person reads rather than parses.</summary>
	private static string Join(IReadOnlyList<string> parts) => parts.Count switch
	{
		1 => parts[0],
		2 => $"{parts[0]} and {parts[1]}",
		_ => $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}"
	};
}
