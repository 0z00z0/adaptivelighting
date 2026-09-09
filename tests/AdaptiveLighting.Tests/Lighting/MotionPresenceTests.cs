using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Movement in a managed room counts as somebody being home, and how that expires.</summary>
// Driven through the whole engine rather than through a hand-made HouseState, because the defect was the
// composition: the presence verdict was read ahead of everything and the rooms never saw the movement.
[TestClass]
public sealed class MotionPresenceTests
{
	private const string Person = "person.a";
	private const string Motion = "binary_sensor.stue_bevegelse";
	private const string Light = "light.stue_taklys";
	private const string Blocker = "input_boolean.kino";
	private const string Select = "input_select.husmodus";

	private sealed record Fixture(
		TestScheduler Scheduler,
		FakeHaContext Ha,
		FakeLightActuator Actuator,
		FakeStatePublisher Publisher,
		LightingOrchestrator Orchestrator)
	{
		public AreaController Room => Orchestrator.Areas[0];
	}

	private static Fixture Build(
		Action<GlobalConfig>? tweakGlobal = null,
		Action<AreaConfig>? tweakArea = null,
		string personState = "home")
	{
		TestScheduler scheduler = new();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		FakeHaContext ha = new();
		ha.SetState(Person, personState);
		ha.SetState(Light, "off");
		ha.SetState(Motion, "off");
		ha.SetState(Blocker, "off");
		ha.SetState(Select, "Normal");

		GlobalConfig global = new()
		{
			Persons = [Person],
			AwayDebounceMinutes = 5,
			MotionPresenceMinutes = 30
		};

		tweakGlobal?.Invoke(global);

		// No lux sensor anywhere, so the room always counts as dark and the darkness gate is never the reason.
		AreaConfig area = new()
		{
			Name = "Stue",
			Lights = [Light],
			MotionSensors = [Motion]
		};

		tweakArea?.Invoke(area);

		AdaptiveLightingConfig config = new()
		{
			Global = global,
			Periods = [new TimePeriodConfig { Name = "day", Start = "07:00", BrightnessPct = 60, ColorTempKelvin = 3000 }],
			Areas = [area]
		};

		FakeLightActuator actuator = new();
		FakeStatePublisher publisher = new();

		LightingOrchestrator orchestrator = new(
			ha, new FakeHaRegistry(), scheduler, config,
			actuator, publisher, new FakeNotifier(), NullLoggerFactory.Instance);

		orchestrator.Start();

		return new Fixture(scheduler, ha, actuator, publisher, orchestrator);
	}

	private static HouseModeConfig AwayOptionResettingOnPresence() => new()
	{
		Entity = Select,
		Options =
		[
			new() { Value = "Normal", Kind = ModeKind.Normal },
			new() { Value = "Borte", Kind = ModeKind.Away, ResetOnPresence = true, ResetPresenceGraceMinutes = 0 }
		]
	};

	/// <summary>Sends everybody away and lets the departure debounce run, so the house has swept and settled.</summary>
	private static void EmptyTheHouse(Fixture t)
	{
		t.Ha.Trigger(Person, "not_home");
		t.Scheduler.AdvanceBy(TimeSpan.FromMinutes(5).Ticks);

		Assert.AreEqual(AreaState.Away, t.Room.State, "arranged: the house has gone away and swept");
		t.Actuator.Clear();
	}

	private static void Move(Fixture t)
	{
		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Motion, "off");
	}

	// ---- movement while away brings the house back ---------------------------------------------

	[TestMethod]
	public void Movement_While_Away_Brings_The_House_Back()
	{
		Fixture t = Build();
		EmptyTheHouse(t);

		Move(t);

		Assert.AreNotEqual(AreaState.Away, t.Room.State,
			"somebody is moving about inside; a house that stays away has locked them in the dark");
	}

	// The whole point of the report from the cabin: one wave, not two. The presence monitor subscribes to the
	// motion sensors before any area does, so the house is already out of Away when the room's own handler runs.
	[TestMethod]
	public void One_Movement_Is_Enough_To_Light_The_Room()
	{
		Fixture t = Build();
		EmptyTheHouse(t);

		Move(t);

		Assert.AreEqual(AreaState.AutoActive, t.Room.State);
		Assert.IsTrue(t.Actuator.Last is { On: true },
			"the first movement lights the room; needing a second is the defect wearing a different hat");
	}

	[TestMethod]
	public void Movement_Without_A_Tracker_Anywhere_Near_Home_Still_Counts()
	{
		Fixture t = Build(personState: "not_home");

		// Never home at all: this is the cabin with the phone left behind, not a house somebody left.
		t.Scheduler.AdvanceBy(TimeSpan.FromMinutes(10).Ticks);
		t.Actuator.Clear();

		Move(t);

		Assert.AreEqual(AreaState.AutoActive, t.Room.State);
		Assert.IsTrue(t.Actuator.Last is { On: true });
	}

	[TestMethod]
	public void The_Manual_Light_Button_Stops_Refusing_Once_Something_Has_Moved()
	{
		Fixture t = Build();
		EmptyTheHouse(t);

		Assert.IsNotNull(t.Room.LightNowRefusal(), "arranged: an empty house refuses the button");

		Move(t);

		Assert.IsNull(t.Room.LightNowRefusal(),
			"there was no way to light a room from inside the building, and that is what this fixes");
	}

	// ---- the room's own gates still apply ------------------------------------------------------

	[TestMethod]
	public void A_Room_Whose_Blocking_Entity_Is_On_Still_Refuses_The_Movement()
	{
		Fixture t = Build(tweakArea: area => area.IgnoreWhenOn = [Blocker]);
		EmptyTheHouse(t);
		t.Ha.Trigger(Blocker, "on");

		Move(t);

		Assert.AreEqual(0, t.Actuator.Applied.Count,
			"the house is occupied again, and the room still declines: making the house present defeats no room gate");
		Assert.AreEqual(AreaState.AutoVacant, t.Room.State);
	}

	[TestMethod]
	public void A_Disabled_Room_Still_Commands_Nothing()
	{
		Fixture t = Build(tweakArea: area => area.Enabled = false);

		t.Ha.Trigger(Person, "not_home");
		t.Scheduler.AdvanceBy(TimeSpan.FromMinutes(5).Ticks);
		t.Actuator.Clear();

		Move(t);

		Assert.AreEqual(AreaState.Disabled, t.Room.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void The_Master_Switch_Still_Wins_Over_Movement()
	{
		Fixture t = Build(tweakGlobal: global =>
		{
			global.KillSwitchEntity = Blocker;
			global.KillSwitchActiveWhenOff = false;
		});

		t.Ha.Trigger(Blocker, "on");
		t.Ha.Trigger(Person, "not_home");
		t.Scheduler.AdvanceBy(TimeSpan.FromMinutes(5).Ticks);
		t.Actuator.Clear();

		Move(t);

		Assert.AreEqual(AreaState.Disabled, t.Room.State, "the master switch outranks the away state as it does everything");
		Assert.AreEqual(0, t.Actuator.Applied.Count, "nothing may command a light while the master switch is on");
	}

	// ---- a chosen away mode still outranks movement --------------------------------------------

	// Movement overrules trackers that cannot see anybody. It does not overrule somebody choosing Away on the
	// dial, which is a standing instruction and not a failure to observe.
	[TestMethod]
	public void A_Chosen_Away_Mode_Is_Not_Undone_By_Movement()
	{
		Fixture t = Build(tweakGlobal: global => global.HouseMode = new HouseModeConfig
		{
			Entity = Select,
			Options =
			[
				new() { Value = "Normal", Kind = ModeKind.Normal },
				new() { Value = "Borte", Kind = ModeKind.Away }
			]
		});

		t.Ha.Trigger(Select, "Borte");
		Assert.AreEqual(AreaState.Away, t.Room.State, "arranged: the dial says away");
		t.Actuator.Clear();

		Move(t);

		Assert.AreEqual(AreaState.Away, t.Room.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	// The machinery the report said could not work: the option resets the select on movement, and until now the
	// presence verdict said away in the same instant and the room stayed dark anyway.
	[TestMethod]
	public void An_Option_Resetting_On_Presence_Now_Actually_Reaches_Home()
	{
		Fixture t = Build(
			personState: "not_home",
			tweakGlobal: global => global.HouseMode = AwayOptionResettingOnPresence());

		t.Ha.Trigger(Select, "Borte");
		t.Scheduler.AdvanceBy(TimeSpan.FromMinutes(1).Ticks);
		t.Actuator.Clear();

		Move(t);

		// The reset is a service call on the select; Home Assistant echoing it back is what the engine reacts to.
		t.Ha.Trigger(Select, "Normal");

		Assert.AreNotEqual(AreaState.Away, t.Room.State,
			"the select came back to Normal and nobody's phone is here; only movement can hold the house home");
	}

	// ---- becoming away again -------------------------------------------------------------------

	[TestMethod]
	public void The_House_Becomes_Away_Again_When_The_Window_And_The_Debounce_Have_Run()
	{
		Fixture t = Build(personState: "not_home");

		Move(t);
		Assert.AreEqual(AreaState.AutoActive, t.Room.State, "arranged: movement holds the house home");

		// The window itself. Nothing has moved since, but the house is still occupied.
		t.Scheduler.AdvanceBy(TimeSpan.FromMinutes(29).Ticks);
		Assert.AreNotEqual(AreaState.Away, t.Room.State, "the window has not run out yet");

		// Expiry is a departure, so it goes through the same debounce a phone leaving goes through.
		t.Scheduler.AdvanceBy(TimeSpan.FromMinutes(1).Ticks);
		Assert.AreNotEqual(AreaState.Away, t.Room.State, "the window has run out; the debounce has not");

		t.Scheduler.AdvanceBy(TimeSpan.FromMinutes(5).Ticks);
		Assert.AreEqual(AreaState.Away, t.Room.State,
			"one animal must not be able to lock a house permanently occupied");
	}

	[TestMethod]
	public void Every_Further_Movement_Starts_The_Window_Again()
	{
		Fixture t = Build(personState: "not_home");

		Move(t);

		// Four movements at twenty-minute intervals: each one is inside the previous window and restarts it.
		for (int step = 0; step < 4; step++)
		{
			t.Scheduler.AdvanceBy(TimeSpan.FromMinutes(20).Ticks);
			Assert.AreNotEqual(AreaState.Away, t.Room.State, $"movement {step} should still be holding the house home");
			Move(t);
		}

		t.Scheduler.AdvanceBy(TimeSpan.FromMinutes(35).Ticks);
		Assert.AreEqual(AreaState.Away, t.Room.State, "once the movement stops, the last window is the one that runs out");
	}

	// Movement inside the debounce is an arrival like any other, so the departure that was counting down is
	// abandoned rather than confirmed.
	[TestMethod]
	public void Movement_Inside_The_Departure_Debounce_Cancels_The_Departure()
	{
		Fixture t = Build();

		t.Ha.Trigger(Person, "not_home");
		t.Scheduler.AdvanceBy(TimeSpan.FromMinutes(2).Ticks);

		Move(t);

		t.Scheduler.AdvanceBy(TimeSpan.FromMinutes(10).Ticks);

		Assert.AreNotEqual(AreaState.Away, t.Room.State,
			"the phone left the house but its owner did not, and the room is standing in front of the sensor");
	}

	// ---- the away sweep --------------------------------------------------------------------------

	// The sweep runs on the departure and is neither undone nor repeated by the movement that follows it: the
	// lights stay off until the room's own gates say otherwise, and coming back is a fresh arrival.
	[TestMethod]
	public void The_Away_Sweep_Runs_Once_Per_Departure_And_Is_Not_Re_Run_By_The_Return()
	{
		Fixture t = Build(personState: "not_home", tweakArea: area =>
		{
			area.IgnoreWhenOn = [Blocker];

			// Longer than the whole run, so the room's own vacancy timeout cannot contribute an off-command and
			// every off counted below is the leaving sweep.
			area.VacancyTimeoutSeconds = 7200;
		});

		// Light the room, then block it so the return cannot light it again and the only commands are sweeps.
		Move(t);
		Assert.IsTrue(t.Actuator.Last is { On: true }, "arranged: the room is lit");
		t.Ha.Trigger(Blocker, "on");
		t.Actuator.Clear();

		t.Scheduler.AdvanceBy(TimeSpan.FromMinutes(40).Ticks);
		Assert.AreEqual(AreaState.Away, t.Room.State, "arranged: the window and the debounce have run");

		int sweeps = t.Actuator.Applied.Count(applied => !applied.Command.On);
		Assert.AreEqual(1, sweeps, "one departure, one sweep");
		t.Actuator.Clear();

		Move(t);

		Assert.AreEqual(0, t.Actuator.Applied.Count,
			"coming back neither undoes the sweep nor runs it again; the room is simply out of Away");
		Assert.AreEqual(AreaState.AutoVacant, t.Room.State);
	}

	[TestMethod]
	public void The_Return_Is_Reported_As_An_Arrival_And_Not_As_A_Mode_Change()
	{
		Fixture t = Build();
		EmptyTheHouse(t);
		t.Publisher.Snapshots.Clear();

		Move(t);

		AreaSnapshot arrival = t.Publisher.Snapshots
			.First(snapshot => snapshot.State is not AreaState.Away);

		Assert.AreEqual(TransitionReason.FirstPersonArrived, arrival.Reason,
			"movement is somebody being home, so it reads as an arrival and not as a mode somebody set");
	}

	// ---- switching the rule off -------------------------------------------------------------------

	[TestMethod]
	public void A_Zero_Window_Watches_Trackers_Only()
	{
		Fixture t = Build(tweakGlobal: global => global.MotionPresenceMinutes = 0);
		EmptyTheHouse(t);

		Move(t);

		Assert.AreEqual(AreaState.Away, t.Room.State,
			"a household that wants the old behaviour turns the window off and keeps it");
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void A_Negative_Window_Is_Read_As_Off_And_Never_As_Already_Expired()
	{
		Fixture t = Build(tweakGlobal: global => global.MotionPresenceMinutes = -5);
		EmptyTheHouse(t);

		Move(t);

		Assert.AreEqual(AreaState.Away, t.Room.State);
	}
}
