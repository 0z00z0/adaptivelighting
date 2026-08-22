namespace AdaptiveLighting.Tests.Extensions;

[TestClass]
public sealed class NameExtensionsTests
{
	[TestMethod]
	public void SameName_Ignores_Case_And_Surrounding_Whitespace()
	{
		Assert.IsTrue("Kveld".SameName("kveld"));
		Assert.IsTrue("  Kveld  ".SameName("Kveld"));
		Assert.IsTrue("\tKveld\r\n".SameName(" kVELd "), "Trim takes every whitespace character, not only the space");
		Assert.IsFalse("Kveld".SameName("Kveld2"));
	}

	[TestMethod]
	public void SameName_Compares_Ordinally()
	{
		// Turkish dotless i: culture-sensitive comparison folds these together, ordinal does not.
		Assert.IsFalse("ID".SameName("ıd"));
		Assert.IsTrue("ID".SameName("id"));
	}

	[TestMethod]
	public void SameName_MatchesTwoNulls_AndNothingElseAgainstNull()
	{
		string? nothing = null;

		Assert.IsTrue(nothing.SameName(null));
		Assert.IsFalse(nothing.SameName("Kveld"));
		Assert.IsFalse("Kveld".SameName(null));
	}

	[TestMethod]
	public void SameName_TreatsBlankAndNullAsDifferent()
	{
		string? nothing = null;

		Assert.IsFalse(nothing.SameName(""));
		Assert.IsTrue("".SameName("   "), "both sides trim to the same empty string");
	}
}
