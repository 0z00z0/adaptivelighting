using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Tests.Lighting;

[TestClass]
public sealed class PeriodExtensionsTests
{
	private static List<TimePeriodConfig> Periods() =>
	[
		new() { Id = "period_001", Name = "Morgen", Start = "06:30" },
		new() { Name = "Kveld", Start = "18:00" },
		new() { Id = "period_003", Name = "Natt", Start = "23:00" }
	];

	[TestMethod]
	public void ByKey_MatchesTheId()
	{
		Assert.AreEqual("Morgen", Periods().ByKey("period_001")?.Name);
		Assert.AreEqual("Morgen", Periods().ByKey("PERIOD_001")?.Name, "keys match case-insensitively");
		Assert.AreEqual("Morgen", Periods().ByKey("  period_001  ")?.Name, "a stray space is not a different period");
	}

	[TestMethod]
	public void ByKey_NeverMatchesADisplayName_WithOrWithoutAnId()
	{
		Assert.IsNull(Periods().ByKey("Natt"), "a period with an id is not found by its name");
		Assert.IsNull(Periods().ByKey("Kveld"), "nor is one without an id");
	}

	[TestMethod]
	public void ByKey_UnknownEmptyAndNull_AreNothing()
	{
		Assert.IsNull(Periods().ByKey("siesta"));
		Assert.IsNull(Periods().ByKey(null));
		Assert.IsNull(Periods().ByKey(""), "a period without an id is not found by an empty key");
		Assert.IsNull(new List<TimePeriodConfig>().ByKey("period_001"), "an empty schedule resolves nothing");
	}

	[TestMethod]
	public void ByKey_RefusesANullSchedule()
	{
		List<TimePeriodConfig>? none = null;

		Assert.ThrowsException<ArgumentNullException>(() => none!.ByKey("period_001"));
	}
}
