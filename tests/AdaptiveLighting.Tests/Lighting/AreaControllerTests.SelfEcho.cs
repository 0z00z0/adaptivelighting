using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using NetDaemon.HassModel;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void Our_Own_Echo_Is_Not_Read_As_A_Human()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");

		Advance(t, TimeSpan.FromSeconds(1));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 178 }, new Context { Id = "echo", UserId = "nd-user" });

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
	}

	// The echo window must be SelfEchoWindowSeconds + TransitionSeconds. A fixed one reads the tail of the
	// engine's own night fade as a human at the dimmer.
	[TestMethod]
	public void An_Echo_From_The_Middle_Of_A_Long_Fade_Is_Still_Ours()
	{
		var t = Build(s =>
		{
			s.NightTransitionSeconds = 30;
			s.Darkness = DarknessSource.Always;
		});
		t.Ha.Trigger(Motion, "on");

		// 20 s in: past the 8 s echo window, well inside the 30 s fade the engine itself commanded.
		Advance(t, TimeSpan.FromSeconds(20));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 150 }, new Context { Id = "echo", UserId = "nd-user" });

		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "the engine must not override itself mid-fade");
	}

	[TestMethod]
	public void An_Echo_After_The_Window_And_The_Fade_Have_Both_Passed_Is_A_Human()
	{
		var t = Build(s =>
		{
			s.NightTransitionSeconds = 30;
			s.Darkness = DarknessSource.Always;
		});
		t.Ha.Trigger(Motion, "on");

		Advance(t, TimeSpan.FromSeconds(45));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 150 }, PhysicalDevice());

		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State, "the window must close eventually, or nothing is ever an override");
	}
}
