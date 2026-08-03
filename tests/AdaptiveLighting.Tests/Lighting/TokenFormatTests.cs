using System.Globalization;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     How a value is written into a sentence, and how it comes back.
/// </summary>
[TestClass]
public sealed class TokenFormatTests
{
	// ===================== durations =====================

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

	[TestMethod]
	public void A_Duration_That_Does_Not_Divide_Keeps_Its_Remainder()
	{
		Assert.AreEqual("1 min 30 s", TokenFormat.Duration(90), "90 s is not '2 min'");
		Assert.AreEqual("1 h 30 min", TokenFormat.Duration(5400));
		Assert.AreEqual("1 h 1 min 1 s", TokenFormat.Duration(3661));
	}

	[TestMethod]
	public void A_Duration_Of_Nothing_Or_Less_Is_Zero_Seconds()
	{
		Assert.AreEqual("0 s", TokenFormat.Duration(0));
		Assert.AreEqual("0 s", TokenFormat.Duration(-30));
	}

	[TestMethod]
	public void Minutes_Reach_The_Same_Words_As_Seconds()
	{
		Assert.AreEqual("2 h", TokenFormat.DurationFromMinutes(120));
		Assert.AreEqual("10 min", TokenFormat.DurationFromMinutes(10));
	}

	// ===================== proportions and quantities =====================

	[TestMethod]
	public void A_Proportion_Is_Written_With_Its_Sign()
	{
		Assert.AreEqual("50 %", TokenFormat.Percent(50));
		Assert.AreEqual("100 %", TokenFormat.Percent(100));
		Assert.AreEqual("12.5 %", TokenFormat.Percent(12.5), "a factor somebody chose on purpose survives");
		Assert.AreEqual("50 %", TokenFormat.PercentFromFraction(0.5), "the schema's 0-1 factors convert");
	}

	// The degree sign sets tight. Every other unit takes a space.
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

	// A shortlist that writes "10 min" and hands back 10 sets a ten-second timeout, and every surface looks right.
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

	[TestMethod]
	public void An_Edit_Reads_Back_As_Minutes_For_The_Settings_Stored_That_Way()
	{
		TokenChoice twoHours = TokenChoices.DurationsInMinutes(120).Single();

		Assert.AreEqual(120, new SentenceEdit("k", TokenKind.Duration, twoHours.Value).Minutes);
	}

	[TestMethod]
	public void An_Edit_Reads_Back_As_A_Flag()
	{
		Assert.IsTrue(new SentenceEdit("k", TokenKind.Toggle, "true").Flag);
		Assert.IsFalse(new SentenceEdit("k", TokenKind.Toggle, "false").Flag);
		Assert.IsFalse(new SentenceEdit("k", TokenKind.Toggle, "").Flag, "nonsense is not truth");
	}

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

	// nb-NO writes decimals with a comma. Follow the current culture and "0,5" parses back as five, so the
	// warning dim goes to 500 %. Invisible on the developer's machine, certain on the owner's.
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
