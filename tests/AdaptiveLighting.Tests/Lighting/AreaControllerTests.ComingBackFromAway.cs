using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Tests.Common;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void Returning_From_Away_Adopts_Lights_The_Sweep_Left_On()
	{
		// The room opted out of the leaving sweep, so it is still lit when the house comes back.
		AreaFixture t = Build(s => s.SkipAwaySweep = true, seed: ha =>
		{
			ha.SetState(Light, "on", new() { ["brightness"] = 178.5 });
			ha.SetState(Lux, "5");
		});

		t.House.OnNext(AwayHouse());
		Assert.AreEqual(AreaState.Away, t.Area.State);
		t.Actuator.Clear();

		t.House.OnNext(House());

		Assert.AreEqual(AreaState.AutoActive, t.Area.State,
			"a room that is still lit is taken charge of, so something is armed to switch it off");
		Assert.AreEqual(0, t.Actuator.Applied.Count, "adoption observes; it commands nothing");

		Advance(t, TimeSpan.FromSeconds(600 + 30 + 1));

		Assert.IsTrue(t.Actuator.Last is { On: false }, "and the vacancy timeout ends it, as it would in any other room");
	}

	[TestMethod]
	public void Leaving_A_Guest_Scene_Adopts_Lights_The_Scene_Left_On()
	{
		AreaFixture t = Build(seed: ha =>
		{
			ha.SetState(Light, "on", new() { ["brightness"] = 178.5 });
			ha.SetState(Lux, "5");
		});

		t.House.OnNext(House(kind: ModeKind.Guest, modeValue: "Gjester", scene: "scene.gjest"));
		Assert.AreEqual(AreaState.SceneHold, t.Area.State);
		t.Actuator.Clear();

		t.House.OnNext(House());

		Assert.AreEqual(AreaState.AutoActive, t.Area.State,
			"the scene left the room lit, and nothing else would ever switch it off");
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}
}
