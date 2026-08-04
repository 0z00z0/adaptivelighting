using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     Which period is in force, asked by the schedule editor and by the room page's levels table.
/// </summary>
[TestClass]
public sealed class ScheduleTests
{
	private static readonly SunTimes Sun = new(new TimeOnly(4, 30), new TimeOnly(21, 45));

	private static List<TimePeriodConfig> Day() =>
	[
		new TimePeriodConfig { Name = "morgen", Start = "06:30" },
		new TimePeriodConfig { Name = "dag", Start = "09:00" },
		new TimePeriodConfig { Name = "kveld", Start = "sunset-01:00" },
		new TimePeriodConfig { Name = "natt", Start = "23:00" }
	];

	[TestMethod]
	public void The_Period_In_Force_Is_The_Latest_Start_Already_Passed()
	{
		Assert.AreEqual("morgen", Schedule.InForceAt(Day(), Sun, new TimeOnly(7, 0))?.Name);
		Assert.AreEqual("dag", Schedule.InForceAt(Day(), Sun, new TimeOnly(12, 0))?.Name);
		Assert.AreEqual("natt", Schedule.InForceAt(Day(), Sun, new TimeOnly(23, 30))?.Name);
	}

	[TestMethod]
	public void The_Small_Hours_Belong_To_Yesterdays_Last_Period()
	{
		Assert.AreEqual("natt", Schedule.InForceAt(Day(), Sun, new TimeOnly(3, 0))?.Name);
	}

	// The running order can differ from the list order: with sunset at 21:45 the evening starts at 20:45, and in
	// December the same string starts it before three in the afternoon.
	[TestMethod]
	public void A_Sun_Anchored_Start_Is_Resolved_Rather_Than_Assumed()
	{
		Assert.AreEqual("dag", Schedule.InForceAt(Day(), Sun, new TimeOnly(20, 0))?.Name);
		Assert.AreEqual("kveld", Schedule.InForceAt(Day(), Sun, new TimeOnly(21, 0))?.Name);

		SunTimes winter = new(new TimeOnly(9, 20), new TimeOnly(15, 10));

		Assert.AreEqual("kveld", Schedule.InForceAt(Day(), winter, new TimeOnly(15, 0))?.Name);
	}

	[TestMethod]
	public void An_Unplaceable_Period_Is_Never_In_Force()
	{
		List<TimePeriodConfig> broken =
		[
			new TimePeriodConfig { Name = "dag", Start = "09:00" },
			new TimePeriodConfig { Name = "tull", Start = "not a time" }
		];

		Assert.AreEqual("dag", Schedule.InForceAt(broken, Sun, new TimeOnly(23, 0))?.Name);

		// Polar night: the sun-anchored boundary has nowhere to sit, so only the clock period resolves.
		List<TimePeriodConfig> sunOnly = [new TimePeriodConfig { Name = "kveld", Start = "sunset" }];

		Assert.IsNull(Schedule.InForceAt(sunOnly, SunTimes.Unknown, new TimeOnly(12, 0)));
	}

	[TestMethod]
	public void An_Empty_Schedule_Has_No_Period_In_Force()
	{
		Assert.IsNull(Schedule.InForceAt([], Sun, new TimeOnly(12, 0)));
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

		Assert.AreEqual("natt", Schedule.InForceNow(Day(), global, Sun, new TimeOnly(12, 0), "Natt")?.Name);
		Assert.AreEqual("morgen", Schedule.InForceNow(Day(), global, Sun, new TimeOnly(23, 30), "Morgen")?.Name);

		Assert.IsTrue(Schedule.HomeAssistantDecides(global));
	}

	// In the mirror direction the engine writes the select from its own schedule, so reading it back could
	// return a stale value.
	[TestMethod]
	public void Under_Adaptive_Lightings_Authority_The_Select_Is_Ignored_Entirely()
	{
		GlobalConfig global = House(PeriodAuthority.AdaptiveLighting);

		Assert.AreEqual("dag", Schedule.InForceNow(Day(), global, Sun, new TimeOnly(12, 0), "Natt")?.Name);
		Assert.IsNull(Schedule.NamedBySelect(Day(), global, "Natt"));
		Assert.IsFalse(Schedule.HomeAssistantDecides(global));
	}

	// PeriodSelectReader.For builds no reader without an entity, so the engine stays on its own schedule.
	[TestMethod]
	public void An_Authority_Without_An_Entity_Decides_Nothing()
	{
		GlobalConfig global = House(PeriodAuthority.HomeAssistant, entity: null);

		Assert.IsFalse(Schedule.HomeAssistantDecides(global));
		Assert.AreEqual("dag", Schedule.InForceNow(Day(), global, Sun, new TimeOnly(12, 0), "Natt")?.Name);

		// A house that has never heard of the feature resolves as it always did.
		Assert.AreEqual("dag", Schedule.InForceNow(Day(), new GlobalConfig(), Sun, new TimeOnly(12, 0), "Natt")?.Name);
	}

	// The three fallbacks are the calculator's own. A page that degrades differently shows a period no room runs.
	[TestMethod]
	public void An_Unreadable_Or_Unmapped_Select_Falls_Back_To_The_Clock()
	{
		GlobalConfig global = House(PeriodAuthority.HomeAssistant);

		Assert.AreEqual("dag", Schedule.InForceNow(Day(), global, Sun, new TimeOnly(12, 0), null)?.Name,
			"unreadable: unknown, unavailable, or no such entity");

		Assert.AreEqual("dag", Schedule.InForceNow(Day(), global, Sun, new TimeOnly(12, 0), "Fest")?.Name,
			"an option no row maps");

		global.PeriodSelect!.Options.Add(new PeriodSelectOptionConfig { Value = "Kveld", PeriodId = "gone" });

		Assert.AreEqual("dag", Schedule.InForceNow(Day(), global, Sun, new TimeOnly(12, 0), "Kveld")?.Name,
			"a mapping naming a period the schedule no longer has");
	}

	[TestMethod]
	public void The_Match_Is_The_Engines_On_Both_Sides()
	{
		GlobalConfig global = House(PeriodAuthority.HomeAssistant);

		// PeriodSelectConfig.PeriodFor trims and ignores case on the select's own value.
		Assert.AreEqual("natt", Schedule.NamedBySelect(Day(), global, "  natt  ")?.Name);

		// Both sides go through TimePeriodConfig.Key, which trims, so a padded id resolves here and in
		// CircadianCalculator.OverriddenPeriod alike. A page can no longer badge what the engine refuses.
		List<TimePeriodConfig> padded =
		[
			new TimePeriodConfig { Id = " natt ", Name = "natt", Start = "23:00" },
			new TimePeriodConfig { Id = "dag", Name = "dag", Start = "09:00" }
		];

		Assert.AreEqual("natt", Schedule.NamedBySelect(padded, global, "Natt")?.Name);
		Assert.AreEqual("natt", Schedule.InForceNow(padded, global, Sun, new TimeOnly(12, 0), "Natt")?.Name);
	}
}
