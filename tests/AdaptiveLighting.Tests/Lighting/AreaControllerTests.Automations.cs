using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

using NetDaemon.HassModel;

using Fixture = AdaptiveLighting.Tests.Common.AreaFixture;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void An_Automation_Counts_As_Manual_By_Default()
	{
		Fixture t = Build();
		t.Ha.Trigger(Motion, "on");

		t.Ha.Trigger(Light, "off", null, new Context { Id = "x", UserId = "u", ParentId = "automation" });

		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State);
	}

	[TestMethod]
	public void An_Automation_Is_Ignored_When_TreatAutomationsAsManual_Is_False()
	{
		Fixture t = Build(tweakGlobal: g => g.TreatAutomationsAsManual = false);
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));

		t.Ha.Trigger(Light, "off", null, new Context { Id = "x", UserId = "u", ParentId = "automation" });

		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "the knob must actually do something");
	}

	// A hall told to ignore automations must not take the bathroom's hair-dryer automation with it, and a room can
	// hold an automation's change in a house that ignores them.
	[TestMethod]
	public void An_Automation_Ignored_In_One_Room_Still_Holds_In_A_Room_That_Follows_The_House()
	{
		Fixture follows = Build();
		Fixture ignores = Build(treatAutomationsAsManual: false);
		Fixture holds = Build(tweakGlobal: g => g.TreatAutomationsAsManual = false, treatAutomationsAsManual: true);

		foreach (Fixture room in new[] { follows, ignores, holds })
		{
			room.Ha.Trigger(Motion, "on");
			Advance(room, TimeSpan.FromSeconds(30));
			room.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, new Context { Id = "run", ParentId = "trigger" });
		}

		Assert.AreEqual(AreaState.OverriddenOn, follows.Area.State, "a room stating nothing follows a house that holds automations");
		Assert.AreEqual(AreaState.AutoActive, ignores.Area.State, "a room saying no leaves the automation's change alone");
		Assert.AreEqual(AreaState.OverriddenOn, holds.Area.State, "a room saying yes holds it in a house that says no");
	}

	// A hall that ignores its automation still says which automation it ignored, once per run.
	[TestMethod]
	public void An_Automation_Change_Left_Alone_Is_Reported_Once_With_The_Automations_Name()
	{
		Fixture t = Build(treatAutomationsAsManual: false, nameOrigins: true);
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));
		t.Publisher.Snapshots.Clear();

		Context run = new() { Id = "run", ParentId = "trigger" };
		t.Ha.RaiseEvent(ChangeOriginNames.AutomationTriggeredEvent, new { name = "Evening lights" }, new Context { Id = "run" });
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 13 }, run);
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 14 }, run);

		AreaSnapshot[] reported = [.. t.Publisher.Snapshots.Where(snapshot => snapshot.Reason == TransitionReason.AutomationIgnored)];

		Assert.AreEqual(1, reported.Length, "one automation run is one row, however many updates it sends");
		Assert.AreEqual("By automation: Evening lights", reported[0].ChangedBy);
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
	}
}
