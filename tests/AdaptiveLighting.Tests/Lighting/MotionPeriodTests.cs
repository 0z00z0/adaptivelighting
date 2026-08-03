using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     <see cref="TimePeriodConfig.StartsOnMotion"/>: movement starting a period, and the two bounds that keep it
///     from meaning anything else.
/// </summary>
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

	/// <summary>The three-period table, with morning set to start on motion and to wake the house when it does.</summary>
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
		new() { Name = "evening", Start = "18:00", BrightnessPct = 60, ColorTempKelvin = 2700 },
		new() { Name = "night", Start = "23:00", SetsMode = "Sover", BrightnessPct = 10, ColorTempKelvin = 2200 }
	];

	private sealed record Rig(FakeHaContext Ha, TestScheduler Scheduler, ModeMonitor Monitor);

	private static Rig Started(
		List<TimePeriodConfig> periods,
		DateTimeOffset startAt,
		string initialSelect = "Sover",
		GlobalConfig? global = null,
		IReadOnlyDictionary<string, IReadOnlyList<string>>? areas = null)
	{
		TestScheduler scheduler = new();
		scheduler.AdvanceTo(startAt.Ticks);

		FakeHaContext ha = new();
		ha.SetState(Select, initialSelect);
		ha.SetState(Gang, "off");
		ha.SetState(Kjokken, "off");

		global ??= new GlobalConfig { CircadianTickSeconds = 60, HouseMode = Mode() };

		ModeMonitor monitor = new(
			ha, global, NullLogger.Instance, scheduler, periods, () => SunTimes.Unknown,
			[Gang, Kjokken], null,
			PeriodSelectReader.For(ha, global, NullLogger.Instance),
			areas ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
			{
				[GangArea] = [Gang],
				[KjokkenArea] = [Kjokken]
			});

		monitor.Start();
		return new Rig(ha, scheduler, monitor);
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

	// ---- the Start bound -----------------------------------------------------------------------

	// The rule the whole feature hangs on: a trip to the kitchen in the small hours is not the morning.
	[TestMethod]
	public void Motion_BeforeThePeriodsOwnStart_DoesNotStartIt()
	{
		Rig rig = Started(Periods(), new DateTimeOffset(2026, 1, 15, 2, 0, 0, TimeSpan.Zero));

		Move(rig, Gang);

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"),
			"02:00 is before morning@06:30, so movement cannot pull the morning forward to it");
	}

	// At 02:00 the period in force is the previous evening's night, whose Start has not come round today.
	[TestMethod]
	public void Motion_InAPeriodStillRunningFromYesterday_DoesNotReEnterIt()
	{
		List<TimePeriodConfig> periods = Periods(morningStartsOnMotion: false);
		periods.Single(p => p.Name == "night").StartsOnMotion = true;

		Rig rig = Started(periods, new DateTimeOffset(2026, 1, 15, 2, 0, 0, TimeSpan.Zero), initialSelect: "Hjemme");

		Move(rig, Gang);

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Sover"),
			"night began at 23:00 yesterday; its Start has not come round again, so motion does not restart it");
	}

	// ---- the case that fires --------------------------------------------------------------------

	// The engine came up at 06:40 with no note of the last run, so nothing told it the morning had begun.
	[TestMethod]
	public void Motion_AfterTheStartHasPassed_StartsThePeriod()
	{
		Rig rig = Started(Periods(), QuarterPastMorning);

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"), "nothing has moved the mode yet");

		Move(rig, Gang);

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Hjemme"),
			"the morning's Start had gone by and the engine had not entered it; the movement does");
	}

	[TestMethod]
	public void Motion_WithoutStartsOnMotion_StartsNothing()
	{
		Rig rig = Started(Periods(morningStartsOnMotion: false), QuarterPastMorning);

		Move(rig, Gang);

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"), "the period does not ask to start on motion");
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

		Advance(rig, TimeSpan.FromHours(5));   // lunchtime, still the same period
		Move(rig, Gang);

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Hjemme"),
			"walking back in at lunch does not restart the morning");
	}

	// The tick's own entry has to spend the day's one start, or the mode would be re-asserted over a human.
	[TestMethod]
	public void MotionStart_AfterTheClockAlreadyEnteredThePeriod_DoesNothing()
	{
		Rig rig = Started(Periods(), new DateTimeOffset(2026, 1, 15, 6, 29, 0, TimeSpan.Zero));

		Advance(rig, TimeSpan.FromMinutes(2));   // one tick crosses 06:30
		Assert.AreEqual(1, SelectCalls(rig.Ha, "Hjemme"), "the boundary itself started the morning");

		rig.Ha.Trigger(Select, "Hjemme");
		rig.Ha.Trigger(Select, "Sover");
		Move(rig, Gang);

		Assert.AreEqual(1, SelectCalls(rig.Ha, "Hjemme"),
			"the clock already began the morning today, so movement has nothing left to start");
	}

	// ---- which rooms may start it -----------------------------------------------------------------

	[TestMethod]
	public void MotionStart_NamedRooms_OnlyThoseRoomsSensorsCount()
	{
		Rig rig = Started(Periods(morningStartsOnMotion: true, KjokkenArea), QuarterPastMorning);

		Move(rig, Gang);
		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"),
			"the hall is not the kitchen, and a bedroom sensor must not start the house's morning");

		Move(rig, Kjokken);
		Assert.AreEqual(1, SelectCalls(rig.Ha, "Hjemme"), "movement in the named room starts it");
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

	// The dropdown is the boundary under its own authority, so nothing else may move a period.
	[TestMethod]
	public void MotionStart_UnderHomeAssistantPeriodAuthority_DoesNotStartPeriods()
	{
		GlobalConfig global = new()
		{
			CircadianTickSeconds = 60,
			HouseMode = Mode(),
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
		rig.Ha.SetState(PeriodSelect, "Natt");

		Move(rig, Gang);

		Assert.AreEqual(0, SelectCalls(rig.Ha, "Hjemme"),
			"the household's dropdown owns the time of day; movement does not get a vote");
	}

	private static void Advance(Rig rig, TimeSpan by) => rig.Scheduler.AdvanceBy(by.Ticks);
}
