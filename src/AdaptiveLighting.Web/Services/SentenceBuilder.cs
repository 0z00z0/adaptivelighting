using Microsoft.AspNetCore.Components;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     Builds one sentence from prose and typed values.
/// </summary>
/// <remarks>
///     <para>
///         Fluent because a sentence is written left to right and the code that builds it should read the same
///         way — a call site is meant to be legible as the sentence it produces, so that changing the wording is
///         editing English rather than assembling a list.
///     </para>
///     <para>
///         Each typed method formats its own value, so the words in the sentence and the value carried back can
///         never disagree. That is the failure this class exists to prevent: a token that says "10 min" and
///         hands back <c>10</c> would set a ten-<i>second</i> timeout, and nothing in the UI would look wrong.
///     </para>
///     <para>
///         Not a component and not a page. Everything here is pure, which is what lets the §3 sentence table be
///         a test rather than a screenshot.
///     </para>
/// </remarks>
public sealed class SentenceBuilder
{
	private static readonly IReadOnlyList<TokenChoice> NoChoices = [];

	private readonly List<SentencePart> _parts = [];

	/// <summary>Starts a sentence, optionally with its opening words.</summary>
	/// <param name="text">The opening prose, if the sentence starts with words rather than a value.</param>
	public static SentenceBuilder Start(string text = "")
	{
		SentenceBuilder builder = new();

		return text.Length > 0 ? builder.Text(text) : builder;
	}

	/// <summary>Adds prose. Written exactly as given, spaces and punctuation included.</summary>
	/// <param name="text">The words.</param>
	public SentenceBuilder Text(string text)
	{
		_parts.Add(new SentenceText(text));

		return this;
	}

	/// <summary>Adds an already-built token, for a caller that assembled one itself.</summary>
	/// <param name="token">The token.</param>
	/// <exception cref="ArgumentNullException"><paramref name="token"/> is <c>null</c>.</exception>
	public SentenceBuilder Token(SentenceToken token)
	{
		ArgumentNullException.ThrowIfNull(token);

		_parts.Add(token);

		return this;
	}

	/// <summary>Adds a span of time, written in the largest exact unit and carried in seconds.</summary>
	/// <param name="key">What the page switches on — an <c>AreaSettings</c> property name for area sentences.</param>
	/// <param name="label">The setting's name, as the All-settings rows write it.</param>
	/// <param name="seconds">The current value.</param>
	/// <param name="choices">The curated shortlist the popover offers.</param>
	/// <param name="origin">Whether this is the room's own value or the house's.</param>
	/// <param name="houseSeconds">The house default, for the road back. Only kept when the value is the room's own.</param>
	/// <param name="editable">Whether this value can be changed here.</param>
	public SentenceBuilder Duration(
		string key,
		string label,
		int seconds,
		IReadOnlyList<TokenChoice>? choices = null,
		TokenOrigin origin = TokenOrigin.None,
		int? houseSeconds = null,
		bool editable = true) =>
		Token(new SentenceToken(
			key,
			TokenKind.Duration,
			TokenFormat.Duration(seconds),
			origin,
			label,
			choices ?? NoChoices,
			HouseText(origin, houseSeconds is { } house ? TokenFormat.Duration(house) : null),
			editable));

	/// <summary>Adds a proportion, written with a percent sign and carried as 0-100.</summary>
	/// <param name="key">What the page switches on.</param>
	/// <param name="label">The setting's name.</param>
	/// <param name="percent">The current value, 0-100.</param>
	/// <param name="choices">The curated shortlist.</param>
	/// <param name="origin">Whether this is the room's own value or the house's.</param>
	/// <param name="housePercent">The house default, 0-100.</param>
	/// <param name="editable">Whether this value can be changed here.</param>
	public SentenceBuilder Percent(
		string key,
		string label,
		double percent,
		IReadOnlyList<TokenChoice>? choices = null,
		TokenOrigin origin = TokenOrigin.None,
		double? housePercent = null,
		bool editable = true) =>
		Token(new SentenceToken(
			key,
			TokenKind.Percentage,
			TokenFormat.Percent(percent),
			origin,
			label,
			choices ?? NoChoices,
			HouseText(origin, housePercent is { } house ? TokenFormat.Percent(house) : null),
			editable));

	/// <summary>Adds a quantity with a unit — lux, degrees, kelvin.</summary>
	/// <param name="key">What the page switches on.</param>
	/// <param name="label">The setting's name.</param>
	/// <param name="value">The current value.</param>
	/// <param name="unit">Its unit.</param>
	/// <param name="choices">The curated shortlist.</param>
	/// <param name="origin">Whether this is the room's own value or the house's.</param>
	/// <param name="houseValue">The house default.</param>
	/// <param name="editable">Whether this value can be changed here.</param>
	public SentenceBuilder Number(
		string key,
		string label,
		double value,
		string unit = "",
		IReadOnlyList<TokenChoice>? choices = null,
		TokenOrigin origin = TokenOrigin.None,
		double? houseValue = null,
		bool editable = true) =>
		Token(new SentenceToken(
			key,
			TokenKind.Number,
			TokenFormat.Number(value, unit),
			origin,
			label,
			choices ?? NoChoices,
			HouseText(origin, houseValue is { } house ? TokenFormat.Number(house, unit) : null),
			editable));

	/// <summary>
	///     Adds one of a fixed set of named options.
	/// </summary>
	/// <remarks>
	///     The words come from <paramref name="choices"/> rather than from the caller, so the value written in
	///     the sentence is by construction the same words the popover shows as current. A value with no matching
	///     option is written as itself — a document holding something the shortlist does not offer should say so
	///     rather than silently render as the nearest option.
	/// </remarks>
	/// <param name="key">What the page switches on.</param>
	/// <param name="label">The setting's name.</param>
	/// <param name="value">The current value, in its carried form.</param>
	/// <param name="choices">Every option, one of which should match <paramref name="value"/>.</param>
	/// <param name="origin">Whether this is the room's own value or the house's.</param>
	/// <param name="houseValue">The house default, in its carried form.</param>
	/// <param name="editable">Whether this value can be changed here.</param>
	/// <exception cref="ArgumentNullException"><paramref name="choices"/> is <c>null</c>.</exception>
	public SentenceBuilder Choice(
		string key,
		string label,
		string value,
		IReadOnlyList<TokenChoice> choices,
		TokenOrigin origin = TokenOrigin.None,
		string? houseValue = null,
		bool editable = true)
	{
		ArgumentNullException.ThrowIfNull(choices);

		return Token(new SentenceToken(
			key,
			TokenKind.Choice,
			WordsFor(choices, value),
			origin,
			label,
			choices,
			HouseText(origin, houseValue is null ? null : WordsFor(choices, houseValue)),
			editable));
	}

	/// <summary>
	///     Adds a yes/no, written as what it means here and flipped in place.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The two texts are the setting's meaning in this sentence, not the words "on" and "off": a token
	///         reading <i>brightens with daylight</i> / <i>follows the schedule</i> leaves the sentence readable
	///         in both states, which "on" would not. That is the whole reason a boolean is worth putting in prose
	///         at all.
	///     </para>
	///     <para>
	///         The natural partner of <see cref="When"/>: a toggle that gates other settings goes in the
	///         sentence, and the clause describing those settings is built only when it is on.
	///     </para>
	/// </remarks>
	/// <param name="key">What the page switches on.</param>
	/// <param name="label">The setting's name, as the All-settings rows write it.</param>
	/// <param name="value">The current value.</param>
	/// <param name="onText">What the sentence says when it is on.</param>
	/// <param name="offText">What the sentence says when it is off.</param>
	/// <param name="origin">Whether this is the room's own value or the house's.</param>
	/// <param name="houseValue">The house default, for the road back.</param>
	/// <param name="editable">Whether this value can be changed here.</param>
	public SentenceBuilder Toggle(
		string key,
		string label,
		bool value,
		string onText,
		string offText,
		TokenOrigin origin = TokenOrigin.None,
		bool? houseValue = null,
		bool editable = true)
	{
		IReadOnlyList<TokenChoice> choices = TokenChoices.Of((onText, "true"), (offText, "false"));

		return Token(new SentenceToken(
			key,
			TokenKind.Toggle,
			value ? onText : offText,
			origin,
			label,
			choices,
			HouseText(origin, houseValue is { } house ? (house ? onText : offText) : null),
			editable));
	}

	/// <summary>
	///     Adds a small drawing inline, for a value that is a shape rather than a quantity.
	/// </summary>
	/// <param name="altText">The figure in words. Read aloud in the drawing's place, and asserted on in tests.</param>
	/// <param name="content">The drawing — inline SVG, sized to sit on a line of text.</param>
	/// <exception cref="ArgumentNullException"><paramref name="content"/> is <c>null</c>.</exception>
	public SentenceBuilder Figure(string altText, RenderFragment content)
	{
		ArgumentNullException.ThrowIfNull(content);

		_parts.Add(new SentenceFigure(altText, content));

		return this;
	}

	/// <summary>
	///     Adds a clause only when it applies.
	/// </summary>
	/// <remarks>
	///     <para>
	///         How this model says "that setting only matters while this one is on": the dependent clause is not
	///         built. A setting that cannot take effect should not be on the page at all — greying it out still
	///         spends the reader's attention on it, still invites the tap, and still has to explain itself. The
	///         sentence simply gets shorter, and grows back on the same tap that turns the gate on.
	///     </para>
	///     <para>
	///         Keeping it fluent matters: the alternative is an <c>if</c> around half a sentence, and the call
	///         site stops reading like the English it produces.
	///     </para>
	/// </remarks>
	/// <param name="condition">Whether the clause applies.</param>
	/// <param name="clause">Builds the clause. Not called at all when <paramref name="condition"/> is false.</param>
	/// <exception cref="ArgumentNullException"><paramref name="clause"/> is <c>null</c>.</exception>
	public SentenceBuilder When(bool condition, Action<SentenceBuilder> clause)
	{
		ArgumentNullException.ThrowIfNull(clause);

		if (condition)
			clause(this);

		return this;
	}

	/// <summary>Adds a Home Assistant entity id, written and carried verbatim.</summary>
	/// <param name="key">What the page switches on.</param>
	/// <param name="label">The setting's name.</param>
	/// <param name="entityId">The current entity.</param>
	/// <param name="choices">The entities worth offering, if any.</param>
	/// <param name="origin">Whether this is the room's own value or the house's.</param>
	/// <param name="houseEntityId">The house default.</param>
	/// <param name="editable">Whether this value can be changed here.</param>
	public SentenceBuilder Entity(
		string key,
		string label,
		string entityId,
		IReadOnlyList<TokenChoice>? choices = null,
		TokenOrigin origin = TokenOrigin.None,
		string? houseEntityId = null,
		bool editable = true) =>
		Token(new SentenceToken(
			key,
			TokenKind.Entity,
			entityId,
			origin,
			label,
			choices ?? NoChoices,
			HouseText(origin, houseEntityId),
			editable));

	/// <summary>The finished sentence.</summary>
	public Sentence Build() => new([.. _parts]);

	/// <summary>
	///     The house default is only carried when the room has departed from it.
	/// </summary>
	/// <remarks>
	///     A token that already follows the house has no road back to offer, and offering one anyway would put
	///     "Use house setting (10 min)" under a value that is the house's 10 min — an action that does nothing,
	///     phrased as though it did something.
	/// </remarks>
	private static string? HouseText(TokenOrigin origin, string? houseText) =>
		origin == TokenOrigin.Own ? houseText : null;

	private static string WordsFor(IReadOnlyList<TokenChoice> choices, string value) =>
		choices.FirstOrDefault(choice => string.Equals(choice.Value, value, StringComparison.Ordinal))?.Text ?? value;
}
