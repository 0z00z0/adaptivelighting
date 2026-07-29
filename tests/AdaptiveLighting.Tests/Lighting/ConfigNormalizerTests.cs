using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The save-time normaliser (09 §6): pure-default option rows are dropped so the document stays minimal,
///     except the designated Normal row, which stays explicit; and a never-adopted empty HouseMode is dropped.
/// </summary>
[TestClass]
public sealed class ConfigNormalizerTests
{
	private static HouseModeConfig Cabin() => new()
	{
		Entity = "input_select.husmodus",
		Options =
		[
			new() { Value = "Normal", Kind = ModeKind.Normal },
			new() { Value = "Borte", Kind = ModeKind.Away, Scene = "scene.borte" },
			new() { Value = "Sover", Kind = ModeKind.Sleep },
			new() { Value = "Kveld", Kind = ModeKind.Normal }   // a pure-default extra row
		]
	};

	[TestMethod]
	public void Normalize_DropsPureDefaultRows_ButKeepsTheNormalTarget()
	{
		var config = new AdaptiveLightingConfig { Global = new GlobalConfig { HouseMode = Cabin() } };

		ConfigNormalizer.Normalize(config);

		var options = config.Global.HouseMode!.Options.Select(o => o.Value).ToList();
		CollectionAssert.Contains(options, "Normal", "the designated Normal row stays even though it is a pure default");
		CollectionAssert.Contains(options, "Borte", "an away row carries a scene, so it is not a pure default");
		CollectionAssert.Contains(options, "Sover", "a sleep row is not a pure default");
		CollectionAssert.DoesNotContain(options, "Kveld", "a pure-default extra Normal row is dropped");
	}

	[TestMethod]
	public void Normalize_KeepsRowsThatCarryAReset()
	{
		var mode = new HouseModeConfig
		{
			Entity = "input_select.husmodus",
			Options =
			[
				new() { Value = "Normal", Kind = ModeKind.Normal },
				new() { Value = "Kveld", Kind = ModeKind.Normal, ResetOnPeriodStart = "morning" }
			]
		};
		var config = new AdaptiveLightingConfig { Global = new GlobalConfig { HouseMode = mode } };

		ConfigNormalizer.Normalize(config);

		var options = config.Global.HouseMode!.Options.Select(o => o.Value).ToList();
		CollectionAssert.Contains(options, "Kveld", "a row carrying a reset trigger is not a pure default");
	}

	[TestMethod]
	public void Normalize_KeepsAPureDefaultOption_ReferencedByAPeriodSetsMode()
	{
		var mode = new HouseModeConfig
		{
			Entity = "input_select.husmodus",
			Options =
			[
				new() { Value = "Hjemme", Kind = ModeKind.Normal },
				new() { Value = "Dag", Kind = ModeKind.Normal }   // a pure-default row, but a period sets it
			]
		};
		var config = new AdaptiveLightingConfig
		{
			Global = new GlobalConfig { HouseMode = mode },
			Periods = [new() { Name = "day", Start = "07:00", SetsMode = "Dag" }, new() { Name = "night", Start = "22:30" }],
			Areas = [new() { Name = "Stue", AreaId = "stue" }]
		};

		ConfigNormalizer.Normalize(config);

		var options = config.Global.HouseMode!.Options.Select(o => o.Value).ToList();
		CollectionAssert.Contains(options, "Dag", "an option a period's SetsMode names survives normalisation");
		Assert.IsTrue(ConfigValidator.Validate(config).IsValid,
			"and the normalised document still validates — the SetsMode still resolves to an option");
	}

	[TestMethod]
	public void Normalize_DropsEmptyHouseMode()
	{
		var config = new AdaptiveLightingConfig { Global = new GlobalConfig { HouseMode = new HouseModeConfig() } };

		ConfigNormalizer.Normalize(config);

		Assert.IsNull(config.Global.HouseMode, "a never-adopted, empty HouseMode is dropped so the document stays clean");
	}

	// ===================== a room's own levels =====================

	/// <summary>
	///     A row with neither value set says nothing, and an editor that draws a row per period produces one the
	///     moment somebody clears both fields. The engine ignores it either way, so this is tidying — but a file
	///     nobody can read is how a room's levels stop being reviewable.
	/// </summary>
	[TestMethod]
	public void Normalize_DropsLevelsRowsThatSayNothing()
	{
		var config = new AdaptiveLightingConfig
		{
			Areas =
			[
				new()
				{
					AreaId = "stue",
					Levels =
					[
						new() { Period = "night", BrightnessPct = 8 },
						new() { Period = "evening" },                    // drawn, then both fields cleared
						new() { Period = "day", ColorTempKelvin = 4000 }
					]
				}
			]
		};

		ConfigNormalizer.Normalize(config);

		var periods = config.Areas[0].Levels.Select(level => level.Period).ToList();
		CollectionAssert.AreEqual(new[] { "night", "day" }, periods,
			"the rows that say something survive, in the order they were written");
	}

	/// <summary>A row naming no period but carrying a value is not empty, so it stays and the validator warns about it.</summary>
	[TestMethod]
	public void Normalize_KeepsALevelsRowThatCarriesAValue_EvenWithNoPeriod()
	{
		var config = new AdaptiveLightingConfig
		{
			Areas = [new() { AreaId = "stue", Levels = [new() { BrightnessPct = 40 }] }]
		};

		ConfigNormalizer.Normalize(config);

		Assert.AreEqual(1, config.Areas[0].Levels.Count,
			"dropping it would delete a number somebody typed; the validator names the missing period instead");
	}

	[TestMethod]
	public void Normalize_KeepsHouseMode_WithEntityOrOptions()
	{
		var withEntity = new AdaptiveLightingConfig
		{
			Global = new GlobalConfig { HouseMode = new HouseModeConfig { Entity = "input_select.husmodus" } }
		};
		ConfigNormalizer.Normalize(withEntity);
		Assert.IsNotNull(withEntity.Global.HouseMode, "an entity has been chosen — keep it");

		var withOptions = new AdaptiveLightingConfig
		{
			Global = new GlobalConfig { HouseMode = new HouseModeConfig { Options = [new() { Value = "Sover", Kind = ModeKind.Sleep }] } }
		};
		ConfigNormalizer.Normalize(withOptions);
		Assert.IsNotNull(withOptions.Global.HouseMode, "a classified option has been set — keep it");
	}
}
