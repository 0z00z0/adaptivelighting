using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     That room state reads as shape plus colour, and never as colour alone.
/// </summary>
/// <remarks>
///     This is an accessibility fix, so the tests are the requirement rather than a description of the code. The
///     shipped UI distinguished lit-by-the-engine, set-by-hand and switched-off by colour only: nothing to a
///     colourblind reader, nothing in a greyscale screenshot, nothing in bright sunlight on a phone. What has to
///     stay true is that no two states the eye must tell apart are drawn the same way — and that is exactly the
///     kind of property a later, well-meaning edit breaks by reusing one glyph for one more state.
/// </remarks>
[TestClass]
public sealed class StateGlyphTests
{
	private static AreaState[] AllStates => Enum.GetValues<AreaState>();

	/// <summary>Every state a room can be in is drawable, whatever the engine adds next.</summary>
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

	/// <summary>
	///     No two states are drawn identically. The whole point, stated as an invariant.
	/// </summary>
	/// <remarks>
	///     Shape, colour and word together must differ. Two hand states are allowed to share a shape and a
	///     colour — they are one fact, that somebody decided — but their words then have to do the telling,
	///     and this catches the day somebody gives them the same word too.
	/// </remarks>
	[TestMethod]
	public void No_Two_States_Are_Drawn_The_Same_Way()
	{
		StateMark[] marks = [.. AllStates.Select(StateGlyph.For)];

		Assert.AreEqual(marks.Length, marks.Distinct().Count(),
			"two states rendering to the same shape, colour and word cannot be told apart at all");
	}

	/// <summary>
	///     The engine holding a room lit and the engine about to switch it off are different shapes.
	/// </summary>
	/// <remarks>
	///     The approved design allowed the warning dim to borrow the auto loop, distinguished by amber and a
	///     blink, and named that a compromise. It was drawn its own glyph instead: a shared shape means the two
	///     states are one picture in greyscale and one picture to a colourblind reader, which defeats the reason
	///     for drawing shapes at all. This test is that decision, so it cannot be quietly undone.
	/// </remarks>
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

	/// <summary>Every state that means something has a shape, and the three that mean something differ.</summary>
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

	/// <summary>
	///     A room merely watching gets no shape at all — the dark-cockpit rule, carried into iconography.
	/// </summary>
	/// <remarks>
	///     Fourteen rooms out of seventeen are watching and nothing else. Fourteen repeated glyphs are texture,
	///     and texture is what the eye has to look past to find the three rooms that need it.
	/// </remarks>
	[TestMethod]
	public void The_Quiet_States_Get_No_Shape()
	{
		Assert.IsNull(StateGlyph.For(AreaState.AutoVacant).Icon);
		Assert.IsNull(StateGlyph.For(AreaState.Away).Icon);
	}

	/// <summary>Only the state that wants attention right now blinks.</summary>
	[TestMethod]
	public void Only_The_Warning_Dim_Blinks()
	{
		foreach (AreaState state in AllStates)
		{
			Assert.AreEqual(state == AreaState.PreOff, StateGlyph.For(state).Blinks,
				$"{state} should {(state == AreaState.PreOff ? "" : "not ")}blink");
		}
	}

	/// <summary>
	///     Only a room the engine is holding lit has a warmth of its own to report.
	/// </summary>
	/// <remarks>
	///     Tinting anything else would be decoration pretending to be data: a room that is off has no colour
	///     temperature, and a room set by hand has one the engine never chose and cannot read back.
	/// </remarks>
	[TestMethod]
	public void Only_A_Room_The_Engine_Is_Holding_Lit_Takes_Its_Own_Warmth()
	{
		foreach (AreaState state in AllStates)
		{
			Assert.AreEqual(state == AreaState.AutoActive, StateGlyph.TakesKelvinTint(state));
		}
	}

	/// <summary>
	///     The two hand states share a shape on purpose, and are told apart by their words.
	/// </summary>
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
