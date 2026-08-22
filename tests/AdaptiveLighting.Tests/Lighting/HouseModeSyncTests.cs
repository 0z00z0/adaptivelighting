using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Comparing the configured house-mode list against the dropdown helper's own, and saying how they drifted.</summary>
/// <remarks>An empty option list is Home Assistant not answering, never a helper emptied; nothing adopts the helper's list.</remarks>
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
	public void Two_Lists_That_Agree_Say_So_And_Have_No_Drift_To_Report()
	{
		HouseModeOptionsDiff diff = HouseModeSync.Compare(Cabin(), ["Normal", "Borte", "Sover"]);

		Assert.IsTrue(diff.CanCompare);
		Assert.IsTrue(diff.Matches);
		Assert.IsFalse(diff.Differs);
		Assert.IsNull(HouseModeSync.Drift(diff), "nothing to describe");
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
	public void A_Difference_Names_What_The_Helper_Gained_And_What_It_Lost()
	{
		HouseModeOptionsDiff diff = HouseModeSync.Compare(Cabin(), ["Normal", "Borte", "Ferie"]);

		Assert.IsTrue(diff.Differs);
		Assert.IsFalse(diff.Matches);
		CollectionAssert.AreEqual(new[] { "Ferie" }, diff.Added.ToArray());
		CollectionAssert.AreEqual(new[] { "Sover" }, diff.Dropped.ToArray());

		string? drift = HouseModeSync.Drift(diff);

		StringAssert.Contains(drift, "Ferie", StringComparison.Ordinal);
		StringAssert.Contains(drift, "Sover", StringComparison.Ordinal);
	}

	[TestMethod]
	public void The_Drift_Sentence_Says_Which_Way_The_Lists_Parted()
	{
		string? gained = HouseModeSync.Drift(HouseModeSync.Compare(Cabin(), ["Normal", "Borte", "Sover", "Ferie"]));
		string? lost = HouseModeSync.Drift(HouseModeSync.Compare(Cabin(), ["Normal", "Borte"]));
		string? both = HouseModeSync.Drift(HouseModeSync.Compare(Cabin(), ["Normal", "Ferie"]));

		Assert.AreNotEqual(gained, lost);
		Assert.AreNotEqual(gained, both);
		Assert.AreNotEqual(lost, both);

		StringAssert.Contains(gained, "It offers Ferie", StringComparison.Ordinal);
		Assert.IsFalse(gained!.Contains("no longer offers", StringComparison.Ordinal),
			"nothing was lost, so nothing is reported lost");

		StringAssert.Contains(lost, "It no longer offers Sover", StringComparison.Ordinal);
	}

	/// <summary>The copy describes, it never proposes: wording that implies a control sends a person looking for one that does not exist.</summary>
	[TestMethod]
	public void The_Drift_Sentence_Never_Offers_To_Rewrite_The_List()
	{
		foreach (string[] live in new[]
		{
			new[] { "Normal", "Borte", "Sover", "Ferie" },
			["Normal", "Borte"],
			["Normal", "Ferie"]
		})
		{
			string drift = HouseModeSync.Drift(HouseModeSync.Compare(Cabin(), live))!;

			foreach (string proposal in new[] { "Taking", "adds", "drops", "Use the helper" })
			{
				Assert.IsFalse(drift.Contains(proposal, StringComparison.OrdinalIgnoreCase),
					$"'{proposal}' reads as an offer, and there is nothing to press: {drift}");
			}
		}
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
		Assert.IsNull(HouseModeSync.Drift(diff));
	}

	[TestMethod]
	public void No_Helper_Is_Nothing_To_Compare()
	{
		Assert.IsFalse(HouseModeSync.Compare(null, ["Normal"]).CanCompare);
		Assert.IsFalse(HouseModeSync.Compare(new HouseModeConfig(), ["Normal"]).CanCompare);
	}

	/// <summary>A mode the helper has stopped offering keeps everything it was configured to mean, so the orphan row can offer to move it.</summary>
	[TestMethod]
	public void Comparing_Never_Touches_The_Configured_Options()
	{
		HouseModeConfig mode = Cabin();

		_ = HouseModeSync.Compare(mode, ["Normal", "Borte", "Ferie"]);

		CollectionAssert.AreEqual(
			new[] { "Normal", "Borte", "Sover" },
			mode.Options.Select(option => option.Value).ToArray());

		HouseModeOptionConfig sover = mode.OptionFor("Sover")!;
		Assert.AreEqual(ModeKind.Sleep, sover.Kind);
		Assert.AreEqual("night", sover.ClampPeriodId);
	}
}
