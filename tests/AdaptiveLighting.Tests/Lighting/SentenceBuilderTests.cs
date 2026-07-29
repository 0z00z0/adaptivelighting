using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The primitive two later pages build their own sentences with.
/// </summary>
/// <remarks>
///     Area sentences have their own tests; these cover the shapes a page will reach for that no area sentence
///     uses yet — a yes/no in prose, a clause that only exists while its gate is on, and a figure standing in for
///     a value no wording explains. Each is asserted here so a page agent can trust the behaviour rather than
///     discover it.
/// </remarks>
[TestClass]
public sealed class SentenceBuilderTests
{
	/// <summary>Prose and values come out in the order they were written.</summary>
	[TestMethod]
	public void A_Sentence_Reads_In_The_Order_It_Was_Built()
	{
		Sentence sentence = SentenceBuilder.Start("Count the house as empty ")
			.Duration("AwayDebounceMinutes", "Away debounce", 300, TokenChoices.DurationsInMinutes(1, 5, 10))
			.Text(" after the last person leaves.")
			.Build();

		Assert.AreEqual("Count the house as empty 5 min after the last person leaves.", sentence.PlainText);
		Assert.AreEqual(3, sentence.Parts.Count);
	}

	/// <summary>
	///     A yes/no is written as what it means here, so the sentence reads in both states.
	/// </summary>
	[TestMethod]
	public void A_Toggle_Is_Written_As_Its_Meaning_Not_As_On_Or_Off()
	{
		Sentence on = Brightening(true);
		Sentence off = Brightening(false);

		Assert.AreEqual("This room brightens with daylight.", on.PlainText);
		Assert.AreEqual("This room follows its schedule.", off.PlainText);

		static Sentence Brightening(bool value) =>
			SentenceBuilder.Start("This room ")
				.Toggle("LuxBrightening", "Brighten with daylight", value, "brightens with daylight", "follows its schedule")
				.Text(".")
				.Build();
	}

	/// <summary>A toggle carries a value a page can apply without parsing English.</summary>
	[TestMethod]
	public void A_Toggle_Offers_Both_States_As_Values()
	{
		SentenceToken token = (SentenceToken)SentenceBuilder.Start()
			.Toggle("k", "Label", true, "on words", "off words")
			.Build().Parts[0];

		Assert.AreEqual(TokenKind.Toggle, token.Kind);
		Assert.AreEqual(2, token.Choices.Count);
		Assert.IsTrue(token.Choices.Any(choice => choice.Value == "true" && choice.Text == "on words"));
		Assert.IsTrue(token.Choices.Any(choice => choice.Value == "false" && choice.Text == "off words"));
	}

	/// <summary>
	///     A setting that cannot take effect is not written at all.
	/// </summary>
	/// <remarks>
	///     How this model says "that only matters while this is on". Greying the clause out would still spend the
	///     reader's attention on it, still invite the tap, and still have to explain itself; the sentence simply
	///     gets shorter, and grows back on the tap that turns the gate on.
	/// </remarks>
	[TestMethod]
	public void A_Gated_Clause_Exists_Only_While_Its_Gate_Is_On()
	{
		Assert.AreEqual(
			"This room brightens with daylight, up to 100 % on the brightest days.",
			Daylight(true).PlainText);

		Assert.AreEqual("This room follows its schedule.", Daylight(false).PlainText);

		static Sentence Daylight(bool gate) =>
			SentenceBuilder.Start("This room ")
				.Toggle("LuxBrightening", "Brighten with daylight", gate, "brightens with daylight", "follows its schedule")
				.When(gate, clause => clause
					.Text(", up to ")
					.Percent("LuxBrightnessCeiling", "Brightness on the brightest days", 100, TokenChoices.Percentages(100)))
				.When(gate, clause => clause.Text(" on the brightest days"))
				.Text(".")
				.Build();
	}

	/// <summary>A gated clause is not merely hidden — it is never built, so nothing can render it by accident.</summary>
	[TestMethod]
	public void A_Gate_That_Is_Off_Leaves_No_Token_Behind()
	{
		Sentence sentence = SentenceBuilder.Start("x")
			.When(false, clause => clause.Number("Never", "Never", 1))
			.Build();

		Assert.AreEqual(0, sentence.Parts.OfType<SentenceToken>().Count());
	}

	/// <summary>A figure stands in the prose and reads aloud as words.</summary>
	[TestMethod]
	public void A_Figure_Reads_Aloud_As_Its_Alt_Text()
	{
		Sentence sentence = SentenceBuilder.Start("spread ")
			.Figure("a straight, even rise", builder => { })
			.Text(" between them.")
			.Build();

		Assert.AreEqual("spread a straight, even rise between them.", sentence.PlainText);
	}

	/// <summary>A choice is written in the words its own shortlist uses, so the popover ticks what the sentence says.</summary>
	[TestMethod]
	public void A_Choice_Is_Written_In_Its_Shortlists_Own_Words()
	{
		SentenceToken token = (SentenceToken)SentenceBuilder.Start()
			.Choice("k", "Label", "sun", TokenChoices.Of(("the sun", "sun"), ("the sensor", "lux")))
			.Build().Parts[0];

		Assert.AreEqual("the sun", token.Text);
	}

	/// <summary>
	///     A value the shortlist does not offer is written as itself rather than silently shown as another.
	/// </summary>
	[TestMethod]
	public void A_Value_Outside_The_Shortlist_Is_Written_As_Itself()
	{
		SentenceToken token = (SentenceToken)SentenceBuilder.Start()
			.Choice("k", "Label", "moon", TokenChoices.Of(("the sun", "sun")))
			.Build().Parts[0];

		Assert.AreEqual("moon", token.Text, "a document holding something unexpected should say so");
	}

	/// <summary>The road back is kept only where there is somewhere to go back to.</summary>
	[TestMethod]
	public void Only_An_Owned_Value_Carries_The_Road_Back()
	{
		Assert.AreEqual("10 min", House(TokenOrigin.Own));
		Assert.IsNull(House(TokenOrigin.Inherited));
		Assert.IsNull(House(TokenOrigin.None));

		static string? House(TokenOrigin origin) =>
			((SentenceToken)SentenceBuilder.Start()
				.Duration("k", "Label", 300, origin: origin, houseSeconds: 600)
				.Build().Parts[0]).HouseText;
	}

	/// <summary>Nulls where a builder needs something are a programming error, not a silently empty sentence.</summary>
	[TestMethod]
	public void The_Builder_Refuses_Nothing_To_Build_With()
	{
		Assert.ThrowsException<ArgumentNullException>(() => SentenceBuilder.Start().Token(null!));
		Assert.ThrowsException<ArgumentNullException>(() => SentenceBuilder.Start().When(true, null!));
		Assert.ThrowsException<ArgumentNullException>(() => SentenceBuilder.Start().Figure("alt", null!));
		Assert.ThrowsException<ArgumentNullException>(() => SentenceBuilder.Start().Choice("k", "l", "v", null!));
	}
}
