using AdaptiveLighting.Engine;
using AdaptiveLighting.Tests.Common;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void Vacancy_Dims_To_PreOff_And_Then_Turns_Off()
	{
		AreaFixture t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Motion, "off");
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(9));
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "the vacancy timeout must not fire early");

		Advance(t, TimeSpan.FromMinutes(1));
		Assert.AreEqual(AreaState.PreOff, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 35 }, "PreOff dims to half the period's brightness");

		Advance(t, TimeSpan.FromSeconds(30));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false });
	}

	[TestMethod]
	public void Motion_During_The_PreOff_Grace_Restores_The_Area()
	{
		AreaFixture t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Motion, "off");
		Advance(t, TimeSpan.FromMinutes(10));
		t.Actuator.Clear();

		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70 }, "the full levels come back, not the dim");

		Advance(t, TimeSpan.FromSeconds(60));
		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "the grace timer must have been cancelled, not merely outrun");
	}

	[TestMethod]
	public void Motion_Restarts_The_Vacancy_Timer()
	{
		AreaFixture t = Build();
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromMinutes(9));

		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Motion, "off");
		Advance(t, TimeSpan.FromMinutes(9));
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);

		Advance(t, TimeSpan.FromMinutes(1));
		Assert.AreEqual(AreaState.PreOff, t.Area.State, "restarting is not cancelling: the timer still fires eventually");
	}
}
