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
		Assert.AreEqual(4, counters.Period);
	}

	[TestMethod]
	public void An_Id_Widens_Past_999_And_Earlier_Ids_Keep_Their_Shape()
	{
		IdCounters counters = new() { Room = 999 };

		Assert.AreEqual("room_1000", ReadableId.NextRoom(counters, []));
		Assert.AreEqual("room_007", ReadableId.Format(ReadableId.RoomPrefix, 7));
		Assert.AreEqual(1000, ReadableId.NumberOf(ReadableId.RoomPrefix, "room_1000"));
		Assert.IsNull(ReadableId.NumberOf(ReadableId.PeriodPrefix, "period_7"), "two digits is not the written shape");
	}
}
