using Microsoft.AspNetCore.Components;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     What kind of value a sentence token holds, which decides how it is written and how it is picked.
/// </summary>
/// <remarks>
///     The kind is not decoration: it is the contract between the component that renders the token and the page
///     that receives the edit. A page reading <see cref="SentenceEdit.Seconds"/> off a
///     <see cref="Percentage"/> token has made a mistake the kind makes visible.
/// </remarks>
public enum TokenKind
{
	/// <summary>A span of time. Carried in whole seconds; written as "30 s", "10 min", "2 h".</summary>
	Duration,

	/// <summary>A proportion. Carried as percent 0-100; written as "50 %". The config's 0-1 factors convert.</summary>
	Percentage,

	/// <summary>A bare quantity with a unit — lux, degrees, kelvin. Carried as its own number.</summary>
	Number,

	/// <summary>One of a fixed set — an enum, a mode, a named option. Carried as the option's own key.</summary>
	Choice,

	/// <summary>
	///     A yes/no, written as the words it means in the sentence and carried as <c>true</c>/<c>false</c>.
	/// </summary>
	/// <remarks>
	///     Its own kind rather than a two-option <see cref="Choice"/>, because two options do not deserve a
	///     popover: a switch that opens a menu to offer "on" and "off" costs a tap and a re-read for a decision
	///     the reader had already made. A toggle token flips where it stands. It matters most for a setting that
	///     gates others — the sentence after it appears or disappears on the same tap.
	/// </remarks>
	Toggle,

	/// <summary>A Home Assistant entity id. Carried verbatim.</summary>
	Entity
}

/// <summary>
///     Where a token's value came from: the house's default, or a decision made for this room.
/// </summary>
/// <remarks>
///     This is the sentence's most load-bearing mark and the reason the amber dot exists. Somebody who has tuned
///     four rooms over six months cannot remember which four; the dot is how the page answers that without
///     being asked. Getting it wrong in the safe-looking direction — showing everything as inherited — quietly
///     tells the owner they have changed nothing.
/// </remarks>
public enum TokenOrigin
{
	/// <summary>Provenance does not apply. The house's own defaults have nothing to inherit from.</summary>
	None,

	/// <summary>The value follows the house default. Written plainly, with no mark.</summary>
	Inherited,

	/// <summary>This room states its own value. Written with the amber dot.</summary>
	Own
}

/// <summary>
///     One value a token offers, as it is written and as it is carried.
/// </summary>
/// <remarks>
///     <para>
///         Two fields because the two must be allowed to differ: "10 min" is what a person picks and <c>600</c> is
///         what the document stores, and a popover that handed back its own label would make every page re-parse
///         English. <see cref="Value"/> is always culture-invariant — see <see cref="TokenFormat"/>.
///     </para>
///     <para>
///         <see cref="Key"/> and <see cref="Kind"/> exist for the option that answers the sentence's question by
///         changing a <i>different</i> setting. Turning a lux threshold off is the room deciding by the sun
///         instead — a change of darkness source, not a lux reading that secretly means "disabled". Carrying the
///         redirect on the option keeps the alternative out of the document: a sentinel like <c>-1</c> would have
///         to be understood by the engine, the validator, every format string and anybody reading the YAML, and
///         each of them is a place for it to be read as a real number.
///     </para>
/// </remarks>
/// <param name="Text">What the option says in the popover.</param>
/// <param name="Value">The canonical value handed back to the page, in the kind's own encoding.</param>
/// <param name="Key">The setting this option changes, when that is not the token's own.</param>
/// <param name="Kind">How <see cref="Value"/> is encoded, when it is not the token's own kind.</param>
public sealed record TokenChoice(string Text, string Value, string? Key = null, TokenKind? Kind = null);

/// <summary>One piece of a sentence: either prose or an inline value.</summary>
public abstract record SentencePart;

/// <summary>Prose. Rendered as written, spaces and punctuation included.</summary>
/// <param name="Text">The words.</param>
public sealed record SentenceText(string Text) : SentencePart;

/// <summary>
///     A small drawing set inline in a sentence, where a picture says what the prose cannot.
/// </summary>
/// <remarks>
///     <para>
///         The escape hatch, and deliberately a narrow one. Most settings are a number with a unit and read
///         perfectly as words; a few are shapes — a response curve, a band, a blend — that a reader understands
///         instantly as a line and never as a sentence. Those get a figure beside their token rather than a
///         paragraph of description nobody finishes.
///     </para>
///     <para>
///         The content is the caller's, because the drawing belongs to the setting and not to the sentence
///         machinery: a component here that tried to know how to plot every future value would be guessing.
///         <see cref="AltText"/> is not optional politeness — it is what <see cref="Sentence.PlainText"/>, a
///         screen reader and a test all read in the figure's place.
///     </para>
/// </remarks>
/// <param name="AltText">The figure in words, for reading aloud and for asserting on.</param>
/// <param name="Content">The drawing. Inline SVG, sized to sit on a line of text.</param>
public sealed record SentenceFigure(string AltText, RenderFragment Content) : SentencePart;

/// <summary>
///     A value inside a sentence, rendered as the inline control that changes it.
/// </summary>
/// <remarks>
///     <para>
///         The design's central move: reading a room's behaviour and changing it are the same act, so the value
///         in the prose <i>is</i> the control. That only works if the token knows enough to be operated without
///         the sentence around it — hence the label (which a screen reader hears in place of the surrounding
///         words), the curated choices, and the house value it can be sent back to.
///     </para>
///     <para>
///         <see cref="Choices"/> is curated rather than complete, deliberately. The popover offers the handful
///         of values a sane house uses; everything between them lives in the All-settings row behind
///         <i>show more</i>. A token with no choices is still a token — it renders, it reads, it just cannot be
///         picked from, which is the honest rendering of a value with no sensible shortlist.
///     </para>
/// </remarks>
/// <param name="Key">
///     What the page switches on to apply the edit. For area settings this is the <c>AreaSettings</c> property
///     name, so a call site reads <c>case nameof(AreaSettings.VacancyTimeoutSeconds)</c> and cannot drift from
///     the schema without the compiler noticing.
/// </param>
/// <param name="Kind">How the value is written and how <see cref="SentenceEdit"/> should be read.</param>
/// <param name="Text">The value as it appears in the sentence, already formatted with its unit.</param>
/// <param name="Origin">Whether this is the room's own value or the house's.</param>
/// <param name="Label">
///     The setting's name in the words the All-settings rows use. The accessible name of the control, and the
///     thing a toast should quote — the surrounding prose is not available to either.
/// </param>
/// <param name="Choices">The curated values the popover offers. Empty is allowed and renders a plain token.</param>
/// <param name="HouseText">
///     The house default, already formatted, when this token is a room's own value — the popover's road back.
///     <c>null</c> when there is nothing to go back to.
/// </param>
/// <param name="Editable">
///     Whether this particular value can be changed here. <c>false</c> renders a dashed, inert token: a value
///     that is genuinely fixed should look different from one nobody has tried to click yet.
/// </param>
public sealed record SentenceToken(
	string Key,
	TokenKind Kind,
	string Text,
	TokenOrigin Origin,
	string Label,
	IReadOnlyList<TokenChoice> Choices,
	string? HouseText = null,
	bool Editable = true) : SentencePart;

/// <summary>
///     One sentence: prose with its values inline.
/// </summary>
/// <param name="Parts">The pieces, in reading order.</param>
public sealed record Sentence(IReadOnlyList<SentencePart> Parts)
{
	/// <summary>
	///     The sentence as plain prose, values included but unmarked.
	/// </summary>
	/// <remarks>
	///     For titles, logs, toasts and tests — anywhere the sentence has to be one string. Also the honest way
	///     to assert on a built sentence without asserting on markup, which this repo has no harness for.
	/// </remarks>
	public string PlainText => string.Concat(Parts.Select(part => part switch
	{
		SentenceText text => text.Text,
		SentenceToken token => token.Text,
		SentenceFigure figure => figure.AltText,
		_ => string.Empty
	}));

	/// <summary>How many of this sentence's values are the room's own — the count the amber dots make visible.</summary>
	public int OwnValueCount => Parts.Count(part => part is SentenceToken { Origin: TokenOrigin.Own });
}

/// <summary>
///     One token changed, handed to the page to apply.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is a request, not a write.</b> The sentence view never touches the document and never saves:
///         the page decides what an edit means, mutates its own copy, and puts it through the one existing save
///         pipeline. A component that wrote its own way to disk would be a second write path beside a
///         deliberately singular one.
///     </para>
///     <para>
///         The value arrives as an invariant string and the accessors below turn it back into a number. That
///         indirection is on purpose: a popover of curated options is a list of strings whatever the kind, and
///         one encoding means the same component serves durations, percentages, lux and enums without a
///         generic parameter that every call site would have to spell out.
///     </para>
/// </remarks>
/// <param name="Key">The token's key — the <c>AreaSettings</c> property name, for area sentences.</param>
/// <param name="Kind">The token's kind, so a page can assert it is reading the value the way it was written.</param>
/// <param name="Value">The chosen value in its canonical encoding.</param>
public sealed record SentenceEdit(string Key, TokenKind Kind, string Value)
{
	/// <summary>The value as whole seconds. For <see cref="TokenKind.Duration"/>.</summary>
	public int Seconds => (int)Math.Round(Number, MidpointRounding.AwayFromZero);

	/// <summary>The value as whole minutes, for the settings the schema keeps in minutes.</summary>
	public int Minutes => (int)Math.Round(Number / 60.0, MidpointRounding.AwayFromZero);

	/// <summary>The value as a span. For <see cref="TokenKind.Duration"/>.</summary>
	public TimeSpan Span => TimeSpan.FromSeconds(Seconds);

	/// <summary>The value as percent, 0-100. For <see cref="TokenKind.Percentage"/>.</summary>
	public double Percent => Number;

	/// <summary>The value as a fraction, 0-1 — what the schema's <c>*Factor</c> properties hold.</summary>
	public double Fraction => Number / 100.0;

	/// <summary>The value as a number, whatever its unit. Zero when the value is not numeric.</summary>
	public double Number =>
		double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;

	/// <summary>The value as an integer.</summary>
	public int Integer => (int)Math.Round(Number, MidpointRounding.AwayFromZero);

	/// <summary>The value as a yes/no. For <see cref="TokenKind.Toggle"/>.</summary>
	public bool Flag => bool.TryParse(Value, out bool parsed) && parsed;

	/// <summary>
	///     The value as one of an enum's members. For <see cref="TokenKind.Choice"/> tokens built from an enum.
	/// </summary>
	/// <typeparam name="TEnum">The enum the choice came from.</typeparam>
	/// <param name="value">The parsed member, or the default when the value is not one.</param>
	/// <returns>Whether the value named a member.</returns>
	public bool TryEnum<TEnum>(out TEnum value) where TEnum : struct, Enum =>
		Enum.TryParse(Value, ignoreCase: false, out value);
}
