using AdaptiveLighting.Engine;
using AdaptiveLighting.Tests.Common;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The room page's Test button through its life: start, the countdown ending, and a stop.</summary>
// A second press is AreaControllerTests.A_Second_Test_Moves_The_Test_And_Leaves_One_Return_Outstanding.
// Read as a person sees it: what the lamp was told, and whether the room's report still names a test.
[TestClass]
public sealed class LevelTestButtonTests
{
	private static readonly TimeSpan Length = TimeSpan.FromSeconds(AreaController.LevelTestSeconds);

	[TestMethod]
	public void A_Press_Shows_The_Level_For_Five_Seconds_Then_Gives_The_Lights_Back()
	{
		AreaFixture room = new AreaTestBuilder().Build();

		Assert.IsNull(room.Area.TestPeriod("day"));
		Assert.IsTrue(room.Actuator.Last is { On: true, BrightnessPct: 90 });
		Assert.AreEqual(room.Scheduler.Now + TimeSpan.FromSeconds(5), room.Publisher.Snapshots[^1].TestEndsAt, "a five-second countdown");

		Advance(room, Length - TimeSpan.FromMilliseconds(100));
		Assert.IsTrue(room.Actuator.Last is { On: true }, "still showing the period just before the end");

		Advance(room, TimeSpan.FromMilliseconds(100));
		Assert.IsTrue(room.Actuator.Last is { On: false }, "an empty dark room goes back to dark");
		Assert.IsNull(room.Publisher.Snapshots[^1].TestingPeriodId, "the report stops naming the test when it ends");
	}

	[TestMethod]
	public void Stopping_A_Test_Gives_The_Lights_Back_At_Once_And_Nothing_Follows()
	{
		AreaFixture room = new AreaTestBuilder().Build();
		room.Area.TestPeriod("day");
		Advance(room, TimeSpan.FromSeconds(2));

		room.Area.EndTest();

		Assert.IsTrue(room.Actuator.Last is { On: false }, "the lights go back the moment the test is stopped");
		Assert.IsNull(room.Publisher.Snapshots[^1].TestingPeriodId, "and the countdown is gone");

		int commands = room.Actuator.Applied.Count;
		Advance(room, Length);
		Assert.AreEqual(commands, room.Actuator.Applied.Count, "the old deadline passing sends nothing more");
		Assert.IsFalse(room.Area.IsTestingLevels, "the test is not started again on its own");
	}

	private static void Advance(AreaFixture room, TimeSpan by) => room.Scheduler.AdvanceBy(by.Ticks);
}
