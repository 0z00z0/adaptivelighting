using AdaptiveLighting.Engine;
using AdaptiveLighting.Tests.Common;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	// The trackers are published for a person to look at and decide nothing, so an empty house on its own is
	// still a house the engine manages.
	[TestMethod]
	public void A_House_The_Trackers_Call_Empty_Is_Not_Away()
	{
		AreaFixture t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		t.House.OnNext(House(home: false));

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "nothing was swept, because nothing said the house was away");
	}

	[TestMethod]
	public void The_House_Going_Away_Sweeps_The_Area_Off_And_Motion_Then_Does_Nothing()
	{
		AreaFixture t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		t.House.OnNext(AwayHouse());

		Assert.AreEqual(AreaState.Away, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false });

		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(AreaState.Away, t.Area.State);
	}

	[TestMethod]
	public void An_Area_With_SkipAwaySweep_Goes_Away_Without_Being_Swept()
	{
		AreaFixture t = Build(s => s.SkipAwaySweep = true);
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		t.House.OnNext(AwayHouse());

		Assert.AreEqual(AreaState.Away, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "outdoor and security lights opt out of the sweep");
	}

	[TestMethod]
	public void The_Sweep_Beats_An_Override()
	{
		AreaFixture t = Build();
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));   // clear the echo window of our own turn_on first
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);
		t.Actuator.Clear();

		t.House.OnNext(AwayHouse());

		Assert.AreEqual(AreaState.Away, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false }, "the house is set to away, so those levels are nobody's");
	}

	[TestMethod]
	public void The_Sweep_Reaches_A_Suppressed_Area_Too()
	{
		AreaFixture t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Light, "off", null, PhysicalDevice());

		t.House.OnNext(AwayHouse());

		Assert.AreEqual(AreaState.Away, t.Area.State);
	}
}
