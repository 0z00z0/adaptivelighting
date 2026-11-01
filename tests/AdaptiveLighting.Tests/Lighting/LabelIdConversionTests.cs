using Adaptive.Engine;
using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The one-time move of the label settings onto label ids. A wrong include label stops every room.</summary>
[TestClass]
public sealed class LabelIdConversionTests
{
	private static readonly IReadOnlyList<RegistryLabel> House =
	[
		new RegistryLabel("lamp_rule", "Lamps"),
		new RegistryLabel("quiet", "Quiet corner"),
		new RegistryLabel("twin_a", "Twin"),
		new RegistryLabel("twin_b", "twin")
	];

	[TestMethod]
	public void A_Name_Exactly_One_Label_Has_Becomes_That_Labels_Id()
	{
		GlobalConfig global = new() { IncludeLabel = "lamps", ExcludeLabel = "Quiet corner" };

		Assert.IsTrue(LabelIdConversion.Apply(global, House));
		Assert.AreEqual("lamp_rule", global.IncludeLabel);
		Assert.AreEqual("quiet", global.ExcludeLabel);
	}

	[TestMethod]
	public void A_Stored_Id_Is_Left_Alone_And_Nothing_Is_Written()
	{
		GlobalConfig global = new() { IncludeLabel = "lamp_rule", MotionLabel = "quiet" };

		Assert.IsFalse(LabelIdConversion.Apply(global, House), "a converted document must not be written again");
		Assert.AreEqual("lamp_rule", global.IncludeLabel);
		Assert.AreEqual("quiet", global.MotionLabel);
	}

	[TestMethod]
	public void A_Name_No_Label_Has_Or_Several_Share_Is_Left_As_Written()
	{
		GlobalConfig global = new() { IncludeLabel = "Nowhere", ExcludeLabel = "Twin" };

		Assert.IsFalse(LabelIdConversion.Apply(global, House), "never guessed");
		Assert.AreEqual("Nowhere", global.IncludeLabel);
		Assert.AreEqual("Twin", global.ExcludeLabel);
	}

	[TestMethod]
	public void An_Unreadable_Registry_Changes_Nothing()
	{
		GlobalConfig global = new() { IncludeLabel = "Lamps", ExcludeLabel = "adaptive-exclude" };

		Assert.IsFalse(LabelIdConversion.Apply(global, knownLabels: null));
		Assert.AreEqual("Lamps", global.IncludeLabel);
		Assert.AreEqual("adaptive-exclude", global.ExcludeLabel);
	}

	[TestMethod]
	public void The_Retired_Hyphenated_Defaults_Are_Cleared_When_No_Label_Has_That_Name()
	{
		GlobalConfig global = new() { ExcludeLabel = "adaptive-exclude", MotionLabel = "adaptive-motion" };

		Assert.IsTrue(LabelIdConversion.Apply(global, House));
		Assert.AreEqual(string.Empty, global.ExcludeLabel);
		Assert.AreEqual(string.Empty, global.MotionLabel);
		Assert.IsFalse(LabelIdConversion.Apply(global, House), "the second start must change nothing");
	}

	[TestMethod]
	public void The_Retired_Underscore_Defaults_Are_Cleared_Unless_A_Label_Has_That_Id()
	{
		GlobalConfig global = new() { ExcludeLabel = "adaptive_exclude", MotionLabel = "adaptive_motion" };

		Assert.IsTrue(LabelIdConversion.Apply(global, House));
		Assert.AreEqual(string.Empty, global.ExcludeLabel);
		Assert.AreEqual(string.Empty, global.MotionLabel);

		GlobalConfig labelled = new() { MotionLabel = "adaptive_motion" };

		LabelIdConversion.Apply(labelled, [.. House, new RegistryLabel("adaptive_motion", "Motion")]);
		Assert.AreEqual("adaptive_motion", labelled.MotionLabel, "a house that has the label keeps it");
	}

	[TestMethod]
	public void An_Id_Differing_Only_In_Case_Is_Stored_As_The_Labels_Own_Id()
	{
		GlobalConfig global = new() { IncludeLabel = "Lamp_Rule" };

		Assert.IsTrue(LabelIdConversion.Apply(global, House));
		Assert.AreEqual("lamp_rule", global.IncludeLabel);
	}

	[TestMethod]
	public void A_Document_That_Already_Has_Readable_Ids_Is_Not_Converted_Again()
	{
		// A stale id that is now another label's name: matching it by name would move the rule to a different label.
		GlobalConfig global = new() { IncludeLabel = "light", IdCounters = new IdCounters { Periods = 3 } };

		Assert.IsFalse(LabelIdConversion.Apply(global, [new RegistryLabel("light_2", "Light")]));
		Assert.AreEqual("light", global.IncludeLabel);
	}
}
