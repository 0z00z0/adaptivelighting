using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The computed views on <see cref="GlobalConfig"/>: the effective kill switch and the effective motion device classes.</summary>
[TestClass]
public sealed class GlobalConfigTests
{
	// A household that wants the old tracker-only behaviour writes zero by hand, and every ordinary save from the
	// app must leave it standing. Nothing in the UI edits this yet, so the round trip is the whole guarantee.
	[TestMethod]
	public void MotionPresenceMinutes_Survives_A_Save_And_A_Reload()
	{
		AdaptiveLightingConfig config = new()
		{
			Global = new GlobalConfig { MotionPresenceMinutes = 0 },
			Periods = [new TimePeriodConfig { Name = "day", Start = "07:00" }]
		};

		AdaptiveLightingConfig reloaded = LightingConfigDocument
			.Deserialize(LightingConfigDocument.Serialize(config))
			.Config;

		Assert.AreEqual(0, reloaded.Global.MotionPresenceMinutes);
	}

	[TestMethod]
	public void A_Document_That_Never_Heard_Of_MotionPresenceMinutes_Gets_The_Default()
	{
		AdaptiveLightingConfig reloaded = LightingConfigDocument
			.Deserialize("AdaptiveLighting.Configuration.AdaptiveLightingConfig:\n  Global:\n    AwayDebounceMinutes: 5\n")
			.Config;

		Assert.AreEqual(30, reloaded.Global.MotionPresenceMinutes,
			"the setting is additive: an older file is silence, and silence is the default");
	}

	[TestMethod]
	public void EffectiveKillSwitchEntity_PrefersExplicit_ElseDefault_ElseNull()
	{
		Assert.IsNull(new GlobalConfig().EffectiveKillSwitchEntity, "unset with no default resolves to nothing");

		var defaulted = new GlobalConfig { DefaultKillSwitchEntity = "input_boolean.builtin" };
		Assert.AreEqual("input_boolean.builtin", defaulted.EffectiveKillSwitchEntity, "the defaulted built-in fills in");

		var explicitEntity = new GlobalConfig
		{
			KillSwitchEntity = "switch.explicit",
			DefaultKillSwitchEntity = "input_boolean.builtin"
		};
		Assert.AreEqual("switch.explicit", explicitEntity.EffectiveKillSwitchEntity, "an explicit entity wins over the default");
	}

	[TestMethod]
	public void An_Empty_KillSwitchEntity_Falls_Back_To_The_Default()
	{
		var empty = new GlobalConfig { KillSwitchEntity = "", DefaultKillSwitchEntity = "input_boolean.builtin" };
		Assert.AreEqual("input_boolean.builtin", empty.EffectiveKillSwitchEntity, "\"\" is absent, not a chosen entity");
	}

	[TestMethod]
	public void EffectiveMotionDeviceClasses_UsesTheConfiguredListWhenPresent()
	{
		var configured = new GlobalConfig { MotionDeviceClasses = ["motion", "vibration"] };
		CollectionAssert.AreEqual(new[] { "motion", "vibration" }, configured.EffectiveMotionDeviceClasses.ToList());
	}

	[TestMethod]
	public void EffectiveMotionDeviceClasses_FallsBackToTheBuiltInSetWhenEmpty()
	{
		var empty = new GlobalConfig { MotionDeviceClasses = [] };
		CollectionAssert.AreEqual(GlobalConfig.DefaultMotionDeviceClasses.ToList(), empty.EffectiveMotionDeviceClasses.ToList());
	}
}
