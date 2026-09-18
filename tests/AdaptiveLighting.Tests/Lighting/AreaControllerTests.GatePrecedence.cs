using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Fixture = AdaptiveLighting.Tests.Common.AreaFixture;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	// Two gates closed at once: the activity row names whichever the ladder asks first. Every other refusal test
	// closes one gate, so these are what pin the order of the rungs in HouseGates.FirstClosed.

	[TestMethod]
	public void Movement_In_A_Disabled_Room_Under_The_Master_Switch_Is_Refused_By_The_Master_Switch()
	{
		Fixture t = Build(s => s.Enabled = false);
		t.House.OnNext(House(killed: true));

		Assert.AreEqual(AutoOnBlock.KillSwitch, DeclinedMovementReason(t));
	}

	[TestMethod]
	public void Movement_In_A_Disabled_Room_While_The_House_Is_Away_Is_Refused_By_The_Room_Being_Off()
	{
		Fixture t = Build(s => s.Enabled = false);
		t.House.OnNext(AwayHouse());

		Assert.AreEqual(AutoOnBlock.Disabled, DeclinedMovementReason(t));
	}

	[TestMethod]
	public void Movement_In_A_Disabled_Room_Under_A_Guest_Scene_Is_Refused_By_The_Room_Being_Off()
	{
		Fixture t = Build(s => s.Enabled = false);
		t.House.OnNext(House(kind: ModeKind.Guest, modeValue: "Guests", scene: "scene.guests"));

		Assert.AreEqual(AutoOnBlock.Disabled, DeclinedMovementReason(t));
	}

	/// <summary>Walks into the room and returns the gate its one refusal row names.</summary>
	private static AutoOnBlock? DeclinedMovementReason(Fixture t)
	{
		t.Publisher.Snapshots.Clear();

		t.Ha.Trigger(Motion, "on");

		AreaSnapshot[] declined = [.. t.Publisher.Snapshots.Where(s => s.Reason == TransitionReason.Motion)];

		Assert.AreEqual(1, declined.Length, "one movement, one refusal row");
		Assert.AreEqual(0, t.Actuator.Applied.Count, "and no light");

		return declined[0].AutoOnBlockedBy;
	}
}
