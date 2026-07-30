using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     The identity sheet's one sentence, split where the markup has to put something interactive in.
/// </summary>
/// <remarks>
///     Three runs of prose rather than one <see cref="Sentence"/>, because two of the four values in this
///     sentence are not tokens: the house name is free text (no <see cref="TokenKind"/> carries that, and adding
///     one would touch the shared token machinery for a control used exactly once), and the people are chips with
///     live presence dots. Everything that <i>is</i> a token stays a token — see <see cref="IdentitySentence.Delay"/>.
/// </remarks>
/// <param name="BeforeName">The words up to the house name.</param>
/// <param name="AfterName">The words between the house name and the person chips.</param>
/// <param name="BeforeDelay">The words between the person chips and the empty-house delay.</param>
/// <param name="AfterDelay">The words after the delay, ending the sentence.</param>
public sealed record IdentityParts(string BeforeName, string AfterName, string BeforeDelay, string AfterDelay);

/// <summary>
///     The two facts discovery cannot know — what this house is called and who lives in it — written as the
///     product's own grammar rather than as a form.
/// </summary>
/// <remarks>
///     <para>
///         Pure, for the reason every other projection here is pure: this repo has no Razor render harness, and a
///         sentence assembled inside markup is a sentence nothing can assert about. What is worth asserting is
///         the wording of a sentence a new owner reads once and never again, and the two rules underneath it —
///         a blank name falls back to the shipped default rather than to an empty house, and the checklist's
///         status line is a summary of the document rather than a visited-flag.
///     </para>
///     <para>
///         <b>Nothing here writes.</b> The staging lives on <see cref="CommissioningDraft"/> and reaches disk only
///         through the commit button's one <c>LightingEngineHost.Save</c>.
///     </para>
/// </remarks>
public static class IdentitySentence
{
	/// <summary>
	///     What an unnamed house is called.
	/// </summary>
	/// <remarks>
	///     The same string <see cref="AdaptiveLightingConfig.CreateDefault"/> seeds, so skipping this sheet leaves
	///     the document saying exactly what it already said. A second spelling here would rename every house that
	///     never touched the sheet.
	/// </remarks>
	public const string DefaultHouseName = "Adaptive lighting";

	/// <summary>The prose around the sheet's three controls.</summary>
	public static IdentityParts Parts { get; } = new(
		"This house is called ",
		". ",
		" decide Home and Away; the house counts as empty ",
		" after the last person leaves.");

	/// <summary>
	///     What the sheet shows as the house's name: what the document holds, or the shipped default.
	/// </summary>
	/// <param name="configName">The document's <see cref="AdaptiveLightingConfig.ConfigName"/>.</param>
	public static string Display(string? configName) =>
		string.IsNullOrWhiteSpace(configName) ? DefaultHouseName : configName.Trim();

	/// <summary>
	///     What a typed name is staged as, or <c>null</c> when the box was cleared.
	/// </summary>
	/// <remarks>
	///     <c>null</c> rather than the default string, so clearing the box restores inheritance instead of pinning
	///     the placeholder as a real name — a house called "Adaptive lighting" in the YAML and a house that never
	///     answered read identically to every later reader, and only one of them is true.
	/// </remarks>
	/// <param name="typed">Whatever is in the box.</param>
	public static string? Normalize(string? typed) =>
		string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();

	/// <summary>
	///     The empty-house delay as the sentence's one real token, sharing
	///     <see cref="HouseSentences.AwayDebounceChoices"/> with the House tab.
	/// </summary>
	/// <remarks>
	///     <see cref="TokenOrigin.None"/>: this is a house-wide setting, so there is no house above it to inherit
	///     from and no amber dot to earn. The label is the words the House tab's own row uses, so a screen reader
	///     hears one name for one setting across both surfaces.
	/// </remarks>
	/// <param name="minutes">The staged or stored <see cref="GlobalConfig.AwayDebounceMinutes"/>.</param>
	public static SentenceToken Delay(int minutes) => new(
		nameof(GlobalConfig.AwayDebounceMinutes),
		TokenKind.Duration,
		TokenFormat.DurationFromMinutes(Math.Max(0, minutes)),
		TokenOrigin.None,
		"Count the house as empty after",
		HouseSentences.AwayDebounceChoices);

	/// <summary>
	///     The whole sentence as prose, for asserting on and for reading aloud.
	/// </summary>
	/// <param name="houseName">The name as <see cref="Display"/> resolves it.</param>
	/// <param name="people">The people still counted, in the order the chips render.</param>
	/// <param name="minutes">The empty-house delay.</param>
	/// <exception cref="ArgumentNullException"><paramref name="people"/> is <c>null</c>.</exception>
	public static string PlainText(string? houseName, IReadOnlyList<string> people, int minutes)
	{
		ArgumentNullException.ThrowIfNull(people);

		return string.Concat(
			Parts.BeforeName,
			Display(houseName),
			Parts.AfterName,
			PeopleClause(people),
			Parts.BeforeDelay,
			TokenFormat.DurationFromMinutes(Math.Max(0, minutes)),
			Parts.AfterDelay);
	}

	/// <summary>
	///     The checklist's status line: the answers as they stand, never a tick for having visited.
	/// </summary>
	/// <remarks>
	///     <para>
	///         This is what makes a replayed wizard a review rather than an interrogation (§6): the line reads the
	///         document, so somebody returning to a half-commissioned house sees their own answers rather than a
	///         blank item. It is also why nothing here takes a "was this sheet opened" flag.
	///     </para>
	///     <para>
	///         The people are named rather than counted up to three, then counted. Three names is the width of the
	///         item; seventeen would push the checklist into a paragraph, which §3's rule forbids.
	///     </para>
	/// </remarks>
	/// <param name="houseName">The name as <see cref="Display"/> resolves it.</param>
	/// <param name="people">The people still counted, in the order the chips render.</param>
	/// <param name="minutes">The empty-house delay.</param>
	/// <exception cref="ArgumentNullException"><paramref name="people"/> is <c>null</c>.</exception>
	public static string StatusLine(string? houseName, IReadOnlyList<string> people, int minutes)
	{
		ArgumentNullException.ThrowIfNull(people);

		string who = people.Count switch
		{
			0 => "nobody counted",
			<= 3 => string.Join(", ", people),
			_ => $"{string.Join(", ", people.Take(2))} and {people.Count - 2} more"
		};

		return $"{Display(houseName)} · {who} · empty after {TokenFormat.DurationFromMinutes(Math.Max(0, minutes))}";
	}

	/// <summary>
	///     The people as the sentence says them, which is not how a chip row says them.
	/// </summary>
	/// <remarks>
	///     Nobody counted is a real state and it is a bad one — a house watching no people never becomes empty and
	///     never sweeps — so it gets a clause that says what follows rather than an empty gap in the sentence.
	/// </remarks>
	private static string PeopleClause(IReadOnlyList<string> people) => people.Count switch
	{
		0 => "Nobody",
		1 => people[0],
		2 => $"{people[0]} and {people[1]}",
		_ => $"{string.Join(", ", people.Take(people.Count - 1))} and {people[^1]}"
	};
}
