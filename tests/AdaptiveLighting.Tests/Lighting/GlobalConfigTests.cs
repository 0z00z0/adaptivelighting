using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The computed views on <see cref="GlobalConfig"/>: the effective kill switch (explicit wins over the
///     defaulted built-in) and the effective motion device classes (the configured list, or the built-in set
///     when nothing was configured).
/// </summary>
[TestClass]
public sealed class GlobalConfigTests
{
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
