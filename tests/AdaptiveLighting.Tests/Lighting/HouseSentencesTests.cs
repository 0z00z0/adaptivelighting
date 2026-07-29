using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The house's own behaviour as English: the blend, the away debounce, and one sentence per house mode.
/// </summary>
/// <remarks>
///     <para>
///         These sentences are the only place several house-wide settings are ever read by a person, so a
///         sentence that renders the wrong knob is a setting nobody can find. They are asserted through
///         <see cref="Sentence.PlainText"/> rather than through markup, for the same reason the area sentences
///         are: this repo has no Razor render harness and is not getting one.
///     </para>
///     <para>
///         Two of them carry real risk. The blend token folds a switch and a number into one value, so it must
///         mean "step at the boundary" at zero and never write a zero into the minute count somebody chose. And
///         a mode token's key has to name which of four modes it belongs to, which is an encoding — so it is
///         written and read by one class, and round-tripped here.
///     </para>
/// </remarks>
[TestClass]
public sealed class HouseSentencesTests
{
	private static string Text(Sentence sentence) => sentence.PlainText;

	private static SentenceToken[] Tokens(Sentence sentence) =>
		[.. sentence.Parts.OfType<SentenceToken>()];

	// ===================== blending =====================

	/// <summary>Blending on reads as a span, and the token carries the minutes rather than the words.</summary>
	[TestMethod]
	public void Blending_Reads_As_A_Span()
	{
		GlobalConfig global = new() { SmoothTransitions = true, BlendMinutes = 30 };

		Assert.AreEqual("Lights ease over 30 min when one period hands over to the next.", Text(HouseSentences.Blend(global)));
	}

	/// <summary>Blending off is not "0 min": it is a different instruction, and the sentence says which.</summary>
	[TestMethod]
	public void Blending_Off_Reads_As_A_Step()
	{
		GlobalConfig global = new() { SmoothTransitions = false, BlendMinutes = 30 };

		Assert.AreEqual("Lights step at the boundary when one period hands over to the next.", Text(HouseSentences.Blend(global)));
	}

	/// <summary>
	///     The value the document holds is always offered, so the popover never opens with nothing ticked — the
	///     rendering of a control that has lost its own state.
	/// </summary>
	[TestMethod]
	public void The_Blend_Shortlist_Always_Offers_The_Current_Value()
	{
		IReadOnlyList<TokenChoice> curated = HouseSentences.BlendChoices(22);

		CollectionAssert.Contains(curated.Select(choice => choice.Value).ToArray(), "22");
		CollectionAssert.AreEqual(
			new[] { "0", "15", "22", "30", "60" },
			curated.Select(choice => choice.Value).ToArray(),
			"the offered spans stay in order however the current value lands among them");

		Assert.AreEqual("step at the boundary", curated[0].Text);
	}

	/// <summary>A house already on a curated value is not offered it twice.</summary>
	[TestMethod]
	public void The_Blend_Shortlist_Does_Not_Repeat_A_Curated_Value()
	{
		Assert.AreEqual(4, HouseSentences.BlendChoices(30).Count);
		Assert.AreEqual(4, HouseSentences.BlendChoices(0).Count);
	}

	// ===================== the away debounce =====================

	/// <summary>Stored in minutes, carried in seconds — the one conversion this sentence has to get right.</summary>
	[TestMethod]
	public void The_Away_Debounce_Reads_And_Carries_The_Same_Span()
	{
		Sentence sentence = HouseSentences.AwayDebounce(new GlobalConfig { AwayDebounceMinutes = 5 });

		StringAssert.StartsWith(Text(sentence), "Count the house as empty 5 min after the last person leaves");

		SentenceToken token = Tokens(sentence).Single();

		Assert.AreEqual(nameof(GlobalConfig.AwayDebounceMinutes), token.Key);
		Assert.AreEqual(TokenKind.Duration, token.Kind);
		CollectionAssert.Contains(token.Choices.Select(choice => choice.Value).ToArray(), "300");
	}

	// ===================== the modes =====================

	/// <summary>No select, no sentences — the card is not rendered rather than rendered empty.</summary>
	[TestMethod]
	public void A_House_With_No_Mode_Select_Has_No_Mode_Lines()
	{
		Assert.AreEqual(0, HouseSentences.Modes(null, []).Count);
		Assert.AreEqual(0, HouseSentences.Modes(new HouseModeConfig(), []).Count);
	}

	/// <summary>The name is carried beside the sentence so the card can set it in bold and the sentence stays a sentence.</summary>
	[TestMethod]
	public void A_Mode_Line_Carries_Its_Name_Beside_Its_Sentence()
	{
		HouseModeConfig modes = new()
		{
			Entity = "input_select.husmodus",
			Options = [new HouseModeOptionConfig { Value = "Hjemme", Kind = ModeKind.Normal }]
		};

		ModeLine line = HouseSentences.Modes(modes, []).Single();

		Assert.AreEqual("Hjemme", line.Name);
		Assert.AreEqual(ModeKind.Normal, line.Kind);
		Assert.AreEqual(
			"is everyday automatic lighting. The house returns here when another mode ends.",
			Text(line.Sentences.Single()));
	}

	/// <summary>
	///     <b>Only one Normal option is told it is the one the house comes back to.</b> A list can hold several:
	///     <see cref="HouseModeOptionConfig.Kind"/> defaults to <see cref="ModeKind.Normal"/>, so every option taken
	///     from the dropdown helper that the document had not seen before arrives Normal. The engine returns to the
	///     first; printing "the house returns here" under each of them states, of every one but that first, something
	///     that will not happen.
	/// </summary>
	[TestMethod]
	public void Only_The_First_Normal_Mode_Is_Called_The_One_The_House_Returns_To()
	{
		HouseModeConfig modes = new()
		{
			Entity = "input_select.husmodus",
			Options =
			[
				new HouseModeOptionConfig { Value = "Hjemme", Kind = ModeKind.Normal },
				new HouseModeOptionConfig { Value = "Ferie", Kind = ModeKind.Normal }
			]
		};

		IReadOnlyList<ModeLine> lines = HouseSentences.Modes(modes, []);

		StringAssert.Contains(Text(lines[0].Sentences.Single()), "The house returns here");
		StringAssert.Contains(Text(lines[1].Sentences.Single()), "not to this one");
	}

	/// <summary>
	///     The away mode is the one with everything in it: what arms it, what it does, and what ends it —
	///     each clause built only when the document actually says so.
	/// </summary>
	[TestMethod]
	public void An_Away_Mode_Says_What_Arms_It_And_What_Ends_It()
	{
		HouseModeConfig modes = new()
		{
			Entity = "input_select.husmodus",
			Options =
			[
				new HouseModeOptionConfig
				{
					Value = "Borte",
					Kind = ModeKind.Away,
					ActivateAfterNoMotionMinutes = 360,
					ResetOnPresence = true,
					ResetPresenceGraceMinutes = 15
				}
			]
		};

		string text = Text(HouseSentences.Modes(modes, []).Single().Sentences.Single());

		StringAssert.StartsWith(text, "switches on by itself after the house has been still 6 h, then sweeps the lights off");
		StringAssert.Contains(text, "It ends when someone comes home — ignoring the first 15 min");
	}

	/// <summary>A mode nothing arms simply does not carry the clause — the design's When rule, not a greyed row.</summary>
	[TestMethod]
	public void A_Mode_Nothing_Arms_Says_Nothing_About_Arming()
	{
		HouseModeConfig modes = new()
		{
			Entity = "input_select.husmodus",
			Options = [new HouseModeOptionConfig { Value = "Borte", Kind = ModeKind.Away, Scene = "scene.natt" }]
		};

		Sentence sentence = HouseSentences.Modes(modes, []).Single().Sentences.Single();

		Assert.AreEqual(
			"runs the scene.natt scene and pauses automatic lighting until you switch the house back yourself.",
			Text(sentence));
		Assert.AreEqual(0, Tokens(sentence).Length, "with nothing to tune there is nothing to offer a popover for");
	}

	/// <summary>A sleep mode names the period it clamps to, and says so plainly when nothing resolves one.</summary>
	[TestMethod]
	public void A_Sleep_Mode_Names_Its_Clamp_Period()
	{
		HouseModeConfig modes = new()
		{
			Entity = "input_select.husmodus",
			Options = [new HouseModeOptionConfig { Value = "Natt", Kind = ModeKind.Sleep }]
		};

		List<TimePeriodConfig> periods = [new TimePeriodConfig { Name = "Night", Start = "23:00" }];

		StringAssert.Contains(Text(HouseSentences.Modes(modes, periods).Single().Sentences.Single()), "the Night period's limits");
		StringAssert.Contains(Text(HouseSentences.Modes(modes, []).Single().Sentences.Single()), "no period is named for it yet");
	}

	/// <summary>Several endings read as English rather than as a list with a stray comma.</summary>
	[TestMethod]
	public void Several_Endings_Are_Joined_As_English()
	{
		HouseModeConfig modes = new()
		{
			Entity = "input_select.husmodus",
			Options =
			[
				new HouseModeOptionConfig
				{
					Value = "Natt",
					Kind = ModeKind.Sleep,
					ClampPeriod = "Night",
					ResetOnPeriodStart = "Morning",
					ResetAtTime = "input_datetime.morgen"
				}
			]
		};

		StringAssert.Contains(
			Text(HouseSentences.Modes(modes, []).Single().Sentences.Single()),
			"It ends when the Morning period starts, and when input_datetime.morgen comes round.");
	}

	/// <summary>An option the document left blank still gets a line, with a stand-in rather than an empty bold.</summary>
	[TestMethod]
	public void A_Nameless_Option_Still_Gets_A_Line()
	{
		HouseModeConfig modes = new()
		{
			Entity = "input_select.husmodus",
			Options = [new HouseModeOptionConfig { Kind = ModeKind.Guest }]
		};

		Assert.AreEqual("This option", HouseSentences.Modes(modes, []).Single().Name);
	}

	// ===================== the mode token keys =====================

	/// <summary>
	///     The key names which option it belongs to, and is read back by the same class that wrote it — the page
	///     must never parse this string itself, or the two halves of the encoding live in two files.
	/// </summary>
	[TestMethod]
	public void A_Mode_Token_Key_Round_Trips()
	{
		string key = HouseSentences.ModeKey(2, nameof(HouseModeOptionConfig.ResetPresenceGraceMinutes));

		Assert.IsTrue(HouseSentences.TryReadModeKey(key, out int index, out string property));
		Assert.AreEqual(2, index);
		Assert.AreEqual(nameof(HouseModeOptionConfig.ResetPresenceGraceMinutes), property);
	}

	/// <summary>An area setting's key is not a mode's, so the House tab can tell one sentence's edit from another's.</summary>
	[TestMethod]
	public void An_Area_Setting_Key_Is_Not_A_Mode_Key()
	{
		Assert.IsFalse(HouseSentences.TryReadModeKey(nameof(AreaSettings.VacancyTimeoutSeconds), out _, out _));
		Assert.IsFalse(HouseSentences.TryReadModeKey(null, out _, out _));
		Assert.IsFalse(HouseSentences.TryReadModeKey("mode:notanumber:Thing", out _, out _));
		Assert.IsFalse(HouseSentences.TryReadModeKey("mode:1:", out _, out _));
	}

	/// <summary>The tokens a mode sentence renders are keyed to their own option, not to the first one.</summary>
	[TestMethod]
	public void Each_Mode_Keys_Its_Tokens_To_Itself()
	{
		HouseModeConfig modes = new()
		{
			Entity = "input_select.husmodus",
			Options =
			[
				new HouseModeOptionConfig { Value = "Hjemme", Kind = ModeKind.Normal },
				new HouseModeOptionConfig { Value = "Borte", Kind = ModeKind.Away, ResetOnPresence = true },
				new HouseModeOptionConfig { Value = "Gjester", Kind = ModeKind.Guest, ResetOnPresence = true }
			]
		};

		IReadOnlyList<ModeLine> lines = HouseSentences.Modes(modes, []);

		Assert.IsTrue(HouseSentences.TryReadModeKey(Tokens(lines[1].Sentences.Single()).Single().Key, out int away, out _));
		Assert.IsTrue(HouseSentences.TryReadModeKey(Tokens(lines[2].Sentences.Single()).Single().Key, out int guest, out _));

		Assert.AreEqual(1, away);
		Assert.AreEqual(2, guest);
	}
}
