using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Fixture = AdaptiveLighting.Tests.Common.AreaFixture;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void Motion_Lights_The_Area_At_The_Rooms_Own_Level_Where_It_Has_One()
	{
		// The fixture's clock stands at 20:00, in evening, which the house runs at 70 % / 2700 K.
		Fixture t = Build(levels: [new RoomLevelOverride { PeriodId = "evening", BrightnessPct = 25 }]);

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 25, ColorTempKelvin: 2700 },
			"the room's brightness, and the schedule's colour it never said anything about");
	}

	[TestMethod]
	public void The_Snapshot_Says_Which_Levels_This_Room_Names_For_Itself()
	{
		Fixture t = Build(levels: [new RoomLevelOverride { PeriodId = "evening", BrightnessPct = 25 }]);

		AreaSnapshot latest = t.Publisher.Snapshots[^1];

		Assert.AreEqual("evening", latest.PeriodName);
		Assert.AreEqual(RoomLevelSource.Brightness, latest.LevelsFromRoom,
			"a statement about the period, so it holds before the engine has commanded anything");
	}

	[TestMethod]
	public void A_Room_With_No_Levels_Publishes_None_Rather_Than_Nothing()
	{
		Fixture t = Build();

		Assert.AreEqual(RoomLevelSource.None, t.Publisher.Snapshots[^1].LevelsFromRoom,
			"null is reserved for a build that predates the field; a running engine always has an answer");
	}

	[TestMethod]
	public void The_Snapshot_Flag_Follows_The_Period_Across_A_Boundary()
	{
		Fixture t = Build(levels: [new RoomLevelOverride { PeriodId = "evening", BrightnessPct = 25 }]);

		Assert.AreEqual(RoomLevelSource.Brightness, t.Publisher.Snapshots[^1].LevelsFromRoom);

		// 20:00 + 2h35m is 22:35, five minutes into night, which this room does not override.
		Advance(t, TimeSpan.FromMinutes(155));

		AreaSnapshot latest = t.Publisher.Snapshots[^1];

		Assert.AreEqual("night", latest.PeriodName);
		Assert.AreEqual(RoomLevelSource.None, latest.LevelsFromRoom);
	}
}
