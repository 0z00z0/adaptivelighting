using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The label options the pickers are built from.</summary>
/// <remarks>Only the empty case is reachable, and a picker that reads "no labels" as Home Assistant failing to answer is the bug.</remarks>
[TestClass]
public sealed class HaCatalogLabelTests
{
	private static HaCatalog Catalog() =>
		new(new FakeHaContext(), new FakeHaRegistry(), NullLoggerFactory.Instance);

	[TestMethod]
	public void A_House_With_No_Labels_Offers_No_Label_Options()
	{
		HaCatalog catalog = Catalog();

		Assert.AreEqual(0, catalog.LabelOptions().Count);
		Assert.IsTrue(catalog.IsHomeAssistantReady,
			"an empty label list is an answer, not Home Assistant failing to answer");
	}

	[TestMethod]
	public void A_Label_Option_Carries_Both_The_Id_And_The_Name()
	{
		LabelOption option = new("label_abc123", "adaptive");

		Assert.AreEqual("label_abc123", option.Id);
		Assert.AreEqual("adaptive", option.Name, "the name is what a label field stores and what LabelsOf matches");
	}
}
