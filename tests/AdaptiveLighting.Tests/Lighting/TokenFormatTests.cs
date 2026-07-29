using System.Globalization;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     How a value is written into a sentence, and how it comes back.
/// </summary>
/// <remarks>
///     The whole case for sentences is that a room's behaviour should reread itself cold to somebody who has
///     forgotten the vocabulary. "600 s" fails that and "10 min" passes it, so the writing rules are the feature
///     rather than a detail of the markup — and a token that says "10 min" while carrying <c>10</c> would set a
///     ten-second timeout with nothing on screen looking wrong. Both halves are asserted here.
/// </remarks>
[TestClass]
public sealed class TokenFormatTests
{
	// ===================== durations =====================

	/// <summary>The unit ladder: seconds, then minutes, then hours, each as soon as it is the exact one.</summary>
	[TestMethod]
	public void A_Duration_Is_Written_In_The_Largest_Unit_That_Stays_Exact()
	{
		Assert.AreEqual("30 s", TokenFormat.Duration(30));
		Assert.AreEqual("59 s", TokenFormat.Duration(59));
		Assert.AreEqual("1 min", TokenFormat.Duration(60));
		Assert.AreEqual("10 min", TokenFormat.Duration(600), "the design's own example: minutes, not 600 s");
		Assert.AreEqual("59 min", TokenFormat.Duration(3540));
		Assert.AreEqual("1 h", TokenFormat.Duration(3600));
		Assert.AreEqual("2 h", TokenFormat.Duration(7200));
	}

	/// <summary>
	///     Exactness before brevity: a value somebody set is shown back to them, not rounded to a nicer one.
	/// </summary>
	[TestMethod]
	public void A_Duration_That_Does_Not_Divide_Keeps_Its_Remainder()
	{
		Assert.AreEqual("1 min 30 s", TokenFormat.Duration(90), "90 s is not '2 min'");
		Assert.AreEqual("1 h 30 min", TokenFormat.Duration(5400));
		Assert.AreEqual("1 h 1 min 1 s", TokenFormat.Duration(3661));
	}

	/// <summary>A timeout cannot run backwards, and a sentence should not offer to read as though it could.</summary>
	[TestMethod]
	public void A_Duration_Of_Nothing_Or_Less_Is_Zero_Seconds()
	{
		Assert.AreEqual("0 s", TokenFormat.Duration(0));
		Assert.AreEqual("0 s", TokenFormat.Duration(-30));
	}

	/// <summary>The settings the schema keeps in minutes go through the same ladder.</summary>
	[TestMethod]
	public void Minutes_Reach_The_Same_Words_As_Seconds()
	{
		Assert.AreEqual("2 h", TokenFormat.DurationFromMinutes(120));
		Assert.AreEqual("10 min", TokenFormat.DurationFromMinutes(10));
	}

	// ===================== proportions and quantities =====================

	/// <summary>A number, a space, a percent sign — the SI convention and the shipped UI's own habit.</summary>
	[TestMethod]
	public void A_Proportion_Is_Written_With_Its_Sign()
	{
		Assert.AreEqual("50 %", TokenFormat.Percent(50));
		Assert.AreEqual("100 %", TokenFormat.Percent(100));
		Assert.AreEqual("12.5 %", TokenFormat.Percent(12.5), "a factor somebody chose on purpose survives");
		Assert.AreEqual("50 %", TokenFormat.PercentFromFraction(0.5), "the schema's 0-1 factors convert");
	}

	/// <summary>A space before the unit, except the degree sign, which typography sets tight.</summary>
	[TestMethod]
	public void A_Quantity_Carries_Its_Unit_The_Way_A_Reader_Expects()
	{
		Assert.AreEqual("40 lx", TokenFormat.Number(40, "lx"));
		Assert.AreEqual("3°", TokenFormat.Number(3, "°"));
		Assert.AreEqual("-6°", TokenFormat.Number(-6, "°"), "a sun below the horizon is a real setting");
		Assert.AreEqual("10000 lx", TokenFormat.Number(10000, "lx"), "no thousands separator inside a control");
		Assert.AreEqual("1.5", TokenFormat.Number(1.5), "a bare number keeps no unit and no padding");
	}

	// ===================== the round trip =====================

	/// <summary>
	///     The one failure this whole layer exists to prevent: words and carried value disagreeing.
	/// </summary>
	/// <remarks>
	///     A shortlist that wrote "10 min" and handed back <c>10</c> would set a ten-second timeout, and every
	///     surface in the app would look correct while the room went dark eight seconds after someone walked in.
	/// </remarks>
	[TestMethod]
	public void What_A_Choice_Says_And_What_It_Carries_Mean_The_Same_Thing()
	{
		TokenChoice tenMinutes = TokenChoices.DurationsInMinutes(10).Single();

		Assert.AreEqual("10 min", tenMinutes.Text);
		Assert.AreEqual(600, new SentenceEdit("k", TokenKind.Duration, tenMinutes.Value).Seconds);

		TokenChoice half = TokenChoices.Percentages(50).Single();

		Assert.AreEqual("50 %", half.Text);
		Assert.AreEqual(0.5, new SentenceEdit("k", TokenKind.Percentage, half.Value).Fraction, 1e-9,
			"the schema stores a 0-1 factor, so the edit has to offer one");
	}

	/// <summary>Minutes-shaped settings come back as minutes without the page doing arithmetic.</summary>
	[TestMethod]
	public void An_Edit_Reads_Back_As_Minutes_For_The_Settings_Stored_That_Way()
	{
		TokenChoice twoHours = TokenChoices.DurationsInMinutes(120).Single();

		Assert.AreEqual(120, new SentenceEdit("k", TokenKind.Duration, twoHours.Value).Minutes);
	}

	/// <summary>A toggle carries a yes/no that survives the trip through a string.</summary>
	[TestMethod]
	public void An_Edit_Reads_Back_As_A_Flag()
	{
		Assert.IsTrue(new SentenceEdit("k", TokenKind.Toggle, "true").Flag);
		Assert.IsFalse(new SentenceEdit("k", TokenKind.Toggle, "false").Flag);
		Assert.IsFalse(new SentenceEdit("k", TokenKind.Toggle, "").Flag, "nonsense is not truth");
	}

	/// <summary>A choice built from an enum comes back as that enum's member.</summary>
	[TestMethod]
	public void An_Edit_Reads_Back_As_An_Enum_Member()
	{
		SentenceEdit edit = new("k", TokenKind.Choice, nameof(DarknessSource.Always));

		Assert.IsTrue(edit.TryEnum(out DarknessSource source));
		Assert.AreEqual(DarknessSource.Always, source);

		Assert.IsFalse(new SentenceEdit("k", TokenKind.Choice, "Twilight").TryEnum(out DarknessSource _),
			"a value the enum does not have must fail rather than land on the first member");
	}

	// ===================== culture =====================

	/// <summary>
	///     The house this runs in is Norwegian, and <c>nb-NO</c> writes decimals with a comma.
	/// </summary>
	/// <remarks>
	///     Two things would break if formatting followed the current culture. The words would read as typos —
	///     "12,5 %" inside an otherwise English sentence — and, far worse, the carried value would stop
	///     round-tripping: written "0,5" on the host and parsed back with an invariant parser, a half becomes a
	///     five, and the warning dim goes to 500 %. Written down as a test because it is invisible on the
	///     developer's machine and certain on the owner's.
	/// </remarks>
	[TestMethod]
	public void Values_Are_Written_And_Carried_Invariantly_Whatever_The_Machine_Speaks()
	{
		CultureInfo original = CultureInfo.CurrentCulture;

		try
		{
			CultureInfo.CurrentCulture = new CultureInfo("nb-NO");

			Assert.AreEqual("12.5 %", TokenFormat.Percent(12.5));
			Assert.AreEqual("1.5", TokenFormat.Number(1.5));
			Assert.AreEqual("0.5", TokenFormat.Carry(0.5));
			Assert.AreEqual(0.5, new SentenceEdit("k", TokenKind.Number, TokenFormat.Carry(0.5)).Number, 1e-9);
		}
		finally
		{
			CultureInfo.CurrentCulture = original;
		}
	}
}
