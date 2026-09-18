using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Extensions;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     What a label rule survives. Home Assistant keeps a label's id through a rename and hands a reused name to a
///     new label, so which of the two forms the document stores decides whether the right lights are controlled.
/// </summary>
[TestClass]
public sealed class LabelIdentityTests
{
	private static AreaEntityResolver Resolver(FakeHaContext ha, FakeAreaRegistry registry, GlobalConfig global) =>
		new(ha, registry, global, NullLogger.Instance);

	private static AdaptiveLightingConfig Minimal() => new()
	{
		Periods = [new() { Name = "day", Start = "07:00" }],
		Areas = [new() { Name = "Stue", AreaId = "stue" }]
	};

	// ===================== a stored id survives a rename =====================

	/// <summary>The exclude rule, on the one change this whole package exists for.</summary>
	[TestMethod]
	public void An_Exclude_Label_Stored_By_Id_Still_Matches_After_The_Label_Is_Renamed()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.keep", "light.skip"];
		registry.Labels["light.skip"] = ["adaptive_exclude"];
		registry.LabelNames["adaptive_exclude"] = "Never touch";   // renamed in HA after the document was written
		ha.SetState("light.keep", "off");
		ha.SetState("light.skip", "off");

		GlobalConfig global = new() { ExcludeLabel = "adaptive_exclude" };

		Resolver(ha, registry, global).TryResolve(
			new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEqual(new[] { "light.keep" }, area.Lights.ToArray(),
			"the id is what Home Assistant keeps, so a rename must not start commanding a lamp left alone on purpose");
	}

	// ===================== a stored name becomes an id, once =====================

	/// <summary>
	///     The motion rule, through the translation the start-up normalise-and-write step runs. The host's own
	///     registry read cannot be faked, because HassModel's Label has no public constructor, so the step is
	///     exercised at the boundary the host calls.
	/// </summary>
	[TestMethod]
	public void A_Stored_Motion_Label_Name_Becomes_Its_Id_And_Still_Matches()
	{
		GlobalConfig global = new() { MotionLabel = "Counts as motion" };
		IReadOnlyList<RegistryLabel> known = [new RegistryLabel("adaptive_motion", "Counts as motion")];

		Assert.IsTrue(LabelTranslation.Apply(global, known), "a stored name the house knows has to be rewritten");
		Assert.AreEqual("adaptive_motion", global.MotionLabel, "the stored value must become the id, not stay the name");

		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.l", "binary_sensor.mmwave"];
		registry.Labels["binary_sensor.mmwave"] = ["adaptive_motion"];
		registry.LabelNames["adaptive_motion"] = "Counts as motion";
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.mmwave", "off", new() { ["device_class"] = "sound" });

		Resolver(ha, registry, global).TryResolve(
			new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEqual(new[] { "binary_sensor.mmwave" }, area.MotionSensors.ToArray(),
			"the translated id must find the same sensor the name found");
	}

	// ===================== a name nothing answers to is left alone =====================

	/// <summary>The include rule. A label made after the document keeps working, and the warning is the only word said about it.</summary>
	[TestMethod]
	public void An_Untranslatable_Include_Label_Stays_Matches_By_Name_And_Warns()
	{
		GlobalConfig global = new() { IncludeLabel = "adaptive" };

		Assert.IsFalse(LabelTranslation.Apply(global, [new RegistryLabel("other_label", "Something else")]),
			"a name no label answers to must be left exactly as written");
		Assert.AreEqual("adaptive", global.IncludeLabel);

		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.blessed", "light.plain"];
		registry.Labels["light.blessed"] = ["adaptive"];
		ha.SetState("light.blessed", "off");
		ha.SetState("light.plain", "off");

		Resolver(ha, registry, global).TryResolve(
			new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEqual(new[] { "light.blessed" }, area.Lights.ToArray(),
			"an untranslated value has to keep matching by name, or a house loses its lights on the first start");

		AdaptiveLightingConfig config = Minimal();
		config.Global.IncludeLabel = "adaptive";

		ValidationResult result = ConfigValidator.Validate(
			config, new ValidationContext { LabelsInUse = ["adaptive"], KnownLabelIds = ["other_label"] });

		Assert.IsTrue(result.IsValid, "a label stored by name still works, so it can never stop a save");
		Assert.IsTrue(result.Warnings.Any(warning => warning.Contains("adaptive", StringComparison.Ordinal)),
			"the warning has to name the label, or nobody can tell which of the three it means");
	}

	// ===================== a reused name is a different label =====================

	/// <summary>
	///     The exclude rule again, on the quiet fault the ids are for: a label renamed and its old name given to a
	///     new one. Matching either form counts both, which silently widens what the engine leaves alone.
	/// </summary>
	[TestMethod]
	public void A_New_Label_Reusing_The_Old_Name_Does_Not_Match_Once_The_Document_Stores_The_Id()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.old", "light.new"];
		registry.Labels["light.old"] = ["adaptive_exclude"];
		registry.Labels["light.new"] = ["adaptive_exclude_2"];
		registry.LabelNames["adaptive_exclude"] = "Left alone";           // renamed
		registry.LabelNames["adaptive_exclude_2"] = "adaptive_exclude";   // a new label taking the old name
		ha.SetState("light.old", "off");
		ha.SetState("light.new", "off");

		GlobalConfig global = new() { ExcludeLabel = "adaptive_exclude" };

		Resolver(ha, registry, global).TryResolve(
			new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		CollectionAssert.AreEqual(new[] { "light.new" }, area.Lights.ToArray(),
			"the stored id names one label, so the lamp carrying the new label of the same name must still be driven");
	}
}
