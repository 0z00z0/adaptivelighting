using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The identity sheet's one sentence, and the two rules underneath it.
/// </summary>
/// <remarks>
///     What is worth asserting is the wording a new owner reads once, and the difference between a house that has
///     no name and a house named after the placeholder — a distinction the YAML has no other way to keep.
/// </remarks>
[TestClass]
public sealed class IdentitySentenceTests
{
	// ===================== the name =====================

	/// <summary>A house that never answered is called what the seed calls it, not left blank.</summary>
	[TestMethod]
	public void Unnamed_House_Shows_The_Shipped_Default()
	{
		Assert.AreEqual(IdentitySentence.DefaultHouseName, IdentitySentence.Display(null));
		Assert.AreEqual(IdentitySentence.DefaultHouseName, IdentitySentence.Display("   "));
	}

	/// <summary>A named house is shown by its name, trimmed.</summary>
	[TestMethod]
	public void Named_House_Shows_Its_Name()
	{
		Assert.AreEqual("B1", IdentitySentence.Display("  B1 "));
	}

	/// <summary>
	///     Clearing the box stages <c>null</c>, not the placeholder: a document naming the house "Adaptive
	///     lighting" and one that says nothing read identically to every later reader, and only one is true.
	/// </summary>
	[TestMethod]
	public void Clearing_The_Name_Stages_Nothing_Rather_Than_The_Placeholder()
	{
		Assert.IsNull(IdentitySentence.Normalize(""));
		Assert.IsNull(IdentitySentence.Normalize("  "));
		Assert.AreEqual("Hytta", IdentitySentence.Normalize(" Hytta "));
	}

	// ===================== the delay token =====================

	/// <summary>
	///     The delay is a real token on the real shortlist, so the sheet and the House tab offer one set of values.
	/// </summary>
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

	/// <summary>A negative span cannot be written, so it cannot be shown either.</summary>
	[TestMethod]
	public void Delay_Refuses_To_Show_A_Negative_Span()
	{
		Assert.AreEqual("0 s", IdentitySentence.Delay(-5).Text);
	}

	// ===================== the sentence =====================

	/// <summary>The sentence the mock-up shows, assembled from the three answers.</summary>
	[TestMethod]
	public void Reads_As_One_Sentence()
	{
		Assert.AreEqual(
			"This house is called B1. Espen, Nora and Bilen decide Home and Away; the house counts as empty 5 min after the last person leaves.",
			IdentitySentence.PlainText("B1", ["Espen", "Nora", "Bilen"], 5));
	}

	/// <summary>Two people are joined with "and", not with a comma — English, not a list.</summary>
	[TestMethod]
	public void Two_People_Are_Joined_With_And()
	{
		StringAssert.Contains(IdentitySentence.PlainText("B1", ["Espen", "Nora"], 5), "Espen and Nora decide Home and Away");
	}

	/// <summary>
	///     A house watching nobody never becomes empty. It is a real state and a bad one, so the sentence says what
	///     follows rather than leaving a hole where the names were.
	/// </summary>
	[TestMethod]
	public void Nobody_Counted_Still_Reads_As_A_Sentence()
	{
		StringAssert.Contains(IdentitySentence.PlainText(null, [], 5), "Nobody decide Home and Away");
	}

	// ===================== the checklist status line =====================

	/// <summary>The status line summarises the answers, so returning to the board is a review.</summary>
	[TestMethod]
	public void Status_Line_Summarises_The_Answers()
	{
		Assert.AreEqual("B1 · Espen, Nora · empty after 5 min", IdentitySentence.StatusLine("B1", ["Espen", "Nora"], 5));
	}

	/// <summary>Past three, the people are counted rather than named — the item has one line, not a paragraph.</summary>
	[TestMethod]
	public void Status_Line_Counts_A_Crowd()
	{
		Assert.AreEqual(
			"B1 · Espen, Nora and 3 more · empty after 10 min",
			IdentitySentence.StatusLine("B1", ["Espen", "Nora", "Kari", "Ola", "Bilen"], 10));
	}

	/// <summary>An emptied person list says so, because a house watching nobody is worth noticing at a glance.</summary>
	[TestMethod]
	public void Status_Line_Says_When_Nobody_Is_Counted()
	{
		Assert.AreEqual("Adaptive lighting · nobody counted · empty after 5 min", IdentitySentence.StatusLine(null, [], 5));
	}
}
