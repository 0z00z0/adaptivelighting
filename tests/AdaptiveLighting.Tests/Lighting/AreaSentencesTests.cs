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
	///     <para>
	///         Asserted on the whole sentence rather than on the tokens inside it, because the prose between the
	///         values is the part that makes them mean something: "dim to 50 % for 30 s" and "dim to 30 s for 50 %"
	///         contain the same tokens.
	///     </para>
	///     <para>
	///         The sun clause is gone because the default darkness source moved from Either to Lux: a room that
	///         configures nothing is now decided by its light sensor alone, and by nothing at all when it has none.
	///     </para>
	/// </remarks>
	[TestMethod]
	public void An_Untouched_Room_Reads_As_The_Design_Writes_It()
	{
		IReadOnlyList<Sentence> sentences = AreaSentences.ForArea(Room(), Defaults());

		Assert.AreEqual(2, sentences.Count, "movement and hands; no flags are on");

		Assert.AreEqual(
			"Lights when someone moves and it's darker than 1000 lx. " +
			"After 10 min without movement, dim to 50 % for 30 s, then off.",
			sentences[0].PlainText);

		Assert.AreEqual(
			"Hand changes hold for 2 h; after somebody switches them off by hand, movement is ignored until the "
			+ "room has been empty 10 min.",
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

	// ===================== the light-level shortlist =====================

	/// <summary>
	///     The rungs climb by a factor of about three, not by a fixed amount.
	/// </summary>
	/// <remarks>
	///     Illuminance spans four orders of magnitude, so a ladder with a constant <i>difference</i> between rungs
	///     is either unusably fine at the bottom or useless at the top. A constant <i>ratio</i> is the only shape
	///     that covers 3 lx and 10 000 lx in the same handful of taps, and this asserts the shape rather than the
	///     particular numbers, so a later re-tuning stays honest.
	/// </remarks>
	[TestMethod]
	public void The_Light_Level_Rungs_Climb_By_Ratio_Not_By_Difference()
	{
		IReadOnlyList<double> ladder = AreaSentences.LuxLadder;

		for (int index = 1; index < ladder.Count; index++)
		{
			double ratio = ladder[index] / ladder[index - 1];

			Assert.IsTrue(ratio is >= 2.5 and <= 4,
				$"{ladder[index - 1]} lx to {ladder[index]} lx is a factor of {ratio}, which is not a half-decade step");
		}
	}

	/// <summary>
	///     Every light level a real house meets is on a rung or beside one.
	/// </summary>
	/// <remarks>
	///     The measurements are from the house this was written for: a shaded outdoor sensor reading 1–3 lx at
	///     night and up to 3 706 by day, an unshaded one reaching 10 000–50 000, and an interior room that is
	///     genuinely dark while its sensor says 170. A shortlist topping out at 60 lx could express none of them.
	/// </remarks>
	[TestMethod]
	public void The_Light_Level_Rungs_Reach_Every_Reading_A_House_Meets()
	{
		IReadOnlyList<double> ladder = AreaSentences.LuxLadder;

		Assert.IsTrue(ladder.Any(rung => rung <= 3),
			"deep night: a shaded sensor bottoms out at 1–3 lx, and 'only when it is truly night' has to be sayable");

		Assert.IsTrue(ladder.Any(rung => rung is >= 170 and <= 400),
			"indoor dark: a room reading 170 lx is still dark, so a rung has to sit above that reading");

		Assert.IsTrue(ladder.Any(rung => rung is >= 1000 and <= 4000),
			"overcast day: the shaded sensor tops out at 3 706 lx");

		Assert.IsTrue(ladder.Any(rung => rung >= 10000),
			"full daylight: an unshaded sensor works in tens of thousands");
	}

	/// <summary>
	///     The value the room is on is always one of the offered ones, even when nothing on the ladder matches it.
	/// </summary>
	/// <remarks>
	///     The detail view now takes any reading up to 65 535, so the ladder cannot hold every value a room can be
	///     on. A popover that opened on eight alternatives with nothing ticked would have the same fault as one
	///     missing its own default: the reader cannot tell where they are from where they could go.
	/// </remarks>
	[TestMethod]
	public void A_Typed_Light_Level_Is_Slotted_Into_The_Shortlist_In_Its_Place()
	{
		string[] offered = [.. AreaSentences.LuxChoicesFor(620).Select(choice => choice.Text)];

		CollectionAssert.Contains(offered, "620 lx", "a room sitting on a typed value must see it ticked");

		Assert.AreEqual(
			Array.IndexOf(offered, "300 lx") + 1,
			Array.IndexOf(offered, "620 lx"),
			"and it belongs between the rungs it falls between, not appended at the end");

		Assert.AreEqual(
			AreaSentences.LuxLadder.Count + 1,
			AreaSentences.LuxChoicesFor(1000).Count,
			"a value already on the ladder is not offered twice — the ladder plus the off option is the whole list");
	}

	/// <summary>
	///     A room on a hand-typed threshold opens its popover with that threshold showing.
	/// </summary>
	[TestMethod]
	public void A_Room_On_A_Typed_Threshold_Sees_It_In_Its_Own_Popover()
	{
		AreaConfig room = Room();
		room.LuxThreshold = 620;

		SentenceToken token = TokenFor(AreaSentences.ForArea(room, Defaults()), nameof(AreaSettings.LuxThreshold));

		Assert.AreEqual("620 lx", token.Text);
		Assert.IsTrue(token.Choices.Any(choice => choice.Text == token.Text),
			"the token shows a value its own shortlist does not offer");
	}

	/// <summary>
	///     Off is a change of darkness source, not a lux value that secretly means disabled.
	/// </summary>
	/// <remarks>
	///     A sentinel — <c>-1</c>, or <c>0</c> read as "never dark" — would have to be understood by the engine,
	///     the validator, every format string and anybody reading the YAML, and would render as a real reading
	///     wherever one of them forgot. The schema already has the word for "stop consulting the sensor", and it
	///     is <see cref="DarknessSource.Sun"/>.
	/// </remarks>
	[TestMethod]
	public void Turning_The_Light_Sensor_Off_Changes_The_Source_Rather_Than_The_Number()
	{
		TokenChoice off = AreaSentences.LuxChoicesFor(1000)[^1];

		Assert.AreEqual(AreaSentences.LuxOff, off, "off is the last thing offered, after every value");
		Assert.AreEqual(nameof(AreaSettings.Darkness), off.Key, "it edits the darkness rule, not the threshold");
		Assert.AreEqual(TokenKind.Choice, off.Kind);
		Assert.AreEqual(nameof(DarknessSource.Sun), off.Value);

		Assert.IsFalse(
			AreaSentences.LuxChoicesFor(1000).SkipLast(1).Any(choice => choice.Key is not null),
			"every other option is an ordinary threshold, keyed by the token it sits in");

		AreaConfig room = Room();
		room.Darkness = DarknessSource.Either;

		Assert.IsTrue(RoomSettings.Apply(room, new SentenceEdit(off.Key!, off.Kind!.Value, off.Value)));
		Assert.AreEqual(DarknessSource.Sun, room.Darkness, "the room now decides by the sun alone");
		Assert.IsNull(room.LuxThreshold, "and the threshold it had is left exactly as it was");
	}

	/// <summary>
	///     With the sensor out of the rule, the room stops showing a threshold at all.
	/// </summary>
	/// <remarks>
	///     The proof that off means something: the clause carrying the lux value is gone from the sentence, rather
	///     than still there showing a number that no longer decides anything.
	/// </remarks>
	[TestMethod]
	public void A_Room_With_The_Sensor_Off_Shows_No_Threshold()
	{
		AreaConfig room = Room();
		room.Darkness = DarknessSource.Sun;

		Assert.IsFalse(
			AreaSentences.ForArea(room, Defaults())
				.SelectMany(sentence => sentence.Parts)
				.OfType<SentenceToken>()
				.Any(token => token.Key == nameof(AreaSettings.LuxThreshold)));
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
