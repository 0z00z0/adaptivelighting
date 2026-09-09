using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>A house that configures its own away mode: quiet puts it there, a sensor seeing somebody brings it back.</summary>
// Driven through the whole engine rather than through a hand-made HouseState, because the defect was the
// composition: the tracker verdict was read ahead of the selector, so the configured reset returned the selector
// to Normal and the house was pulled back to away in the same instant.
[TestClass]
public sealed class ConfiguredAwayTests
{
	private const string Person = "person.a";
	private const string Motion = "binary_sensor.stue_bevegelse";
	private const string Light = "light.stue_taklys";
	private const string Blocker = "input_boolean.kino";
	private const string Select = "input_select.husmodus";
	private const string NormalOption = "Normal";
	private const string AwayOption = "Borte";

	private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(60);
	private static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);

	private sealed record Fixture(
		TestScheduler Scheduler,
		FakeHaContext Ha,
		FakeLightActuator Actuator,
		LightingOrchestrator Orchestrator)
	{
		public AreaController Room => Orchestrator.Areas[0];
	}

	/// <summary>The away option the report describes: an hour of quiet puts the house there, a sensor brings it back.</summary>
	private static HouseModeConfig ConfiguredAway(
		bool quiet = true,
		bool reset = true,
		HouseModeAuthority authority = HouseModeAuthority.AdaptiveLighting) => new()
	{
		Entity = Select,
		Authority = authority,
		Options =
		[
			new() { Value = NormalOption, Kind = ModeKind.Normal },
			new()
			{
				Value = AwayOption,
				Kind = ModeKind.Away,
				ActivateAfterNoMotionMinutes = quiet ? (int)Quiet.TotalMinutes : null,
				ResetOnPresence = reset,
				ResetPresenceGraceMinutes = (int)Grace.TotalMinutes
			}
		]
	};

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
		ha.SetState(Select, NormalOption);

		GlobalConfig global = new()
		{
			Persons = [Person],
			AwayDebounceMinutes = 5,
			HouseMode = ConfiguredAway()
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

		LightingOrchestrator orchestrator = new(
			ha, new FakeHaRegistry(), scheduler, config,
			actuator, new FakeStatePublisher(), new FakeNotifier(), NullLoggerFactory.Instance);

		orchestrator.Start();

		return new Fixture(scheduler, ha, actuator, orchestrator);
	}

	private static void Advance(Fixture t, TimeSpan by) => t.Scheduler.AdvanceBy(by.Ticks);

	private static void Move(Fixture t)
	{
		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Motion, "off");
	}

	private static int SelectCalls(Fixture t, string option) =>
		t.Ha.Calls.Count(call =>
			call.Domain == "input_select"
			&& call.Service == "select_option"
			&& call.Data?.GetType().GetProperty("option")?.GetValue(call.Data) as string == option);

	/// <summary>Lets the configured quiet time run out and echoes the selector Home Assistant would echo.</summary>
	private static void GoQuietUntilAway(Fixture t)
	{
		Advance(t, Quiet + TimeSpan.FromMinutes(1));

		Assert.AreEqual(1, SelectCalls(t, AwayOption), "arranged: the quiet rule wrote the selector");

		t.Ha.Trigger(Select, AwayOption);
		Assert.AreEqual(AreaState.Away, t.Room.State, "arranged: the house is away and has swept");
		t.Actuator.Clear();
	}

	// ---- the reported failure ------------------------------------------------------------------

	// The whole report: the mechanism the house owner configured could never win, because the tracker verdict was
	// read ahead of the selector and said away in the same instant the reset returned it to Normal.
	[TestMethod]
	public void Movement_Reaching_The_Reset_Brings_The_House_Home_While_Every_Tracker_Is_Away()
	{
		Fixture t = Build(personState: "not_home");
		GoQuietUntilAway(t);

		// Past the grace, so leaving cannot be what is being tested.
		Advance(t, Grace + TimeSpan.FromMinutes(1));

		Move(t);

		Assert.AreEqual(1, SelectCalls(t, NormalOption), "the sensor saw somebody, so the reset wrote the selector");

		// The reset is a service call on the selector; Home Assistant echoing it back is what the engine reacts to.
		t.Ha.Trigger(Select, NormalOption);

		Assert.AreNotEqual(AreaState.Away, t.Room.State,
			"the configured reset decides; a phone left on a worktop does not put the house back");
	}

	[TestMethod]
	public void The_Room_Stops_Refusing_Once_The_Reset_Has_Run()
	{
		Fixture t = Build(personState: "not_home");
		GoQuietUntilAway(t);
		Advance(t, Grace + TimeSpan.FromMinutes(1));

		Assert.IsNotNull(t.Room.LightNowRefusal(), "arranged: an away house refuses the button");

		Move(t);
		t.Ha.Trigger(Select, NormalOption);

		Assert.IsNull(t.Room.LightNowRefusal(),
			"there was no way to light a room from inside the building, and that is what this fixes");

		Move(t);

		Assert.AreEqual(AreaState.AutoActive, t.Room.State);
		Assert.IsTrue(t.Actuator.Last is { On: true }, "movement lights the room again");
	}

	// ---- and away again ------------------------------------------------------------------------

	// The other direction, and it needs no help: the option's own "no movement for" setting is the house owner's
	// answer to what makes the house away.
	[TestMethod]
	public void Quiet_For_The_Configured_Time_Puts_The_House_Away_Again()
	{
		Fixture t = Build();
		GoQuietUntilAway(t);
		Advance(t, Grace + TimeSpan.FromMinutes(1));

		Move(t);
		t.Ha.Trigger(Select, NormalOption);
		Assert.AreNotEqual(AreaState.Away, t.Room.State, "arranged: the house is home again");

		Advance(t, Quiet + TimeSpan.FromMinutes(1));

		Assert.AreEqual(2, SelectCalls(t, AwayOption), "the quiet rule fires again on the next quiet spell");

		t.Ha.Trigger(Select, AwayOption);

		Assert.AreEqual(AreaState.Away, t.Room.State,
			"one movement must not hold a house occupied for ever; quiet is what ends it");
	}

	// Walking out past the hall sensor must not cancel the mode just set.
	[TestMethod]
	public void Movement_Inside_The_Grace_Does_Not_Cancel_The_Mode_Just_Set()
	{
		Fixture t = Build(personState: "not_home");
		GoQuietUntilAway(t);

		Advance(t, Grace - TimeSpan.FromMinutes(1));
		Move(t);

		Assert.AreEqual(0, SelectCalls(t, NormalOption), "inside the grace the reset is ignored, as it always was");
		Assert.AreEqual(AreaState.Away, t.Room.State);
	}

	// ---- what the rule does not reach ----------------------------------------------------------

	[TestMethod]
	public void A_House_With_No_Away_Option_Is_Left_To_Its_Trackers()
	{
		Fixture t = Build(tweakGlobal: global => global.HouseMode = null);

		t.Ha.Trigger(Person, "not_home");
		Advance(t, TimeSpan.FromMinutes(6));

		Assert.AreEqual(AreaState.Away, t.Room.State, "nothing else answers the question, so the trackers still do");
	}

	// The reset alone describes only the way back. Switching the trackers off there would leave a house that can
	// never sweep at all.
	[TestMethod]
	public void An_Option_That_Resets_But_Never_Activates_Is_Left_To_Its_Trackers()
	{
		Fixture t = Build(tweakGlobal: global => global.HouseMode = ConfiguredAway(quiet: false));

		t.Ha.Trigger(Person, "not_home");
		Advance(t, TimeSpan.FromMinutes(6));

		Assert.AreEqual(AreaState.Away, t.Room.State);
	}

	[TestMethod]
	public void An_Option_That_Activates_But_Never_Resets_Is_Left_To_Its_Trackers()
	{
		Fixture t = Build(tweakGlobal: global => global.HouseMode = ConfiguredAway(reset: false));

		t.Ha.Trigger(Person, "not_home");
		Advance(t, TimeSpan.FromMinutes(6));

		Assert.AreEqual(AreaState.Away, t.Room.State);
	}

	// Both halves are dormant while Home Assistant owns the selector, so the configuration is inert and the
	// trackers are all the house has left.
	[TestMethod]
	public void A_House_Whose_Selector_Home_Assistant_Owns_Is_Left_To_Its_Trackers()
	{
		Fixture t = Build(tweakGlobal: global =>
			global.HouseMode = ConfiguredAway(authority: HouseModeAuthority.HomeAssistant));

		t.Ha.Trigger(Person, "not_home");
		Advance(t, TimeSpan.FromMinutes(6));

		Assert.AreEqual(AreaState.Away, t.Room.State);
	}

	// Neither half can fire without one, and the reset would have nothing to default its sensor list to.
	[TestMethod]
	public void A_House_With_No_Motion_Sensor_Anywhere_Is_Left_To_Its_Trackers()
	{
		Fixture t = Build(tweakArea: area => area.MotionSensors = []);

		t.Ha.Trigger(Person, "not_home");
		Advance(t, TimeSpan.FromMinutes(6));

		Assert.AreEqual(AreaState.Away, t.Room.State);
	}

	// ---- the room's own gates still apply ------------------------------------------------------

	[TestMethod]
	public void A_Room_Whose_Blocking_Entity_Is_On_Still_Refuses_The_Movement()
	{
		Fixture t = Build(personState: "not_home", tweakArea: area => area.IgnoreWhenOn = [Blocker]);
		GoQuietUntilAway(t);
		Advance(t, Grace + TimeSpan.FromMinutes(1));
		t.Ha.Trigger(Blocker, "on");

		Move(t);
		t.Ha.Trigger(Select, NormalOption);
		Move(t);

		Assert.AreEqual(0, t.Actuator.Applied.Count,
			"the house is home again, and the room still declines: making the house present defeats no room gate");
		Assert.AreEqual(AreaState.AutoVacant, t.Room.State);
	}
}
