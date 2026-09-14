using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void Manual_Off_Suppresses_The_Area_And_Motion_Respects_It()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");

		t.Ha.Trigger(Light, "off", null, PhysicalDevice());
		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State);

		t.Actuator.Clear();
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State, "the human turned these lights off; motion does not undo that");
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void Manual_Off_During_PreOff_Also_Suppresses()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Motion, "off");
		Advance(t, TimeSpan.FromMinutes(10));
		Assert.AreEqual(AreaState.PreOff, t.Area.State);

		Advance(t, TimeSpan.FromSeconds(20));
		t.Ha.Trigger(Light, "off", null, PhysicalDevice());

		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State);

		t.Actuator.Clear();
		Advance(t, TimeSpan.FromSeconds(30));
		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State, "the pre-off timer must not fire into a suppressed area");
	}

	[TestMethod]
	public void Suppression_Lifts_After_VacancyResetMinutes_Of_No_Motion()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Light, "off", null, PhysicalDevice());

		Advance(t, TimeSpan.FromMinutes(9));
		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State);

		Advance(t, TimeSpan.FromMinutes(1));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State,
			"ten vacant minutes is ten vacant minutes — not ten plus the vacancy timeout");
	}

	[TestMethod]
	public void Motion_Restarts_The_Suppression_Reset_Timer()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Light, "off", null, PhysicalDevice());

		Advance(t, TimeSpan.FromMinutes(9));
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromMinutes(9));
		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State);

		Advance(t, TimeSpan.FromMinutes(2));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
	}
}
