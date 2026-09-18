using AdaptiveLighting.Engine;

using Fixture = AdaptiveLighting.Tests.Common.AreaFixture;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void A_Room_With_No_Motion_Sensor_Is_Still_Published()
	{
		Fixture t = Build(withMotionSensor: false);

		Assert.IsTrue(t.Publisher.Snapshots.Count > 0, "the room must reach the interface, not vanish from it");
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
	}

	// The two failure modes a hold judged on movement can have where no movement is ever reported: off almost at
	// once because the room reads as instantly vacant, and never off because it never does.
	[TestMethod]
	public void A_Movement_Led_Hold_With_No_Motion_Sensor_Runs_The_Whole_Vacancy_Timeout()
	{
		Fixture t = Build(
			s =>
			{
				s.OverrideUntilVacant = true;
				s.VacancyTimeoutSeconds = 300;
			},
			withMotionSensor: false);

		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(1));
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State,
			"a room that never reports movement must not read as instantly vacant");
		Assert.AreEqual(0, t.Actuator.Applied.Count);

		Advance(t, TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(59));
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State, "the hold must not end before the vacancy timeout");

		Advance(t, TimeSpan.FromSeconds(1));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State, "with nothing to restart it the hold ends on its own");
		Assert.IsTrue(t.Actuator.Last is { On: false }, "and the room settles off, the same as any other empty room");
	}

	[TestMethod]
	public void A_Fixed_Hold_With_No_Motion_Sensor_Ends_On_Its_Own_Clock()
	{
		Fixture t = Build(s => s.OverrideDurationMinutes = 5, withMotionSensor: false);

		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		Advance(t, TimeSpan.FromMinutes(4));
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);

		Advance(t, TimeSpan.FromMinutes(1));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false });
	}

	// The vacancy timer is what a sensor-less room runs on once it is adopted rather than held, and that is the
	// path the pre-off warning hangs off.
	[TestMethod]
	public void A_Lit_Room_With_No_Motion_Sensor_Is_Adopted_Then_Dimmed_And_Switched_Off()
	{
		Fixture t = Build(
			seed: ha => ha.SetState(Light, "on", new() { ["brightness"] = 178.5 }),
			withMotionSensor: false);

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(10));
		Assert.AreEqual(AreaState.PreOff, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 35 });

		Advance(t, TimeSpan.FromSeconds(30));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false });
	}

	[TestMethod]
	public void Manual_On_From_AutoVacant_Also_Overrides()
	{
		Fixture t = Build();

		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());

		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);
	}
}
