using AdaptiveLighting.Configuration;
using AdaptiveLighting.LastSeen;

namespace AdaptiveLighting.Tests.LastSeen;

/// <summary>The cache files entities under the same rules the engine discovers them by.</summary>
/// <remarks>
///     A copied default here would file a house's sensors one way while the engine finds them another, and no test
///     or log would report the drift. These assertions are the only thing that notices.
/// </remarks>
[TestClass]
public sealed class LastSeenOptionsTests
{
	[TestMethod]
	public void The_Motion_Label_Is_The_Engines_Default()
	{
		LastSeenOptions options = new();
		AdaptiveLightingConfig config = new();

		Assert.AreEqual(GlobalConfig.DefaultMotionLabel, options.MotionLabel);
		Assert.AreEqual(config.Global.MotionLabel, options.MotionLabel);
	}

	[TestMethod]
	public void The_Motion_Device_Classes_Are_The_Engines_Defaults()
	{
		LastSeenOptions options = new();
		AdaptiveLightingConfig config = new();

		CollectionAssert.AreEqual(
			GlobalConfig.DefaultMotionDeviceClasses.ToList(),
			options.MotionDeviceClasses.ToList());

		CollectionAssert.AreEqual(
			config.Global.EffectiveMotionDeviceClasses.ToList(),
			options.MotionDeviceClasses.ToList());
	}

	[TestMethod]
	public void The_Illuminance_Device_Class_Is_The_Engines_Default()
	{
		LastSeenOptions options = new();
		AdaptiveLightingConfig config = new();

		Assert.AreEqual(GlobalConfig.DefaultIlluminanceDeviceClass, options.IlluminanceDeviceClass);
		Assert.AreEqual(config.Global.IlluminanceDeviceClass, options.IlluminanceDeviceClass);
	}
}
