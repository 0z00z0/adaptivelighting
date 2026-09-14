using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void The_Kill_Switch_Muzzles_The_Engine_And_Releases_Cleanly()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		t.House.OnNext(House(killed: true));
		Assert.AreEqual(AreaState.Disabled, t.Area.State);

		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(0, t.Actuator.Applied.Count, "a disabled engine sends nothing");

		t.House.OnNext(House());
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);

		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
	}

	[TestMethod]
	public void The_Kill_Switch_Is_Entered_From_Any_State()
	{
		var overridden = Build();
		overridden.Ha.Trigger(Motion, "on");
		Advance(overridden, TimeSpan.FromSeconds(30));   // clear the echo window of our own turn_on first
		overridden.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		Assert.AreEqual(AreaState.OverriddenOn, overridden.Area.State);
		overridden.House.OnNext(House(killed: true));
		Assert.AreEqual(AreaState.Disabled, overridden.Area.State);

		var suppressed = Build();
		suppressed.Ha.Trigger(Motion, "on");
		suppressed.Ha.Trigger(Light, "off", null, PhysicalDevice());
		Assert.AreEqual(AreaState.SuppressedOff, suppressed.Area.State);
		suppressed.House.OnNext(House(killed: true));
		Assert.AreEqual(AreaState.Disabled, suppressed.Area.State);

		var preOff = Build();
		preOff.Ha.Trigger(Motion, "on");
		preOff.Ha.Trigger(Motion, "off");
		Advance(preOff, TimeSpan.FromMinutes(10));
		Assert.AreEqual(AreaState.PreOff, preOff.Area.State);
		preOff.Actuator.Clear();
		preOff.House.OnNext(House(killed: true));
		Assert.AreEqual(AreaState.Disabled, preOff.Area.State);

		// The pre-off grace had 30 s left. A disabled engine must not spend them turning the lights off.
		Advance(preOff, TimeSpan.FromMinutes(1));
		Assert.AreEqual(0, preOff.Actuator.Applied.Count);
	}

	[TestMethod]
	public void A_Disabled_Area_Never_Commands_Anything()
	{
		var t = Build(s => s.Enabled = false);

		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	// Muzzle-then-release must land where stop-then-start does; AutoVacant arms no vacancy timeout.
	[TestMethod]
	public void Releasing_The_Kill_Switch_Adopts_A_Room_Left_Lit()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Motion, "off");
		t.Ha.SetState(Light, "on", new() { ["brightness"] = 178 });

		t.House.OnNext(House(killed: true));
		Assert.AreEqual(AreaState.Disabled, t.Area.State);

		t.Actuator.Clear();
		t.House.OnNext(House());

		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "a lit room is the engine's again the moment it is allowed to act");
		Assert.AreEqual(0, t.Actuator.Applied.Count, "adoption observes; it does not command");
		Assert.IsNotNull(t.Publisher.Snapshots[^1].NextChangeAt, "and it arms the timeout that ends the burning");

		// The vacancy timeout, then the pre-off grace.
		Advance(t, TimeSpan.FromSeconds(601));
		Advance(t, TimeSpan.FromSeconds(31));

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsFalse(t.Actuator.Last!.On, "the room does not burn on for ever because the engine was muzzled once");
	}

	[TestMethod]
	public void Releasing_The_Kill_Switch_Over_A_Dark_Room_Changes_Nothing()
	{
		var t = Build();
		t.House.OnNext(House(killed: true));
		t.Actuator.Clear();

		t.House.OnNext(House());

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void Re_Enabling_While_The_House_Is_Away_Lands_In_Away_Not_AutoVacant()
	{
		var t = Build();
		t.House.OnNext(House(killed: true));
		Assert.AreEqual(AreaState.Disabled, t.Area.State);

		t.House.OnNext(AwayHouse());

		Assert.AreEqual(AreaState.Away, t.Area.State);
	}
}
