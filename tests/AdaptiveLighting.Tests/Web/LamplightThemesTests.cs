using AdaptiveLighting.Lamplight;

namespace AdaptiveLighting.Tests.Web;

/// <summary>Pins the stored theme ids and the two defaults. An id is a storage key: changing one silently
/// resets every browser that had chosen it, so this is the one check that has to catch that.</summary>
[TestClass]
public sealed class LamplightThemesTests
{
	[TestMethod]
	public void Ids_And_Defaults_Are_Pinned()
	{
		CollectionAssert.AreEqual(
			new[] { "system", "warm-charcoal", "plum-dusk", "blackout", "0z0", "paper", "fern" },
			LamplightThemes.All.Select(theme => theme.Id).ToArray());

		Assert.AreSame(LamplightThemes.WarmCharcoal, LamplightThemes.DarkDefault);
		Assert.AreSame(LamplightThemes.Paper, LamplightThemes.LightDefault);
	}
}
