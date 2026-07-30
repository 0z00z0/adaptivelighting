using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     Which period is in force, asked by the schedule editor and by the room page's levels table.
/// </summary>
/// <remarks>
///     Both surfaces mark the running period, and the answer is not the list's first or last entry: a sun-anchored
///     boundary moves through the year, and before the day's first boundary the period in force is yesterday's.
/// </remarks>
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

	/// <summary>The period in force is the most recent start at or before now.</summary>
	[TestMethod]
	public void The_Period_In_Force_Is_The_Latest_Start_Already_Passed()
	{
		Assert.AreEqual("morgen", Schedule.InForceAt(Day(), Sun, new TimeOnly(7, 0))?.Name);
		Assert.AreEqual("dag", Schedule.InForceAt(Day(), Sun, new TimeOnly(12, 0))?.Name);
		Assert.AreEqual("natt", Schedule.InForceAt(Day(), Sun, new TimeOnly(23, 30))?.Name);
	}

	/// <summary>Before the day's first boundary, the period in force began yesterday.</summary>
	[TestMethod]
	public void The_Small_Hours_Belong_To_Yesterdays_Last_Period()
	{
		Assert.AreEqual("natt", Schedule.InForceAt(Day(), Sun, new TimeOnly(3, 0))?.Name);
	}

	/// <summary>
	///     A sun-anchored start is resolved, so the running order can differ from the list order.
	/// </summary>
	/// <remarks>
	///     With sunset at 21:45 the evening starts at 20:45; in December the same string starts it before three in
	///     the afternoon, and a surface reading the list order would badge the wrong row half the year.
	/// </remarks>
	[TestMethod]
	public void A_Sun_Anchored_Start_Is_Resolved_Rather_Than_Assumed()
	{
		Assert.AreEqual("dag", Schedule.InForceAt(Day(), Sun, new TimeOnly(20, 0))?.Name);
		Assert.AreEqual("kveld", Schedule.InForceAt(Day(), Sun, new TimeOnly(21, 0))?.Name);

		SunTimes winter = new(new TimeOnly(9, 20), new TimeOnly(15, 10));

		Assert.AreEqual("kveld", Schedule.InForceAt(Day(), winter, new TimeOnly(15, 0))?.Name);
	}

	/// <summary>A period the engine cannot place is never "now", because the engine cannot run it either.</summary>
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

	/// <summary>An empty schedule has no period in force, rather than a first row by default.</summary>
	[TestMethod]
	public void An_Empty_Schedule_Has_No_Period_In_Force()
	{
		Assert.IsNull(Schedule.InForceAt([], Sun, new TimeOnly(12, 0)));
	}

	// ===================== who owns the time of day =====================

	/// <summary>A house with a select, in whichever direction its authority names.</summary>
	private static GlobalConfig House(PeriodAuthority authority, string? entity = "input_select.tid") =>
		new()
		{
			PeriodSelect = new PeriodSelectConfig
			{
				Entity = entity,
				Authority = authority,
				Options =
				[
					new PeriodSelectOptionConfig { Value = "Natt", Period = "natt" },
					new PeriodSelectOptionConfig { Value = "Morgen", Period = "morgen" }
				]
			}
		};

	/// <summary>
	///     <b>The whole reason this pair exists.</b> Under Home Assistant's authority the engine takes the period
	///     from the dropdown and never from the clock, and a page that went on resolving from the clock would badge
	///     a period no room was running — at noon, with the dropdown on Natt, it said "dag".
	/// </summary>
	[TestMethod]
	public void Under_Home_Assistant_Authority_The_Select_Decides_And_The_Clock_Does_Not()
	{
		GlobalConfig global = House(PeriodAuthority.HomeAssistant);

		Assert.AreEqual("natt", Schedule.InForceNow(Day(), global, Sun, new TimeOnly(12, 0), "Natt")?.Name);
		Assert.AreEqual("morgen", Schedule.InForceNow(Day(), global, Sun, new TimeOnly(23, 30), "Morgen")?.Name);

		Assert.IsTrue(Schedule.HomeAssistantDecides(global));
	}

	/// <summary>
	///     The mirror direction reads nothing. There the engine writes the select from its own schedule, so a page
	///     that took the select as an input could be shown a stale mirror and believe it.
	/// </summary>
	[TestMethod]
	public void Under_Adaptive_Lightings_Authority_The_Select_Is_Ignored_Entirely()
	{
		GlobalConfig global = House(PeriodAuthority.AdaptiveLighting);

		Assert.AreEqual("dag", Schedule.InForceNow(Day(), global, Sun, new TimeOnly(12, 0), "Natt")?.Name);
		Assert.IsNull(Schedule.NamedBySelect(Day(), global, "Natt"));
		Assert.IsFalse(Schedule.HomeAssistantDecides(global));
	}

	/// <summary>
	///     An authority with no entity behind it is not an authority: <c>PeriodSelectReader.For</c> builds no
	///     reader without one, so the engine is still running entirely off its own schedule — and a page that
	///     announced the start times were dead would be announcing it about a rule that is still deciding.
	/// </summary>
	[TestMethod]
	public void An_Authority_Without_An_Entity_Decides_Nothing()
	{
		GlobalConfig global = House(PeriodAuthority.HomeAssistant, entity: null);

		Assert.IsFalse(Schedule.HomeAssistantDecides(global));
		Assert.AreEqual("dag", Schedule.InForceNow(Day(), global, Sun, new TimeOnly(12, 0), "Natt")?.Name);

		// A house that has never heard of the feature resolves exactly as it always did.
		Assert.AreEqual("dag", Schedule.InForceNow(Day(), new GlobalConfig(), Sun, new TimeOnly(12, 0), "Natt")?.Name);
	}

	/// <summary>
	///     <b>It falls back for exactly the reasons the engine falls back.</b> An unreadable select, a value no row
	///     maps, and a mapping naming a period the schedule no longer has all leave the calculator resolving from
	///     the clock, so the page has to do the same or its degraded state is a different one from the engine's.
	/// </summary>
	[TestMethod]
	public void An_Unreadable_Or_Unmapped_Select_Falls_Back_To_The_Clock()
	{
		GlobalConfig global = House(PeriodAuthority.HomeAssistant);

		Assert.AreEqual("dag", Schedule.InForceNow(Day(), global, Sun, new TimeOnly(12, 0), null)?.Name,
			"unreadable: unknown, unavailable, or no such entity");

		Assert.AreEqual("dag", Schedule.InForceNow(Day(), global, Sun, new TimeOnly(12, 0), "Fest")?.Name,
			"an option no row maps");

		global.PeriodSelect!.Options.Add(new PeriodSelectOptionConfig { Value = "Kveld", Period = "gone" });

		Assert.AreEqual("dag", Schedule.InForceNow(Day(), global, Sun, new TimeOnly(12, 0), "Kveld")?.Name,
			"a mapping naming a period the schedule no longer has");
	}

	/// <summary>
	///     The option string is compared loosely and the period name is not, because that is exactly how the
	///     engine compares them. A page stricter or looser than the engine on either side would resolve a
	///     different row from the one the lights are running.
	/// </summary>
	[TestMethod]
	public void The_Match_Is_The_Engines_On_Both_Sides()
	{
		GlobalConfig global = House(PeriodAuthority.HomeAssistant);

		// PeriodSelectConfig.PeriodFor trims and ignores case on the select's own value.
		Assert.AreEqual("natt", Schedule.NamedBySelect(Day(), global, "  natt  ")?.Name);

		// CircadianCalculator.OverriddenPeriod compares TimePeriodConfig.Name as it stands, so a period whose name
		// carries a stray space is one the engine leaves on the schedule — and so does this.
		List<TimePeriodConfig> padded =
		[
			new TimePeriodConfig { Name = " natt ", Start = "23:00" },
			new TimePeriodConfig { Name = "dag", Start = "09:00" }
		];

		Assert.IsNull(Schedule.NamedBySelect(padded, global, "Natt"));
		Assert.AreEqual("dag", Schedule.InForceNow(padded, global, Sun, new TimeOnly(12, 0), "Natt")?.Name);
	}
}
