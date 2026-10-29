using Adaptive.Engine;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

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
		Periods = [new() { Id = "day", Name = "day", Start = "07:00" }],
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

	// ===================== a name matches nothing =====================

	/// <summary>The motion rule: a stored name is not an id.</summary>
	[TestMethod]
	public void A_Stored_Label_Name_No_Longer_Matches()
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = ["light.l", "binary_sensor.mmwave"];
		registry.Labels["binary_sensor.mmwave"] = ["presence_rule"];
		registry.LabelNames["presence_rule"] = "Counts as motion";
		ha.SetState("light.l", "off");
		ha.SetState("binary_sensor.mmwave", "off", new() { ["device_class"] = "sound" });

		GlobalConfig global = new() { MotionLabel = "Counts as motion" };

		Resolver(ha, registry, global).TryResolve(
			new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedArea? area, out string? error);

		Assert.IsNotNull(area, error);
		Assert.AreEqual(0, area.MotionSensors.Count, "a label's name must never stand in for its id");
	}

	/// <summary>An unknown label value warns and names the setting; it never stops the house.</summary>
	[TestMethod]
	public void A_Label_Setting_That_Is_Not_A_Label_Id_Is_A_Warning_Naming_The_Setting()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Global.ExcludeLabel = "Never touch";

		ValidationResult result = ConfigValidator.Validate(
			config, new ValidationContext { LabelsInUse = ["other_label"], KnownLabelIds = ["other_label"] });

		Assert.IsTrue(result.IsValid, "an unknown label must never stop the house or a save");
		Assert.IsTrue(result.Warnings.Any(warning =>
				warning.Contains("Global.ExcludeLabel", StringComparison.Ordinal)
				&& warning.Contains("matches nothing", StringComparison.Ordinal)),
			"the warning has to name the setting and say the rule matches nothing");
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
