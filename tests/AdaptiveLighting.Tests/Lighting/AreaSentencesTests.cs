using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The §3 sentence table, asserted rather than screenshotted.
/// </summary>
/// <remarks>
///     <para>
///         These sentences are the only place several settings are ever read by a person. A sentence that renders
///         the wrong knob is therefore a setting nobody can find, and a sentence that shows a value as the
///         house's when it is the room's own quietly tells the owner they have changed nothing. Neither failure
///         looks like a bug on screen.
///     </para>
///     <para>
///         There is no Razor render harness in this repo, which is exactly why the projection is a pure function
///         and the design's own table is a test.
///     </para>
/// </remarks>
[TestClass]
public sealed class AreaSentencesTests
{
	private static AreaSettings Defaults() => new();

	private static AreaConfig Room() => new() { Name = "Stue", AreaId = "stue" };

	private static SentenceToken TokenFor(IReadOnlyList<Sentence> sentences, string key) =>
		sentences
			.SelectMany(sentence => sentence.Parts)
			.OfType<SentenceToken>()
			.Single(token => token.Key == key);

	// ===================== the sentences themselves =====================

	/// <summary>
	///     A room that overrides nothing reads exactly as the design writes it.
	/// </summary>
	/// <remarks>
	///     Asserted on the whole sentence rather than on the tokens inside it, because the prose between the
	///     values is the part that makes them mean something: "dim to 50 % for 30 s" and "dim to 30 s for 50 %"
	///     contain the same tokens.
	/// </remarks>
	[TestMethod]
	public void An_Untouched_Room_Reads_As_The_Design_Writes_It()
	{
		IReadOnlyList<Sentence> sentences = AreaSentences.ForArea(Room(), Defaults());

		Assert.AreEqual(2, sentences.Count, "movement and hands; no flags are on");

		Assert.AreEqual(
			"Lights when someone moves and it's darker than 1000 lx — or the sun is below 3°. " +
			"After 10 min without movement, dim to 50 % for 30 s, then off.",
			sentences[0].PlainText);

		Assert.AreEqual(
			"Hand changes hold for 2 h; after a manual off, movement is ignored until the room is empty 10 min.",
			sentences[1].PlainText);
	}

	/// <summary>The darkness rule changes the shape of the clause, not just a value inside it.</summary>
	[TestMethod]
	public void Each_Darkness_Rule_Gets_Its_Own_Opening()
	{
		AreaSettings defaults = Defaults();

		Assert.IsTrue(First(DarknessSource.Lux, defaults).StartsWith(
			"Lights when someone moves and it's darker than 1000 lx.", StringComparison.Ordinal),
			"the sensor alone: no mention of the sun");

		Assert.IsTrue(First(DarknessSource.Sun, defaults).StartsWith(
			"Lights when someone moves and the sun is below 3°.", StringComparison.Ordinal),
			"the sun alone: no mention of lux");

		Assert.IsTrue(First(DarknessSource.Always, defaults).StartsWith(
			"Lights when someone moves — whatever the daylight.", StringComparison.Ordinal),
			"a windowless room does not check anything");

		static string First(DarknessSource source, AreaSettings defaults)
		{
			AreaConfig room = Room();
			room.Darkness = source;

			return AreaSentences.ForArea(room, defaults).First().PlainText;
		}
	}

	/// <summary>
	///     The windowless case keeps its rule reachable from the sentence, since it has no threshold to show.
	/// </summary>
	[TestMethod]
	public void The_Windowless_Room_Can_Change_Its_Rule_From_The_Sentence()
	{
		AreaConfig room = Room();
		room.Darkness = DarknessSource.Always;

		SentenceToken token = TokenFor(AreaSentences.ForArea(room, Defaults()), nameof(AreaSettings.Darkness));

		Assert.AreEqual(TokenKind.Choice, token.Kind);
		Assert.AreEqual("whatever the daylight", token.Text);
		Assert.AreEqual(4, token.Choices.Count, "all four ways of deciding stay reachable");
	}

	// ===================== provenance =====================

	/// <summary>
	///     A value the room states is its own, and it carries the way back to the house's.
	/// </summary>
	[TestMethod]
	public void A_Room_That_States_A_Value_Owns_It()
	{
		AreaConfig room = Room();
		room.VacancyTimeoutSeconds = 300;

		SentenceToken token = TokenFor(AreaSentences.ForArea(room, Defaults()), nameof(AreaSettings.VacancyTimeoutSeconds));

		Assert.AreEqual(TokenOrigin.Own, token.Origin);
		Assert.AreEqual("5 min", token.Text);
		Assert.AreEqual("10 min", token.HouseText, "the popover needs the road back, already written out");
	}

	/// <summary>A value the room leaves alone follows the house, and has no road back to offer.</summary>
	[TestMethod]
	public void A_Value_The_Room_Leaves_Alone_Follows_The_House()
	{
		SentenceToken token = TokenFor(
			AreaSentences.ForArea(Room(), Defaults()),
			nameof(AreaSettings.VacancyTimeoutSeconds));

		Assert.AreEqual(TokenOrigin.Inherited, token.Origin);
		Assert.AreEqual("10 min", token.Text);
		Assert.IsNull(token.HouseText,
			"offering 'use house setting (10 min)' under the house's own 10 min is an action that does nothing");
	}

	/// <summary>
	///     Provenance is read off the schema's <c>null</c>, never guessed by comparing values.
	/// </summary>
	/// <remarks>
	///     A room that deliberately pins 10 min while the house also says 10 min has made a decision — one taken
	///     precisely so a later change to the house leaves this room alone. Comparing values instead of reading
	///     the null would erase exactly those overrides, and erase them silently.
	/// </remarks>
	[TestMethod]
	public void Pinning_The_House_Value_Is_Still_The_Rooms_Own_Decision()
	{
		AreaSettings defaults = Defaults();

		AreaConfig room = Room();
		room.VacancyTimeoutSeconds = defaults.VacancyTimeoutSeconds;

		SentenceToken token = TokenFor(AreaSentences.ForArea(room, defaults), nameof(AreaSettings.VacancyTimeoutSeconds));

		Assert.AreEqual(TokenOrigin.Own, token.Origin);
	}

	/// <summary>The house's own defaults inherit from nothing, so nothing is marked.</summary>
	[TestMethod]
	public void The_House_Defaults_Are_Marked_Neither_Inherited_Nor_Owned()
	{
		IReadOnlyList<Sentence> sentences = AreaSentences.ForDefaults(Defaults());

		SentenceToken[] tokens = [.. sentences.SelectMany(sentence => sentence.Parts).OfType<SentenceToken>()];

		Assert.IsTrue(tokens.Length > 0);
		Assert.IsTrue(Array.TrueForAll(tokens, token => token.Origin == TokenOrigin.None),
			"a default has no house above it to follow or depart from");
		Assert.AreEqual(0, sentences.Sum(sentence => sentence.OwnValueCount), "and therefore no amber dots");
	}

	/// <summary>The house and a room untouched by anyone say the same thing, which is the point of inheritance.</summary>
	[TestMethod]
	public void The_House_Reads_The_Same_As_A_Room_That_Follows_It()
	{
		AreaSettings defaults = Defaults();

		Assert.AreEqual(
			AreaSentences.ForArea(Room(), defaults).First().PlainText,
			AreaSentences.ForDefaults(defaults).First().PlainText);
	}

	/// <summary>The count behind the amber dots, which is what "3 of 21 are this room's own" is counting.</summary>
	[TestMethod]
	public void Own_Values_Are_Counted_Per_Sentence()
	{
		AreaConfig room = Room();
		room.VacancyTimeoutSeconds = 300;
		room.PreOffSeconds = 60;
		room.OverrideDurationMinutes = 60;

		IReadOnlyList<Sentence> sentences = AreaSentences.ForArea(room, Defaults());

		Assert.AreEqual(2, sentences[0].OwnValueCount, "the timeout and the dim length are in the first sentence");
		Assert.AreEqual(1, sentences[1].OwnValueCount, "the hold is in the second");
	}

	// ===================== the flags sentence =====================

	/// <summary>An ordinary room has nothing to add, and adds nothing.</summary>
	[TestMethod]
	public void A_Room_With_No_Flags_Gets_No_Third_Sentence()
	{
		Assert.AreEqual(2, AreaSentences.ForArea(Room(), Defaults()).Count,
			"a paragraph reporting an absence is the noise this design spends its budget avoiding");
	}

	/// <summary>Flags become one sentence, joined the way the design writes it.</summary>
	[TestMethod]
	public void Flags_Become_One_Sentence()
	{
		AreaConfig room = Room();
		room.RespectSleepMode = true;
		room.WelcomeHome = true;

		IReadOnlyList<Sentence> sentences = AreaSentences.ForArea(room, Defaults());

		Assert.AreEqual(3, sentences.Count);
		Assert.AreEqual(
			"This room is gentle while the house sleeps, and welcomes the first person home.",
			sentences[2].PlainText);
	}

	/// <summary>
	///     Blocking auto-on entirely already implies the gentler rule, so only the stronger clause is written.
	/// </summary>
	/// <remarks>
	///     Both flags on is a real and sensible document — a bedroom that both caps its levels and refuses to
	///     come on by itself. Listing both would read as a contradiction: "never comes on by itself while the
	///     house sleeps, and is gentle while the house sleeps".
	/// </remarks>
	[TestMethod]
	public void The_Stronger_Sleep_Rule_Speaks_For_Both()
	{
		AreaConfig room = Room();
		room.RespectSleepMode = true;
		room.SleepBlocksAutoOn = true;

		Assert.AreEqual(
			"This room never comes on by itself while the house sleeps.",
			AreaSentences.ForArea(room, Defaults())[2].PlainText);
	}

	/// <summary>A blocker entity is a fact about the room worth a clause.</summary>
	[TestMethod]
	public void A_Blocker_Earns_Its_Own_Clause()
	{
		AreaConfig room = Room();
		room.IgnoreWhenOn = ["media_player.projector"];

		Assert.AreEqual(
			"This room is left alone while its blocker is on.",
			AreaSentences.ForArea(room, Defaults())[2].PlainText);
	}

	/// <summary>English, not a comma-joined list: the joiner is where an off-by-one shows as a product bug.</summary>
	[TestMethod]
	public void Clauses_Are_Joined_The_Way_English_Joins_Them()
	{
		Assert.AreEqual("", AreaSentences.JoinClauses([]));
		Assert.AreEqual("one", AreaSentences.JoinClauses(["one"]));
		Assert.AreEqual("one, and two", AreaSentences.JoinClauses(["one", "two"]));
		Assert.AreEqual("one, two, and three", AreaSentences.JoinClauses(["one", "two", "three"]));
	}

	// ===================== the contract the pages code against =====================

	/// <summary>
	///     Every token's key is the settings property it changes.
	/// </summary>
	/// <remarks>
	///     This is the contract two later pages apply edits through. A key that drifts from the schema turns
	///     <c>case nameof(AreaSettings.VacancyTimeoutSeconds)</c> into a branch nothing reaches — an edit that
	///     silently does nothing, which is the worst way for a settings page to fail.
	/// </remarks>
	[TestMethod]
	public void Every_Token_Is_Keyed_By_The_Setting_It_Changes()
	{
		AreaConfig room = Room();
		room.Darkness = DarknessSource.Always;

		string[] names =
		[
			.. typeof(AreaSettings).GetProperties().Select(property => property.Name)
		];

		foreach (SentenceToken token in AreaSentences.ForArea(room, Defaults())
			.SelectMany(sentence => sentence.Parts).OfType<SentenceToken>())
		{
			CollectionAssert.Contains(names, token.Key, $"'{token.Key}' is not a settings property");
			Assert.IsTrue(token.Label.Length > 0, $"{token.Key} needs a name a screen reader can read");
		}
	}

	/// <summary>
	///     Every shortlist offers the value the house actually ships with.
	/// </summary>
	/// <remarks>
	///     A curated list that omits its own default opens a popover with nothing ticked, and the reader cannot
	///     tell whether they are looking at the current value or at five alternatives to it.
	/// </remarks>
	[TestMethod]
	public void Every_Shortlist_Contains_The_Value_It_Is_Offered_For()
	{
		foreach (SentenceToken token in AreaSentences.ForDefaults(Defaults())
			.SelectMany(sentence => sentence.Parts).OfType<SentenceToken>())
		{
			Assert.IsTrue(
				token.Choices.Any(choice => choice.Text == token.Text),
				$"{token.Key} shows '{token.Text}', which its own shortlist does not offer");
		}
	}

	/// <summary>Null arguments are a programming error, not a blank sentence.</summary>
	[TestMethod]
	public void The_Projection_Refuses_Nothing_To_Project()
	{
		Assert.ThrowsException<ArgumentNullException>(() => AreaSentences.ForArea(null!, Defaults()));
		Assert.ThrowsException<ArgumentNullException>(() => AreaSentences.ForArea(Room(), null!));
		Assert.ThrowsException<ArgumentNullException>(() => AreaSentences.ForDefaults(null!));
	}
}
