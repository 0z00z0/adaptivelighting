using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     Comparing the configured house-mode list against the dropdown helper's own, and taking the helper's.
/// </summary>
/// <remarks>
///     <para>
///         <b>What was wrong.</b> The list was rebuilt from the helper only when the entity <i>changed</i>. A house
///         whose helper was picked long ago and has since gained an option had no way to ask for it and nothing on
///         screen saying there was anything to ask for — from the owner's side the feature did not exist.
///     </para>
///     <para>
///         <b>What must stay wrong-proof.</b> Two of these tests are about refusals rather than features. An
///         unreachable Home Assistant answers with an empty option list, which is indistinguishable from a helper
///         that has no options — and read as a comparison it says every configured mode should be dropped. A page
///         that offered to empty somebody's house modes because the connection was down would be the worst button
///         in this application, so "cannot tell" is a state of its own and adopting an empty list does nothing.
///     </para>
/// </remarks>
[TestClass]
public sealed class HouseModeSyncTests
{
	private static HouseModeConfig Cabin() => new()
	{
		Entity = "input_select.husmodus",
		Options =
		[
			new() { Value = "Normal", Kind = ModeKind.Normal },
			new() { Value = "Borte", Kind = ModeKind.Away, Scene = "scene.away", ResetOnPresence = true },
			new() { Value = "Sover", Kind = ModeKind.Sleep, ClampPeriod = "night" }
		]
	};

	// ===================== the comparison =====================

	/// <summary>
	///     The state the owner is actually in most of the time: the two lists agree. It has to be legible as an
	///     answer — a section that could only ever show a call to action would leave somebody unable to tell
	///     "nothing to do" from "this feature is broken", which is the report that started this.
	/// </summary>
	[TestMethod]
	public void Two_Lists_That_Agree_Say_So_And_Offer_Nothing()
	{
		HouseModeOptionsDiff diff = HouseModeSync.Compare(Cabin(), ["Normal", "Borte", "Sover"]);

		Assert.IsTrue(diff.CanCompare);
		Assert.IsTrue(diff.Matches);
		Assert.IsFalse(diff.Differs);
		Assert.IsNull(HouseModeSync.Title(diff), "nothing to head");
		Assert.IsNull(HouseModeSync.Summary(diff), "nothing to say it would do");
	}

	/// <summary>
	///     Order is how a dropdown is drawn, not a fact about the house. A call to action raised over it would
	///     never stop being raised, and pressing it would change nothing anybody could see.
	/// </summary>
	[TestMethod]
	public void A_Different_Order_Is_Not_A_Difference()
	{
		Assert.IsTrue(HouseModeSync.Compare(Cabin(), ["Sover", "Normal", "Borte"]).Matches);
	}

	/// <summary>Case and stray whitespace are the same option, exactly as the engine reads them.</summary>
	[TestMethod]
	public void Options_Are_Matched_The_Way_The_Engine_Matches_Them()
	{
		Assert.IsTrue(HouseModeSync.Compare(Cabin(), [" normal ", "BORTE", "Sover"]).Matches);
	}

	/// <summary>
	///     What the helper gained and what it no longer offers, named rather than counted — this button drops
	///     configuration somebody wrote by hand, and "2 options will be removed" leaves them working out which two
	///     while deciding whether to press it.
	/// </summary>
	[TestMethod]
	public void A_Difference_Names_What_Would_Be_Added_And_What_Dropped()
	{
		HouseModeOptionsDiff diff = HouseModeSync.Compare(Cabin(), ["Normal", "Borte", "Ferie"]);

		Assert.IsTrue(diff.Differs);
		Assert.IsFalse(diff.Matches);
		CollectionAssert.AreEqual(new[] { "Ferie" }, diff.Added.ToArray());
		CollectionAssert.AreEqual(new[] { "Sover" }, diff.Dropped.ToArray());

		string? summary = HouseModeSync.Summary(diff);

		StringAssert.Contains(summary, "Ferie");
		StringAssert.Contains(summary, "Sover");
		StringAssert.Contains(summary, "adds");
		StringAssert.Contains(summary, "drops");
	}

	/// <summary>
	///     Three headings, because the three cases are different news. Options only the document has are usually a
	///     rename or a deletion in Home Assistant, and a heading saying the helper had "changed" would send
	///     somebody looking for a change they did not make.
	/// </summary>
	[TestMethod]
	public void The_Heading_Says_Which_Way_The_Lists_Drifted()
	{
		string? gained = HouseModeSync.Title(HouseModeSync.Compare(Cabin(), ["Normal", "Borte", "Sover", "Ferie"]));
		string? lost = HouseModeSync.Title(HouseModeSync.Compare(Cabin(), ["Normal", "Borte"]));
		string? both = HouseModeSync.Title(HouseModeSync.Compare(Cabin(), ["Normal", "Ferie"]));

		Assert.AreNotEqual(gained, lost);
		Assert.AreNotEqual(gained, both);
		Assert.AreNotEqual(lost, both);

		StringAssert.Contains(HouseModeSync.Summary(HouseModeSync.Compare(Cabin(), ["Normal", "Borte", "Sover", "Ferie"])), "adds Ferie");
		Assert.IsFalse(
			HouseModeSync.Summary(HouseModeSync.Compare(Cabin(), ["Normal", "Borte", "Sover", "Ferie"]))!
				.Contains("drops", StringComparison.Ordinal),
			"nothing would be dropped, so nothing is threatened");
	}

	/// <summary>
	///     <b>The refusal that matters most.</b> An empty live list is Home Assistant not answering, not a helper
	///     with no options — and the two call for opposite responses. It is "cannot tell", it offers nothing, and
	///     it must never be reported as "every mode you have would be dropped".
	/// </summary>
	[TestMethod]
	public void An_Unreachable_Home_Assistant_Is_Cannot_Tell_And_Not_An_Empty_Helper()
	{
		HouseModeOptionsDiff diff = HouseModeSync.Compare(Cabin(), []);

		Assert.IsFalse(diff.CanCompare);
		Assert.IsFalse(diff.Differs);
		Assert.IsFalse(diff.Matches, "silence is not agreement either");
		Assert.AreEqual(0, diff.Dropped.Count, "the configured modes are not up for deletion over a dropped connection");
		Assert.IsNull(HouseModeSync.Summary(diff));
	}

	/// <summary>A document with no helper has nothing to compare against, which is neither a match nor a difference.</summary>
	[TestMethod]
	public void No_Helper_Is_Nothing_To_Compare()
	{
		Assert.IsFalse(HouseModeSync.Compare(null, ["Normal"]).CanCompare);
		Assert.IsFalse(HouseModeSync.Compare(new HouseModeConfig(), ["Normal"]).CanCompare);
	}

	// ===================== taking the helper's list =====================

	/// <summary>
	///     <b>An option the helper still offers keeps its whole configuration.</b> Two lists that both offer
	///     "Borte" mean the same thing by it, and rebuilding from scratch would throw away a scene and a reset over
	///     a rename elsewhere in the list.
	/// </summary>
	[TestMethod]
	public void A_Surviving_Option_Keeps_Everything_It_Was_Configured_To_Mean()
	{
		HouseModeConfig mode = Cabin();

		Assert.IsTrue(HouseModeSync.Adopt(mode, ["Normal", "Borte", "Ferie"]));

		HouseModeOptionConfig? borte = mode.OptionFor("Borte");

		Assert.IsNotNull(borte);
		Assert.AreEqual(ModeKind.Away, borte.Kind);
		Assert.AreEqual("scene.away", borte.Scene);
		Assert.IsTrue(borte.ResetOnPresence);

		Assert.IsNull(mode.OptionFor("Sover"), "an option the helper no longer offers can never be selected");
		Assert.IsNotNull(mode.OptionFor("Ferie"));
		Assert.AreEqual(ModeKind.Normal, mode.OptionFor("Ferie")!.Kind,
			"a new option arrives with nothing guessed about it — this is a person's edit, not a discovery");

		CollectionAssert.AreEqual(
			new[] { "Normal", "Borte", "Ferie" },
			mode.Options.Select(option => option.Value).ToArray(),
			"the list reads the way the dropdown does");
	}

	/// <summary>
	///     Adopting when the lists already agree changes nothing, so the panel that offered it cannot come back
	///     saying there is still something to take.
	/// </summary>
	[TestMethod]
	public void Adopting_What_Is_Already_There_Is_A_No_Op()
	{
		HouseModeConfig mode = Cabin();

		Assert.IsFalse(HouseModeSync.Adopt(mode, ["Normal", "Borte", "Sover"]));
		Assert.AreEqual(3, mode.Options.Count);
		Assert.IsTrue(HouseModeSync.Compare(mode, ["Normal", "Borte", "Sover"]).Matches);
	}

	/// <summary>
	///     <b>The same refusal, on the acting side.</b> The panel only appears when the helper has answered, but a
	///     connection that drops between the render and the click must not empty the list either.
	/// </summary>
	[TestMethod]
	public void Adopting_An_Empty_List_Leaves_The_Modes_Alone()
	{
		HouseModeConfig mode = Cabin();

		Assert.IsFalse(HouseModeSync.Adopt(mode, []));
		Assert.AreEqual(3, mode.Options.Count, "an unreachable Home Assistant does not get to delete a house's modes");

		Assert.IsFalse(HouseModeSync.Adopt(null, ["Normal"]), "and a document with no house mode has nothing to rebuild");
	}

	/// <summary>
	///     Adopting settles the comparison it was offered from. Anything else would be a call to action that
	///     survives being answered.
	/// </summary>
	[TestMethod]
	public void Adopting_Settles_The_Difference_That_Offered_It()
	{
		HouseModeConfig mode = Cabin();
		string[] live = ["Normal", "Borte", "Ferie"];

		Assert.IsTrue(HouseModeSync.Compare(mode, live).Differs);

		HouseModeSync.Adopt(mode, live);

		Assert.IsTrue(HouseModeSync.Compare(mode, live).Matches);
	}

	/// <summary>
	///     <b>Adopting must not move the house's reset target.</b> <see cref="HouseModeOptionConfig.Kind"/> defaults
	///     to <see cref="ModeKind.Normal"/>, so an option the helper offers and the document has never seen arrives
	///     Normal — and a list can then hold several. The engine has always taken the first, which is the option
	///     that was already there; what a house returns to when a mode ends must not change because somebody
	///     accepted a new option into the list.
	/// </summary>
	/// <remarks>
	///     Pinned here because the settings page reads exactly this: its "Normal — reset target" badge is now the
	///     option <see cref="HouseModeConfig.NormalOption"/> returns and not merely any option whose kind is Normal,
	///     which is what let it label two of them at once.
	/// </remarks>
	[TestMethod]
	public void Adopting_A_New_Option_Leaves_The_Reset_Target_Where_It_Was()
	{
		HouseModeConfig mode = Cabin();

		HouseModeSync.Adopt(mode, ["Normal", "Borte", "Sover", "Ferie"]);

		HouseModeOptionConfig adopted = mode.OptionFor("Ferie")!;

		Assert.AreEqual(ModeKind.Normal, adopted.Kind, "nothing is guessed from an option's wording, so it lands on the default");
		Assert.AreEqual("Normal", mode.NormalOption?.Value, "but the reset target is still the option that already held it");
		Assert.IsFalse(ReferenceEquals(adopted, mode.NormalOption), "so the newcomer is Normal without being the reset target");
	}
}
