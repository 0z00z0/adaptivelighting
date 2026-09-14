using AdaptiveLighting.Engine;

using Fixture = AdaptiveLighting.Tests.Common.AreaFixture;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	// Home Assistant writes 'unavailable' with a context carrying neither a user nor a parent, the same shape a
	// wall switch reports. Both ends of the change have to read on or off before it counts as a person.

	[TestMethod]
	public void A_Light_Dropping_Off_The_Network_Is_Not_A_Human_Switching_It_Off()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);

		// Past the echo window, so nothing here is mistaken for the tail of the engine's own command.
		Advance(t, TimeSpan.FromSeconds(30));
		t.Ha.Trigger(Light, "unavailable", null, PhysicalDevice());

		Assert.AreEqual(AreaState.AutoActive, t.Area.State,
			"a bulb losing its radio is not a person at the switch, and must not suppress the room");
	}

	[TestMethod]
	public void A_Light_Coming_Back_From_Unavailable_Is_Not_A_Human_Switching_It_On()
	{
		var t = Build();
		Advance(t, TimeSpan.FromSeconds(30));

		t.Ha.Trigger(Light, "unavailable", null, PhysicalDevice());
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State,
			"a device reconnecting is indistinguishable from a hand at the switch, and the safe reading is neither");
	}

	[TestMethod]
	public void A_Lights_Very_First_Report_Is_Not_An_Override()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));

		t.Ha.Trigger(Light, "unknown", null, PhysicalDevice());

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
	}

	// A group reads on or off from the members still answering, so lit bulbs dropping out turn the group off and
	// their return turns it on, with no person anywhere near a switch.
	[TestMethod]
	public void A_Bulb_Leaving_Or_Rejoining_Its_Group_Is_Not_A_Hand_But_A_Real_Change_Still_Is()
	{
		const string group = "light.hall_group";
		const string lit = "light.hall_bulb_1";
		const string dark = "light.hall_bulb_2";

		Fixture t = Build(
			lights: [group],
			leavesOfEntry: new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
			{
				[group] = new HashSet<string>(StringComparer.Ordinal) { lit, dark }
			},
			seed: ha =>
			{
				ha.SetState(group, "off");
				ha.SetState(lit, "off");
				ha.SetState(dark, "off");
			});

		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		Advance(t, TimeSpan.FromSeconds(30));

		t.Ha.Trigger(lit, "unavailable", null, PhysicalDevice());
		t.Ha.Trigger(group, "off", null, PhysicalDevice());
		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "a bulb dropping out is not a person switching the room off");

		Advance(t, TimeSpan.FromSeconds(30));
		t.Ha.Trigger(lit, "on", new() { ["brightness"] = 178 }, PhysicalDevice());
		t.Ha.Trigger(group, "on", new() { ["brightness"] = 178 }, PhysicalDevice());
		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "a bulb rejoining is not a person switching the room on");

		// The control: once the window has passed, the same change on the group is a hand at the switch again.
		Advance(t, TimeSpan.FromSeconds(30));
		t.Ha.Trigger(group, "off", null, PhysicalDevice());
		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State, "a real off on the group is still read as a person");
	}

	/// <summary>The guard must not swallow the thing it sits in front of.</summary>
	[TestMethod]
	public void A_Real_Off_Is_Still_Read_As_A_Human()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));

		t.Ha.Trigger(Light, "off", null, PhysicalDevice());

		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State);
	}
}
