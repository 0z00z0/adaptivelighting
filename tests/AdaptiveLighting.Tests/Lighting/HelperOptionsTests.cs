using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     Reconciling a Home Assistant dropdown against the rows a document stores for it.
/// </summary>
/// <remarks>
///     The rule worth guarding is the empty case: an unanswered connection must not read as a helper whose
///     options have all been deleted. Both the house mode and the period select had their own copy of it.
/// </remarks>
[TestClass]
public sealed class HelperOptionsTests
{
	[TestMethod]
	public void Live_Options_Keep_Home_Assistants_Order_And_Lose_Duplicates()
	{
		HelperOptions options = HelperOptions.Reconcile([" Kveld ", "Dag", "kveld", "", null!], []);

		CollectionAssert.AreEqual(new[] { "Kveld", "Dag" }, options.Live.ToArray(),
			"trimmed, de-duplicated case-insensitively, and in the order reported");
	}

	[TestMethod]
	public void A_Stored_Value_The_Helper_No_Longer_Offers_Is_An_Orphan()
	{
		HelperOptions options = HelperOptions.Reconcile(["Dag", "Kveld"], ["Dag", "Natt"]);

		CollectionAssert.AreEqual(new[] { "Natt" }, options.Orphans.ToArray());
		CollectionAssert.AreEqual(new[] { "Kveld" }, options.Unmapped.ToArray(), "live, and nothing names it");
		CollectionAssert.AreEqual(new[] { "Dag", "Kveld", "Natt" }, options.Display.ToArray(),
			"live first, orphans behind them");
	}

	/// <summary>An empty option list is a connection that has not answered, not a helper somebody emptied.</summary>
	[TestMethod]
	public void Nothing_Reported_Orphans_Nothing()
	{
		HelperOptions options = HelperOptions.Reconcile([], ["Dag", "Natt"]);

		Assert.IsFalse(options.Answered);
		Assert.AreEqual(0, options.Orphans.Count,
			"read as an answer, a blinking connection would strike every stored row through at once");
		Assert.IsTrue(options.IsLive("Dag"), "and every row still counts as live meanwhile");
		CollectionAssert.AreEqual(new[] { "Dag", "Natt" }, options.Display.ToArray(),
			"so the document is still what gets rendered");
	}

	[TestMethod]
	public void Matching_Ignores_Case_And_Surrounding_Space()
	{
		HelperOptions options = HelperOptions.Reconcile(["Dag"], [" dag "], activeValue: "DAG ");

		Assert.AreEqual(0, options.Orphans.Count);
		Assert.AreEqual(0, options.Unmapped.Count);
		Assert.IsTrue(options.IsActive("dag"));
		Assert.IsFalse(options.IsActive("Kveld"));
	}

	[TestMethod]
	public void An_Unreadable_Active_Value_Marks_Nothing_Active()
	{
		HelperOptions options = HelperOptions.Reconcile(["Dag"], ["Dag"], activeValue: null);

		Assert.IsFalse(options.IsActive("Dag"));
	}
}
