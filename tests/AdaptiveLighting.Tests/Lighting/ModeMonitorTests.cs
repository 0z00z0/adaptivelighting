using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The mode brain (09 §3.2): reading the select, deriving kind/scene, and the set → retain → reset lifecycle —
///     period-entry SetsMode, the three reset triggers (period, presence with grace, time), and the master-switch
///     default. Everything runs on a virtual <see cref="TestScheduler"/> and a <see cref="FakeHaContext"/>.
/// </summary>
[TestClass]
public sealed class ModeMonitorTests
{
	private const string Select = "input_select.husmodus";
	private const string Gang = "binary_sensor.gang_bevegelse";
	private const string Kjokken = "binary_sensor.kjokken_bevegelse";
	private const string Person = "person.alex";
	private const string Tracker = "device_tracker.alex_phone";
	private const string SleepToggle = "input_boolean.sover";
	private const string GuestToggle = "input_boolean.gjest";
	private static readonly DateTimeOffset Evening = new(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);

	private static HouseModeConfig Mode() => new()
	{
		Entity = Select,
		Options =
		[
			new() { Value = "Hjemme", Kind = ModeKind.Normal },
			new() { Value = "Sover", Kind = ModeKind.Sleep },
			new() { Value = "Borte", Kind = ModeKind.Away, Scene = "scene.borte" },
			new() { Value = "Gjester", Kind = ModeKind.Guest, Scene = "scene.gjest" }
		]
	};

	private static List<TimePeriodConfig> Periods() =>
	[
		new() { Name = "morning", Start = "06:30", BrightnessPct = 60, ColorTempKelvin = 3000 },
		new() { Name = "evening", Start = "18:00", BrightnessPct = 60, ColorTempKelvin = 2700 },
		new() { Name = "night", Start = "23:00", BrightnessPct = 10, ColorTempKelvin = 2200, SetsMode = "Sover" }
	];

	private sealed record Rig(FakeHaContext Ha, TestScheduler Scheduler, ModeMonitor Monitor, FakeLastPeriodStore LastPeriod);

	/// <summary>
	///     The note recording which period the last run ended in, in memory.
	/// </summary>
	/// <remarks>
	///     <paramref name="recalled"/> is what the previous run left: <c>null</c> is a first run, a deleted note or a
	///     corrupt one, which <see cref="LastPeriodStore"/> deliberately does not tell apart.
	/// </remarks>
	private sealed class FakeLastPeriodStore(string? recalled = null) : ILastPeriodStore
	{
		/// <summary>Every period written, in order, so "written once on a change" can be asserted.</summary>
		public List<string> Saved { get; } = [];

		public string? Load() => recalled;

		public bool TrySave(string periodName)
		{
			Saved.Add(periodName);
			return true;
		}
	}

	/// <summary>A note that throws on both operations, standing in for any store a host might supply.</summary>
	private sealed class ThrowingLastPeriodStore : ILastPeriodStore
	{
		public string? Load() => throw new InvalidOperationException("the note is unreadable");

		public bool TrySave(string periodName) => throw new InvalidOperationException("the note cannot be written");
	}

	private static Rig Build(
		GlobalConfig? global = null,
		List<TimePeriodConfig>? periods = null,
		IReadOnlyCollection<string>? motion = null,
		DateTimeOffset? startAt = null,
		Action<FakeHaContext>? seed = null,
		ILastPeriodStore? lastPeriod = null)
	{
		var scheduler = new TestScheduler();
		scheduler.AdvanceTo((startAt ?? Evening).Ticks);

		var ha = new FakeHaContext();
		global ??= new GlobalConfig { CircadianTickSeconds = 60, HouseMode = Mode() };
		seed?.Invoke(ha);

		var note = lastPeriod as FakeLastPeriodStore ?? new FakeLastPeriodStore();

		var monitor = new ModeMonitor(
			ha, global, NullLogger.Instance, scheduler,
			periods ?? Periods(), () => SunTimes.Unknown, motion ?? [], lastPeriod ?? note,
			PeriodSelectReader.For(ha, global, NullLogger.Instance));

		return new Rig(ha, scheduler, monitor, note);
	}

	private static Rig Started(
		GlobalConfig? global = null,
		List<TimePeriodConfig>? periods = null,
		IReadOnlyCollection<string>? motion = null,
		DateTimeOffset? startAt = null,
		string initialSelect = "Hjemme",
		Action<FakeHaContext>? seed = null,
		ILastPeriodStore? lastPeriod = null)
	{
		var rig = Build(global, periods, motion, startAt, ha =>
		{
			ha.SetState(Select, initialSelect);
			seed?.Invoke(ha);
		}, lastPeriod);
		rig.Monitor.Start();
		return rig;
	}

	/// <summary>The rig a restart test wants: a note saying the previous run ended in <paramref name="periodName"/>.</summary>
	private static FakeLastPeriodStore EndedIn(string periodName) => new(periodName);

	private static void Activate(Rig rig, string value) => rig.Ha.Trigger(Select, value);

	private static void Advance(Rig rig, TimeSpan by) => rig.Scheduler.AdvanceBy(by.Ticks);

	private static int SelectCalls(FakeHaContext ha, string option) =>
		ha.Calls.Count(c => c.Domain == "input_select" && c.Service == "select_option" && OptionOf(c) == option);

	private static string? OptionOf(ServiceCall call) =>
		call.Data?.GetType().GetProperty("option")?.GetValue(call.Data) as string;

	// ---- Kind / scene derivation --------------------------------------------------------------

	[TestMethod]
	public void ActiveKindAndScene_DeriveFromOption()
	{
		var rig = Build();

		rig.Ha.SetState(Select, "Sover");
		Assert.AreEqual(ModeKind.Sleep, rig.Monitor.ActiveKind);
		Assert.IsNull(rig.Monitor.ActiveScene, "a sleep option carries no scene");

		rig.Ha.SetState(Select, "Borte");
		Assert.AreEqual(ModeKind.Away, rig.Monitor.ActiveKind);
		Assert.AreEqual("scene.borte", rig.Monitor.ActiveScene);

		rig.Ha.SetState(Select, "Hjemme");
		Assert.AreEqual(ModeKind.Normal, rig.Monitor.ActiveKind);
		Assert.IsNull(rig.Monitor.ActiveScene);
	}

	[TestMethod]
	public void ActiveScene_IsReturnedForAnyKind_IncludingSleep()
	{
		var mode = Mode();
		mode.OptionFor("Sover")!.Scene = "scene.natt";   // a scene on a Sleep option
		var rig = Build(new GlobalConfig { CircadianTickSeconds = 60, HouseMode = mode });

		rig.Ha.SetState(Select, "Sover");
		Assert.AreEqual(ModeKind.Sleep, rig.Monitor.ActiveKind);
		Assert.AreEqual("scene.natt", rig.Monitor.ActiveScene, "a scene applies on entry to any kind now, sleep included");
	}

	[TestMethod]
	public void CurrentModeValue_NullOnUnavailableUnknownUnconfigured()
	{
		var ha = new FakeHaContext();

		using var unconfigured = new ModeMonitor(ha, new GlobalConfig(), NullLogger.Instance,
			new TestScheduler(), [], () => SunTimes.Unknown, []);
		Assert.IsNull(unconfigured.CurrentModeValue);

		var rig = Build();
		Assert.IsNull(rig.Monitor.CurrentModeValue, "no state reported yet");

		rig.Ha.SetState(Select, "unavailable");
		Assert.IsNull(rig.Monitor.CurrentModeValue);

		rig.Ha.SetState(Select, "Sover");
		Assert.AreEqual("Sover", rig.Monitor.CurrentModeValue);
	}

	[TestMethod]
	public void UnrecognisedValue_WarnsOncePerValue()
	{
		var ha = new FakeHaContext();
		var logger = new CountingLogger();
		var monitor = new ModeMonitor(ha, new GlobalConfig { HouseMode = Mode() }, logger,
			new TestScheduler(), Periods(), () => SunTimes.Unknown, []);

		ha.SetState(Select, "Natt");   // a live value nothing classifies

		Assert.AreEqual("Natt", monitor.CurrentModeValue);
		Assert.AreEqual(ModeKind.Normal, monitor.ActiveKind);
		_ = monitor.CurrentModeValue;
		_ = monitor.ActiveKind;

		Assert.AreEqual(1, logger.Warnings, "warns once per distinct unclassified value");
	}

	// ---- Retention ----------------------------------------------------------------------------

	[TestMethod]
	public void Retention_NoTrigger_StaysSet()
	{
		var rig = Started(startAt: Evening, initialSelect: "Hjemme");
		Activate(rig, "Sover");    // sleep, and Mode()'s Sover has no reset trigger

		Advance(rig, TimeSpan.FromHours(6));   // crosses night (SetsMode Sover, already Sover) and beyond

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"), "nothing resets a retained mode");
	}

	// ---- Period entry → SetsMode --------------------------------------------------------------

	[TestMethod]
	public void PeriodEntry_SetsMode_FiresOnceAtEntry()
	{
		var rig = Started(startAt: new DateTimeOffset(2026, 1, 15, 22, 0, 0, TimeSpan.Zero), initialSelect: "Hjemme");

		Advance(rig, TimeSpan.FromMinutes(90));   // past night@23:00

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Sover"), "SetsMode fires exactly once on entry");
	}

	[TestMethod]
	public void PeriodEntry_SetsMode_NotWhenAlreadyThatMode()
	{
		var rig = Started(startAt: new DateTimeOffset(2026, 1, 15, 22, 0, 0, TimeSpan.Zero), initialSelect: "Sover");

		Advance(rig, TimeSpan.FromMinutes(90));

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Sover"), "the select is already on Sover; no redundant set");
	}

	[TestMethod]
	public void PeriodEntry_SetsMode_HumanOverrideMidPeriodStands()
	{
		var rig = Started(startAt: new DateTimeOffset(2026, 1, 15, 22, 0, 0, TimeSpan.Zero), initialSelect: "Hjemme");

		Advance(rig, TimeSpan.FromMinutes(90));    // past night@23:00 → one Sover set
		Assert.AreEqual(1, SelectCalls(rig.Ha, "Sover"));

		Activate(rig, "Hjemme");                   // a human overrides mid-night
		Advance(rig, TimeSpan.FromMinutes(45));    // stays in night (to ~00:15)

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Sover"), "entry is edge-triggered; the override stands");
	}

	// ---- Restart across a period boundary ------------------------------------------------------

	// 23:30 — half an hour into night, whose SetsMode is Sover. Every restart test starts here and varies only
	// which period the note says the previous run ended in.
	private static readonly DateTimeOffset HalfPastNight = new(2026, 1, 15, 23, 30, 0, TimeSpan.Zero);

	/// <summary>
	///     A boundary that went by while the engine was stopped applies the new period's mode on the first tick.
	/// </summary>
	/// <remarks>
	///     The defect this pins: entry is edge-triggered, so a boundary crossed during an outage was a boundary
	///     nothing ever noticed and the house kept its daytime mode until the same hour came round again. The note
	///     says the last run ended in evening; it is night now, so night began while nothing was watching.
	/// </remarks>
	[TestMethod]
	public void StartAfterABoundaryWentBy_AppliesSetsMode_OnTheFirstTick()
	{
		var rig = Started(startAt: HalfPastNight, initialSelect: "Hjemme", lastPeriod: EndedIn("evening"));

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Sover"), "nothing is written before the first tick");

		Advance(rig, TimeSpan.FromMinutes(1));   // one tick, no boundary crossed while running

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Sover"), "night began while the engine was down, so night's mode applies");
	}

	/// <summary>Once, not on every tick — the select's echo is asynchronous, so the flag has to be the guard.</summary>
	[TestMethod]
	public void StartAfterABoundaryWentBy_AppliesSetsMode_OnlyOnce()
	{
		var rig = Started(startAt: HalfPastNight, initialSelect: "Hjemme", lastPeriod: EndedIn("evening"));

		Advance(rig, TimeSpan.FromMinutes(30));   // thirty ticks, still inside night

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Sover"), "one call, however many ticks pass with HA silent");
	}

	/// <summary>
	///     A boundary really did go by, so the new period's mode applies over whatever the select stands on.
	/// </summary>
	/// <remarks>
	///     <b>This is the owner's rule, and it is not the cautious one.</b> An earlier draft wrote only over the
	///     Normal option, on the ground that a restart cannot tell a deliberate Gjester from a stale one. He chose
	///     the other trade: a crossed boundary is a real event and the schedule is entitled to act on it, exactly as
	///     it would have done had the engine been running to watch the boundary arrive. The protection against
	///     overruling a person is the boundary test itself, not the standing mode.
	/// </remarks>
	[TestMethod]
	public void StartAfterABoundaryWentBy_AppliesSetsMode_OverANonNormalMode()
	{
		var rig = Started(startAt: HalfPastNight, initialSelect: "Gjester", lastPeriod: EndedIn("evening"));

		Advance(rig, TimeSpan.FromMinutes(5));

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Sover"), "night began; night's mode wins over the standing option");
	}

	/// <summary>
	///     Restarting inside the period you were already in changes nothing, and a mode set by hand survives.
	/// </summary>
	/// <remarks>
	///     This is the case a deploy actually produces most of the time, several times a day. No boundary went by,
	///     so there is no event to act on and re-asserting the period's mode would only undo somebody's choice.
	/// </remarks>
	[TestMethod]
	public void StartInsideTheSamePeriod_LeavesAHandSetModeAlone()
	{
		var rig = Started(startAt: HalfPastNight, initialSelect: "Gjester", lastPeriod: EndedIn("night"));

		Advance(rig, TimeSpan.FromMinutes(30));

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Sover"), "same period as when it stopped: nothing happened to act on");
	}

	/// <summary>
	///     With no note of the previous run, nothing is applied — not knowing is not knowing a boundary was crossed.
	/// </summary>
	/// <remarks>
	///     A first run, a deleted note and a corrupt one are the same answer, and the safe half is inertia. The cost
	///     is one missed re-application, after which the note exists; the cost of guessing the other way is a mode
	///     overwritten on no evidence, on a path a corrupt file could trigger at every single start.
	/// </remarks>
	[TestMethod]
	public void StartWithNoNoteOfThePreviousRun_AppliesNothing()
	{
		var rig = Started(startAt: HalfPastNight, initialSelect: "Hjemme", lastPeriod: new FakeLastPeriodStore());

		Advance(rig, TimeSpan.FromMinutes(30));

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Sover"), "nothing is assumed from an absent note");
	}

	/// <summary>A note that cannot be read or written is inert: it costs the behaviour, never the tick.</summary>
	/// <remarks>
	///     <see cref="LastPeriodStore"/> promises never to throw, and the monitor catches anyway — the store is an
	///     interface a host supplies, and a throw out of <see cref="ModeMonitor.Start"/> or out of the tick would
	///     take the engine with it. A blank line in a configuration file once did exactly that.
	/// </remarks>
	[TestMethod]
	public void StartWithAnUnreadableNote_DoesNotThrow_AndAppliesNothing()
	{
		var rig = Started(startAt: HalfPastNight, initialSelect: "Hjemme", lastPeriod: new ThrowingLastPeriodStore());

		Advance(rig, TimeSpan.FromMinutes(30));   // the write throws on every period change too

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Sover"), "an unreadable note reads as 'we do not know'");
	}

	/// <summary>The period is written down when it changes, and not on every tick.</summary>
	[TestMethod]
	public void ThePeriodIsRecorded_OnceWhenItChanges()
	{
		var rig = Started(startAt: new DateTimeOffset(2026, 1, 15, 22, 55, 0, TimeSpan.Zero),
			initialSelect: "Hjemme", lastPeriod: EndedIn("evening"));

		Advance(rig, TimeSpan.FromMinutes(3));   // three ticks, all still in evening
		CollectionAssert.AreEqual(Array.Empty<string>(), rig.LastPeriod.Saved,
			"the note already says evening, so there is nothing to write");

		Advance(rig, TimeSpan.FromMinutes(30));   // crosses 23:00 into night, then ticks on inside it
		CollectionAssert.AreEqual(new[] { "night" }, rig.LastPeriod.Saved,
			"one write on the change, and none for the ticks that follow it");
	}

	/// <summary>A period that sets no mode writes nothing, however the boundary was detected.</summary>
	[TestMethod]
	public void StartAfterABoundaryWentBy_WithoutSetsMode_WritesNothing()
	{
		// evening@18:00 carries no SetsMode; the note says the last run ended in morning.
		var rig = Started(startAt: Evening, initialSelect: "Hjemme", lastPeriod: EndedIn("morning"));

		Advance(rig, TimeSpan.FromMinutes(30));

		Assert.AreEqual(0, rig.Ha.Calls.Count(c => c.Domain == "input_select" && c.Service == "select_option"));
	}

	/// <summary>
	///     A restart is not an entry, and the period-start reset must not fire for one.
	/// </summary>
	/// <remarks>
	///     Routing the restart through <see cref="ModeMonitor"/>'s entry path would have been fewer lines and would
	///     have cancelled a retained Away or Guest mode as a side effect of a deploy. A reset trigger that fires
	///     because somebody redeployed is not a trigger at all — asserted here with the boundary genuinely crossed,
	///     so the mode is applied and only the reset is withheld.
	/// </remarks>
	[TestMethod]
	public void StartAfterABoundaryWentBy_DoesNotFireThePeriodStartReset()
	{
		var mode = Mode();
		mode.OptionFor("Borte")!.ResetOnPeriodStart = "night";
		var global = new GlobalConfig { CircadianTickSeconds = 60, HouseMode = mode };

		var rig = Started(global, startAt: HalfPastNight, initialSelect: "Borte", lastPeriod: EndedIn("evening"));

		Advance(rig, TimeSpan.FromMinutes(30));

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Sover"), "the boundary went by, so night's mode is applied");
		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"), "but the engine restarted mid-night; it did not enter night");
	}

	/// <summary>
	///     An unreadable select does not spend the restart's one chance; the mode is applied when it answers.
	/// </summary>
	/// <remarks>
	///     After a Home Assistant restart an <c>input_select</c> can read <c>unavailable</c> for a while, which is
	///     exactly the moment this rule exists for. Spending the chance on a value nobody could read would waste it.
	/// </remarks>
	[TestMethod]
	public void StartAfterABoundaryWentBy_WaitsForASelectThatIsNotAnsweringYet()
	{
		var rig = Started(startAt: HalfPastNight, initialSelect: "unavailable", lastPeriod: EndedIn("evening"));

		Advance(rig, TimeSpan.FromMinutes(5));
		Assert.AreEqual(0, SelectCalls(rig.Ha, "Sover"), "nothing is written over a select that has not answered");

		rig.Ha.SetState(Select, "Hjemme");
		Advance(rig, TimeSpan.FromMinutes(1));

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Sover"), "once it answers, the boundary it missed is acted on");
	}

	/// <summary>A first tick that does cross a boundary is an ordinary entry, and is not doubled by the restart rule.</summary>
	[TestMethod]
	public void StartJustBeforeABoundary_EntersNormally_WithoutDoubleSetting()
	{
		var rig = Started(startAt: new DateTimeOffset(2026, 1, 15, 22, 59, 30, TimeSpan.Zero),
			initialSelect: "Hjemme", lastPeriod: EndedIn("morning"));

		Advance(rig, TimeSpan.FromMinutes(5));   // the first tick lands inside night, having crossed 23:00

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Sover"), "entry did the work; the restart rule did not repeat it");
	}

	// ---- Period-start reset -------------------------------------------------------------------

	[TestMethod]
	public void PeriodReset_EnteringNamedPeriod_ResetsToNormal()
	{
		var mode = Mode();
		mode.OptionFor("Sover")!.ResetOnPeriodStart = "morning";
		var global = new GlobalConfig { CircadianTickSeconds = 60, HouseMode = mode };

		var rig = Started(global, startAt: new DateTimeOffset(2026, 1, 16, 6, 0, 0, TimeSpan.Zero), initialSelect: "Sover");

		Advance(rig, TimeSpan.FromMinutes(40));   // past morning@06:30

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Hjemme"), "the waking period ends the night");
	}

	// ---- Presence reset -----------------------------------------------------------------------

	private static GlobalConfig AwayResetsOnPresence(IReadOnlyList<string> sensors)
	{
		var mode = Mode();
		var borte = mode.OptionFor("Borte")!;
		borte.ResetOnPresence = true;
		borte.ResetPresenceSensors = [.. sensors];
		borte.ResetPresenceGraceMinutes = 15;
		return new GlobalConfig { CircadianTickSeconds = 60, HouseMode = mode };
	}

	[TestMethod]
	public void PresenceReset_WithinGrace_Ignored_ThenAfterGrace_Resets()
	{
		var rig = Started(AwayResetsOnPresence([Gang]), startAt: Evening, initialSelect: "Hjemme",
			seed: ha => ha.SetState(Gang, "off"));
		Activate(rig, "Borte");

		Advance(rig, TimeSpan.FromMinutes(5));
		rig.Ha.Trigger(Gang, "on");
		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"), "presence inside the grace is ignored");

		Advance(rig, TimeSpan.FromMinutes(11));    // now 16 min after activation
		rig.Ha.Trigger(Gang, "off");
		rig.Ha.Trigger(Gang, "on");                // a fresh turn-on after the grace
		Assert.AreEqual(1, SelectCalls(rig.Ha, "Hjemme"), "a fresh arrival after the grace resets");
	}

	[TestMethod]
	public void PresenceReset_AlreadyOnAtGraceExpiry_DoesNotReset()
	{
		var rig = Started(AwayResetsOnPresence([Gang]), startAt: Evening, initialSelect: "Hjemme",
			seed: ha => ha.SetState(Gang, "off"));
		Activate(rig, "Borte");

		Advance(rig, TimeSpan.FromMinutes(5));
		rig.Ha.Trigger(Gang, "on");                // turns on inside the grace
		Advance(rig, TimeSpan.FromMinutes(30));    // grace expires with the sensor already on — no new edge

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"), "edge-triggered: an already-on sensor does not reset");
	}

	[TestMethod]
	public void PresenceReset_PersonArrivingHome_Counts()
	{
		var rig = Started(AwayResetsOnPresence([Person]), startAt: Evening, initialSelect: "Hjemme",
			seed: ha => ha.SetState(Person, "not_home"));
		Activate(rig, "Borte");

		Advance(rig, TimeSpan.FromMinutes(20));
		rig.Ha.Trigger(Person, "home");

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Hjemme"), "a person transitioning to home is an arrival");
	}

	[TestMethod]
	public void PresenceReset_DeviceTrackerArrivingHome_Counts()
	{
		var rig = Started(AwayResetsOnPresence([Tracker]), startAt: Evening, initialSelect: "Hjemme",
			seed: ha => ha.SetState(Tracker, "not_home"));
		Activate(rig, "Borte");

		Advance(rig, TimeSpan.FromMinutes(20));
		rig.Ha.Trigger(Tracker, "home");

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Hjemme"),
			"a device_tracker transitioning to home is an arrival, not an on/off edge");
	}

	// ---- auto-away by inactivity ---------------------------------------------------------------

	private static GlobalConfig AwayActivatesOnNoMotion(int minutes)
	{
		var mode = Mode();
		mode.OptionFor("Borte")!.ActivateAfterNoMotionMinutes = minutes;
		return new GlobalConfig { CircadianTickSeconds = 60, HouseMode = mode };
	}

	// A single all-day period, so period entry and SetsMode never interfere with the idle-timer tests.
	private static List<TimePeriodConfig> FlatPeriod() =>
		[new() { Name = "all", Start = "00:00", BrightnessPct = 50, ColorTempKelvin = 3000 }];

	[TestMethod]
	public void NoMotionActivation_AfterIdleWindow_SwitchesToTheMode_Once()
	{
		var rig = Started(AwayActivatesOnNoMotion(360), periods: FlatPeriod(), motion: [Gang],
			startAt: Evening, initialSelect: "Hjemme", seed: ha => ha.SetState(Gang, "off"));

		Advance(rig, TimeSpan.FromHours(5));
		Assert.AreEqual(0, SelectCalls(rig.Ha, "Borte"), "before six quiet hours, nothing happens");

		Advance(rig, TimeSpan.FromHours(2));   // now seven hours idle
		Assert.AreEqual(1, SelectCalls(rig.Ha, "Borte"),
			"six hours with no motion switches the house to Borte, and the latch holds it to one call");
	}

	[TestMethod]
	public void NoMotionActivation_MotionRestartsTheClock()
	{
		var rig = Started(AwayActivatesOnNoMotion(360), periods: FlatPeriod(), motion: [Gang],
			startAt: Evening, initialSelect: "Hjemme", seed: ha => ha.SetState(Gang, "off"));

		Advance(rig, TimeSpan.FromHours(5));
		rig.Ha.Trigger(Gang, "on");            // motion restarts the six-hour clock
		rig.Ha.Trigger(Gang, "off");

		Advance(rig, TimeSpan.FromHours(5));    // only five hours since the motion
		Assert.AreEqual(0, SelectCalls(rig.Ha, "Borte"), "motion restarted the clock");

		Advance(rig, TimeSpan.FromHours(2));    // now past six hours since the motion
		Assert.AreEqual(1, SelectCalls(rig.Ha, "Borte"), "a fresh six quiet hours activates");
	}

	[TestMethod]
	public void NoMotionActivation_AlreadyOnTheMode_DoesNothing()
	{
		var rig = Started(AwayActivatesOnNoMotion(360), periods: FlatPeriod(), motion: [Gang],
			startAt: Evening, initialSelect: "Borte", seed: ha => ha.SetState(Gang, "off"));

		Advance(rig, TimeSpan.FromHours(7));
		Assert.AreEqual(0, SelectCalls(rig.Ha, "Borte"), "already standing on Borte — no redundant switch");
	}

	[TestMethod]
	public void PresenceReset_ToggleOff_DoesNotSubscribe_EvenWithSensorsListed()
	{
		var mode = Mode();
		var borte = mode.OptionFor("Borte")!;
		borte.ResetOnPresence = false;         // the toggle is off
		borte.ResetPresenceSensors = [Gang];   // but a sensor is listed
		var global = new GlobalConfig { CircadianTickSeconds = 60, HouseMode = mode };

		var rig = Started(global, startAt: Evening, initialSelect: "Hjemme", seed: ha => ha.SetState(Gang, "off"));
		Activate(rig, "Borte");

		Advance(rig, TimeSpan.FromMinutes(30));
		rig.Ha.Trigger(Gang, "on");

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"),
			"ResetOnPresence off means no subscription — a listed sensor alone must not reset");
	}

	[TestMethod]
	public void PresenceReset_EmptyList_UsesAreaMotionUnion()
	{
		var rig = Started(AwayResetsOnPresence([]), periods: Periods(), motion: [Kjokken],
			startAt: Evening, initialSelect: "Hjemme", seed: ha => ha.SetState(Kjokken, "off"));
		Activate(rig, "Borte");

		Advance(rig, TimeSpan.FromMinutes(20));
		rig.Ha.Trigger(Kjokken, "on");

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Hjemme"), "an empty sensor list resets on any area motion sensor");
	}

	// ---- ActivateWhileOn overlay --------------------------------------------------------------

	private static GlobalConfig WithActivation(string optionValue, params string[] entities)
	{
		var mode = Mode();
		mode.OptionFor(optionValue)!.ActivateWhileOn = [.. entities];
		return new GlobalConfig { CircadianTickSeconds = 60, HouseMode = mode };
	}

	[TestMethod]
	public void ActivateWhileOn_EntityOn_OverridesSelect()
	{
		var rig = Build(WithActivation("Borte", SleepToggle));
		rig.Ha.SetState(Select, "Hjemme");
		rig.Ha.SetState(SleepToggle, "off");

		Assert.AreEqual(ModeKind.Normal, rig.Monitor.ActiveKind, "with the entity off the select decides");
		Assert.IsNull(rig.Monitor.ActiveScene);

		rig.Ha.SetState(SleepToggle, "on");
		Assert.AreEqual(ModeKind.Away, rig.Monitor.ActiveKind, "the entity on forces Borte over the select's Hjemme");
		Assert.AreEqual("scene.borte", rig.Monitor.ActiveScene, "the effective option's scene is applied");
	}

	[TestMethod]
	public void ActivateWhileOn_Empty_LeavesSelectDeciding()
	{
		var rig = Build();   // Mode() lists no ActivateWhileOn anywhere
		rig.Ha.SetState(Select, "Borte");
		rig.Ha.SetState(SleepToggle, "on");   // an unrelated toggle is on

		Assert.AreEqual(ModeKind.Away, rig.Monitor.ActiveKind, "empty ActivateWhileOn lists leave the select in charge");
	}

	[TestMethod]
	public void ActivateWhileOn_TwoActive_FirstOptionInListWins()
	{
		var mode = Mode();   // list order: Hjemme, Sover, Borte, Gjester
		mode.OptionFor("Sover")!.ActivateWhileOn = [SleepToggle];
		mode.OptionFor("Gjester")!.ActivateWhileOn = [GuestToggle];
		var rig = Build(new GlobalConfig { CircadianTickSeconds = 60, HouseMode = mode });

		rig.Ha.SetState(Select, "Hjemme");
		rig.Ha.SetState(SleepToggle, "on");
		rig.Ha.SetState(GuestToggle, "on");

		Assert.AreEqual(ModeKind.Sleep, rig.Monitor.ActiveKind, "Sover precedes Gjester in the options list, so it wins");
	}

	[TestMethod]
	public void ActivateWhileOn_DoesNotWriteSelect_ButRepublishesOnChange()
	{
		var rig = Started(WithActivation("Borte", SleepToggle), startAt: Evening, initialSelect: "Hjemme",
			seed: ha => ha.SetState(SleepToggle, "off"));

		var changes = 0;
		using var subscription = rig.Monitor.Changed.Subscribe(_ => changes++);

		rig.Ha.Trigger(SleepToggle, "on");

		Assert.AreEqual(ModeKind.Away, rig.Monitor.ActiveKind, "the mode re-evaluates when the listed entity turns on");
		Assert.IsTrue(changes >= 1, "a state change on a listed entity republishes house state");
		Assert.AreEqual(0, rig.Ha.Calls.Count(c => c.Domain == "input_select" && c.Service == "select_option"),
			"the overlay never writes the select back — no feedback loop");
	}

	// ---- what is forcing the mode --------------------------------------------------------------
	//
	// The incident behind all of these: a cabin's Away option listed an input_boolean that had been on for hours,
	// every settings save re-asserted Away, and the only trace was "Everyone left the house" while both person
	// entities read home. The engine reported a cause it had never checked.

	[TestMethod]
	public void Forced_SelectAlone_ReportsNothingForcing()
	{
		Rig rig = Build(WithActivation("Borte", SleepToggle));
		rig.Ha.SetState(Select, "Borte");
		rig.Ha.SetState(SleepToggle, "off");

		Assert.AreEqual(ModeKind.Away, rig.Monitor.ActiveKind, "the select says Borte and nothing is overriding it");
		Assert.IsNull(rig.Monitor.Forced, "a mode somebody chose at the select is not a forced mode");
	}

	[TestMethod]
	public void Forced_WhileEntityOn_NamesTheEntityAndItsState()
	{
		Rig rig = Build(WithActivation("Borte", SleepToggle));
		rig.Ha.SetState(Select, "Hjemme");
		rig.Ha.SetState(SleepToggle, "on");

		ForcedMode forced = rig.Monitor.Forced!;

		Assert.IsNotNull(forced, "an entity holding Away over the select's Hjemme is a forced mode");
		Assert.AreEqual(ModeForceSource.WhileEntityOn, forced.Source);
		Assert.AreEqual(ModeKind.Away, forced.Kind);
		Assert.AreEqual("Borte", forced.OptionValue);
		Assert.AreEqual(SleepToggle, forced.EntityId, "the entity is named, or the reader is left hunting the house");
		Assert.AreEqual("on", forced.EntityState);
	}

	/// <summary>The sentence that would have ended the incident in seconds, pinned verbatim.</summary>
	[TestMethod]
	public void Forced_Describe_NamesTheEntityAndItsStateInOneSentence()
	{
		Rig rig = Build(WithActivation("Borte", "input_boolean.occupancy"));
		rig.Ha.SetState(Select, "Hjemme");
		rig.Ha.SetState("input_boolean.occupancy", "on");

		Assert.AreEqual(
			"Away mode is forced while input_boolean.occupancy is on.",
			rig.Monitor.Forced!.Describe());
	}

	/// <summary>Every kind, not only Away: an entity holding the house asleep is the same fault in a different mode.</summary>
	[TestMethod]
	public void Forced_WhileEntityOn_ReportsSleepAsReadilyAsAway()
	{
		Rig rig = Build(WithActivation("Sover", SleepToggle));
		rig.Ha.SetState(Select, "Hjemme");
		rig.Ha.SetState(SleepToggle, "on");

		ForcedMode forced = rig.Monitor.Forced!;

		Assert.AreEqual(ModeKind.Sleep, forced.Kind);
		Assert.AreEqual("Sleep mode is forced while input_boolean.sover is on.", forced.Describe());
	}

	[TestMethod]
	public void Forced_EntityGoesOff_StopsReportingAForce()
	{
		Rig rig = Started(WithActivation("Borte", SleepToggle), startAt: Evening, initialSelect: "Hjemme",
			seed: ha => ha.SetState(SleepToggle, "on"));

		Assert.IsNotNull(rig.Monitor.Forced);

		rig.Ha.Trigger(SleepToggle, "off");

		Assert.IsNull(rig.Monitor.Forced, "with the entity off the select decides again, and nothing is forced");
	}

	[TestMethod]
	public void Forced_NoMotionActivation_IsReportedAsTheEnginesDoing()
	{
		Rig rig = Started(AwayActivatesOnNoMotion(360), periods: FlatPeriod(), motion: [Gang],
			startAt: Evening, initialSelect: "Hjemme", seed: ha => ha.SetState(Gang, "off"));

		Advance(rig, TimeSpan.FromHours(7));
		Assert.AreEqual(1, SelectCalls(rig.Ha, "Borte"), "seven quiet hours switched the house");

		// Home Assistant echoes the write back, exactly as it would in the house.
		rig.Ha.Trigger(Select, "Borte");

		ForcedMode forced = rig.Monitor.Forced!;

		Assert.IsNotNull(forced, "a mode the idle timer wrote is the engine's doing, not a household decision");
		Assert.AreEqual(ModeForceSource.NoMotionTimeout, forced.Source);
		Assert.AreEqual(ModeKind.Away, forced.Kind);
		Assert.IsNull(forced.EntityId, "no entity is holding it — the house simply went quiet");
		Assert.AreEqual(
			"Away mode was set because the whole house went quiet, not because anyone left.",
			forced.Describe());
	}

	[TestMethod]
	public void Forced_NoMotionActivation_ClaimEndsWhenTheSelectMovesAway()
	{
		Rig rig = Started(AwayActivatesOnNoMotion(360), periods: FlatPeriod(), motion: [Gang],
			startAt: Evening, initialSelect: "Hjemme", seed: ha => ha.SetState(Gang, "off"));

		Advance(rig, TimeSpan.FromHours(7));
		rig.Ha.Trigger(Select, "Borte");
		Assert.IsNotNull(rig.Monitor.Forced);

		// Somebody turns the dial back. The engine's claim on the value goes with it, and a later move onto Borte
		// by hand is that person's doing rather than the idle rule's.
		rig.Ha.Trigger(Select, "Hjemme");
		Assert.IsNull(rig.Monitor.Forced);

		rig.Ha.Trigger(Select, "Borte");
		Assert.IsNull(rig.Monitor.Forced, "a hand-set Borte is not the idle rule's, whatever the rule did earlier");
	}

	// ---- the period select ---------------------------------------------------------------------

	private const string PeriodSelect = "input_select.tid_pa_dagen";

	private static GlobalConfig WithPeriodSelect(PeriodAuthority authority, params (string Value, string Period)[] options) =>
		new()
		{
			CircadianTickSeconds = 60,
			HouseMode = Mode(),
			PeriodSelect = new PeriodSelectConfig
			{
				Entity = PeriodSelect,
				Authority = authority,
				Options = [.. options.Select(row => new PeriodSelectOptionConfig { Value = row.Value, Period = row.Period })]
			}
		};

	/// <summary>The three periods of <see cref="Periods"/>, mapped to a Norwegian dropdown.</summary>
	private static (string Value, string Period)[] Norwegian() =>
		[("Morgen", "morning"), ("Kveld", "evening"), ("Natt", "night")];

	private static int PeriodSelectCalls(FakeHaContext ha, string option) =>
		ha.Calls.Count(c => c.Domain == "input_select" && c.Service == "select_option"
			&& c.Target?.EntityIds?.Contains(PeriodSelect) == true && OptionOf(c) == option);

	private static int AnyPeriodSelectCalls(FakeHaContext ha) =>
		ha.Calls.Count(c => c.Domain == "input_select" && c.Service == "select_option"
			&& c.Target?.EntityIds?.Contains(PeriodSelect) == true);

	[TestMethod]
	public void PeriodSelect_UnderOurAuthority_IsWrittenToTheActivePeriod_Once()
	{
		Rig rig = Started(WithPeriodSelect(PeriodAuthority.AdaptiveLighting, Norwegian()),
			startAt: Evening, initialSelect: "Hjemme", seed: ha => ha.SetState(PeriodSelect, "Morgen"));

		Advance(rig, TimeSpan.FromMinutes(1));

		Assert.AreEqual(1, PeriodSelectCalls(rig.Ha, "Kveld"), "20:00 is the evening period, and the select says so");

		// Home Assistant echoes the write back, exactly as it would in the house.
		rig.Ha.Trigger(PeriodSelect, "Kveld");
		Advance(rig, TimeSpan.FromMinutes(5));

		Assert.AreEqual(1, PeriodSelectCalls(rig.Ha, "Kveld"),
			"the write is idempotent — a select already showing the period is left alone");
	}

	[TestMethod]
	public void PeriodSelect_UnderOurAuthority_FollowsTheBoundary()
	{
		Rig rig = Started(WithPeriodSelect(PeriodAuthority.AdaptiveLighting, Norwegian()),
			startAt: Evening, initialSelect: "Hjemme", seed: ha => ha.SetState(PeriodSelect, "Kveld"));

		Advance(rig, TimeSpan.FromHours(2) + TimeSpan.FromMinutes(59));   // 22:59, still the evening
		Assert.AreEqual(0, AnyPeriodSelectCalls(rig.Ha), "the evening is already showing, so nothing is written");

		Advance(rig, TimeSpan.FromMinutes(1));   // 23:00 — one tick inside the night

		Assert.AreEqual(1, PeriodSelectCalls(rig.Ha, "Natt"), "the boundary moved, so the mirror moved with it");

		rig.Ha.Trigger(PeriodSelect, "Natt");   // Home Assistant echoes it back
		Advance(rig, TimeSpan.FromMinutes(30));

		Assert.AreEqual(1, PeriodSelectCalls(rig.Ha, "Natt"), "and then it is left alone for the rest of the period");
	}

	/// <summary>
	///     A select that never echoes is asked again, rather than being written once and abandoned.
	/// </summary>
	/// <remarks>
	///     The deliberate cost of comparing against what the select actually reads instead of remembering what was
	///     asked for. In the house Home Assistant echoes within milliseconds, so this is one call; the retry is what
	///     makes an option the select rejects, or a helper that came back on the wrong value, self-correcting rather
	///     than permanently wrong. The log line is bounded separately, on the distinct option.
	/// </remarks>
	[TestMethod]
	public void PeriodSelect_UnderOurAuthority_AsksAgainWhileTheSelectStillDisagrees()
	{
		Rig rig = Started(WithPeriodSelect(PeriodAuthority.AdaptiveLighting, Norwegian()),
			startAt: Evening, initialSelect: "Hjemme", seed: ha => ha.SetState(PeriodSelect, "Morgen"));

		Advance(rig, TimeSpan.FromMinutes(3));   // three ticks, and the select never moves

		Assert.AreEqual(3, PeriodSelectCalls(rig.Ha, "Kveld"),
			"a write that landed nowhere is retried, so the mirror cannot be left silently wrong");
	}

	/// <summary>
	///     A select somebody moved by hand — or one that came back from a Home Assistant restart on the wrong
	///     option — is put right rather than left disagreeing with the lights for hours. A remembered "we already
	///     asked for this" latch would have made both of those permanent.
	/// </summary>
	[TestMethod]
	public void PeriodSelect_UnderOurAuthority_CorrectsADriftedSelect()
	{
		Rig rig = Started(WithPeriodSelect(PeriodAuthority.AdaptiveLighting, Norwegian()),
			startAt: Evening, initialSelect: "Hjemme", seed: ha => ha.SetState(PeriodSelect, "Kveld"));

		Advance(rig, TimeSpan.FromMinutes(1));
		Assert.AreEqual(0, AnyPeriodSelectCalls(rig.Ha));

		rig.Ha.Trigger(PeriodSelect, "Morgen");   // somebody turns the dial at eight in the evening

		Assert.AreEqual(1, PeriodSelectCalls(rig.Ha, "Kveld"),
			"the flip is seen at once, and the engine says what the period actually is");
	}

	[TestMethod]
	public void PeriodSelect_UnderOurAuthority_WritesNothing_ForAnUnmappedPeriod()
	{
		Rig rig = Started(WithPeriodSelect(PeriodAuthority.AdaptiveLighting, [("Natt", "night")]),
			startAt: Evening, initialSelect: "Hjemme", seed: ha => ha.SetState(PeriodSelect, "Morgen"));

		Advance(rig, TimeSpan.FromMinutes(1));

		Assert.AreEqual(0, AnyPeriodSelectCalls(rig.Ha),
			"no row maps the evening, so there is nothing to write — and guessing at an option is not the answer");
	}

	/// <summary>The whole of the authority rule, from the outside: Home Assistant's select is never written to.</summary>
	[TestMethod]
	public void PeriodSelect_UnderHomeAssistantAuthority_IsNeverWritten()
	{
		Rig rig = Started(WithPeriodSelect(PeriodAuthority.HomeAssistant, Norwegian()),
			startAt: Evening, initialSelect: "Hjemme", seed: ha => ha.SetState(PeriodSelect, "Morgen"));

		Advance(rig, TimeSpan.FromHours(6));   // across two of the table's boundaries

		Assert.AreEqual(0, AnyPeriodSelectCalls(rig.Ha),
			"the engine follows this select; writing it would have it chasing its own tail through Home Assistant");
	}

	/// <summary>
	///     Under Home Assistant's authority the select <i>is</i> the period boundary, so a period's SetsMode has to
	///     fire on the flip rather than up to a whole tick later.
	/// </summary>
	[TestMethod]
	public void PeriodSelect_UnderHomeAssistantAuthority_AFlipFiresSetsMode()
	{
		Rig rig = Started(WithPeriodSelect(PeriodAuthority.HomeAssistant, Norwegian()),
			startAt: Evening, initialSelect: "Hjemme", seed: ha => ha.SetState(PeriodSelect, "Kveld"));

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Sover"), "the evening sets no mode");

		// Periods() gives night SetsMode: Sover. The clock has not moved at all.
		rig.Ha.Trigger(PeriodSelect, "Natt");

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Sover"),
			"the household selected the night, so the night's mode applies — without waiting for the tick");
	}

	[TestMethod]
	public void PeriodSelect_UnderHomeAssistantAuthority_AnUnmappedOptionLeavesTheScheduleInCharge()
	{
		Rig rig = Started(WithPeriodSelect(PeriodAuthority.HomeAssistant, Norwegian()),
			startAt: Evening, initialSelect: "Hjemme", seed: ha => ha.SetState(PeriodSelect, "Siesta"));

		Advance(rig, TimeSpan.FromHours(4));   // 20:00 → midnight, over the 23:00 night boundary

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Sover"),
			"an option nothing maps is not an opinion, so the clock's own night boundary still arrives");
	}

	[TestMethod]
	public void PeriodSelect_Absent_ChangesNothing()
	{
		Rig rig = Started(startAt: Evening, initialSelect: "Hjemme");

		Advance(rig, TimeSpan.FromHours(4));

		Assert.AreEqual(0, AnyPeriodSelectCalls(rig.Ha), "no select is configured, so none is written");
		Assert.AreEqual(1, SelectCalls(rig.Ha, "Sover"), "and the schedule drives the mode exactly as it always did");
	}

	// ---- Master-switch default ----------------------------------------------------------------

	[TestMethod]
	public void KillSwitch_EnabledFlagPolarity_ByDefault()
	{
		var rig = Build(new GlobalConfig { KillSwitchEntity = "input_boolean.enabled", HouseMode = Mode() });

		rig.Ha.SetState("input_boolean.enabled", "on");
		Assert.IsFalse(rig.Monitor.KillSwitchActive);

		rig.Ha.SetState("input_boolean.enabled", "off");
		Assert.IsTrue(rig.Monitor.KillSwitchActive, "an enabled-flag off means muzzled");
	}

	[TestMethod]
	public void KillSwitch_TrueKillPolarity_WhenActiveWhenOffFalse()
	{
		var rig = Build(new GlobalConfig { KillSwitchEntity = "input_boolean.kill", KillSwitchActiveWhenOff = false, HouseMode = Mode() });

		rig.Ha.SetState("input_boolean.kill", "off");
		Assert.IsFalse(rig.Monitor.KillSwitchActive);

		rig.Ha.SetState("input_boolean.kill", "on");
		Assert.IsTrue(rig.Monitor.KillSwitchActive);
	}

	[TestMethod]
	public void KillSwitch_Unavailable_FailsOpen()
	{
		var rig = Build(new GlobalConfig { KillSwitchEntity = "input_boolean.missing", HouseMode = Mode() });
		Assert.IsFalse(rig.Monitor.KillSwitchActive, "an entity that vanished must not muzzle the house");
	}

	[TestMethod]
	public void KillSwitch_HonoursDefaultedEntity()
	{
		var global = new GlobalConfig
		{
			KillSwitchEntity = null,
			DefaultKillSwitchEntity = "input_boolean.enable",
			HouseMode = Mode()
		};
		var rig = Build(global);

		rig.Ha.SetState("input_boolean.enable", "off");
		Assert.IsTrue(rig.Monitor.KillSwitchActive, "the defaulted enable switch off means the engine is muzzled");

		rig.Ha.SetState("input_boolean.enable", "on");
		Assert.IsFalse(rig.Monitor.KillSwitchActive);
	}

	[TestMethod]
	public void KillSwitch_DefaultedForcesEnabledFlagPolarity_EvenWhenActiveWhenOffFalse()
	{
		// A blank KillSwitchEntity with a defaulted built-in switch: polarity is forced to the enabled-flag reading
		// (off = muzzled), whatever KillSwitchActiveWhenOff says — that flag only governs an explicit entity.
		var global = new GlobalConfig
		{
			KillSwitchEntity = null,
			KillSwitchActiveWhenOff = false,
			DefaultKillSwitchEntity = "input_boolean.enable",
			HouseMode = Mode()
		};
		var rig = Build(global);

		rig.Ha.SetState("input_boolean.enable", "on");
		Assert.IsFalse(rig.Monitor.KillSwitchActive, "the defaulted enable switch on means the engine is NOT killed");

		rig.Ha.SetState("input_boolean.enable", "off");
		Assert.IsTrue(rig.Monitor.KillSwitchActive, "off means muzzled");
	}

	// ---- Reset with no Normal target ----------------------------------------------------------

	[TestMethod]
	public void Reset_NoOp_WhenNoOptionIsNormal()
	{
		// Every option is tagged, so nothing is Normal: a reset trigger has no target and must no-op rather than
		// clobber the select onto a Sleep/Away option.
		var mode = new HouseModeConfig
		{
			Entity = Select,
			Options =
			[
				new() { Value = "Sover", Kind = ModeKind.Sleep },
				new() { Value = "Borte", Kind = ModeKind.Away, ResetOnPresence = true, ResetPresenceSensors = [Gang], ResetPresenceGraceMinutes = 0 }
			]
		};
		var global = new GlobalConfig { CircadianTickSeconds = 60, HouseMode = mode };
		var rig = Started(global, startAt: Evening, initialSelect: "Sover", seed: ha => ha.SetState(Gang, "off"));
		Activate(rig, "Borte");

		Advance(rig, TimeSpan.FromMinutes(1));
		rig.Ha.Trigger(Gang, "on");

		Assert.AreEqual(0, rig.Ha.Calls.Count(c => c.Domain == "input_select" && c.Service == "select_option"),
			"no Normal option resolves, so the reset is a no-op — no select_option is dispatched");
	}

	/// <summary>An <see cref="ILogger"/> that counts warnings, so the once-per-value tripwire can be asserted.</summary>
	private sealed class CountingLogger : ILogger
	{
		public int Warnings { get; private set; }

		public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (logLevel == LogLevel.Warning)
				Warnings++;
		}

		private sealed class NullScope : IDisposable
		{
			public static readonly NullScope Instance = new();

			public void Dispose()
			{
			}
		}
	}
}
