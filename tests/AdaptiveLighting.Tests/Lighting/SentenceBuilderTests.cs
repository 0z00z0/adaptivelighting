using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The sentence primitive, in the shapes no area sentence uses yet: a yes/no in prose, a gated clause, and a bare figure.</summary>
[TestClass]
public sealed class SentenceBuilderTests
{
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

	// A page applies the carried value; it never parses the English.
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

	// An off gate builds no token at all, so nothing downstream can render one by accident.
	[TestMethod]
	public void A_Gate_That_Is_Off_Leaves_No_Token_Behind()
	{
		Sentence sentence = SentenceBuilder.Start("x")
			.When(false, clause => clause.Number("Never", "Never", 1))
			.Build();

		Assert.AreEqual(0, sentence.Parts.OfType<SentenceToken>().Count());
	}

	[TestMethod]
	public void A_Figure_Reads_Aloud_As_Its_Alt_Text()
	{
		Sentence sentence = SentenceBuilder.Start("spread ")
			.Figure("a straight, even rise", builder => { })
			.Text(" between them.")
			.Build();

		Assert.AreEqual("spread a straight, even rise between them.", sentence.PlainText);
	}

	[TestMethod]
	public void A_Choice_Is_Written_In_Its_Shortlists_Own_Words()
	{
		SentenceToken token = (SentenceToken)SentenceBuilder.Start()
			.Choice("k", "Label", "sun", TokenChoices.Of(("the sun", "sun"), ("the sensor", "lux")))
			.Build().Parts[0];

		Assert.AreEqual("the sun", token.Text);
	}

	[TestMethod]
	public void A_Value_Outside_The_Shortlist_Is_Written_As_Itself()
	{
		SentenceToken token = (SentenceToken)SentenceBuilder.Start()
			.Choice("k", "Label", "moon", TokenChoices.Of(("the sun", "sun")))
			.Build().Parts[0];

		Assert.AreEqual("moon", token.Text, "a document holding something unexpected should say so");
	}

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

	[TestMethod]
	public void The_Builder_Refuses_Nothing_To_Build_With()
	{
		Assert.ThrowsException<ArgumentNullException>(() => SentenceBuilder.Start().Token(null!));
		Assert.ThrowsException<ArgumentNullException>(() => SentenceBuilder.Start().When(true, null!));
		Assert.ThrowsException<ArgumentNullException>(() => SentenceBuilder.Start().Figure("alt", null!));
		Assert.ThrowsException<ArgumentNullException>(() => SentenceBuilder.Start().Choice("k", "l", "v", null!));
	}
}
