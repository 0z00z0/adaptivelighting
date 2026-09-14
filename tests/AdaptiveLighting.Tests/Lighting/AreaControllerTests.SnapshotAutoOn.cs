using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	// Two of the refusals leave the area in AutoVacant, the same state as one waiting for someone to walk in,
	// so the snapshot has to carry the gate as well.
	[TestMethod]
	public void A_Sleeping_House_Is_Published_As_The_Reason_Auto_On_Would_Refuse()
	{
		Fixture t = Build(s => s.SleepBlocksAutoOn = true);

		t.House.OnNext(House(kind: ModeKind.Sleep));
		Advance(t, TimeSpan.FromMinutes(1));

		AreaSnapshot latest = t.Publisher.Snapshots[^1];

		Assert.AreEqual(AreaState.AutoVacant, latest.State, "the refusal is invisible in the state, which is the point");
		Assert.AreEqual(true, latest.IsDark, "and invisible in the darkness verdict too");
		Assert.AreEqual(AutoOnBlock.Sleep, latest.AutoOnBlockedBy);
		Assert.IsNull(latest.AutoOnBlockingEntity, "no entity is holding this one off");
	}

	[TestMethod]
	public void A_Blocking_Entity_Is_Published_By_Name()
	{
		Fixture t = Build(ignoreWhenOn: [Blocker]);

		t.Ha.SetState(Lux, "5000");
		Advance(t, TimeSpan.FromMinutes(1));

		t.Ha.SetState(Blocker, "on");
		t.Ha.SetState(Lux, "5");
		Advance(t, TimeSpan.FromMinutes(1));

		AreaSnapshot dusk = t.Publisher.Snapshots[^1];

		Assert.AreEqual(AreaState.AutoVacant, dusk.State);
		Assert.AreEqual(true, dusk.IsDark, "dusk: the verdict just flipped, which is why this report exists");
		Assert.AreEqual(AutoOnBlock.EntityOn, dusk.AutoOnBlockedBy);
		Assert.AreEqual(Blocker, dusk.AutoOnBlockingEntity);
	}

	[TestMethod]
	public void An_Area_With_Nothing_In_The_Way_Publishes_An_Open_Gate()
	{
		Fixture t = Build();

		Assert.AreEqual(AutoOnBlock.None, t.Publisher.Snapshots[0].AutoOnBlockedBy);
	}

	// The snapshot reads the gate the engine consults, not a second copy of its rules.
	[TestMethod]
	public void What_The_Snapshot_Reports_Is_What_Motion_Actually_Does()
	{
		Fixture t = Build(s => s.SleepBlocksAutoOn = true);
		t.House.OnNext(House(kind: ModeKind.Sleep));
		Advance(t, TimeSpan.FromMinutes(1));

		Assert.AreNotEqual(AutoOnBlock.None, t.Publisher.Snapshots[^1].AutoOnBlockedBy);

		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State, "which is exactly what the report promised");
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}
}
