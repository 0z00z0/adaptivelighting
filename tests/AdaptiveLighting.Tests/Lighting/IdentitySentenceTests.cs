using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

[TestClass]
public sealed class IdentitySentenceTests
{
	// ===================== the name =====================

	[TestMethod]
	public void Unnamed_House_Shows_The_Shipped_Default()
	{
		Assert.AreEqual(IdentitySentence.DefaultHouseName, IdentitySentence.Display(null));
		Assert.AreEqual(IdentitySentence.DefaultHouseName, IdentitySentence.Display("   "));
	}

	[TestMethod]
	public void Named_House_Shows_Its_Name()
	{
		Assert.AreEqual("B1", IdentitySentence.Display("  B1 "));
	}

	// The placeholder and an unset name read alike to every later reader, so only null is true.
	[TestMethod]
	public void Clearing_The_Name_Stages_Nothing_Rather_Than_The_Placeholder()
	{
		Assert.IsNull(IdentitySentence.Normalize(""));
		Assert.IsNull(IdentitySentence.Normalize("  "));
		Assert.AreEqual("Hytta", IdentitySentence.Normalize(" Hytta "));
	}

	// ===================== the delay token =====================

	// One shortlist behind both the sheet and the House tab.
	[TestMethod]
	public void Delay_Is_A_Duration_Token_On_The_House_Shortlist()
	{
		SentenceToken token = IdentitySentence.Delay(5);

		Assert.AreEqual(TokenKind.Duration, token.Kind);
		Assert.AreEqual("5 min", token.Text);
		Assert.AreEqual(TokenOrigin.None, token.Origin);
		CollectionAssert.AreEqual(
			(System.Collections.ICollection)HouseSentences.AwayDebounceChoices,
			(System.Collections.ICollection)token.Choices);
	}

	[TestMethod]
	public void Delay_Refuses_To_Show_A_Negative_Span()
	{
		Assert.AreEqual("0 s", IdentitySentence.Delay(-5).Text);
	}

	// ===================== the sentence =====================

	[TestMethod]
	public void Reads_As_One_Sentence()
	{
		Assert.AreEqual(
			"This house is called B1. Espen, Nora and Bilen decide Home and Away; the house counts as empty 5 min after the last person leaves.",
			IdentitySentence.PlainText("B1", ["Espen", "Nora", "Bilen"], 5));
	}

	[TestMethod]
	public void Two_People_Are_Joined_With_And()
	{
		StringAssert.Contains(IdentitySentence.PlainText("B1", ["Espen", "Nora"], 5), "Espen and Nora decide Home and Away");
	}

	// A house watching nobody never becomes empty. Real state, so the sentence names it.
	[TestMethod]
	public void Nobody_Counted_Still_Reads_As_A_Sentence()
	{
		StringAssert.Contains(IdentitySentence.PlainText(null, [], 5), "Nobody decide Home and Away");
	}

	// ===================== the checklist status line =====================

	[TestMethod]
	public void Status_Line_Summarises_The_Answers()
	{
		Assert.AreEqual("B1 · Espen, Nora · empty after 5 min", IdentitySentence.StatusLine("B1", ["Espen", "Nora"], 5));
	}

	[TestMethod]
	public void Status_Line_Counts_A_Crowd()
	{
		Assert.AreEqual(
			"B1 · Espen, Nora and 3 more · empty after 10 min",
			IdentitySentence.StatusLine("B1", ["Espen", "Nora", "Kari", "Ola", "Bilen"], 10));
	}

	[TestMethod]
	public void Status_Line_Says_When_Nobody_Is_Counted()
	{
		Assert.AreEqual("Adaptive lighting · nobody counted · empty after 5 min", IdentitySentence.StatusLine(null, [], 5));
	}
}
