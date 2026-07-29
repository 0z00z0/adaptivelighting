using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     Which period is in force, asked by the schedule editor and by the room page's levels table.
/// </summary>
/// <remarks>
///     Both surfaces mark the running period, and the answer is not the list's first or last entry: a sun-anchored
///     boundary moves through the year, and before the day's first boundary the period in force is yesterday's.
/// </remarks>
[TestClass]
public sealed class ScheduleTests
{
	private static readonly SunTimes Sun = new(new TimeOnly(4, 30), new TimeOnly(21, 45));

	private static List<TimePeriodConfig> Day() =>
	[
		new TimePeriodConfig { Name = "morgen", Start = "06:30" },
		new TimePeriodConfig { Name = "dag", Start = "09:00" },
		new TimePeriodConfig { Name = "kveld", Start = "sunset-01:00" },
		new TimePeriodConfig { Name = "natt", Start = "23:00" }
	];

	/// <summary>The period in force is the most recent start at or before now.</summary>
	[TestMethod]
	public void The_Period_In_Force_Is_The_Latest_Start_Already_Passed()
	{
		Assert.AreEqual("morgen", Schedule.InForceAt(Day(), Sun, new TimeOnly(7, 0))?.Name);
		Assert.AreEqual("dag", Schedule.InForceAt(Day(), Sun, new TimeOnly(12, 0))?.Name);
		Assert.AreEqual("natt", Schedule.InForceAt(Day(), Sun, new TimeOnly(23, 30))?.Name);
	}

	/// <summary>Before the day's first boundary, the period in force began yesterday.</summary>
	[TestMethod]
	public void The_Small_Hours_Belong_To_Yesterdays_Last_Period()
	{
		Assert.AreEqual("natt", Schedule.InForceAt(Day(), Sun, new TimeOnly(3, 0))?.Name);
	}

	/// <summary>
	///     A sun-anchored start is resolved, so the running order can differ from the list order.
	/// </summary>
	/// <remarks>
	///     With sunset at 21:45 the evening starts at 20:45; in December the same string starts it before three in
	///     the afternoon, and a surface reading the list order would badge the wrong row half the year.
	/// </remarks>
	[TestMethod]
	public void A_Sun_Anchored_Start_Is_Resolved_Rather_Than_Assumed()
	{
		Assert.AreEqual("dag", Schedule.InForceAt(Day(), Sun, new TimeOnly(20, 0))?.Name);
		Assert.AreEqual("kveld", Schedule.InForceAt(Day(), Sun, new TimeOnly(21, 0))?.Name);

		SunTimes winter = new(new TimeOnly(9, 20), new TimeOnly(15, 10));

		Assert.AreEqual("kveld", Schedule.InForceAt(Day(), winter, new TimeOnly(15, 0))?.Name);
	}

	/// <summary>A period the engine cannot place is never "now", because the engine cannot run it either.</summary>
	[TestMethod]
	public void An_Unplaceable_Period_Is_Never_In_Force()
	{
		List<TimePeriodConfig> broken =
		[
			new TimePeriodConfig { Name = "dag", Start = "09:00" },
			new TimePeriodConfig { Name = "tull", Start = "not a time" }
		];

		Assert.AreEqual("dag", Schedule.InForceAt(broken, Sun, new TimeOnly(23, 0))?.Name);

		// Polar night: the sun-anchored boundary has nowhere to sit, so only the clock period resolves.
		List<TimePeriodConfig> sunOnly = [new TimePeriodConfig { Name = "kveld", Start = "sunset" }];

		Assert.IsNull(Schedule.InForceAt(sunOnly, SunTimes.Unknown, new TimeOnly(12, 0)));
	}

	/// <summary>An empty schedule has no period in force, rather than a first row by default.</summary>
	[TestMethod]
	public void An_Empty_Schedule_Has_No_Period_In_Force()
	{
		Assert.IsNull(Schedule.InForceAt([], Sun, new TimeOnly(12, 0)));
	}
}
