using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The denominator of "n of m rooms are running": an area switched off was never a candidate.</summary>
[TestClass]
public sealed class ManagedAreaCountTests
{
	private static AdaptiveLightingConfig House(params bool?[] enabled) => new()
	{
		Defaults = new AreaSettings(),
		Areas = [.. enabled.Select((flag, index) => new AreaConfig { AreaId = $"area{index}", Enabled = flag })]
	};

	[TestMethod]
	public void An_Area_Switched_Off_Is_Not_Counted_As_One_That_Should_Be_Running()
	{
		Assert.AreEqual(2, House(true, false, true).ManagedAreaCount);
	}

	/// <summary>Saying nothing means running, which is what every document written before the flag says.</summary>
	[TestMethod]
	public void An_Area_That_Says_Nothing_Counts()
	{
		Assert.AreEqual(3, House(null, null, true).ManagedAreaCount);
	}

	/// <summary>The per-area flag is nullable and falls through to Defaults, so the count has to read the effective value.</summary>
	[TestMethod]
	public void The_House_Default_Decides_When_An_Area_Says_Nothing()
	{
		AdaptiveLightingConfig config = House(null, null);
		config.Defaults.Enabled = false;

		Assert.AreEqual(0, config.ManagedAreaCount, "everything is switched off at the house level");

		config.Areas[0].Enabled = true;

		Assert.AreEqual(1, config.ManagedAreaCount, "and an area may still opt back in");
	}

	[TestMethod]
	public void A_House_With_No_Areas_Counts_Nothing()
	{
		Assert.AreEqual(0, new AdaptiveLightingConfig().ManagedAreaCount);
	}
}
