using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     The identity sheet's one sentence, split where the markup has to put something interactive in.
/// </summary>
/// <remarks>
///     Runs of prose, not a <see cref="Sentence"/>, because two of the four values are not tokens: the
///     house name is free text and the people are chips with live presence dots.
/// </remarks>
/// <param name="BeforeName">The words up to the house name.</param>
/// <param name="AfterName">The words between the house name and the person chips.</param>
/// <param name="BeforeDelay">The words between the person chips and the empty-house delay.</param>
/// <param name="AfterDelay">The words after the delay, ending the sentence.</param>
public sealed record IdentityParts(string BeforeName, string AfterName, string BeforeDelay, string AfterDelay);

/// <summary>
///     The two facts discovery cannot know: what this house is called and who lives in it.
/// </summary>
/// <remarks>
///     Nothing here writes. The staging lives on <see cref="CommissioningDraft"/> and reaches disk only through
///     the commit button's one <c>LightingEngineHost.Save</c>.
/// </remarks>
public static class IdentitySentence
{
	// The same string CreateDefault seeds. A second spelling here renames every house that skipped the sheet.
	public const string DefaultHouseName = "Adaptive lighting";

	/// <summary>The prose around the sheet's three controls.</summary>
	public static IdentityParts Parts { get; } = new(
		"This house is called ",
		". ",
		" decide Home and Away; the house counts as empty ",
		" after the last person leaves.");

	/// <summary>What the sheet shows as the house's name: what the document holds, or the shipped default.</summary>
	public static string Display(string? configName) =>
		string.IsNullOrWhiteSpace(configName) ? DefaultHouseName : configName.Trim();

	/// <summary>What a typed name is staged as, or <c>null</c> when the box was cleared.</summary>
	/// <remarks>
	///     <c>null</c>, not <see cref="DefaultHouseName"/>: writing the placeholder out as a real name makes a
	///     house that never answered indistinguishable from one that chose that name.
	/// </remarks>
	public static string? Normalize(string? typed) =>
		string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();

	/// <summary>
	///     The empty-house delay as the sentence's one real token, sharing
	///     <see cref="HouseSentences.AwayDebounceChoices"/> and its label with the House tab.
	/// </summary>
	public static SentenceToken Delay(int minutes) => new(
		nameof(GlobalConfig.AwayDebounceMinutes),
		TokenKind.Duration,
		TokenFormat.DurationFromMinutes(Math.Max(0, minutes)),
		TokenOrigin.None,
		"Count the house as empty after",
		HouseSentences.AwayDebounceChoices);

	/// <summary>The whole sentence as prose, for asserting on and for reading aloud.</summary>
	/// <param name="houseName">The name as <see cref="Display"/> resolves it.</param>
	/// <param name="people">The people still counted, in the order the chips render.</param>
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

	/// <summary>The checklist's status line: the answers as they stand, never a tick for having visited.</summary>
	/// <remarks>Read off the document, so nothing here needs a "was this sheet opened" flag.</remarks>
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

	/// <summary>The people as the sentence says them, which is not how a chip row says them.</summary>
	private static string PeopleClause(IReadOnlyList<string> people) => people.Count switch
	{
		0 => "Nobody",
		1 => people[0],
		2 => $"{people[0]} and {people[1]}",
		_ => $"{string.Join(", ", people.Take(people.Count - 1))} and {people[^1]}"
	};
}
