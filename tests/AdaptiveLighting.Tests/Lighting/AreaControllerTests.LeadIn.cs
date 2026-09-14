using AdaptiveLighting.Engine;

using Fixture = AdaptiveLighting.Tests.Common.AreaFixture;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void A_Lead_In_Lights_A_Dark_Room_Dimly_And_Movement_Inside_Raises_It()
	{
		Fixture t = Build(leadIn: [Steps], seed: ha => ha.SetState(Steps, "off"));

		t.Ha.Trigger(Steps, "on");

		Assert.AreEqual(AreaState.PreOff, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 35 }, "the lead-in lights at the dim light, half the evening's 70 %");
		Assert.IsTrue(t.Publisher.Snapshots[^1].IsLeadIn is true, "the snapshot must carry the lead-in, not just the reason of one publish");

		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 89 }, PhysicalDevice());
		Assert.AreEqual(AreaState.PreOff, t.Area.State, "the lead-in's own echo must not hold the hall at the dim light");

		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70 }, "movement inside raises the room to its own level");
		Assert.IsFalse(t.Publisher.Snapshots[^1].IsLeadIn is true, "leaving PreOff clears it, so the badge does not outlive the dim light it described");
	}

	[TestMethod]
	public void A_Lead_In_Nobody_Follows_Goes_Off_When_The_Dim_Light_Runs_Out()
	{
		Fixture t = Build(leadIn: [Steps], seed: ha => ha.SetState(Steps, "off"));

		t.Ha.Trigger(Steps, "on");
		Advance(t, TimeSpan.FromSeconds(20));
		t.Ha.Trigger(Steps, "off");
		t.Ha.Trigger(Steps, "on");

		Advance(t, TimeSpan.FromSeconds(29));
		Assert.AreEqual(AreaState.PreOff, t.Area.State, "a second lead-in starts the dim light's wait again");

		Advance(t, TimeSpan.FromSeconds(1));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false }, "nobody came in, so the lights go off");

		t.Actuator.Clear();
		Advance(t, TimeSpan.FromMinutes(15));
		Assert.AreEqual(0, t.Actuator.Applied.Count, "a lead-in alone starts no vacancy countdown that could command later");
	}

	[TestMethod]
	public void A_Lead_In_Is_Refused_By_A_Blocking_Entity()
	{
		Fixture t = Build(ignoreWhenOn: [Blocker], leadIn: [Steps], seed: ha =>
		{
			ha.SetState(Steps, "off");
			ha.SetState(Blocker, "on");
		});

		t.Ha.Trigger(Steps, "on");

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "the projector's block refuses the lead-in as it refuses movement");
	}
}
