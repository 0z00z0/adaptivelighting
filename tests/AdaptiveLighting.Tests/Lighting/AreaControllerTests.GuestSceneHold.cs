using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	private static HouseModeConfig GuestSceneMode()
	{
		var mode = SoverMode();
		mode.Options.Add(new HouseModeOptionConfig { Value = "Gjester", Kind = ModeKind.Guest, Scene = "scene.gjest" });
		return mode;
	}

	[TestMethod]
	public void Guest_WithAScene_HoldsTheArea_AndIgnoresMotionForCommanding()
	{
		var t = Build(tweakGlobal: g => g.HouseMode = GuestSceneMode());
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		t.House.OnNext(House(kind: ModeKind.Guest, modeValue: "Gjester", scene: "scene.gjest"));

		Assert.AreEqual(AreaState.SceneHold, t.Area.State, "a guest scene holds the area");
		Assert.AreEqual(0, t.Actuator.Applied.Count, "the scene is the look; the area commands nothing");

		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(AreaState.SceneHold, t.Area.State, "motion is recorded but does not command out of the hold");
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void Guest_SceneResetToNormal_ExitsSceneHoldToAutoVacant()
	{
		var t = Build(tweakGlobal: g => g.HouseMode = GuestSceneMode());
		t.House.OnNext(House(kind: ModeKind.Guest, modeValue: "Gjester", scene: "scene.gjest"));
		Assert.AreEqual(AreaState.SceneHold, t.Area.State);

		t.House.OnNext(House());   // back to Normal / Home, no scene

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State, "resetting to Normal releases the hold");
	}

	[TestMethod]
	public void FromAway_IntoAGuestScene_EntersSceneHold_WithoutWelcomeHome()
	{
		var t = Build(tweak: s => s.WelcomeHome = true, tweakGlobal: g => g.HouseMode = GuestSceneMode());

		t.House.OnNext(AwayHouse());
		Assert.AreEqual(AreaState.Away, t.Area.State);
		t.Actuator.Clear();

		// The scene-hold check runs before the was-Away recovery, so a scene selected while Away lands in
		// SceneHold instead of firing the welcome-home ApplyTarget.
		t.House.OnNext(House(kind: ModeKind.Guest, modeValue: "Gjester", scene: "scene.gjest"));

		Assert.AreEqual(AreaState.SceneHold, t.Area.State, "a scene mode entered from Away lands in SceneHold");
		Assert.AreEqual(0, t.Actuator.Applied.Count, "and commands nothing — the scene is the look");
	}

	[TestMethod]
	public void Guest_WithoutAScene_DoesNotEnterSceneHold()
	{
		var mode = SoverMode();
		mode.Options.Add(new HouseModeOptionConfig { Value = "Gjester", Kind = ModeKind.Guest });   // no scene
		var t = Build(tweakGlobal: g => g.HouseMode = mode);
		t.Ha.Trigger(Motion, "on");

		t.House.OnNext(House(kind: ModeKind.Guest, modeValue: "Gjester"));

		Assert.AreNotEqual(AreaState.SceneHold, t.Area.State, "a guest mode with no scene does not hold the area");
		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "it stays on the baseline instead");
	}
}
