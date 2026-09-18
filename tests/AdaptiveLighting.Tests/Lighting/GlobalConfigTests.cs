using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The computed views on <see cref="GlobalConfig"/>: the effective kill switch and the effective motion device classes.</summary>
[TestClass]
public sealed class GlobalConfigTests
{
	[TestMethod]
	public void EffectiveKillSwitchEntity_PrefersExplicit_ElseDefault_ElseNull()
	{
		Assert.IsNull(new GlobalConfig().EffectiveKillSwitchEntity(null), "unset with no default resolves to nothing");

		GlobalConfig defaulted = new GlobalConfig();
		Assert.AreEqual("input_boolean.builtin", defaulted.EffectiveKillSwitchEntity("input_boolean.builtin"), "the defaulted built-in fills in");

		GlobalConfig explicitEntity = new GlobalConfig
		{
			KillSwitchEntity = "switch.explicit"
		};
		Assert.AreEqual("switch.explicit", explicitEntity.EffectiveKillSwitchEntity("input_boolean.builtin"), "an explicit entity wins over the default");
	}

	[TestMethod]
	public void An_Empty_KillSwitchEntity_Falls_Back_To_The_Default()
	{
		GlobalConfig empty = new GlobalConfig { KillSwitchEntity = "" };
		Assert.AreEqual("input_boolean.builtin", empty.EffectiveKillSwitchEntity("input_boolean.builtin"), "\"\" is absent, not a chosen entity");
	}

	[TestMethod]
	public void EffectiveMotionDeviceClasses_UsesTheConfiguredListWhenPresent()
	{
		GlobalConfig configured = new GlobalConfig { MotionDeviceClasses = ["motion", "vibration"] };
		CollectionAssert.AreEqual(new[] { "motion", "vibration" }, configured.EffectiveMotionDeviceClasses.ToList());
	}

	[TestMethod]
	public void EffectiveMotionDeviceClasses_FallsBackToTheBuiltInSetWhenEmpty()
	{
		GlobalConfig empty = new GlobalConfig { MotionDeviceClasses = [] };
		CollectionAssert.AreEqual(GlobalConfig.DefaultMotionDeviceClasses.ToList(), empty.EffectiveMotionDeviceClasses.ToList());
	}
}
