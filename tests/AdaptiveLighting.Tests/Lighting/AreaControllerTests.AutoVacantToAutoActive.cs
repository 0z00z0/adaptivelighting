using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Tests.Common;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void Motion_When_Dark_Turns_The_Area_On_At_The_Periods_Levels()
	{
		AreaFixture t = Build();

		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70, ColorTempKelvin: 2700 });
	}

	[TestMethod]
	public void Motion_When_Not_Dark_Is_Logged_But_Not_Acted_On()
	{
		AreaFixture t = Build();
		t.Ha.SetState(Lux, "5000");

		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void Auto_On_Is_Blocked_While_An_IgnoreWhenOn_Entity_Is_On()
	{
		AreaFixture t = Build(s => s.Darkness = DarknessSource.Always, ignoreWhenOn: [Blocker]);
		t.Ha.SetState(Blocker, "on");

		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count);

		t.Ha.SetState(Blocker, "off");
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
	}
}
