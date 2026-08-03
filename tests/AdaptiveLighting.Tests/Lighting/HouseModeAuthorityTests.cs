using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     Who owns the house mode, and which of the engine's own ways of setting it that leaves dormant. The pages
///     draw the "not in force" lines from these answers, so a wrong one leaves a live rule looking dead.
/// </summary>
[TestClass]
public sealed class HouseModeAuthorityTests
{
	private static List<TimePeriodConfig> Day() =>
	[
		new TimePeriodConfig { Name = "dag", Start = "09:00" },
		new TimePeriodConfig { Name = "natt", Start = "23:00", SetsMode = "Sover" }
	];

	private static GlobalConfig House(HouseModeAuthority authority, string? entity = "input_select.husmodus") =>
		new()
		{
			HouseMode = new HouseModeConfig
			{
				Entity = entity,
				Authority = authority,
				Options =
				[
					new HouseModeOptionConfig { Value = "Normal", Kind = ModeKind.Normal },
					new HouseModeOptionConfig
					{
						Value = "Sover",
						Kind = ModeKind.Sleep,
						ActivateWhileOn = ["input_boolean.sover"]
					},
					new HouseModeOptionConfig
					{
						Value = "Borte",
						Kind = ModeKind.Away,
						ActivateAfterNoMotionMinutes = 360,
						ResetOnPresence = true
					}
				]
			}
		};

	[TestMethod]
	public void Home_Assistants_Authority_With_An_Entity_Owns_The_Mode()
	{
		Assert.IsTrue(ModeAuthority.HomeAssistantDecides(House(HouseModeAuthority.HomeAssistant)));
	}

	[TestMethod]
	public void Adaptive_Lightings_Authority_Leaves_The_Mode_To_The_Engine()
	{
		GlobalConfig global = House(HouseModeAuthority.AdaptiveLighting);

		Assert.IsFalse(ModeAuthority.HomeAssistantDecides(global));
		Assert.IsFalse(ModeAuthority.Dormant(global, Day()).Any);
	}

	// The engine binds nothing without an entity, so an authority naming none must stand no rule down.
	[TestMethod]
	public void An_Authority_Without_An_Entity_Decides_Nothing()
	{
		GlobalConfig global = House(HouseModeAuthority.HomeAssistant, entity: null);

		Assert.IsFalse(ModeAuthority.HomeAssistantDecides(global));
		Assert.IsFalse(ModeAuthority.Dormant(global, Day()).Any);

		// A house that has never heard of the feature answers the same way, with no HouseMode block at all.
		Assert.IsFalse(ModeAuthority.HomeAssistantDecides(new GlobalConfig()));
		Assert.IsFalse(ModeAuthority.Dormant(new GlobalConfig(), Day()).Any);
	}

	// EntityId trims, so a hand-edited file holding nothing but spaces is an entity nobody can read.
	[TestMethod]
	public void An_Entity_Of_Nothing_But_Spaces_Decides_Nothing()
	{
		GlobalConfig global = House(HouseModeAuthority.HomeAssistant, entity: "   ");

		Assert.IsFalse(ModeAuthority.HomeAssistantDecides(global));
		Assert.IsFalse(ModeAuthority.Dormant(global, Day()).Any);
	}

	[TestMethod]
	public void Under_Home_Assistants_Authority_Every_Engine_Side_Mode_Rule_Reports_Dormant()
	{
		DormantModeRules dormant = ModeAuthority.Dormant(House(HouseModeAuthority.HomeAssistant), Day());

		Assert.IsTrue(dormant.Any);
		Assert.AreEqual(1, dormant.SetsModePeriods);
		Assert.AreEqual(1, dormant.ActivateWhileOnOptions);
		Assert.AreEqual(1, dormant.AutoAwayOptions);
		Assert.AreEqual(1, dormant.ResetTriggerOptions);
		Assert.AreEqual(4, dormant.Names.Count);
	}

	// ModeMonitor.Reset stands down with the three rules that set the mode, so the page must say so too.
	[TestMethod]
	public void A_Reset_Trigger_Reports_Dormant_Because_It_Writes_The_Select_Too()
	{
		GlobalConfig global = House(HouseModeAuthority.HomeAssistant);
		global.HouseMode!.Options[1].ResetOnPeriodStart = "morgen";

		Assert.AreEqual(2, ModeAuthority.Dormant(global, Day()).ResetTriggerOptions);
	}

	// The reset target has no reset trigger of its own; ConfigValidator refuses the fields on a Normal option.
	[TestMethod]
	public void A_Normal_Option_Is_Never_Counted_As_A_Dormant_Reset()
	{
		GlobalConfig global = House(HouseModeAuthority.HomeAssistant);
		global.HouseMode!.Options[0].ResetOnPresence = true;

		Assert.AreEqual(1, ModeAuthority.Dormant(global, Day()).ResetTriggerOptions);
	}

	// Counted from the document, so a house configuring none of the three has nothing to report and the page
	// renders no notice at all.
	[TestMethod]
	public void A_Rule_Nobody_Configured_Is_Not_Reported_As_Dormant()
	{
		GlobalConfig global = new()
		{
			HouseMode = new HouseModeConfig
			{
				Entity = "input_select.husmodus",
				Authority = HouseModeAuthority.HomeAssistant,
				Options = [new HouseModeOptionConfig { Value = "Normal", Kind = ModeKind.Normal }]
			}
		};

		List<TimePeriodConfig> plain = [new TimePeriodConfig { Name = "dag", Start = "09:00" }];

		DormantModeRules dormant = ModeAuthority.Dormant(global, plain);

		Assert.IsFalse(dormant.Any);
		Assert.AreEqual(0, dormant.Names.Count);
	}

	// A no-motion window of zero is off, not a rule set to fire instantly.
	[TestMethod]
	public void A_Zeroed_No_Motion_Window_Is_Not_A_Dormant_Rule()
	{
		GlobalConfig global = House(HouseModeAuthority.HomeAssistant);
		global.HouseMode!.Options[2].ActivateAfterNoMotionMinutes = 0;

		Assert.AreEqual(0, ModeAuthority.Dormant(global, Day()).AutoAwayOptions);
	}

	[TestMethod]
	public void Each_Dormant_Rule_Is_Named_Once_And_Counted()
	{
		GlobalConfig global = House(HouseModeAuthority.HomeAssistant);
		global.HouseMode!.Options.Add(new HouseModeOptionConfig
		{
			Value = "Gjester",
			Kind = ModeKind.Guest,
			ActivateWhileOn = ["input_boolean.gjester"]
		});

		DormantModeRules dormant = ModeAuthority.Dormant(global, Day());

		Assert.AreEqual(2, dormant.ActivateWhileOnOptions);
		Assert.AreEqual(4, dormant.Names.Count);
		Assert.IsTrue(dormant.Names.Any(name => name.Contains("2 modes turned on", StringComparison.Ordinal)));
		Assert.IsTrue(dormant.Names.Any(name => name.Contains("1 period that", StringComparison.Ordinal)));
	}
}
