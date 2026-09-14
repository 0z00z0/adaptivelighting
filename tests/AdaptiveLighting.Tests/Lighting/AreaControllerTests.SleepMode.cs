using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void SleepBlocksAutoOn_Stops_The_Area_Lighting_At_All()
	{
		var t = Build(s => s.SleepBlocksAutoOn = true);
		t.House.OnNext(House(kind: ModeKind.Sleep));

		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void RespectSleepMode_Holds_The_Evening_Target_To_The_Night_Level()
	{
		// Sover is Sleep-kind with no ClampPeriodId, so the clamp falls back to the period named "night".
		var t = Build(s => s.RespectSleepMode = true, g => g.HouseMode = SoverMode());
		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 15 },
			"20:00 says 70%, but a sleeping house is held to night's own 15% whatever the clock says");
	}

	[TestMethod]
	public void Sleep_Mode_Turning_On_Retargets_An_Active_Area()
	{
		var t = Build(s => s.RespectSleepMode = true, g => g.HouseMode = SoverMode());
		t.Ha.Trigger(Motion, "on");
		Assert.IsTrue(t.Actuator.Last is { BrightnessPct: 70 });
		t.Actuator.Clear();

		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 15 },
			"the sleeping house is held to the night period's own level");
	}
}
