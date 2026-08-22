using System.Globalization;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Which period is in force, asked by the schedule editor, the room page and the mode cards through one calculator.</summary>
[TestClass]
public sealed class ScheduleTests
{
	private static readonly SunTimes Sun = new(new TimeOnly(4, 30), new TimeOnly(21, 45));

	// UTC, as every other calculator test: TimeZoneInfo.Local would mean a different hour on every box and on CI.
	private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

	private static DateTimeOffset At(int hour, int minute = 0) =>
		new(2026, 8, 11, hour, minute, 0, TimeSpan.Zero);

	private static List<TimePeriodConfig> Day() =>
	[
		new TimePeriodConfig { Name = "morgen", Start = "06:30" },
		new TimePeriodConfig { Name = "dag", Start = "09:00" },
		new TimePeriodConfig { Name = "kveld", Start = "sunset-01:00" },
		new TimePeriodConfig { Name = "natt", Start = "23:00" }
	];

	private static PeriodInForce InForce(
		IReadOnlyList<TimePeriodConfig> periods,
		DateTimeOffset now,
		GlobalConfig? global = null,
		string? selectValue = null,
		SunTimes? sun = null,
		Func<string, DateOnly, bool>? heldBack = null) =>
		Schedule.InForceNow(periods, global ?? new GlobalConfig(), sun ?? Sun, now, selectValue, heldBack, Zone);

	[TestMethod]
	public void The_Period_In_Force_Is_The_Latest_Start_Already_Passed()
	{
		Assert.AreEqual("morgen", InForce(Day(), At(7)).Period?.Name);
		Assert.AreEqual("dag", InForce(Day(), At(12)).Period?.Name);
		Assert.AreEqual("natt", InForce(Day(), At(23, 30)).Period?.Name);
	}

	[TestMethod]
	public void The_Small_Hours_Belong_To_Yesterdays_Last_Period()
	{
		Assert.AreEqual("natt", InForce(Day(), At(3)).Period?.Name);
	}

	// The running order can differ from the list order: with sunset at 21:45 the evening starts at 20:45, and in
	// December the same string starts it before three in the afternoon.
	[TestMethod]
	public void A_Sun_Anchored_Start_Is_Resolved_Rather_Than_Assumed()
	{
		Assert.AreEqual("dag", InForce(Day(), At(20)).Period?.Name);
		Assert.AreEqual("kveld", InForce(Day(), At(21)).Period?.Name);

		SunTimes winter = new(new TimeOnly(9, 20), new TimeOnly(15, 10));

		Assert.AreEqual("kveld", InForce(Day(), At(15), sun: winter).Period?.Name);
	}

	[TestMethod]
	public void An_Unplaceable_Period_Is_Never_In_Force()
	{
		List<TimePeriodConfig> broken =
		[
			new TimePeriodConfig { Name = "dag", Start = "09:00" },
			new TimePeriodConfig { Name = "tull", Start = "not a time" }
		];

		Assert.AreEqual("dag", InForce(broken, At(23)).Period?.Name);

		// Polar night: the sun-anchored boundary has nowhere to sit, so only the clock period resolves.
		List<TimePeriodConfig> sunOnly = [new TimePeriodConfig { Name = "kveld", Start = "sunset" }];

		Assert.IsNull(InForce(sunOnly, At(12), sun: SunTimes.Unknown).Period);
	}

	[TestMethod]
	public void An_Empty_Schedule_Has_No_Period_In_Force()
	{
		Assert.IsNull(InForce([], At(12)).Period);
		Assert.AreEqual(PeriodInForceRule.None, InForce([], At(12)).Rule);
	}

	// ===================== who owns the time of day =====================

	private static GlobalConfig House(PeriodAuthority authority, string? entity = "input_select.tid") =>
		new()
		{
			PeriodSelect = new PeriodSelectConfig
			{
				Entity = entity,
				Authority = authority,
				Options =
				[
					new PeriodSelectOptionConfig { Value = "Natt", PeriodId = "natt" },
					new PeriodSelectOptionConfig { Value = "Morgen", PeriodId = "morgen" }
				]
			}
		};

	// Under Home Assistant's authority the engine takes the period from the dropdown and never from the clock.
	[TestMethod]
	public void Under_Home_Assistant_Authority_The_Select_Decides_And_The_Clock_Does_Not()
	{
		GlobalConfig global = House(PeriodAuthority.HomeAssistant);

		PeriodInForce noon = InForce(Day(), At(12), global, "Natt");

		Assert.AreEqual("natt", noon.Period?.Name);
		Assert.AreEqual(PeriodInForceRule.Select, noon.Rule);

		Assert.AreEqual("morgen", InForce(Day(), At(23, 30), global, "Morgen").Period?.Name);

		Assert.IsTrue(Schedule.HomeAssistantDecides(global));
	}

	// In the mirror direction the engine writes the select from its own schedule, so reading it back could be stale.
	[TestMethod]
	public void Under_Adaptive_Lightings_Authority_The_Select_Is_Ignored_Entirely()
	{
		GlobalConfig global = House(PeriodAuthority.AdaptiveLighting);

		PeriodInForce noon = InForce(Day(), At(12), global, "Natt");

		Assert.AreEqual("dag", noon.Period?.Name);
		Assert.AreEqual(PeriodInForceRule.Clock, noon.Rule);
		Assert.IsNull(Schedule.NamedBySelect(Day(), global, "Natt"));
		Assert.IsFalse(Schedule.HomeAssistantDecides(global));
	}

	// PeriodSelectReader.For builds no reader without an entity, so the engine stays on its own schedule.
	[TestMethod]
	public void An_Authority_Without_An_Entity_Decides_Nothing()
	{
		GlobalConfig global = House(PeriodAuthority.HomeAssistant, entity: null);

		Assert.IsFalse(Schedule.HomeAssistantDecides(global));
		Assert.AreEqual("dag", InForce(Day(), At(12), global, "Natt").Period?.Name);

		// A house that has never heard of the feature resolves as it always did.
		Assert.AreEqual("dag", InForce(Day(), At(12), new GlobalConfig(), "Natt").Period?.Name);
	}

	// The three fallbacks are the calculator's own. A page that degrades differently shows a period no room runs.
	[TestMethod]
	public void An_Unreadable_Or_Unmapped_Select_Falls_Back_To_The_Clock()
	{
		GlobalConfig global = House(PeriodAuthority.HomeAssistant);

		Assert.AreEqual("dag", InForce(Day(), At(12), global, null).Period?.Name,
			"unreadable: unknown, unavailable, or no such entity");

		Assert.AreEqual("dag", InForce(Day(), At(12), global, "Fest").Period?.Name,
			"an option no row maps");

		global.PeriodSelect!.Options.Add(new PeriodSelectOptionConfig { Value = "Kveld", PeriodId = "gone" });

		PeriodInForce stale = InForce(Day(), At(12), global, "Kveld");

		Assert.AreEqual("dag", stale.Period?.Name, "a mapping naming a period the schedule no longer has");
		Assert.AreEqual(PeriodInForceRule.Clock, stale.Rule, "and the badge must not claim the dropdown named it");
	}

	[TestMethod]
	public void The_Match_Is_The_Engines_On_Both_Sides()
	{
		GlobalConfig global = House(PeriodAuthority.HomeAssistant);

		// PeriodSelectConfig.PeriodFor trims and ignores case on the select's own value.
		Assert.AreEqual("natt", Schedule.NamedBySelect(Day(), global, "  natt  ")?.Name);

		// Both sides go through TimePeriodConfig.Key, which trims, so a padded id resolves here and in
		// CircadianCalculator.OverriddenPeriod alike.
		List<TimePeriodConfig> padded =
		[
			new TimePeriodConfig { Id = " natt ", Name = "natt", Start = "23:00" },
			new TimePeriodConfig { Id = "dag", Name = "dag", Start = "09:00" }
		];

		Assert.AreEqual("natt", Schedule.NamedBySelect(padded, global, "Natt")?.Name);
		Assert.AreEqual("natt", InForce(padded, At(12), global, "Natt").Period?.Name);
	}

	// ===================== a period that waits for movement =====================

	private static List<TimePeriodConfig> WaitsForMovement() =>
	[
		new TimePeriodConfig { Name = "natt", Start = "23:00" },
		new TimePeriodConfig { Name = "morgen", Start = "06:30", StartsOnMotion = true },
		new TimePeriodConfig { Name = "dag", Start = "09:00" }
	];

	[TestMethod]
	public void With_The_Engines_Latch_A_Period_Waiting_For_Movement_Leaves_The_Previous_One_In_Force()
	{
		List<TimePeriodConfig> periods = WaitsForMovement();
		MotionPeriodLatch latch = MotionPeriodLatch.For(periods, new GlobalConfig());

		PeriodInForce waiting = InForce(periods, At(7), heldBack: latch.IsHeldBack);

		Assert.AreEqual("natt", waiting.Period?.Name, "07:00 is past 06:30, but nobody has moved");
		Assert.AreEqual(PeriodInForceRule.HeldBack, waiting.Rule);

		latch.MarkBegun("morgen", new DateOnly(2026, 8, 11));

		PeriodInForce begun = InForce(periods, At(7), heldBack: latch.IsHeldBack);

		Assert.AreEqual("morgen", begun.Period?.Name, "movement started it, so it is the period in force");
		Assert.AreEqual(PeriodInForceRule.Clock, begun.Rule);
	}

	// The null predicate is what every caller passes with no engine attached: the clock places every period.
	[TestMethod]
	public void Without_A_Latch_A_Period_Waiting_For_Movement_Is_Placed_By_The_Clock()
	{
		List<TimePeriodConfig> periods = WaitsForMovement();

		PeriodInForce clock = InForce(periods, At(7));

		Assert.AreEqual("morgen", clock.Period?.Name);
		Assert.AreEqual(PeriodInForceRule.Clock, clock.Rule);
	}

	// ===================== the three surfaces agree =====================

	/// <summary>One instant, one document and one latch, driven through the room page's path, the schedule editor's path and <see cref="ModeService.ComputePreview"/>.</summary>
	// Built around the current local instant because ComputePreview resolves in TimeZoneInfo.Local; nothing here
	// asserts a wall clock, only that the three answers are the same one.
	[TestMethod]
	public void The_Room_Page_The_Schedule_Editor_And_The_Mode_Cards_Give_One_Answer()
	{
		DateTimeOffset now = DateTimeOffset.Now;
		TimeOnly local = TimeOnly.FromDateTime(now.LocalDateTime);

		// Relative to now, so the assertion holds in any zone: "previous" has started, "held" has started by the
		// clock and is waiting for movement, "next" has not come round.
		AdaptiveLightingConfig config = new()
		{
			Periods =
			[
				new TimePeriodConfig { Name = "previous", Start = Clock(local.AddHours(-2)), BrightnessPct = 20 },
				new TimePeriodConfig { Name = "held", Start = Clock(local.AddHours(-1)), BrightnessPct = 80, StartsOnMotion = true },
				new TimePeriodConfig { Name = "next", Start = Clock(local.AddHours(1)), BrightnessPct = 50 }
			]
		};

		MotionPeriodLatch latch = MotionPeriodLatch.For(config.Periods, config.Global);
		Func<string, DateOnly, bool> heldBack = latch.IsHeldBack;
		SunTimes sun = SunTimes.Unknown;

		// Room.razor: the document's own periods and globals, the page's instant, the engine's latch.
		string? roomPage = Schedule
			.InForceNow(config.Periods, config.Global, sun, now, null, heldBack)
			.Period?.Name;

		// PeriodsEditor.razor: the draft list it is editing, the same rule.
		PeriodInForce editor = Schedule.InForceNow(config.Periods, config.Global, sun, now, null, heldBack);

		// ModeService: one card per house-mode option, resolved through the same calculator.
		string? modeCard = ModeService
			.ComputePreview(config, ModeKind.Normal, now, sun, null, heldBack)
			.ActivePeriodName;

		Assert.AreEqual("previous", roomPage, "the held period has not begun, so the previous one is still running");
		Assert.AreEqual(roomPage, editor.Period?.Name);
		Assert.AreEqual(roomPage, modeCard);
		Assert.AreEqual(PeriodInForceRule.HeldBack, editor.Rule, "and the editor's hover can say why");
	}

	private static string Clock(TimeOnly time) => time.ToString("HH\\:mm", CultureInfo.InvariantCulture);
}
