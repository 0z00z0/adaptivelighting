using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     Comparing the configured house-mode list against the dropdown helper's own, and taking the helper's.
/// </summary>
/// <remarks>
///     An unreachable Home Assistant answers with an empty option list, so empty is "cannot tell" and never
///     "drop every configured mode".
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
			new() { Value = "Sover", Kind = ModeKind.Sleep, ClampPeriodId = "night" }
		]
	};

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

	[TestMethod]
	public void A_Different_Order_Is_Not_A_Difference()
	{
		Assert.IsTrue(HouseModeSync.Compare(Cabin(), ["Sover", "Normal", "Borte"]).Matches);
	}

	/// <summary>Case and surrounding whitespace are ignored, as the engine ignores them.</summary>
	[TestMethod]
	public void Options_Are_Matched_The_Way_The_Engine_Matches_Them()
	{
		Assert.IsTrue(HouseModeSync.Compare(Cabin(), [" normal ", "BORTE", "Sover"]).Matches);
	}

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

	/// <summary>An empty live list is Home Assistant not answering; the two readings call for opposite responses.</summary>
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

	[TestMethod]
	public void No_Helper_Is_Nothing_To_Compare()
	{
		Assert.IsFalse(HouseModeSync.Compare(null, ["Normal"]).CanCompare);
		Assert.IsFalse(HouseModeSync.Compare(new HouseModeConfig(), ["Normal"]).CanCompare);
	}

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

	[TestMethod]
	public void Adopting_What_Is_Already_There_Is_A_No_Op()
	{
		HouseModeConfig mode = Cabin();

		Assert.IsFalse(HouseModeSync.Adopt(mode, ["Normal", "Borte", "Sover"]));
		Assert.AreEqual(3, mode.Options.Count);
		Assert.IsTrue(HouseModeSync.Compare(mode, ["Normal", "Borte", "Sover"]).Matches);
	}

	/// <summary>The panel appears only once the helper has answered, but the connection can drop before the click.</summary>
	[TestMethod]
	public void Adopting_An_Empty_List_Leaves_The_Modes_Alone()
	{
		HouseModeConfig mode = Cabin();

		Assert.IsFalse(HouseModeSync.Adopt(mode, []));
		Assert.AreEqual(3, mode.Options.Count, "an unreachable Home Assistant does not get to delete a house's modes");

		Assert.IsFalse(HouseModeSync.Adopt(null, ["Normal"]), "and a document with no house mode has nothing to rebuild");
	}

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
	///     <see cref="HouseModeOptionConfig.Kind"/> defaults to Normal, so a list can hold several; the reset
	///     target is the first, which <see cref="HouseModeConfig.NormalOption"/> returns and the settings page reads.
	/// </summary>
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
