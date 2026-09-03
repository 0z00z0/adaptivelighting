using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The save-time normaliser drops pure-default option rows and a never-adopted empty HouseMode, keeping the designated Normal row.</summary>
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
				new() { Value = "Kveld", Kind = ModeKind.Normal, ResetOnPeriodStartId = "morning" }
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
			Periods = [new() { Name = "day", Start = "07:00", SetsModeId = "Dag" }, new() { Name = "night", Start = "22:30" }],
			Areas = [new() { Name = "Stue", AreaId = "stue" }]
		};

		ConfigNormalizer.Normalize(config);

		var options = config.Global.HouseMode!.Options.Select(o => o.Value).ToList();
		CollectionAssert.Contains(options, "Dag", "an option a period's SetsModeId names survives normalisation");
		Assert.IsTrue(ConfigValidator.Validate(config).IsValid,
			"and the normalised document still validates — the SetsModeId still resolves to an option");
	}

	[TestMethod]
	public void Normalize_DropsEmptyHouseMode()
	{
		var config = new AdaptiveLightingConfig { Global = new GlobalConfig { HouseMode = new HouseModeConfig() } };

		ConfigNormalizer.Normalize(config);

		Assert.IsNull(config.Global.HouseMode, "a never-adopted, empty HouseMode is dropped so the document stays clean");
	}

	// An editor drawing a row per period produces an empty one the moment somebody clears both fields.
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
						new() { PeriodId = "night", BrightnessPct = 8 },
						new() { PeriodId = "evening" },                    // drawn, then both fields cleared
						new() { PeriodId = "day", ColorTempKelvin = 4000 }
					]
				}
			]
		};

		ConfigNormalizer.Normalize(config);

		var periods = config.Areas[0].Levels.Select(level => level.PeriodId).ToList();
		CollectionAssert.AreEqual(new[] { "night", "day" }, periods,
			"the rows that say something survive, in the order they were written");
	}

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

	[TestMethod]
	public void Normalize_TrimsAndDedupesStartsOnMotionAreas()
	{
		var config = new AdaptiveLightingConfig
		{
			Periods =
			[
				new()
				{
					Name = "morning",
					Start = "06:00",
					StartsOnMotion = true,
					StartsOnMotionAreas = [" kjokken ", "", "kjokken", "gang", "\t"]
				}
			]
		};

		ConfigNormalizer.Normalize(config);

		CollectionAssert.AreEqual(new[] { "kjokken", "gang" }, config.Periods[0].StartsOnMotionAreas,
			"trimmed, blanks and repeats gone, in the order they were written");
	}

	[TestMethod]
	public void Normalize_DropsTheRoomListToNull_WhenTheMotionStartIsOff()
	{
		var config = new AdaptiveLightingConfig
		{
			Periods = [new() { Name = "morning", Start = "06:00", StartsOnMotionAreas = ["kjokken"] }]
		};

		ConfigNormalizer.Normalize(config);

		Assert.IsNull(config.Periods[0].StartsOnMotionAreas,
			"nothing reads it while the feature is off, and only null keeps the key out of the saved document");
	}

	[TestMethod]
	public void Normalize_DropsTheRoomListToNull_WhenTheMotionStartNamesNoRoom()
	{
		var config = new AdaptiveLightingConfig
		{
			Periods =
			[
				new() { Name = "morning", Start = "06:00", StartsOnMotion = true, StartsOnMotionAreas = [" ", ""] }
			]
		};

		ConfigNormalizer.Normalize(config);

		Assert.IsNull(config.Periods[0].StartsOnMotionAreas,
			"a list of nothing but blanks means any room, which is what an absent key means");
	}

	/// <summary>Both zero values are the old behaviour, and OmitNull writes what a document says either way.</summary>
	[TestMethod]
	public void Normalize_LeavesColorControlAndHouseModeAuthorityAlone()
	{
		var config = new AdaptiveLightingConfig
		{
			Global = new GlobalConfig
			{
				HouseMode = new HouseModeConfig
				{
					Entity = "input_select.husmodus",
					Authority = HouseModeAuthority.HomeAssistant
				}
			},
			Defaults = new AreaSettings { ColorControl = ColorControl.EqualChannels },
			Areas = [new() { AreaId = "stue", ColorControl = ColorControl.Kelvin }]
		};

		ConfigNormalizer.Normalize(config);

		Assert.AreEqual(HouseModeAuthority.HomeAssistant, config.Global.HouseMode!.Authority);
		Assert.AreEqual(ColorControl.EqualChannels, config.Defaults.ColorControl);
		Assert.AreEqual(ColorControl.Kelvin, config.Areas[0].ColorControl);
	}

	private static AdaptiveLightingConfig WithPeriodSelect(PeriodSelectConfig? select) =>
		new() { Global = new GlobalConfig { PeriodSelect = select } };

	[TestMethod]
	public void Normalize_DropsEmptyPeriodSelectOptionRows()
	{
		AdaptiveLightingConfig config = WithPeriodSelect(new PeriodSelectConfig
		{
			Entity = "input_select.tid_pa_dagen",
			Options =
			[
				new() { Value = "Kveld", PeriodId = "evening" },
				new(),                                        // an editor's row somebody cleared both fields on
				new() { Value = "  ", PeriodId = "\t" }
			]
		});

		ConfigNormalizer.Normalize(config);

		Assert.AreEqual(1, config.Global.PeriodSelect!.Options.Count, "a row saying nothing is not stored");
		Assert.AreEqual("Kveld", config.Global.PeriodSelect.Options[0].Value);
	}

	[TestMethod]
	public void Normalize_KeepsAHalfFilledRow_SoTheValidatorCanNameIt()
	{
		AdaptiveLightingConfig config = WithPeriodSelect(new PeriodSelectConfig
		{
			Entity = "input_select.tid_pa_dagen",
			Options = [new() { Value = "Kveld" }]   // an option chosen, no period yet
		});

		ConfigNormalizer.Normalize(config);

		Assert.AreEqual(1, config.Global.PeriodSelect!.Options.Count,
			"half a row is a half-finished decision, not a blank one — dropping it would hide the error");
	}

	// A page binds the object into existence to draw a form. That must not leave a block in the file.
	[TestMethod]
	public void Normalize_DropsAPeriodSelectThatSaysNothing()
	{
		AdaptiveLightingConfig empty = WithPeriodSelect(new PeriodSelectConfig());
		ConfigNormalizer.Normalize(empty);
		Assert.IsNull(empty.Global.PeriodSelect, "no entity and no rows is no opinion");

		AdaptiveLightingConfig clearedRows = WithPeriodSelect(new PeriodSelectConfig { Options = [new(), new()] });
		ConfigNormalizer.Normalize(clearedRows);
		Assert.IsNull(clearedRows.Global.PeriodSelect, "and a block holding only cleared rows is the same thing");

		AdaptiveLightingConfig authorityOnly = WithPeriodSelect(
			new PeriodSelectConfig { Authority = PeriodAuthority.HomeAssistant });
		ConfigNormalizer.Normalize(authorityOnly);
		Assert.IsNull(authorityOnly.Global.PeriodSelect,
			"an authority without an entity decides nothing, so it is not a reason to keep the block");
	}

	[TestMethod]
	public void Normalize_KeepsAPeriodSelect_WithAnEntityOrRows()
	{
		AdaptiveLightingConfig withEntity = WithPeriodSelect(
			new PeriodSelectConfig { Entity = "input_select.tid_pa_dagen" });
		ConfigNormalizer.Normalize(withEntity);
		Assert.IsNotNull(withEntity.Global.PeriodSelect, "an entity has been chosen — keep it");

		AdaptiveLightingConfig withRows = WithPeriodSelect(
			new PeriodSelectConfig { Options = [new() { Value = "Kveld", PeriodId = "evening" }] });
		ConfigNormalizer.Normalize(withRows);
		Assert.IsNotNull(withRows.Global.PeriodSelect, "a mapping has been written — keep it");
	}

	/// <summary>A brightness is a whole percent from the save on, so no surface has to choose how to round one.</summary>
	[TestMethod]
	public void Normalize_RoundsAHalfPercentBrightnessToAWholeOne()
	{
		AdaptiveLightingConfig config = AdaptiveLightingConfig.CreateDefault();
		config.Periods[0].BrightnessPct = 62.5;
		config.Areas =
		[
			new AreaConfig
			{
				AreaId = "stue",
				Levels = [new RoomLevelOverride { PeriodId = config.Periods[0].Key, BrightnessPct = 62.5 }]
			}
		];

		ConfigNormalizer.Normalize(config);

		Assert.AreEqual(63, config.Periods[0].BrightnessPct);
		Assert.AreEqual(63, config.Areas[0].Levels![0].BrightnessPct);
	}

	[TestMethod]
	public void Normalize_LeavesAWholePercentBrightnessAlone()
	{
		AdaptiveLightingConfig config = AdaptiveLightingConfig.CreateDefault();
		double[] before = [.. config.Periods.Select(period => period.BrightnessPct)];

		ConfigNormalizer.Normalize(config);

		CollectionAssert.AreEqual(before, config.Periods.Select(period => period.BrightnessPct).ToArray());
	}

	/// <summary>Away from zero, so the number a person reads is the one they would have written.</summary>
	[TestMethod]
	public void A_Half_Rounds_Up_And_Not_To_The_Even_Neighbour()
	{
		Assert.AreEqual(63, ConfigNormalizer.Whole(62.5));
		Assert.AreEqual(64, ConfigNormalizer.Whole(63.5));
		Assert.AreEqual(62, ConfigNormalizer.Whole(62.4));
		Assert.AreEqual(0, ConfigNormalizer.Whole(0.4));
		Assert.AreEqual(100, ConfigNormalizer.Whole(100));
	}

	[TestMethod]
	public void Normalize_LeavesADocumentWithoutAPeriodSelect_ByteIdentical()
	{
		AdaptiveLightingConfig config = AdaptiveLightingConfig.CreateDefault();
		string before = LightingConfigDocument.Serialize(config);

		string after = LightingConfigDocument.Serialize(ConfigNormalizer.Normalize(config));

		Assert.AreEqual(before, after);
		Assert.IsFalse(after.Contains("PeriodSelect", StringComparison.Ordinal),
			"a household that never adopted the select must not find one written into its file");
	}
}
