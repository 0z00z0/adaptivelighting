using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     <see cref="TimePeriodConfig.StartsOnMotion"/>: a period that does not begin at its <c>Start</c>, what
///     movement does to it, and the three bounds that keep it from meaning anything else.
/// </summary>
/// <remarks>
///     Every rig carries the calculator an area would be built with, on the same
///     <see cref="MotionPeriodLatch"/> the monitor writes, because the point of the feature is the levels and not
///     only the house mode.
/// </remarks>
[TestClass]
public sealed class MotionPeriodTests
{
	private const string Select = "input_select.husmodus";
	private const string PeriodSelect = "input_select.tid_pa_dagen";
	private const string Gang = "binary_sensor.gang_bevegelse";
	private const string Kjokken = "binary_sensor.kjokken_bevegelse";
	private const string GangArea = "gang";
	private const string KjokkenArea = "kjokken";

	// A quarter of an hour after morning@06:30, so the period's own Start has gone by and the clock is inside it.
	private static readonly DateTimeOffset QuarterPastMorning = new(2026, 1, 15, 6, 45, 0, TimeSpan.Zero);

	private static HouseModeConfig Mode() => new()
	{
		Entity = Select,
		Options =
		[
			new() { Value = "Hjemme", Kind = ModeKind.Normal },
			new() { Value = "Sover", Kind = ModeKind.Sleep }
		]
	};

	/// <summary>The four-period table, with morning set to wait for movement and to wake the house when it comes.</summary>
	private static List<TimePeriodConfig> Periods(bool morningStartsOnMotion = true, params string[] morningAreas) =>
	[
		new()
		{
			Name = "morning",
			Start = "06:30",
			SetsMode = "Hjemme",
			StartsOnMotion = morningStartsOnMotion,
			StartsOnMotionAreas = [.. morningAreas],
			BrightnessPct = 60,
			ColorTempKelvin = 3000
		},
		new() { Name = "day", Start = "09:00", BrightnessPct = 90, ColorTempKelvin = 4000 },
		new() { Name = "evening", Start = "18:00", BrightnessPct = 60, ColorTempKelvin = 2700 },
		new() { Name = "night", Start = "23:00", SetsMode = "Sover", BrightnessPct = 10, ColorTempKelvin = 2200 }
	];

	/// <summary>The note recording which period the last run ended in. <c>null</c> is a first run or a lost note.</summary>
	private sealed class FakeLastPeriodStore(string? recalled = null) : ILastPeriodStore
	{
		/// <summary>Every period written, in order.</summary>
		public List<string> Saved { get; } = [];

		public string? Load() => recalled;

		public bool TrySave(string periodName)
		{
			Saved.Add(periodName);
			return true;
		}
	}

	private sealed record Rig(FakeHaContext Ha, TestScheduler Scheduler, ModeMonitor Monitor, CircadianCalculator Rooms);

	private static Rig Started(
		List<TimePeriodConfig> periods,
		DateTimeOffset startAt,
		string initialSelect = "Sover",
		GlobalConfig? global = null,
		IReadOnlyDictionary<string, IReadOnlyList<string>>? areas = null,
		ILastPeriodStore? lastPeriod = null)
	{
		TestScheduler scheduler = new();
		scheduler.AdvanceTo(startAt.Ticks);

		FakeHaContext ha = new();
		ha.SetState(Select, initialSelect);
		ha.SetState(Gang, "off");
		ha.SetState(Kjokken, "off");

		// Stepped, so a level assertion reads the period's own numbers and not a point on the blend.
		global ??= new GlobalConfig { CircadianTickSeconds = 60, HouseMode = Mode(), SmoothTransitions = false };

		PeriodSelectReader? periodSelect = PeriodSelectReader.For(ha, global, NullLogger.Instance);

		// What the orchestrator does: one latch, handed to the monitor and to every area's calculator.
		MotionPeriodLatch latch = MotionPeriodLatch.For(periods, global);

		ModeMonitor monitor = new(
			ha, global, NullLogger.Instance, scheduler, periods, () => SunTimes.Unknown,
			[Gang, Kjokken], lastPeriod,
			periodSelect,
			areas ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
			{
				[GangArea] = [Gang],
				[KjokkenArea] = [Kjokken]
			},
			latch);

		CircadianCalculator rooms = new(
			periods, global, () => SunTimes.Unknown, null, periodSelect?.ReadPeriod, latch.IsHeldBack);

		monitor.Start();
		return new Rig(ha, scheduler, monitor, rooms);
	}

	private static void Move(Rig rig, string sensor)
	{
		rig.Ha.Trigger(sensor, "on");
		rig.Ha.Trigger(sensor, "off");
	}

	private static int SelectCalls(FakeHaContext ha, string option) =>
		ha.Calls.Count(c => c.Domain == "input_select" && c.Service == "select_option"
			&& c.Target?.EntityIds?.Contains(Select) == true
			&& c.Data?.GetType().GetProperty("option")?.GetValue(c.Data) as string == option);

	// ---- the clock does not start it ---------------------------------------------------------------

	// The rule the rewrite turns on: the boundary going by is not the period beginning.
	[TestMethod]
	public void TheClock_DoesNotStartAPeriodThatWaitsForMotion()
	{
		Rig rig = Started(Periods(), new DateTimeOffset(2026, 1, 15, 6, 29, 0, TimeSpan.Zero));

		Advance(rig, TimeSpan.FromMinutes(2));   // one tick crosses 06:30
		DateTimeOffset now = new(2026, 1, 15, 6, 31, 0, TimeSpan.Zero);

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"), "06:30 went by and nothing moved, so the morning has not begun");
		Assert.AreEqual("night", rig.Rooms.ActivePeriodName(now), "the house is still on last night's period");
		Assert.AreEqual(10d, rig.Rooms.GetTarget(now)!.BrightnessPct, "and on last night's levels");
	}

	// The two answers are resolved once and shared, so a held period cannot be in force for one and not the other.
	[TestMethod]
	public void AHeldPeriod_IsAbsentFromBothPublicAnswers()
	{
		Rig rig = Started(Periods(), QuarterPastMorning);

		Assert.AreEqual("night", rig.Rooms.ActivePeriodName(QuarterPastMorning));
		Assert.AreEqual("night", rig.Rooms.GetTarget(QuarterPastMorning)!.PeriodName);
	}

	// ---- the Start bound -----------------------------------------------------------------------

	// A trip to the kitchen in the small hours is not the morning.
	[TestMethod]
	public void Motion_BeforeThePeriodsOwnStart_DoesNotStartIt()
	{
		Rig rig = Started(Periods(), new DateTimeOffset(2026, 1, 15, 2, 0, 0, TimeSpan.Zero));

		Move(rig, Gang);

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"),
			"02:00 is before morning@06:30, so movement cannot pull the morning forward to it");
	}

	// At 02:00 the instance of night that would be in force began at 23:00 yesterday, not today.
	[TestMethod]
	public void Motion_InAPeriodStillRunningFromYesterday_DoesNotStartIt()
	{
		List<TimePeriodConfig> periods = Periods(morningStartsOnMotion: false);
		periods.Single(p => p.Name == "night").StartsOnMotion = true;

		Rig rig = Started(periods, new DateTimeOffset(2026, 1, 15, 2, 0, 0, TimeSpan.Zero), initialSelect: "Hjemme");

		Move(rig, Gang);

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Sover"),
			"night's Start has not come round again today, so movement does not start it");
	}

	// ---- the case that fires --------------------------------------------------------------------

	[TestMethod]
	public void Motion_AfterTheStartHasPassed_StartsThePeriod_AndTheLevelsChange()
	{
		Rig rig = Started(Periods(), QuarterPastMorning);

		Assert.AreEqual(10d, rig.Rooms.GetTarget(QuarterPastMorning)!.BrightnessPct, "night levels until somebody moves");
		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"), "and nothing has moved the mode yet");

		Move(rig, Gang);

		LightTarget target = rig.Rooms.GetTarget(QuarterPastMorning)!;

		Assert.AreEqual("morning", target.PeriodName, "the movement begins the period for the whole house");
		Assert.AreEqual(60d, target.BrightnessPct);
		Assert.AreEqual(3000, target.ColorTempKelvin, "warmth arrives with the brightness, not on a later tick");
		Assert.AreEqual(1, SelectCalls(rig.Ha, "Hjemme"), "and so does its SetsMode");
	}

	[TestMethod]
	public void Motion_WithoutStartsOnMotion_StartsNothing()
	{
		Rig rig = Started(Periods(morningStartsOnMotion: false), QuarterPastMorning);

		Move(rig, Gang);

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"), "the period does not ask to start on motion");
		Assert.AreEqual("morning", rig.Rooms.ActivePeriodName(QuarterPastMorning),
			"and it began on the clock like any other period");
	}

	// ---- it falls through on its own ---------------------------------------------------------------

	// An empty house must never be stranded on last night's levels.
	[TestMethod]
	public void TheNextPeriodsStart_OvertakesAPeriodThatNeverBegan()
	{
		Rig rig = Started(Periods(), QuarterPastMorning);
		DateTimeOffset afterDayBegins = new(2026, 1, 15, 9, 5, 0, TimeSpan.Zero);

		Advance(rig, TimeSpan.FromHours(3));   // through 09:00 with nobody home

		Assert.AreEqual("day", rig.Rooms.ActivePeriodName(afterDayBegins), "day@09:00 overtakes a morning that never began");
		Assert.AreEqual(90d, rig.Rooms.GetTarget(afterDayBegins)!.BrightnessPct);
		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"), "and the morning's SetsMode never fired");
	}

	// Once overtaken it is not on offer any more: the schedule has moved past it.
	[TestMethod]
	public void MotionAfterTheOvertake_DoesNotStartTheSkippedPeriod()
	{
		Rig rig = Started(Periods(), QuarterPastMorning);

		Advance(rig, TimeSpan.FromHours(3));
		Move(rig, Gang);

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"), "the day is in force; the morning is over, started or not");
	}

	// ---- once per local day ----------------------------------------------------------------------

	[TestMethod]
	public void MotionStart_FiresOnceADay_EvenIfTheModeIsChangedSince()
	{
		Rig rig = Started(Periods(), QuarterPastMorning);

		Move(rig, Gang);
		Assert.AreEqual(1, SelectCalls(rig.Ha, "Hjemme"));

		rig.Ha.Trigger(Select, "Hjemme");   // Home Assistant echoes the write back
		rig.Ha.Trigger(Select, "Sover");    // and somebody goes back to bed

		Advance(rig, TimeSpan.FromHours(2));   // 08:45, still the morning
		Move(rig, Gang);

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Hjemme"), "walking back in does not restart the morning");
	}

	// ---- a restart inside the period ---------------------------------------------------------------

	// The note says the last run was already in the morning, so this start is not the period beginning.
	[TestMethod]
	public void ARestartInsideAPeriodThatHadBegun_DoesNotReFireItsModeOnTheNextMovement()
	{
		Rig rig = Started(Periods(), QuarterPastMorning, lastPeriod: new FakeLastPeriodStore("morning"));

		Assert.AreEqual("morning", rig.Rooms.ActivePeriodName(QuarterPastMorning),
			"the note is the evidence that the morning had already begun; the house does not fall back to night");

		Move(rig, Gang);

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"),
			"the morning is already this day's, so movement must not re-assert its mode over a mode somebody chose");
	}

	// Without the note there is no evidence it ever began, so the house waits as it would have without the restart.
	[TestMethod]
	public void ARestartInsideAHeldPeriod_WithNoNote_StillWaitsForMovement()
	{
		Rig rig = Started(Periods(), QuarterPastMorning, lastPeriod: new FakeLastPeriodStore());

		Assert.AreEqual("night", rig.Rooms.ActivePeriodName(QuarterPastMorning));

		Move(rig, Gang);

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Hjemme"));
		Assert.AreEqual("morning", rig.Rooms.ActivePeriodName(QuarterPastMorning));
	}

	// The latch is in memory and a config save rebuilds the engine, so the note is what carries the start across.
	[TestMethod]
	public void MotionStart_RecordsThePeriodAtOnce_NotOnTheNextTick()
	{
		FakeLastPeriodStore note = new();
		Rig rig = Started(Periods(), QuarterPastMorning, lastPeriod: note);

		Move(rig, Gang);

		Assert.AreEqual("morning", note.Saved.LastOrDefault(),
			"a rebuild between the movement and the next tick would otherwise drop the house back to night");
	}

	// A note naming the period before it is a boundary that went by while the engine was down. It still waits.
	[TestMethod]
	public void ARestartWithTheNoteNamingThePreviousPeriod_StillWaitsForMovement()
	{
		Rig rig = Started(Periods(), QuarterPastMorning, lastPeriod: new FakeLastPeriodStore("night"));

		Assert.AreEqual("night", rig.Rooms.ActivePeriodName(QuarterPastMorning),
			"the last run ended in the night, which is exactly the period that keeps running");
		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"));
	}

	// ---- which rooms may start it -----------------------------------------------------------------

	[TestMethod]
	public void MotionStart_NamedRooms_OnlyThoseRoomsSensorsCount()
	{
		Rig rig = Started(Periods(morningStartsOnMotion: true, KjokkenArea), QuarterPastMorning);

		Move(rig, Gang);
		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"),
			"the hall is not the kitchen, and a bedroom sensor must not start the house's morning");
		Assert.AreEqual("night", rig.Rooms.ActivePeriodName(QuarterPastMorning), "nor move the levels");

		Move(rig, Kjokken);
		Assert.AreEqual(1, SelectCalls(rig.Ha, "Hjemme"), "movement in the named room starts it");
		Assert.AreEqual("morning", rig.Rooms.ActivePeriodName(QuarterPastMorning));
	}

	[TestMethod]
	public void MotionStart_NoRoomsNamed_AnyWatchedSensorCounts()
	{
		Rig rig = Started(Periods(), QuarterPastMorning);

		Move(rig, Kjokken);

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Hjemme"), "an empty room list means any room the engine watches");
	}

	// An area id nothing resolves must never widen to "any room"; it can only fire nowhere.
	[TestMethod]
	public void MotionStart_NamedRoomThatResolvesNoSensor_NeverFires()
	{
		Rig rig = Started(Periods(morningStartsOnMotion: true, "loft"), QuarterPastMorning);

		Move(rig, Gang);
		Move(rig, Kjokken);

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"),
			"a named room with no motion sensor resolves to nothing, not to everything");
	}

	// ---- Home Assistant's period authority ---------------------------------------------------------

	// The dropdown is the boundary under its own authority, so nothing is held back and nothing is started.
	[TestMethod]
	public void UnderHomeAssistantPeriodAuthority_NothingIsHeldBack_AndMotionStartsNothing()
	{
		GlobalConfig global = new()
		{
			CircadianTickSeconds = 60,
			HouseMode = Mode(),
			SmoothTransitions = false,
			PeriodSelect = new PeriodSelectConfig
			{
				Entity = PeriodSelect,
				Authority = PeriodAuthority.HomeAssistant,
				Options =
				[
					new() { Value = "Morgen", Period = "morning" },
					new() { Value = "Natt", Period = "night" }
				]
			}
		};

		Rig rig = Started(Periods(), QuarterPastMorning, global: global);
		rig.Ha.SetState(PeriodSelect, "Morgen");

		Assert.AreEqual("morning", rig.Rooms.ActivePeriodName(QuarterPastMorning),
			"the dropdown says morning, so the rooms run the morning without waiting for anybody");

		rig.Ha.SetState(PeriodSelect, "Natt");
		Move(rig, Gang);

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"),
			"the household's dropdown owns the time of day; movement does not get a vote");
		Assert.AreEqual("night", rig.Rooms.ActivePeriodName(QuarterPastMorning));
	}

	private static void Advance(Rig rig, TimeSpan by) => rig.Scheduler.AdvanceBy(by.Ticks);
}
