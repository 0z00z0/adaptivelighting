using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void An_Area_Found_Lit_Is_Adopted_And_Eventually_Turned_Off()
	{
		var t = BuildAlreadyLit();

		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "a lit room is the engine's problem, not nobody's");

		// The vacancy timeout is now running against a light the engine never commanded.
		Advance(t, TimeSpan.FromMinutes(10));
		Assert.AreEqual(AreaState.PreOff, t.Area.State);

		Advance(t, TimeSpan.FromSeconds(30));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false }, "the light the restart orphaned is finally out");
	}

	[TestMethod]
	public void Adoption_Commands_Absolutely_Nothing()
	{
		var t = BuildAlreadyLit();

		Assert.AreEqual(0, t.Actuator.Applied.Count,
			"somebody walking past a restart must notice nothing at all");

		// The target is seeded at adoption so the first tick finds nothing to correct.
		Advance(t, TimeSpan.FromMinutes(5));
		Assert.AreEqual(0, t.Actuator.Applied.Count, "adoption takes charge of the lights, not of their levels");
	}

	/// <summary>Darkness gates auto-on, not adoption.</summary>
	[TestMethod]
	public void A_Lit_Area_Is_Adopted_Even_When_It_Is_Too_Bright_To_Have_Been_Lit()
	{
		var t = BuildAlreadyLit(lux: "5000");

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		Assert.AreEqual(false, t.Publisher.Snapshots[^1].IsDark, "it is not dark, and the snapshot says so");
		Assert.AreEqual(0, t.Actuator.Applied.Count);

		Advance(t, TimeSpan.FromMinutes(11));
		Assert.IsTrue(t.Actuator.Applied.Any(a => a.Command is { On: false }),
			"no light is left burning because the engine forgot it, daylight included");
	}

	[TestMethod]
	public void An_Adopted_Area_Says_It_Was_Adopted_And_Claims_No_Levels()
	{
		var t = BuildAlreadyLit();
		var opening = t.Publisher.Snapshots.Single();

		Assert.AreEqual(TransitionReason.AdoptedAtStartup, opening.Reason);
		Assert.AreEqual(AreaState.AutoActive, opening.State);
		Assert.IsNull(opening.BrightnessPct, "the engine did not choose these levels and must not claim them");
		Assert.IsNull(opening.LastCommandAt, "…and it has not commanded this area at all");
		Assert.IsNotNull(opening.NextChangeAt, "but it has armed the timeout that ends the burning");
	}

	[TestMethod]
	public void An_Area_Found_Dark_Is_Not_Adopted()
	{
		var t = Build();

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.AreEqual(TransitionReason.Startup, t.Publisher.Snapshots.Single().Reason);
		Assert.IsNull(t.Publisher.Snapshots.Single().NextChangeAt, "nothing to wait for: nothing is on");
	}

	[TestMethod]
	public void A_Muzzled_Engine_Adopts_Nothing()
	{
		var t = BuildAlreadyLit(s => s.Enabled = false);

		// Start() declines to adopt, then the house subscription lands the area in Disabled.
		Assert.AreEqual(AreaState.Disabled, t.Area.State);

		Advance(t, TimeSpan.FromMinutes(15));
		Assert.AreEqual(0, t.Actuator.Applied.Count,
			"arming a timer that ends in a command is a command deferred, and a disabled engine makes none");
	}
}
