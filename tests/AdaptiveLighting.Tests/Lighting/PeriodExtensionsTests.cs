using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Tests.Lighting;

[TestClass]
public sealed class PeriodExtensionsTests
{
	private static List<TimePeriodConfig> Periods() =>
	[
		new() { Id = "morning", Name = "Morgen", Start = "06:30" },
		new() { Name = "Kveld", Start = "18:00" },
		new() { Id = "night", Name = "Natt", Start = "23:00" }
	];

	[TestMethod]
	public void ByKey_MatchesTheId_WhenTheDocumentHasOne()
	{
		Assert.AreEqual("Morgen", Periods().ByKey("morning")?.Name);
		Assert.AreEqual("Morgen", Periods().ByKey("MORNING")?.Name, "keys match case-insensitively");
		Assert.AreEqual("Morgen", Periods().ByKey("  morning  ")?.Name, "a stray space is not a different period");
	}

	[TestMethod]
	public void ByKey_FallsBackToTheDisplayName_WhenThereIsNoId()
	{
		Assert.AreEqual("Kveld", Periods().ByKey("Kveld")?.Name);
		Assert.IsNull(Periods().ByKey("evening"), "an id it never had names nothing");
	}

	[TestMethod]
	public void ByKey_UnknownAndNull_AreNothing()
	{
		Assert.IsNull(Periods().ByKey("siesta"));
		Assert.IsNull(Periods().ByKey(null));
		Assert.IsNull(new List<TimePeriodConfig>().ByKey("morning"), "an empty schedule resolves nothing");
	}

	[TestMethod]
	public void ByKey_DoesNotMatchTheNameOfAPeriodThatHasAnId()
	{
		Assert.IsNull(Periods().ByKey("Natt"), "night carries an id, so its display name is not its key");
		Assert.AreEqual("night", Periods().ByName("Natt")?.Key, "and ByName is what still finds it");
	}

	[TestMethod]
	public void ByName_MatchesTheDisplayName_CaseInsensitivelyAndTrimmed()
	{
		Assert.AreEqual("morning", Periods().ByName("morgen")?.Key);
		Assert.AreEqual("morning", Periods().ByName(" Morgen ")?.Key);
		Assert.IsNull(Periods().ByName("morning"), "the id is not a display name");
		Assert.IsNull(Periods().ByName(null));
	}

	[TestMethod]
	public void BothRefuseANullSchedule()
	{
		List<TimePeriodConfig>? none = null;

		Assert.ThrowsException<ArgumentNullException>(() => none!.ByKey("morning"));
		Assert.ThrowsException<ArgumentNullException>(() => none!.ByName("Morgen"));
	}
}
