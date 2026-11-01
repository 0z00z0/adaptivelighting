namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The running number behind <c>period_001</c> and <c>room_001</c>: never reissued, never reshaped.</summary>
[TestClass]
public sealed class ReadableIdTests
{
	[TestMethod]
	public void A_Deleted_Newest_Period_Number_Is_Never_Issued_Again()
	{
		IdCounters counters = new();
		List<TimePeriodConfig> periods = [];

		for (int i = 0; i < 3; i++)
			periods.Add(new TimePeriodConfig { Id = ReadableId.NextPeriod(counters, periods), Name = $"p{i}" });

		periods.RemoveAt(2);

		Assert.AreEqual("period_004", ReadableId.NextPeriod(counters, periods));
		Assert.AreEqual(4, counters.Periods);
	}

	[TestMethod]
	public void An_Id_Widens_Past_999_And_Earlier_Ids_Keep_Their_Shape()
	{
		IdCounters counters = new() { Rooms = 999 };

		Assert.AreEqual("room_1000", ReadableId.NextRoom(counters, []));
		Assert.AreEqual("room_007", ReadableId.Format(ReadableId.RoomPrefix, 7));
		Assert.AreEqual(1000, ReadableId.NumberOf(ReadableId.RoomPrefix, "room_1000"));
		Assert.IsNull(ReadableId.NumberOf(ReadableId.PeriodPrefix, "period_7"), "two digits is not the written shape");
	}

	[TestMethod]
	public void A_Save_Issues_A_Missing_Room_Id_And_A_Missing_Period_Id()
	{
		AdaptiveLightingConfig config = new()
		{
			Periods = [new TimePeriodConfig { Id = "period_002", Name = "day" }, new TimePeriodConfig { Name = "night" }],
			Areas = [new AreaConfig { Name = "Shed" }, new AreaConfig { Name = "Hall", AreaId = "hall" }]
		};

		ConfigNormalizer.Normalize(config);

		Assert.AreEqual("period_003", config.Periods[1].Id);
		Assert.AreEqual("room_001", config.Areas[0].Key, "a room with neither an area nor an id has no key");
		Assert.IsNull(config.Areas[1].Id, "a room with an area is known by it");
	}
}
