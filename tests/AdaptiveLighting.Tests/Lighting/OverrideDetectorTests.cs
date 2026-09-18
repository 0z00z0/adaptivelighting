using AdaptiveLighting.Configuration;
using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Ha;

using Microsoft.Reactive.Testing;

using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Who changed the light: the two heuristics, and the precedence between them.</summary>
[TestClass]
public sealed class OverrideDetectorTests
{
	private const string Light = "light.area";

	private static (OverrideDetector Detector, TestScheduler Scheduler, FakeHaContext Ha) Build(Action<GlobalConfig>? tweak = null)
	{
		var scheduler = new TestScheduler();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		var global = new GlobalConfig();
		tweak?.Invoke(global);

		return (new OverrideDetector(global, scheduler), scheduler, new FakeHaContext());
	}

	/// <summary>Builds the change the detector sees, by pushing it through a context and catching it.</summary>
	private static StateChange Change(FakeHaContext ha, string state, Context? context)
	{
		StateChange? captured = null;
		using var subscription = ha.StateAllChanges().Subscribe(c => captured = c);
		ha.Trigger(Light, state, null, context);
		return captured!;
	}

	// ===================== context inspection =====================

	[TestMethod]
	public void No_User_And_No_Parent_Is_A_Physical_Device()
	{
		var (detector, _, ha) = Build();

		var origin = detector.Classify(Change(ha, "on", new Context { Id = "c" }));

		Assert.AreEqual(ChangeOrigin.PhysicalDevice, origin, "nothing created this on anyone's behalf: the switch reported it");
	}

	[TestMethod]
	public void A_User_With_No_Parent_Is_A_Person_In_The_App()
	{
		var (detector, _, ha) = Build();

		var origin = detector.Classify(Change(ha, "on", new Context { Id = "c", UserId = "someone" }));

		Assert.AreEqual(ChangeOrigin.HaUser, origin);
	}

	[TestMethod]
	public void A_Parent_With_No_User_Is_An_Automation()
	{
		var (detector, _, ha) = Build();

		var origin = detector.Classify(Change(ha, "on", new Context { Id = "c", ParentId = "p" }));

		Assert.AreEqual(ChangeOrigin.Automation, origin);
	}

	// A script a person started carries both ids, so reading the user first would classify every scripted change
	// as a person.
	[TestMethod]
	public void A_Change_Carrying_Both_A_User_And_A_Parent_Is_An_Automation_Not_A_User()
	{
		var (detector, _, ha) = Build();

		var origin = detector.Classify(Change(ha, "on", new Context { Id = "c", UserId = "someone", ParentId = "script.evening" }));

		Assert.AreEqual(ChangeOrigin.Automation, origin, "it is the script that set the level, not the finger that started it");
	}

	[TestMethod]
	public void The_Own_User_Learned_From_The_First_Echoed_Snapshot_Is_Always_Ourselves()
	{
		FakeHaContext ha = new();
		using OwnUser ownUser = new(ha, new HaStatePublisher(ha, new RecordingLogger()), new RecordingLogger());
		TestScheduler scheduler = new();
		OverrideDetector detector = new(new GlobalConfig(), scheduler, ownUserId: () => ownUser.UserId);
		Context ours = new() { Id = "c", UserId = "nd-user", ParentId = "p" };

		Assert.AreEqual(ChangeOrigin.Automation, detector.Classify(Change(ha, "on", ours)), "nothing has come back yet");

		// Sub-second and off UTC, so the echo has to survive the round trip through JSON to match.
		DateTimeOffset sentAt = new DateTimeOffset(2026, 1, 15, 20, 0, 0, 123, TimeSpan.FromHours(1)).AddTicks(4567);
		ownUser.Publish(new AreaSnapshot(
			"Area", AreaState.Disabled, TransitionReason.Startup, ModeKind.Normal, false, null, null, null, null,
			sentAt, null, null, null, null));
		(string type, object? data) = ha.SentEvents.Single();
		ha.RaiseEvent(type, data, new Context { Id = "e", UserId = "nd-user" });

		Assert.AreEqual("nd-user", ownUser.UserId);
		Assert.AreEqual(ChangeOrigin.Self, detector.Classify(Change(ha, "on", ours)), "our own user beats every other reading, parent id included");
	}

	[TestMethod]
	public void A_Change_With_No_Context_At_All_Is_Unknown()
	{
		var (detector, _, ha) = Build();

		var origin = detector.Classify(Change(ha, "on", null));

		Assert.AreEqual(ChangeOrigin.Unknown, origin);
	}

	// ===================== expectation correlation =====================

	[TestMethod]
	public void A_Change_Matching_A_Live_Expectation_Is_Ours_Whatever_Its_Context_Says()
	{
		var (detector, scheduler, ha) = Build();
		detector.ExpectCommand(Light, new LightCommand(true, 70, 2700, 0));

		scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
		var origin = detector.Classify(Change(ha, "on", new Context { Id = "c", UserId = "someone" }));

		Assert.AreEqual(ChangeOrigin.Self, origin);
	}

	[TestMethod]
	public void An_Expectation_Survives_The_Whole_Burst_Of_Changes_One_Command_Provokes()
	{
		var (detector, scheduler, ha) = Build();
		detector.ExpectCommand(Light, new LightCommand(true, 70, 2700, 0));

		for (var i = 0; i < 5; i++)
		{
			scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
			Assert.AreEqual(ChangeOrigin.Self, detector.Classify(Change(ha, "on", new Context { Id = $"c{i}" })),
				"one turn_on settles over several changes, and every one of them is still ours");
		}
	}

	[TestMethod]
	public void An_Expectation_Expires()
	{
		var (detector, scheduler, ha) = Build();   // default echo window: 8 s
		detector.ExpectCommand(Light, new LightCommand(true, 70, 2700, 0));

		scheduler.AdvanceBy(TimeSpan.FromSeconds(9).Ticks);
		var origin = detector.Classify(Change(ha, "on", new Context { Id = "c" }));

		Assert.AreEqual(ChangeOrigin.PhysicalDevice, origin, "a window that never closes means nothing is ever an override");
	}

	// A 30 s fade emits changes for 30 s, so a fixed 8 s window reads the last 22 s as a hand at the dimmer. The
	// window is the echo window plus the transition.
	[TestMethod]
	public void The_Expectation_Window_Spans_The_Commands_Own_Transition()
	{
		var (detector, scheduler, ha) = Build();   // 8 s echo window
		detector.ExpectCommand(Light, new LightCommand(true, 70, 2700, 30));

		scheduler.AdvanceBy(TimeSpan.FromSeconds(20).Ticks);
		Assert.AreEqual(ChangeOrigin.Self, detector.Classify(Change(ha, "on", new Context { Id = "c" })),
			"20 s into a 30 s fade is unambiguously still our own fade");

		scheduler.AdvanceBy(TimeSpan.FromSeconds(37).Ticks);   // 57 s: past 8 + 30
		Assert.AreEqual(ChangeOrigin.PhysicalDevice, detector.Classify(Change(ha, "on", new Context { Id = "c" })),
			"and once the fade and the window are both done, the next change is a human's");
	}

	[TestMethod]
	public void An_Expectation_Does_Not_Cover_A_Change_In_The_Opposite_Direction()
	{
		var (detector, scheduler, ha) = Build();
		detector.ExpectCommand(Light, new LightCommand(true, 70, 2700, 0));

		scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
		var origin = detector.Classify(Change(ha, "off", new Context { Id = "c" }));

		Assert.AreEqual(ChangeOrigin.PhysicalDevice, origin, "we asked for on and the light went off: that was somebody else");
	}

	[TestMethod]
	public void An_Expectation_Is_Scoped_To_Its_Own_Entity()
	{
		var (detector, scheduler, ha) = Build();
		detector.ExpectCommand("light.somewhere_else", new LightCommand(true, 70, 2700, 0));

		scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
		var origin = detector.Classify(Change(ha, "on", new Context { Id = "c" }));

		Assert.AreEqual(ChangeOrigin.PhysicalDevice, origin);
	}

	// ===================== policy =====================

	[TestMethod]
	public void Humans_Are_Manual_And_We_Are_Not()
	{
		var (detector, _, _) = Build();

		Assert.IsTrue(detector.IsManual(ChangeOrigin.PhysicalDevice));
		Assert.IsTrue(detector.IsManual(ChangeOrigin.HaUser));
		Assert.IsFalse(detector.IsManual(ChangeOrigin.Self));
		Assert.IsFalse(detector.IsManual(ChangeOrigin.Unknown), "an unreadable context is not a reason to hand the room over");
	}

	[TestMethod]
	public void Automations_Are_Manual_By_Default_And_Configurably_Not()
	{
		var (defaulted, _, _) = Build();
		Assert.IsTrue(defaulted.IsManual(ChangeOrigin.Automation));

		var (configured, _, _) = Build(g => g.TreatAutomationsAsManual = false);
		Assert.IsFalse(configured.IsManual(ChangeOrigin.Automation));
	}

	// A room that says nothing follows the house either way; a room that answers keeps its answer either way.
	[TestMethod]
	public void A_Room_Follows_The_House_On_Automations_Unless_It_Answers_For_Itself()
	{
		TestScheduler scheduler = new();

		foreach (bool house in new[] { true, false })
		{
			GlobalConfig global = new() { TreatAutomationsAsManual = house };

			Assert.AreEqual(house, new OverrideDetector(global, scheduler).IsManual(ChangeOrigin.Automation),
				$"a room stating nothing follows the house's {house}");
			Assert.IsTrue(new OverrideDetector(global, scheduler, true).IsManual(ChangeOrigin.Automation),
				$"a room saying yes holds an automation's change while the house says {house}");
			Assert.IsFalse(new OverrideDetector(global, scheduler, false).IsManual(ChangeOrigin.Automation),
				$"a room saying no ignores an automation's change while the house says {house}");

			Assert.IsTrue(new OverrideDetector(global, scheduler, false).IsManual(ChangeOrigin.PhysicalDevice),
				"the room's answer is about automations only: a hand at the switch is always manual");
			Assert.IsTrue(new OverrideDetector(global, scheduler, false).IsManual(ChangeOrigin.HaUser));
		}
	}
}
