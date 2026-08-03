using System.Reactive.Subjects;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The paired entity gates: <c>KeepLitWhenOn</c>, which suspends the engine's own off-commands, and
///     <c>IgnoreWhenOn</c>, which refuses auto-on, each under both polarities.
/// </summary>
/// <remarks>
///     The rule both share is that an entity nobody can read applies under neither polarity, so a vanished helper
///     pins a room neither dark nor lit. Everything else here is the hold's edges: it never commands a light on,
///     it never overrules a hand at the switch, and an off it refused still lands once it lets go.
/// </remarks>
[TestClass]
public sealed class HoldLitTests
{
	private const string Motion = "binary_sensor.area_motion";
	private const string Light = "light.area";
	private const string Lux = "sensor.area_lux";
	private const string Holder = "input_boolean.meeting";
	private const string Blocker = "binary_sensor.projector";

	private const int VacancySeconds = 600;
	private const int PreOffSeconds = 30;
	private static readonly TimeSpan OneTick = TimeSpan.FromSeconds(60);

	private sealed record Fixture(
		TestScheduler Scheduler,
		FakeHaContext Ha,
		FakeLightActuator Actuator,
		FakeStatePublisher Publisher,
		BehaviorSubject<HouseState> House,
		AreaController Area);

	/// <summary>Builds a started area at 20:00, inside "evening", lux 5 so the darkness gate is open.</summary>
	private static Fixture Build(
		IReadOnlyList<string>? keepLitWhenOn = null,
		bool keepLitInverted = false,
		IReadOnlyList<string>? ignoreWhenOn = null,
		bool ignoreInverted = false,
		Action<FakeHaContext>? seed = null)
	{
		TestScheduler scheduler = new();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		FakeHaContext ha = new();
		ha.SetState(Motion, "off");
		ha.SetState(Light, "off");
		ha.SetState(Lux, "5");
		seed?.Invoke(ha);

		AreaSettings settings = new()
		{
			VacancyTimeoutSeconds = VacancySeconds,
			PreOffSeconds = PreOffSeconds,
			Darkness = DarknessSource.Lux,
			OverrideDurationMinutes = 120,
			VacancyResetMinutes = 10
		};

		GlobalConfig global = new() { SmoothTransitions = false, CircadianTickSeconds = 60 };

		List<TimePeriodConfig> table =
		[
			new() { Name = "day", Start = "07:00", BrightnessPct = 90, ColorTempKelvin = 4500 },
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new() { Name = "night", Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200 }
		];

		ResolvedArea area = new("Test", settings, [Light], [Motion], [Lux], [.. ignoreWhenOn ?? []])
		{
			KeepLitWhenOn = [.. keepLitWhenOn ?? []],
			IgnoreWhenOnInverted = ignoreInverted,
			KeepLitWhenOnInverted = keepLitInverted
		};

		FakeLightActuator actuator = new();
		FakeStatePublisher publisher = new();
		BehaviorSubject<HouseState> house = new(HouseState.Initial);

		AreaController controller = new(
			ha, scheduler, area, global, table,
			new CircadianCalculator(table, global, () => SunTimes.Unknown),
			actuator, publisher, house, NullLoggerFactory.Instance, areaId: "test_area");

		controller.Start();
		return new Fixture(scheduler, ha, actuator, publisher, house, controller);
	}

	private static void Advance(Fixture fixture, TimeSpan by) => fixture.Scheduler.AdvanceBy(by.Ticks);

	/// <summary>A change with no user and no parent: a wall switch acting on the light itself.</summary>
	private static Context PhysicalDevice() => new() { Id = "physical" };

	/// <summary>Lights the area through motion and forgets the command that did it.</summary>
	private static Fixture Lit(
		IReadOnlyList<string>? keepLitWhenOn = null,
		bool keepLitInverted = false,
		Action<FakeHaContext>? seed = null)
	{
		Fixture t = Build(keepLitWhenOn, keepLitInverted, seed: seed);
		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		t.Actuator.Clear();
		return t;
	}

	// ===================== the hold suspends the engine's own off =====================

	[TestMethod]
	public void Vacancy_Neither_Dims_Nor_Switches_Off_While_A_KeepLitWhenOn_Entity_Is_On()
	{
		Fixture t = Lit([Holder], seed: ha => ha.SetState(Holder, "on"));

		Advance(t, TimeSpan.FromSeconds(VacancySeconds + PreOffSeconds + 60));

		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "the warning dim is a step towards off, so a held area does not take it");
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void An_Expired_Vacancy_Timeout_Settles_On_The_Next_Tick_Once_The_Hold_Releases()
	{
		Fixture t = Lit([Holder], seed: ha => ha.SetState(Holder, "on"));
		Advance(t, TimeSpan.FromSeconds(VacancySeconds + 60));
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);

		// No state change is announced; the tick is what has to notice, or the room stays lit for ever.
		t.Ha.SetState(Holder, "off");
		Advance(t, OneTick);

		Assert.AreEqual(AreaState.PreOff, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 35 });

		Advance(t, TimeSpan.FromSeconds(PreOffSeconds));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false });
	}

	[TestMethod]
	public void A_Hold_Arriving_During_The_PreOff_Dim_Stops_The_Off_And_Settles_When_It_Releases()
	{
		Fixture t = Lit([Holder]);
		Advance(t, TimeSpan.FromSeconds(VacancySeconds));
		Assert.AreEqual(AreaState.PreOff, t.Area.State);
		t.Actuator.Clear();

		t.Ha.SetState(Holder, "on");
		Advance(t, TimeSpan.FromSeconds(PreOffSeconds + 120));

		Assert.AreEqual(AreaState.PreOff, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "held at the dimmed level, which is still lit");

		t.Ha.SetState(Holder, "off");
		Advance(t, OneTick);

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false });
	}

	[TestMethod]
	public void The_Leaving_Sweep_Leaves_A_Held_Area_Lit_And_Sweeps_It_When_The_Hold_Releases()
	{
		Fixture t = Lit([Holder], seed: ha => ha.SetState(Holder, "on"));

		t.House.OnNext(new HouseState(false, ModeKind.Normal, false));

		Assert.AreEqual(AreaState.Away, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "the sweep must not switch off what is being held on");

		t.Ha.SetState(Holder, "off");
		Advance(t, OneTick);

		Assert.IsTrue(t.Actuator.Last is { On: false }, "an empty house does not keep a room lit once the hold lets go");
	}

	// The sweep the hold refused belongs to a house that has since come back. WelcomeHome is off by default, so
	// coming home leaves the area in AutoVacant with no vacancy timer to clear the held-back off; without that
	// clear the hold releasing later swept a room in an occupied house.
	[TestMethod]
	public void A_Sweep_Held_Back_While_Away_Is_Dropped_Once_The_House_Comes_Home()
	{
		Fixture t = Lit([Holder], seed: ha => ha.SetState(Holder, "on"));

		t.House.OnNext(new HouseState(false, ModeKind.Normal, false));
		Assert.AreEqual(AreaState.Away, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "the hold refused the leaving sweep");

		t.House.OnNext(new HouseState(true, ModeKind.Normal, false));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);

		t.Ha.SetState(Holder, "off");
		Advance(t, OneTick);

		Assert.AreEqual(0, t.Actuator.Applied.Count,
			"the house is home again, so the hold letting go must not run the leaving sweep it refused");
	}

	[TestMethod]
	public void An_Expiring_Override_Leaves_A_Held_Area_Alone_Until_The_Hold_Releases()
	{
		Fixture t = Lit([Holder], seed: ha => ha.SetState(Holder, "on"));

		// Clear of the engine's own command, or the detector reads the change as its echo.
		Advance(t, TimeSpan.FromSeconds(30));

		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(121));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count);

		t.Ha.SetState(Holder, "off");
		Advance(t, OneTick);

		Assert.IsTrue(t.Actuator.Last is { On: false });
	}

	// ===================== what the hold must never do =====================

	[TestMethod]
	public void A_Hold_Never_Switches_A_Dark_Room_On_By_Itself()
	{
		Fixture t = Build([Holder], seed: ha => ha.SetState(Holder, "on"));

		Advance(t, TimeSpan.FromHours(1));

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "the hold suppresses off-commands; it issues none of its own");
	}

	[TestMethod]
	public void A_Hold_Does_Not_Rescue_Motion_The_Darkness_Gate_Refused()
	{
		Fixture t = Build([Holder], seed: ha => ha.SetState(Holder, "on"));
		t.Ha.SetState(Lux, "5000");

		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void A_Hand_Switching_The_Lights_Off_Is_Still_Obeyed_While_Held()
	{
		Fixture t = Lit([Holder], seed: ha => ha.SetState(Holder, "on"));

		t.Ha.Trigger(Light, "off", null, PhysicalDevice());

		Assert.AreEqual(AreaState.SuppressedOff, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "the hold suppresses the engine's own off, not a person's");

		Advance(t, TimeSpan.FromMinutes(30));
		Assert.AreEqual(0, t.Actuator.Applied.Count, "and nothing relights the room behind them");
	}

	[TestMethod]
	public void Motion_Under_A_Hold_Rearms_The_Vacancy_Timeout_Instead_Of_Settling_Later()
	{
		Fixture t = Lit([Holder], seed: ha => ha.SetState(Holder, "on"));
		Advance(t, TimeSpan.FromSeconds(VacancySeconds + 60));

		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		t.Ha.SetState(Holder, "off");
		Advance(t, OneTick);

		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "the fresh countdown supersedes the one the hold refused");
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	// ===================== inverted polarity =====================

	[TestMethod]
	public void KeepLitWhenOnInverted_Holds_The_Lights_While_The_Entity_Reads_Off()
	{
		Fixture t = Lit([Holder], keepLitInverted: true, seed: ha => ha.SetState(Holder, "off"));

		Advance(t, TimeSpan.FromSeconds(VacancySeconds + PreOffSeconds + 60));
		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count);

		t.Ha.SetState(Holder, "on");
		Advance(t, OneTick + TimeSpan.FromSeconds(PreOffSeconds));

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State, "on releases the inverted hold");
		Assert.IsTrue(t.Actuator.Last is { On: false });
	}

	[TestMethod]
	public void IgnoreWhenOnInverted_Refuses_Auto_On_While_The_Entity_Reads_Off()
	{
		Fixture t = Build(ignoreWhenOn: [Blocker], ignoreInverted: true, seed: ha => ha.SetState(Blocker, "off"));

		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count);

		t.Ha.SetState(Blocker, "on");
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
	}

	// ===================== an entity nobody can read applies under neither polarity =====================

	/// <summary>Absent from Home Assistant entirely, and the two states that read as present but say nothing.</summary>
	private static IEnumerable<object[]> Unreadable =>
	[
		["(absent)"],
		["unavailable"],
		["unknown"]
	];

	[TestMethod]
	[DynamicData(nameof(Unreadable))]
	public void An_Unreadable_KeepLitWhenOn_Entity_Holds_Nothing(string state)
	{
		Fixture t = Lit([Holder], seed: ha => Seed(ha, Holder, state));

		Advance(t, TimeSpan.FromSeconds(VacancySeconds + PreOffSeconds));

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false }, $"'{state}' is not 'on', so it must not pin the room lit");
	}

	// The failure a "hold while off" checkbox invites: a missing entity reads as not-on, and inverting that
	// would make every vanished helper hold its room lit for ever.
	[TestMethod]
	[DynamicData(nameof(Unreadable))]
	public void An_Unreadable_KeepLitWhenOn_Entity_Holds_Nothing_When_Inverted(string state)
	{
		Fixture t = Lit([Holder], keepLitInverted: true, seed: ha => Seed(ha, Holder, state));

		Advance(t, TimeSpan.FromSeconds(VacancySeconds + PreOffSeconds));

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false }, $"'{state}' is not 'off' either, so it must not pin the room lit");
	}

	[TestMethod]
	[DynamicData(nameof(Unreadable))]
	public void An_Unreadable_IgnoreWhenOn_Entity_Blocks_Nothing(string state)
	{
		Fixture t = Build(ignoreWhenOn: [Blocker], seed: ha => Seed(ha, Blocker, state));

		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoActive, t.Area.State, $"'{state}' is not 'on', so it must not pin the room dark");
	}

	[TestMethod]
	[DynamicData(nameof(Unreadable))]
	public void An_Unreadable_IgnoreWhenOn_Entity_Blocks_Nothing_When_Inverted(string state)
	{
		Fixture t = Build(ignoreWhenOn: [Blocker], ignoreInverted: true, seed: ha => Seed(ha, Blocker, state));

		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoActive, t.Area.State, $"'{state}' is not 'off' either, so it must not pin the room dark");
	}

	private static void Seed(FakeHaContext ha, string entityId, string state)
	{
		if (!string.Equals(state, "(absent)", StringComparison.Ordinal))
			ha.SetState(entityId, state);
	}

	// ===================== reporting =====================

	[TestMethod]
	public void The_Snapshot_Names_The_Entity_Holding_The_Lights_On()
	{
		Fixture t = Lit([Holder], seed: ha => ha.SetState(Holder, "on"));

		Advance(t, TimeSpan.FromSeconds(VacancySeconds));

		AreaSnapshot held = t.Publisher.Snapshots[^1];
		Assert.AreEqual(true, held.IsHeldLit);
		Assert.AreEqual(Holder, held.HeldLitBy);
		Assert.IsNull(held.NextChangeAt, "the countdown it refused is spent, so the area promises nothing");

		t.Ha.SetState(Holder, "off");
		Advance(t, OneTick);

		AreaSnapshot released = t.Publisher.Snapshots[^1];
		Assert.AreEqual(false, released.IsHeldLit, "false is 'nothing is holding it'; null would mean the area cannot say");
		Assert.IsNull(released.HeldLitBy);
	}

	[TestMethod]
	public void An_Area_With_No_KeepLitWhenOn_List_Reports_Not_Held_Rather_Than_Unknown()
	{
		Fixture t = Build();

		AreaSnapshot opening = t.Publisher.Snapshots[0];
		Assert.AreEqual(false, opening.IsHeldLit);
		Assert.IsNull(opening.HeldLitBy);
	}
}
