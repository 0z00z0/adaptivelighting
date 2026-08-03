using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The sentences the room page renders, and where each token's value came from.
/// </summary>
/// <remarks>
///     There is no Razor render harness in this repo, so the projection is a pure function and asserted here.
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
	///     Asserted on the sentence, not on its tokens: "dim to 50 % for 30 s" and "dim to 30 s for 50 %" hold
	///     the same tokens.
	/// </summary>
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
			"Manual changes hold for 2 h; after somebody switches them off manually, movement is ignored until the "
			+ "room has been empty 10 min.",
			sentences[1].PlainText);
	}

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

	/// <summary>A windowless room shows no threshold, so the rule itself has to be the token.</summary>
	[TestMethod]
	public void The_Windowless_Room_Can_Change_Its_Rule_From_The_Sentence()
	{
		AreaConfig room = Room();
		room.Darkness = DarknessSource.Always;

		SentenceToken token = TokenFor(AreaSentences.ForArea(room, Defaults()), nameof(AreaSettings.Darkness));

		Assert.AreEqual(TokenKind.Choice, token.Kind);
		Assert.AreEqual("whatever the daylight", token.Text);
		Assert.AreEqual(3, token.Choices.Count, "all three ways of deciding stay reachable — Either was retired");
	}

	// ===================== the light-level shortlist =====================

	/// <summary>The shape is asserted, never the particular numbers, so a re-tuning stays honest.</summary>
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

	/// <summary>The bounds below are measured from one live house's own sensors.</summary>
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

	/// <summary>The detail view takes any reading up to 65 535, so the ladder cannot hold every value a room is on.</summary>
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
	///     There is no disabled-sentinel threshold. Off is <see cref="DarknessSource.Sun"/>, so the off choice is
	///     keyed on Darkness and leaves LuxThreshold alone.
	/// </summary>
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
		room.Darkness = DarknessSource.Lux;

		Assert.IsTrue(RoomSettings.Apply(room, new SentenceEdit(off.Key!, off.Kind!.Value, off.Value)));
		Assert.AreEqual(DarknessSource.Sun, room.Darkness, "the room now decides by the sun alone");
		Assert.IsNull(room.LuxThreshold, "and the threshold it had is left exactly as it was");
	}

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
	///     Provenance comes from the schema's null. Comparing values would silently erase a room that pins
	///     10 min while the house also says 10 min.
	/// </summary>
	[TestMethod]
	public void Pinning_The_House_Value_Is_Still_The_Rooms_Own_Decision()
	{
		AreaSettings defaults = Defaults();

		AreaConfig room = Room();
		room.VacancyTimeoutSeconds = defaults.VacancyTimeoutSeconds;

		SentenceToken token = TokenFor(AreaSentences.ForArea(room, defaults), nameof(AreaSettings.VacancyTimeoutSeconds));

		Assert.AreEqual(TokenOrigin.Own, token.Origin);
	}

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

	[TestMethod]
	public void The_House_Reads_The_Same_As_A_Room_That_Follows_It()
	{
		AreaSettings defaults = Defaults();

		Assert.AreEqual(
			AreaSentences.ForArea(Room(), defaults).First().PlainText,
			AreaSentences.ForDefaults(defaults).First().PlainText);
	}

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

	[TestMethod]
	public void A_Room_With_No_Flags_Gets_No_Third_Sentence()
	{
		Assert.AreEqual(2, AreaSentences.ForArea(Room(), Defaults()).Count,
			"a paragraph reporting an absence is the noise this design spends its budget avoiding");
	}

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

	/// <summary>Both flags on is an ordinary document; SleepBlocksAutoOn already implies RespectSleepMode.</summary>
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

	[TestMethod]
	public void A_Blocker_Earns_Its_Own_Clause()
	{
		AreaConfig room = Room();
		room.IgnoreWhenOn = ["media_player.projector"];

		Assert.AreEqual(
			"This room is left alone while its blocker is on.",
			AreaSentences.ForArea(room, Defaults())[2].PlainText);
	}

	[TestMethod]
	public void Clauses_Are_Joined_The_Way_English_Joins_Them()
	{
		Assert.AreEqual("", AreaSentences.JoinClauses([]));
		Assert.AreEqual("one", AreaSentences.JoinClauses(["one"]));
		Assert.AreEqual("one, and two", AreaSentences.JoinClauses(["one", "two"]));
		Assert.AreEqual("one, two, and three", AreaSentences.JoinClauses(["one", "two", "three"]));
	}

	// ===================== a room whose lights have no colour temperature =====================

	[TestMethod]
	public void A_Room_Whose_Lights_Take_A_Warmth_Says_Nothing_About_It()
	{
		Assert.AreEqual(2, AreaSentences.ForArea(Room(), Defaults()).Count,
			"detect is the default and the answer is yes, so there is nothing for a person to be told");

		AreaConfig pinned = Room();
		pinned.ColorControl = ColorControl.Kelvin;

		Assert.AreEqual(2, AreaSentences.ForArea(pinned, Defaults()).Count,
			"and saying it out loud changes nothing the schedule does");
	}

	[TestMethod]
	public void A_Room_With_No_Colour_Temperature_Says_So_In_One_Sentence()
	{
		AreaConfig room = Room();
		room.ColorControl = ColorControl.EqualChannels;

		IReadOnlyList<Sentence> sentences = AreaSentences.ForArea(room, Defaults());

		Assert.AreEqual(3, sentences.Count);
		Assert.AreEqual(
			"Warmth: No colour temperature. The schedule's kelvin figure does nothing for these lights, so they "
			+ "run at neutral white.",
			sentences[^1].PlainText);
	}

	/// <summary>The way back out of it, and the way somebody arrives at it, are the same list.</summary>
	[TestMethod]
	public void No_Colour_Temperature_Is_The_First_Thing_The_Warmth_List_Offers()
	{
		AreaConfig room = Room();
		room.ColorControl = ColorControl.EqualChannels;

		SentenceToken token = TokenFor(AreaSentences.ForArea(room, Defaults()), nameof(AreaSettings.ColorControl));

		Assert.AreEqual(TokenKind.Choice, token.Kind);
		Assert.AreEqual(TokenOrigin.Own, token.Origin);
		Assert.AreEqual("No colour temperature", token.Text);
		Assert.AreEqual("Detect it from the lights", token.HouseText, "the house is still reading the fixtures");

		CollectionAssert.AreEqual(
			new[] { "No colour temperature", "Colour temperature in kelvin", "Detect it from the lights" },
			token.Choices.Select(choice => choice.Text).ToArray());
	}

	[TestMethod]
	public void Every_Warmth_Answer_Can_Be_Applied()
	{
		foreach (TokenChoice choice in AreaSentences.WarmthChoices)
		{
			AreaConfig room = Room();

			Assert.IsTrue(
				RoomSettings.Apply(room, new SentenceEdit(nameof(AreaSettings.ColorControl), TokenKind.Choice, choice.Value)),
				$"the warmth list offers {choice.Value}, so the page must know how to apply it");

			Assert.AreEqual(Enum.Parse<ColorControl>(choice.Value), room.ColorControl);
		}
	}

	/// <summary>The levels table reads this to decide whether a kelvin control is worth drawing at all.</summary>
	[TestMethod]
	public void Warmth_Follows_The_House_Until_The_Room_Answers_For_Itself()
	{
		AreaSettings defaults = Defaults();
		defaults.ColorControl = ColorControl.EqualChannels;

		Assert.IsTrue(AreaSentences.WithoutColourTemperature(Room(), defaults));
		Assert.AreEqual(ColorControl.EqualChannels, AreaSentences.WarmthOf(Room(), defaults));

		AreaConfig room = Room();
		room.ColorControl = ColorControl.Kelvin;

		Assert.IsFalse(AreaSentences.WithoutColourTemperature(room, defaults),
			"a room that says its lights take kelvin overrules the house");

		Assert.IsFalse(AreaSentences.WithoutColourTemperature(null, Defaults()),
			"and the house's own page, which has no room, follows its own answer");

		SentenceToken inherited = TokenFor(AreaSentences.ForArea(Room(), defaults), nameof(AreaSettings.ColorControl));

		Assert.AreEqual(TokenOrigin.Inherited, inherited.Origin);
		Assert.IsNull(inherited.HouseText, "already on the house's answer, so there is no road back to offer");
	}

	// ===================== the contract the pages code against =====================

	/// <summary>
	///     Two pages apply edits through this contract. A key that drifts from the schema turns
	///     <c>case nameof(AreaSettings.VacancyTimeoutSeconds)</c> into a branch nothing reaches.
	/// </summary>
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
