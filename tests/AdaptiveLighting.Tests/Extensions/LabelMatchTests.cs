using Adaptive.Engine;

namespace AdaptiveLighting.Tests.Extensions;

/// <summary>What the settings pages show for a stored label id.</summary>
[TestClass]
public sealed class LabelMatchTests
{
	private static readonly IReadOnlyList<RegistryLabel> House = [new RegistryLabel("lamp_rule", "Lamps")];

	private static string? Shown(IReadOnlyList<RegistryLabel> labels, string stored) =>
		LabelMatch.ShownName(labels, label => label.Id, label => label.Name, stored);

	[TestMethod]
	public void A_Known_Id_Shows_The_Labels_Name()
	{
		Assert.AreEqual("Lamps", Shown(House, "lamp_rule"));
	}

	[TestMethod]
	public void An_Unreadable_Registry_Shows_The_Stored_Value()
	{
		Assert.AreEqual("lamp_rule", Shown([], "lamp_rule"), "an empty list is also what an unreadable registry answers");
	}

	[TestMethod]
	public void An_Id_No_Label_Has_Or_Differing_In_Case_Is_Unknown()
	{
		Assert.IsNull(Shown(House, "gone"));
		Assert.IsNull(Shown(House, "Lamp_Rule"), "the stored id is compared ordinally");
	}
}
