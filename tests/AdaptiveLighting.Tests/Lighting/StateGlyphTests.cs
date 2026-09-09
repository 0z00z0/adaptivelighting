using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Room state reads as shape plus colour, never colour alone, which is an accessibility requirement.</summary>
[TestClass]
public sealed class StateGlyphTests
{
	private static AreaState[] AllStates => Enum.GetValues<AreaState>();

	[TestMethod]
	public void Every_State_Has_A_Colour_And_A_Word()
	{
		foreach (AreaState state in AllStates)
		{
			StateMark mark = StateGlyph.For(state);

			Assert.IsTrue(mark.Family.StartsWith("state-", StringComparison.Ordinal), $"{state} has no colour family");
			Assert.IsTrue(mark.Word.Length > 0, $"{state} has no word");
		}
	}

	// Presence has not decided away since the house mode took it over. A chip that still says the house is empty
	// sends somebody to look at a phone tracker over a dropdown.
	[TestMethod]
	public void The_Away_Chip_Names_The_Mode_Rather_Than_Who_Is_Home()
	{
		string word = StateGlyph.For(AreaState.Away).Word;

		StringAssert.Contains(word, "away", "the room is away because the house mode says so");
		Assert.IsFalse(word.Contains("empty", StringComparison.OrdinalIgnoreCase),
			"whether the house is empty is a separate fact the engine does not act on");
	}

	// The two hand states may share a shape and a colour, so shape, colour and word are checked together.
	[TestMethod]
	public void No_Two_States_Are_Drawn_The_Same_Way()
	{
		StateMark[] marks = [.. AllStates.Select(StateGlyph.For)];

		Assert.AreEqual(marks.Length, marks.Distinct().Count(),
			"two states rendering to the same shape, colour and word cannot be told apart at all");
	}

	[TestMethod]
	public void Lit_And_About_To_Go_Dark_Do_Not_Share_A_Shape()
	{
		StateMark lit = StateGlyph.For(AreaState.AutoActive);
		StateMark dimming = StateGlyph.For(AreaState.PreOff);

		Assert.IsNotNull(lit.Icon);
		Assert.IsNotNull(dimming.Icon);
		Assert.AreNotEqual(lit.Icon, dimming.Icon);
		Assert.AreNotEqual(lit.Family, dimming.Family, "and their colours differ too, for everyone else");
	}

	[TestMethod]
	public void The_States_That_Mean_Something_All_Have_Shapes()
	{
		foreach (AreaState state in new[]
		{
			AreaState.AutoActive, AreaState.PreOff, AreaState.OverriddenOn, AreaState.SuppressedOff,
			AreaState.SceneHold, AreaState.Disabled
		})
		{
			Assert.IsNotNull(StateGlyph.For(state).Icon, $"{state} is something happening and needs a shape");
		}

		string[] distinct =
		[
			.. new[] { AreaState.AutoActive, AreaState.PreOff, AreaState.Disabled }
				.Select(state => StateGlyph.For(state).Icon!)
		];

		Assert.AreEqual(3, distinct.Distinct().Count(), "engine-lit, about-to-dim and switched-off are three pictures");
	}

	[TestMethod]
	public void The_Quiet_States_Get_No_Shape()
	{
		Assert.IsNull(StateGlyph.For(AreaState.AutoVacant).Icon);
		Assert.IsNull(StateGlyph.For(AreaState.Away).Icon);
	}

	[TestMethod]
	public void Only_The_Warning_Dim_Blinks()
	{
		foreach (AreaState state in AllStates)
		{
			Assert.AreEqual(state == AreaState.PreOff, StateGlyph.For(state).Blinks,
				$"{state} should {(state == AreaState.PreOff ? "" : "not ")}blink");
		}
	}

	// A room set by hand has a colour temperature the engine never chose and cannot read back.
	[TestMethod]
	public void Only_A_Room_The_Engine_Is_Holding_Lit_Takes_Its_Own_Warmth()
	{
		foreach (AreaState state in AllStates)
		{
			Assert.AreEqual(state == AreaState.AutoActive, StateGlyph.TakesKelvinTint(state));
		}
	}

	[TestMethod]
	public void Both_Hand_States_Are_One_Fact_With_Two_Words()
	{
		StateMark on = StateGlyph.For(AreaState.OverriddenOn);
		StateMark off = StateGlyph.For(AreaState.SuppressedOff);

		Assert.AreEqual(on.Icon, off.Icon, "somebody decided, in both cases");
		Assert.AreEqual(on.Family, off.Family);
		Assert.AreNotEqual(on.Word, off.Word, "which is why the word has to carry the difference");
	}
}
