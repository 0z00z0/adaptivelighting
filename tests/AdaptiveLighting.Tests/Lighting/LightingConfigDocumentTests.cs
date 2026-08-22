using AdaptiveLighting.Configuration;

using Microsoft.Extensions.Logging;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Round-trip tests for the YAML loader.</summary>
[TestClass]
public sealed class LightingConfigDocumentTests
{
	/// <summary>A document with every shape in the schema: sun-anchored periods, explicit lists, overrides.</summary>
	private static AdaptiveLightingConfig Populated() => new()
	{
		ConfigName = "Adaptive lighting [test]",
		Global = new GlobalConfig
		{
			Persons = ["person.alex", "device_tracker.phone"],
			KillSwitchEntity = "input_boolean.adaptive_lighting_enabled",
			KillSwitchActiveWhenOff = false,
			NetDaemonUserId = "abc123",
			AwayDebounceMinutes = 7,
			CircadianTickSeconds = 45,
			SelfEchoWindowSeconds = 9,
			TreatAutomationsAsManual = false,
			SmoothTransitions = false,
			BlendMinutes = 20,
			ExcludeLabel = "no-touch",
			MotionLabel = "is-motion",
			MotionDeviceClasses = ["motion", "vibration"],
			IlluminanceDeviceClass = "illuminance"
		},
		Defaults = new AreaSettings
		{
			VacancyTimeoutSeconds = 900,
			PreOffSeconds = 45,
			PreOffBrightnessFactor = 0.4,
			OverrideDurationMinutes = 90,
			VacancyResetMinutes = 15,
			Darkness = DarknessSource.Sun,
			LuxThreshold = 35,
			LuxHysteresis = 8,
			SunElevationThreshold = 2.5,
			SunEntity = "sun.sun",
			DayTransitionSeconds = 2,
			NightTransitionSeconds = 12,
			RespectSleepMode = true,
			SleepBlocksAutoOn = true,
			SkipAwaySweep = true,
			WelcomeHome = true,
			Enabled = false
		},
		Periods =
		[
			new TimePeriodConfig { Id = "morning-1a1a", Name = "morning", Start = "06:00", BrightnessPct = 60, ColorTempKelvin = 3000 },
			new TimePeriodConfig { Id = "day-2b2b", Name = "day", Start = "sunrise+00:45", BrightnessPct = 90, ColorTempKelvin = 4500 },
			new TimePeriodConfig { Id = "evening-3c3c", Name = "evening", Start = "sunset-01:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new TimePeriodConfig
			{
				Id = "night-4d4d", Name = "night", Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200
			}
		],
		Areas =
		[
			// The common case: an area and one override, everything else inherited.
			new AreaConfig { Name = "Stue", AreaId = "stue", RespectSleepMode = true },

			// The awkward case: explicit lists replacing discovery, and a spread of override types.
			new AreaConfig
			{
				Name = "Uteplass",
				AreaId = "uteplass",
				Lights = ["light.outdoor_front", "light.outdoor_back"],
				MotionSensors = ["binary_sensor.outdoor_mmwave"],
				LuxSensor = "sensor.outdoor_illuminance",
				IgnoreWhenOn = ["binary_sensor.projector"],
				SceneOnMotion = "scene.uteplass_ankomst",
				SceneWhenEmpty = "scene.uteplass_natt",
				VacancyTimeoutSeconds = 1800,
				PreOffSeconds = 60,
				PreOffBrightnessFactor = 0.25,
				OverrideDurationMinutes = 30,
				VacancyResetMinutes = 5,
				Darkness = DarknessSource.Always,
				LuxThreshold = 10,
				LuxHysteresis = 2,
				SunElevationThreshold = 1.0,
				SunEntity = "sun.other",
				DayTransitionSeconds = 0.5,
				NightTransitionSeconds = 20,
				RespectSleepMode = false,
				SleepBlocksAutoOn = false,
				SkipAwaySweep = true,
				WelcomeHome = false,
				Enabled = true
			}
		]
	};

	[TestMethod]
	public void RoundTrip_PreservesGlobal()
	{
		var original = Populated();

		var actual = LightingConfigDocument.Deserialize(LightingConfigDocument.Serialize(original)).Config.Global;
		var expected = original.Global;

		CollectionAssert.AreEqual(expected.Persons, actual.Persons);
		Assert.AreEqual(expected.KillSwitchEntity, actual.KillSwitchEntity);
		Assert.AreEqual(expected.KillSwitchActiveWhenOff, actual.KillSwitchActiveWhenOff);
		Assert.AreEqual(expected.NetDaemonUserId, actual.NetDaemonUserId);
		Assert.AreEqual(expected.AwayDebounceMinutes, actual.AwayDebounceMinutes);
		Assert.AreEqual(expected.CircadianTickSeconds, actual.CircadianTickSeconds);
		Assert.AreEqual(expected.SelfEchoWindowSeconds, actual.SelfEchoWindowSeconds);
		Assert.AreEqual(expected.TreatAutomationsAsManual, actual.TreatAutomationsAsManual);
		Assert.AreEqual(expected.SmoothTransitions, actual.SmoothTransitions);
		Assert.AreEqual(expected.BlendMinutes, actual.BlendMinutes);
		Assert.AreEqual(expected.ExcludeLabel, actual.ExcludeLabel);
		Assert.AreEqual(expected.MotionLabel, actual.MotionLabel);
		CollectionAssert.AreEqual(expected.MotionDeviceClasses, actual.MotionDeviceClasses);
		Assert.AreEqual(expected.IlluminanceDeviceClass, actual.IlluminanceDeviceClass);
	}

	[TestMethod]
	public void RoundTrip_PreservesDefaults()
	{
		var original = Populated();

		var actual = LightingConfigDocument.Deserialize(LightingConfigDocument.Serialize(original)).Config.Defaults;
		var expected = original.Defaults;

		Assert.AreEqual(expected.VacancyTimeoutSeconds, actual.VacancyTimeoutSeconds);
		Assert.AreEqual(expected.PreOffSeconds, actual.PreOffSeconds);
		Assert.AreEqual(expected.PreOffBrightnessFactor, actual.PreOffBrightnessFactor);
		Assert.AreEqual(expected.OverrideDurationMinutes, actual.OverrideDurationMinutes);
		Assert.AreEqual(expected.VacancyResetMinutes, actual.VacancyResetMinutes);
		Assert.AreEqual(expected.Darkness, actual.Darkness);
		Assert.AreEqual(expected.LuxThreshold, actual.LuxThreshold);
		Assert.AreEqual(expected.LuxHysteresis, actual.LuxHysteresis);
		Assert.AreEqual(expected.SunElevationThreshold, actual.SunElevationThreshold);
		Assert.AreEqual(expected.SunEntity, actual.SunEntity);
		Assert.AreEqual(expected.DayTransitionSeconds, actual.DayTransitionSeconds);
		Assert.AreEqual(expected.NightTransitionSeconds, actual.NightTransitionSeconds);
		Assert.AreEqual(expected.RespectSleepMode, actual.RespectSleepMode);
		Assert.AreEqual(expected.SleepBlocksAutoOn, actual.SleepBlocksAutoOn);
		Assert.AreEqual(expected.SkipAwaySweep, actual.SkipAwaySweep);
		Assert.AreEqual(expected.WelcomeHome, actual.WelcomeHome);
		Assert.AreEqual(expected.Enabled, actual.Enabled);
	}

	// ===================== the darkness threshold, and what saying nothing means =====================

	[TestMethod]
	public void A_Document_That_Never_Mentions_A_Lux_Threshold_Counts_As_Dark_Below_1000()
	{
		Assert.AreEqual(1000d, new AreaSettings().LuxThreshold);

		DocumentReadResult read = LightingConfigDocument.Deserialize(
			"""
			AdaptiveLighting.Configuration.AdaptiveLightingConfig:
			  Periods:
			    - Name: day
			      Start: "07:00"
			  Areas:
			    - Name: Stue
			      AreaId: stue
			""");

		Assert.AreEqual(1000d, read.Config.Defaults.LuxThreshold, "saying nothing means the shipped default");
		Assert.IsNull(read.Config.Areas[0].LuxThreshold, "and the room inherits rather than carrying a copy");
		Assert.AreEqual(1000d, read.Config.Areas[0].Effective(read.Config.Defaults).LuxThreshold);
	}

	[TestMethod]
	public void Following_The_Outdoor_Sensor_Is_Off_Until_A_Room_Says_Otherwise()
	{
		DocumentReadResult silent = LightingConfigDocument.Deserialize(
			"""
			AdaptiveLighting.Configuration.AdaptiveLightingConfig:
			  Global:
			    OutdoorLuxSensor: sensor.outdoor_illuminance
			  Periods:
			    - Name: day
			      Start: "07:00"
			  Areas:
			    - Name: Stue
			      AreaId: stue
			""");

		Assert.IsNull(silent.Config.Areas[0].FollowOutdoorLux,
			"an existing document says nothing, and saying nothing must not mean following");

		Assert.IsFalse(LightingConfigDocument.Serialize(silent.Config).Contains("FollowOutdoorLux", StringComparison.Ordinal),
			"and a save must not write the setting into a file that never asked for it");

		AdaptiveLightingConfig opted = new()
		{
			Periods = [new() { Name = "day", Start = "07:00" }],
			Areas = [new() { Name = "Gang", AreaId = "gang", FollowOutdoorLux = true }]
		};

		Assert.AreEqual(true,
			LightingConfigDocument.Deserialize(LightingConfigDocument.Serialize(opted)).Config.Areas[0].FollowOutdoorLux);
	}

	[TestMethod]
	public void RoundTrip_PreservesPeriods()
	{
		var original = Populated();

		var actual = LightingConfigDocument.Deserialize(LightingConfigDocument.Serialize(original)).Config.Periods;

		Assert.AreEqual(original.Periods.Count, actual.Count);

		for (var index = 0; index < original.Periods.Count; index++)
		{
			Assert.AreEqual(original.Periods[index].Name, actual[index].Name);
			Assert.AreEqual(original.Periods[index].Start, actual[index].Start);
			Assert.AreEqual(original.Periods[index].BrightnessPct, actual[index].BrightnessPct);
			Assert.AreEqual(original.Periods[index].ColorTempKelvin, actual[index].ColorTempKelvin);
		}
	}

	[TestMethod]
	public void RoundTrip_PreservesFullyOverriddenArea()
	{
		var original = Populated();

		var actual = LightingConfigDocument.Deserialize(LightingConfigDocument.Serialize(original)).Config.Areas[1];
		var expected = original.Areas[1];

		Assert.AreEqual(expected.Name, actual.Name);
		Assert.AreEqual(expected.AreaId, actual.AreaId);
		CollectionAssert.AreEqual(expected.Lights, actual.Lights);
		CollectionAssert.AreEqual(expected.MotionSensors, actual.MotionSensors);
		Assert.AreEqual(expected.LuxSensor, actual.LuxSensor);
		CollectionAssert.AreEqual(expected.IgnoreWhenOn, actual.IgnoreWhenOn);
		Assert.AreEqual(expected.SceneOnMotion, actual.SceneOnMotion);
		Assert.AreEqual(expected.SceneWhenEmpty, actual.SceneWhenEmpty);
		Assert.AreEqual(expected.VacancyTimeoutSeconds, actual.VacancyTimeoutSeconds);
		Assert.AreEqual(expected.PreOffSeconds, actual.PreOffSeconds);
		Assert.AreEqual(expected.PreOffBrightnessFactor, actual.PreOffBrightnessFactor);
		Assert.AreEqual(expected.OverrideDurationMinutes, actual.OverrideDurationMinutes);
		Assert.AreEqual(expected.VacancyResetMinutes, actual.VacancyResetMinutes);
		Assert.AreEqual(expected.Darkness, actual.Darkness);
		Assert.AreEqual(expected.LuxThreshold, actual.LuxThreshold);
		Assert.AreEqual(expected.LuxHysteresis, actual.LuxHysteresis);
		Assert.AreEqual(expected.SunElevationThreshold, actual.SunElevationThreshold);
		Assert.AreEqual(expected.SunEntity, actual.SunEntity);
		Assert.AreEqual(expected.DayTransitionSeconds, actual.DayTransitionSeconds);
		Assert.AreEqual(expected.NightTransitionSeconds, actual.NightTransitionSeconds);
		Assert.AreEqual(expected.RespectSleepMode, actual.RespectSleepMode);
		Assert.AreEqual(expected.SleepBlocksAutoOn, actual.SleepBlocksAutoOn);
		Assert.AreEqual(expected.SkipAwaySweep, actual.SkipAwaySweep);
		Assert.AreEqual(expected.WelcomeHome, actual.WelcomeHome);
		Assert.AreEqual(expected.Enabled, actual.Enabled);
	}

	// Null means "inherit Defaults": writing nulls back as concrete values freezes every area at the defaults of
	// the day it was first saved.
	[TestMethod]
	public void RoundTrip_LeavesInheritedAreaSettingsNull()
	{
		var original = Populated();

		var area = LightingConfigDocument.Deserialize(LightingConfigDocument.Serialize(original)).Config.Areas[0];

		Assert.AreEqual("stue", area.AreaId);
		Assert.IsTrue(area.RespectSleepMode);

		Assert.IsNull(area.Lights);
		Assert.IsNull(area.MotionSensors);
		Assert.IsNull(area.LuxSensor);
		Assert.IsNull(area.IgnoreWhenOn);
		Assert.IsNull(area.SceneOnMotion);
		Assert.IsNull(area.SceneWhenEmpty);
		Assert.IsNull(area.VacancyTimeoutSeconds);
		Assert.IsNull(area.PreOffSeconds);
		Assert.IsNull(area.PreOffBrightnessFactor);
		Assert.IsNull(area.OverrideDurationMinutes);
		Assert.IsNull(area.VacancyResetMinutes);
		Assert.IsNull(area.Darkness);
		Assert.IsNull(area.LuxThreshold);
		Assert.IsNull(area.LuxHysteresis);
		Assert.IsNull(area.SunElevationThreshold);
		Assert.IsNull(area.SunEntity);
		Assert.IsNull(area.DayTransitionSeconds);
		Assert.IsNull(area.NightTransitionSeconds);
		Assert.IsNull(area.SleepBlocksAutoOn);
		Assert.IsNull(area.SkipAwaySweep);
		Assert.IsNull(area.WelcomeHome);
		Assert.IsNull(area.Enabled);
	}

	// An empty MotionDeviceClasses list means the built-in set; writing the resolved fallback back would pin three
	// device classes on the first save.
	[TestMethod]
	public void Serialize_DoesNotWriteComputedProperties()
	{
		var config = Populated();
		config.Global.MotionDeviceClasses = [];

		var yaml = LightingConfigDocument.Serialize(config);

		StringAssert.DoesNotMatch(yaml, new System.Text.RegularExpressions.Regex(nameof(GlobalConfig.EffectiveMotionDeviceClasses)));
		StringAssert.DoesNotMatch(yaml, new System.Text.RegularExpressions.Regex(nameof(AreaConfig.DisplayName)));

		var reloaded = LightingConfigDocument.Deserialize(yaml).Config;

		Assert.AreEqual(0, reloaded.Global.MotionDeviceClasses.Count);
		CollectionAssert.AreEqual(
			GlobalConfig.DefaultMotionDeviceClasses.ToList(),
			reloaded.Global.EffectiveMotionDeviceClasses.ToList());
	}

	[TestMethod]
	public void Serialize_IsStableAcrossRepeatedRoundTrips()
	{
		var once = LightingConfigDocument.Serialize(Populated());
		var twice = LightingConfigDocument.Serialize(LightingConfigDocument.Deserialize(once).Config);

		Assert.AreEqual(once, twice);
	}

	[TestMethod]
	public void Serialize_KeepsTheRootKeyTheAppModelBindsOn()
	{
		StringAssert.Contains(LightingConfigDocument.Serialize(Populated()), LightingConfigDocument.RootKey);
	}

	[TestMethod]
	public void Deserialize_WithoutTheRootKey_ExplainsWhatIsThere()
	{
		var exception = Assert.ThrowsException<LightingConfigException>(() =>
			LightingConfigDocument.Deserialize("SomeOtherApp:\n  Setting: 1\n"));

		StringAssert.Contains(exception.Message, "SomeOtherApp");
	}

	[TestMethod]
	public void Deserialize_OfNonsense_SaysItIsNotYaml()
	{
		Assert.ThrowsException<LightingConfigException>(() =>
			LightingConfigDocument.Deserialize("\tthis: is: not: yaml\n  - [unclosed\n"));
	}

	[TestMethod]
	public void Deserialize_OfAnEmptySection_GivesAnEmptyDocumentRatherThanThrowing()
	{
		var config = LightingConfigDocument.Deserialize($"{LightingConfigDocument.RootKey}:\n").Config;

		Assert.AreEqual(0, config.Areas.Count);
		Assert.AreEqual(0, config.Periods.Count);
	}

	[TestMethod]
	public void RoundTrip_NoHouseMode_EmitsNoHouseModeOrModeKeys()
	{
		// Populated() carries no HouseMode and no tagged periods, so a save must not acquire either key.
		var yaml = LightingConfigDocument.Serialize(Populated());

		StringAssert.DoesNotMatch(yaml, new System.Text.RegularExpressions.Regex("HouseMode"));
		StringAssert.DoesNotMatch(yaml, new System.Text.RegularExpressions.Regex(@"\bMode:"));
	}

	// ===================== a room's own levels =====================

	/// <summary>Levels survive a write and a read: both values, only one, and a row for a period that no longer exists.</summary>
	[TestMethod]
	public void RoundTrip_PreservesARoomsLevels()
	{
		var original = Populated();
		original.Areas[0].Levels =
		[
			new RoomLevelOverride { PeriodId = "night", BrightnessPct = 8, ColorTempKelvin = 2000 },
			new RoomLevelOverride { PeriodId = "day", BrightnessPct = 55 },
			new RoomLevelOverride { PeriodId = "evening", ColorTempKelvin = 4000 },
			new RoomLevelOverride { PeriodId = "kveld", BrightnessPct = 40 }   // a renamed period: kept, not dropped
		];

		var reloaded = LightingConfigDocument.Deserialize(LightingConfigDocument.Serialize(original)).Config.Areas[0];

		Assert.AreEqual(4, reloaded.Levels.Count, "the list replaces, it does not append or shrink");

		Assert.AreEqual(8d, reloaded.Levels[0].BrightnessPct);
		Assert.AreEqual(2000, reloaded.Levels[0].ColorTempKelvin);

		Assert.AreEqual(55d, reloaded.Levels[1].BrightnessPct);
		Assert.IsNull(reloaded.Levels[1].ColorTempKelvin,
			"a value the room never set must come back unset — writing it as 0 would pin a colour nobody chose");

		Assert.IsNull(reloaded.Levels[2].BrightnessPct);
		Assert.AreEqual(4000, reloaded.Levels[2].ColorTempKelvin);

		Assert.AreEqual("kveld", reloaded.Levels[3].PeriodId, "a row naming no configured period survives the trip too");
	}

	[TestMethod]
	public void Serialize_DoesNotWriteTheDerivedIsEmptyFlag()
	{
		var config = Populated();
		config.Areas[0].Levels = [new RoomLevelOverride { PeriodId = "night", BrightnessPct = 8 }];

		StringAssert.DoesNotMatch(
			LightingConfigDocument.Serialize(config),
			new System.Text.RegularExpressions.Regex("IsEmpty"));
	}

	// IgnoreUnmatchedProperties makes an unknown key silence, so a misspelled Levels in the model reports nothing.
	[TestMethod]
	public void A_Document_With_No_Levels_Key_Loads_As_A_Room_That_Follows_The_Schedule()
	{
		DocumentReadResult read = LightingConfigDocument.Deserialize(
			"""
			AdaptiveLighting.Configuration.AdaptiveLightingConfig:
			  Periods:
			    - Name: day
			      Start: "07:00"
			  Areas:
			    - Name: Stue
			      AreaId: stue
			""");

		Assert.IsFalse(read.UsedLegacyKeys, "a document that never had Levels is not a legacy document");
		Assert.AreEqual(0, read.Config.Areas[0].Levels.Count, "no levels, not null and not a guess");
		Assert.IsTrue(ConfigValidator.Validate(read.Config).IsValid);
	}

	// A bare Levels: assigns null over the model's initialiser and takes the room's controller down at build time.
	[TestMethod]
	public void A_Blank_Levels_Key_Is_Read_As_No_Levels_Rather_Than_Null()
	{
		DocumentReadResult read = LightingConfigDocument.Deserialize(
			"""
			AdaptiveLighting.Configuration.AdaptiveLightingConfig:
			  Periods:
			    - Name: day
			      Start: "07:00"
			  Areas:
			    - Name: Stue
			      AreaId: stue
			      Levels:
			""");

		Assert.IsNotNull(read.Config.Areas[0].Levels);
		Assert.AreEqual(0, read.Config.Areas[0].Levels.Count);
	}

	[TestMethod]
	public void A_Blank_Levels_Entry_Is_Dropped_Rather_Than_Read_As_A_Nameless_Row()
	{
		DocumentReadResult read = LightingConfigDocument.Deserialize(
			"""
			AdaptiveLighting.Configuration.AdaptiveLightingConfig:
			  Periods:
			    - Name: day
			      Start: "07:00"
			  Areas:
			    - Name: Stue
			      AreaId: stue
			      Levels:
			        -
			        - Period: day
			          BrightnessPct: 55
			""");

		Assert.AreEqual(1, read.Config.Areas[0].Levels.Count, "the punctuation goes, the row that says something stays");
		Assert.AreEqual(55d, read.Config.Areas[0].Levels[0].BrightnessPct);
	}

	[TestMethod]
	public void RoundTrip_WithHouseMode_RoundTripsLosslessly()
	{
		var original = Populated();
		original.Global.HouseMode = new HouseModeConfig
		{
			Entity = "input_select.husmodus",
			Options =
			[
				new() { Value = "Normal", Kind = ModeKind.Normal },
				new() { Value = "Borte", Kind = ModeKind.Away, Scene = "scene.borte" },
				new() { Value = "Sover", Kind = ModeKind.Sleep }
			]
		};
		original.Periods.Add(new TimePeriodConfig { Id = "late-5e5e", Name = "late", SetsModeId = "Sover", Start = "23:15", BrightnessPct = 10, ColorTempKelvin = 2000 });

		var reloaded = LightingConfigDocument.Deserialize(LightingConfigDocument.Serialize(original)).Config;

		Assert.AreEqual("input_select.husmodus", reloaded.Global.HouseMode!.Entity);
		Assert.AreEqual(3, reloaded.Global.HouseMode.Options.Count, "the option list replaces, it does not append");
		Assert.AreEqual(ModeKind.Sleep, reloaded.Global.HouseMode.OptionFor("Sover")!.Kind, "Kind survives the round trip");
		Assert.AreEqual(ModeKind.Away, reloaded.Global.HouseMode.OptionFor("Borte")!.Kind);
		Assert.AreEqual("scene.borte", reloaded.Global.HouseMode.OptionFor("Borte")!.Scene, "the option's Scene survives");
		// The reference arrived naming the option's Value; the migration repoints it at the minted Id.
		string? setsMode = reloaded.Periods.Single(p => string.Equals(p.Name, "late", StringComparison.Ordinal)).SetsModeId;

		Assert.AreEqual(reloaded.Global.HouseMode.OptionFor("Sover")!.Id, setsMode, "the period's mode switch resolves by id");
		Assert.AreEqual("Sover", reloaded.Global.HouseMode.OptionValueFor(setsMode), "and still writes 'Sover' to the select");
	}

	// Both kill-switch views are in-memory only, so writing either back puts a resolved fallback in the file as if
	// it had been chosen.
	[TestMethod]
	public void Serialize_DoesNotWriteTheDefaultedOrEffectiveKillSwitch()
	{
		var config = Populated();
		config.Global.DefaultKillSwitchEntity = "input_boolean.netdaemon_builtin";

		var yaml = LightingConfigDocument.Serialize(config);

		StringAssert.DoesNotMatch(yaml, new System.Text.RegularExpressions.Regex(nameof(GlobalConfig.DefaultKillSwitchEntity)));
		StringAssert.DoesNotMatch(yaml, new System.Text.RegularExpressions.Regex(nameof(GlobalConfig.EffectiveKillSwitchEntity)));
		StringAssert.DoesNotMatch(yaml, new System.Text.RegularExpressions.Regex("netdaemon_builtin"));
	}

	[TestMethod]
	public void Deserialize_ReadsTheShippedExampleShape()
	{
		var config = LightingConfigDocument.Deserialize(
			$"""
			{LightingConfigDocument.RootKey}:
			  ConfigName: "Adaptive lighting [Home]"
			  Global:
			    Persons:
			      - person.REPLACE_ME
			    KillSwitchEntity: input_boolean.REPLACE_ME_adaptive_lighting_enabled
			    KillSwitchActiveWhenOff: true
			  Defaults:
			    Darkness: Either
			    LuxThreshold: 40
			  Periods:
			    - Name: night
			      Start: "22:30"
			      BrightnessPct: 15
			      ColorTempKelvin: 2200
			      MaxBrightnessPct: 30
			  Areas:
			    - Name: Example living room
			      AreaId: REPLACE_ME_living_room_area_id
			      RespectSleepMode: true
			""").Config;

		Assert.AreEqual("Adaptive lighting [Home]", config.ConfigName);
		Assert.AreEqual("person.REPLACE_ME", config.Global.Persons.Single());
		Assert.AreEqual(DarknessSource.Lux, config.Defaults.Darkness);
		Assert.AreEqual("22:30", config.Periods.Single().Start);
		Assert.AreEqual("REPLACE_ME_living_room_area_id", config.Areas.Single().AreaId);
		Assert.IsTrue(config.Areas.Single().RespectSleepMode);
	}

	// The top-level key is a fully qualified type name, so moving the library renamed it.
	[TestMethod]
	public void A_Document_Written_Under_A_Previous_Namespace_Still_Loads()
	{
		string legacy = """
			Laget.NetDaemon.AdaptiveLighting.Configuration.AdaptiveLightingConfig:
			  ConfigName: "Adaptive lighting [Home]"
			  Periods:
			    - Name: evening
			      Start: "18:00"
			      BrightnessPct: 70
			      ColorTempKelvin: 2700
			""";

		AdaptiveLightingConfig config = LightingConfigDocument.Deserialize(legacy).Config;

		Assert.AreEqual("Adaptive lighting [Home]", config.ConfigName, "a document under the old namespace key still loads");
		Assert.AreEqual(1, config.Periods.Count);

		StringAssert.Contains(LightingConfigDocument.Serialize(config), LightingConfigDocument.RootKey,
			"and saving rewrites it under the current key, so the file self-migrates");
	}

	// ===================== the pre-2.0 Zones → Areas key translation =====================
	//
	// Deserialize binds with IgnoreUnmatchedProperties, so a document still saying "Zones:" against a model that
	// only has "Areas" binds to zero areas: no exception, no warning, no lights, nothing in the log.

	/// <summary>A document as the two live houses wrote it: the pre-2.0 key names, all through.</summary>
	private const string LegacySchema =
		"""
		AdaptiveLighting.Configuration.AdaptiveLightingConfig:
		  ConfigName: "Adaptive lighting [Home]"
		  Global:
		    ZonesAutoDiscovered: true
		    ExcludeLabel: no-touch
		    OutdoorLuxSensor: sensor.outdoor_illuminance
		  Defaults:
		    LuxThreshold: 35
		    Darkness: Sun
		  Periods:
		    - Name: day
		      Start: "06:00"
		      BrightnessPct: 80
		      ColorTempKelvin: 3500
		    - Name: night
		      Start: "22:30"
		      BrightnessPct: 15
		      ColorTempKelvin: 2200
		      MaxBrightnessPct: 30
		  Zones:
		    - Name: Stue
		      AreaId: stue
		      RespectSleepMode: true
		    - Name: Uteplass
		      AreaId: uteplass
		      Lights:
		        - light.outdoor_front
		      SkipAwaySweep: true
		""";

	// An unmatched key is silence; an unknown enum value is a parse failure. NetDaemon's own binder reads the same
	// type and has no translation pre-pass, so the member has to survive as a name.
	[TestMethod]
	public void The_Retired_Either_Still_Parses_For_Any_Binder()
	{
		Assert.IsTrue(Enum.TryParse("Either", ignoreCase: true, out DarknessSource parsed),
			"a binder that meets this string must not throw — NetDaemon's does exactly this");
		Assert.AreEqual(DarknessSource.Either, parsed);
	}

	[TestMethod]
	public void A_Saved_Document_Stops_Saying_Either()
	{
		AdaptiveLightingConfig config = new();
		config.Defaults.Darkness = DarknessSource.Either;
		config.Areas.Add(new AreaConfig { Name = "Stue", AreaId = "stue", Darkness = DarknessSource.Either });
		config.Areas.Add(new AreaConfig { Name = "Bod", AreaId = "bod", Darkness = DarknessSource.Always });

		AdaptiveLightingConfig normalised = ConfigNormalizer.Normalize(config);

		Assert.AreEqual(DarknessSource.Lux, normalised.Defaults.Darkness);
		Assert.AreEqual(DarknessSource.Lux, normalised.Areas[0].Darkness);
		Assert.AreEqual(DarknessSource.Always, normalised.Areas[1].Darkness, "and a value that still exists is untouched");

		Assert.IsFalse(LightingConfigDocument.Serialize(normalised).Contains("Either", StringComparison.Ordinal),
			"the written file must not carry the retired name forward");
	}

	[TestMethod]
	public void A_Room_Still_Set_To_The_Retired_Either_Loads_As_Sensor()
	{
		const string yaml = """
			AdaptiveLighting.Configuration.AdaptiveLightingConfig:
			  Global:
			    HouseName: Test
			  Defaults:
			    Darkness: Either
			  Areas:
			    - Name: Stue
			      AreaId: stue
			      Darkness: Either
			    - Name: Bod
			      AreaId: bod
			      Darkness: Always
			""";

		DocumentReadResult read = LightingConfigDocument.Deserialize(yaml);

		Assert.AreEqual(DarknessSource.Lux, read.Config.Defaults.Darkness, "the house default is translated");
		Assert.AreEqual(DarknessSource.Lux, read.Config.Areas[0].Darkness, "and so is a room's own");
		Assert.AreEqual(DarknessSource.Always, read.Config.Areas[1].Darkness, "a value that still exists is left alone");

		Assert.IsTrue(read.UsedLegacyKeys,
			"reported, or nothing rewrites the file and the translation runs again on every load");
	}

	[TestMethod]
	public void A_Document_Written_With_The_Pre_2_0_Keys_Loads_With_Its_Areas_And_Says_So()
	{
		DocumentReadResult read = LightingConfigDocument.Deserialize(LegacySchema);

		Assert.IsTrue(read.UsedLegacyKeys, "the reader must report the translation, or nothing will rewrite the file");

		Assert.AreEqual(2, read.Config.Areas.Count, "Zones: must arrive as Areas, not as silence");
		Assert.AreEqual("stue", read.Config.Areas[0].AreaId);
		Assert.IsTrue(read.Config.Areas[0].RespectSleepMode, "and each area keeps everything under its own key");
		Assert.AreEqual("light.outdoor_front", read.Config.Areas[1].Lights!.Single());

		Assert.IsTrue(read.Config.Global.AreasAutoDiscovered,
			"ZonesAutoDiscovered is honoured, or a migrated house has every room proposed at it a second time");

		// Nothing else may be disturbed by the translation.
		Assert.AreEqual("Adaptive lighting [Home]", read.Config.ConfigName);
		Assert.AreEqual("no-touch", read.Config.Global.ExcludeLabel);
		Assert.AreEqual(35, read.Config.Defaults.LuxThreshold);
		Assert.AreEqual(DarknessSource.Sun, read.Config.Defaults.Darkness);
		Assert.AreEqual(2, read.Config.Periods.Count);
		Assert.AreEqual("22:30", read.Config.Periods[1].Start);
	}

	[TestMethod]
	public void A_Document_Written_In_The_Current_Schema_Reports_No_Legacy_Keys()
	{
		DocumentReadResult read = LightingConfigDocument.Deserialize(LightingConfigDocument.Serialize(Populated()));

		Assert.IsFalse(read.UsedLegacyKeys);
		Assert.AreEqual(2, read.Config.Areas.Count);
	}

	[TestMethod]
	public void Serialize_Emits_The_Current_Key_Names_And_Never_The_Legacy_Ones()
	{
		AdaptiveLightingConfig config = LightingConfigDocument.Deserialize(LegacySchema).Config;
		config.Global.AreasAutoDiscovered = true;

		string yaml = LightingConfigDocument.Serialize(config);

		StringAssert.Contains(yaml, "Areas:");
		StringAssert.Contains(yaml, "AreasAutoDiscovered:");
		StringAssert.DoesNotMatch(yaml, new System.Text.RegularExpressions.Regex("Zones"));
		StringAssert.DoesNotMatch(yaml, new System.Text.RegularExpressions.Regex("ZonesAutoDiscovered"));
	}

	[TestMethod]
	public void A_Document_Carrying_Both_Key_Names_Keeps_The_Current_One_And_Warns()
	{
		string both =
			$"""
			{LightingConfigDocument.RootKey}:
			  Global:
			    ZonesAutoDiscovered: false
			    AreasAutoDiscovered: true
			  Periods:
			    - Name: day
			      Start: "06:00"
			      BrightnessPct: 80
			      ColorTempKelvin: 3500
			  Zones:
			    - Name: From the legacy key
			      AreaId: legacy
			  Areas:
			    - Name: From the current key
			      AreaId: current
			""";

		RecordingLogger logger = new();

		DocumentReadResult read = LightingConfigDocument.Deserialize(both, logger);

		Assert.AreEqual("current", read.Config.Areas.Single().AreaId, "the current key wins outright; the legacy one is dropped");
		Assert.IsTrue(read.Config.Global.AreasAutoDiscovered, "and the same rule applies key by key, not document-wide");

		Assert.IsTrue(read.UsedLegacyKeys, "the file still needs rewriting: it carries a key the model does not have");

		Assert.AreEqual(2, logger.Warnings.Count, "one warning per key that was carried twice");
		Assert.IsTrue(logger.Warnings.All(warning =>
				warning.Contains("Zones", StringComparison.Ordinal) && warning.Contains("Areas", StringComparison.Ordinal)),
			$"each warning must name both keys, or nobody can find them in the file. Got: {string.Join(" | ", logger.Warnings)}");
	}

	[TestMethod]
	public void A_Legacy_Document_Read_Written_And_Read_Again_Lands_On_The_Current_Schema()
	{
		DocumentReadResult first = LightingConfigDocument.Deserialize(LegacySchema);

		string rewritten = LightingConfigDocument.Serialize(first.Config);

		DocumentReadResult second = LightingConfigDocument.Deserialize(rewritten);

		Assert.IsFalse(second.UsedLegacyKeys, "one save is enough: the rewritten file needs no translation");
		StringAssert.DoesNotMatch(rewritten, new System.Text.RegularExpressions.Regex("Zones"));

		Assert.AreEqual(first.Config.Areas.Count, second.Config.Areas.Count);
		Assert.AreEqual("stue", second.Config.Areas[0].AreaId);
		Assert.IsTrue(second.Config.Areas[0].RespectSleepMode);
		Assert.AreEqual("uteplass", second.Config.Areas[1].AreaId);
		Assert.AreEqual("light.outdoor_front", second.Config.Areas[1].Lights!.Single());
		Assert.IsTrue(second.Config.Global.AreasAutoDiscovered);
		Assert.AreEqual("Adaptive lighting [Home]", second.Config.ConfigName);
	}

	// Only the two legacy names are matched case-insensitively; the binder matches every surrounding key case-sensitively.
	[TestMethod]
	public void The_Legacy_Key_Names_Are_Matched_Whatever_Case_The_File_Used()
	{
		DocumentReadResult read = LightingConfigDocument.Deserialize(
			$"""
			{LightingConfigDocument.RootKey}:
			  Global:
			    zonesautodiscovered: true
			  zones:
			    - Name: Stue
			      AreaId: stue
			""");

		Assert.IsTrue(read.UsedLegacyKeys);
		Assert.AreEqual("stue", read.Config.Areas.Single().AreaId);
		Assert.IsTrue(read.Config.Global.AreasAutoDiscovered);
	}

	// The two migrations compound: both have to be recognised on one read, or the section is never found and its
	// Zones: is never translated.
	[TestMethod]
	public void A_Document_Under_A_Previous_Namespace_Still_Has_Its_Pre_2_0_Keys_Translated()
	{
		DocumentReadResult read = LightingConfigDocument.Deserialize(
			"""
			Laget.NetDaemon.AdaptiveLighting.Configuration.AdaptiveLightingConfig:
			  ConfigName: "Adaptive lighting [Hytta]"
			  Global:
			    ZonesAutoDiscovered: true
			  Zones:
			    - Name: Kjøkken
			      AreaId: kjokken
			    - Name: Kjeller bad
			      AreaId: kjeller_bad
			""");

		Assert.IsTrue(read.UsedLegacyKeys);
		Assert.AreEqual(2, read.Config.Areas.Count, "the rooms arrive even though the section key is the old one");
		Assert.AreEqual("kjokken", read.Config.Areas[0].AreaId);
		Assert.AreEqual("Kjøkken", read.Config.Areas[0].Name, "and non-ASCII names survive the node-tree round trip");
		Assert.AreEqual("kjeller_bad", read.Config.Areas[1].AreaId);
		Assert.IsTrue(read.Config.Global.AreasAutoDiscovered);
	}

	// A key with nothing under it is valid YAML for null, and YamlDotNet assigns it over the property's initialiser,
	// so a non-nullable type comes back null.
	[TestMethod]
	public void A_Section_Emptied_By_Hand_Loads_As_Absent_Rather_Than_As_Null()
	{
		AdaptiveLightingConfig config = LightingConfigDocument.Deserialize(
			$"""
			{LightingConfigDocument.RootKey}:
			  ConfigName: "Adaptive lighting [Hjemme]"
			  Global:
			  Defaults:
			  Periods:
			  Areas:
			""").Config;

		Assert.AreEqual("Adaptive lighting [Hjemme]", config.ConfigName, "the rest of the document still loads");

		Assert.IsNotNull(config.Global, "an emptied Global: must not come back as null");
		Assert.IsNotNull(config.Defaults, "an emptied Defaults: must not come back as null");
		Assert.IsNotNull(config.Periods, "an emptied Periods: must not come back as null");
		Assert.IsNotNull(config.Areas, "an emptied Areas: must not come back as null");

		Assert.AreEqual(0, config.Periods.Count);
		Assert.AreEqual(0, config.Areas.Count);
		Assert.AreEqual(0, config.Global.Persons.Count);
		Assert.AreEqual(0, config.Global.MotionDeviceClasses.Count);

		// The view over MotionDeviceClasses reads the list, so a null list is a null reference every reader hits.
		Assert.AreEqual(
			GlobalConfig.DefaultMotionDeviceClasses.Count, config.Global.EffectiveMotionDeviceClasses.Count);
	}

	// The same failure inside a period: the normaliser, the validator and ModeMonitor all walk this list.
	[TestMethod]
	public void An_Emptied_StartsOnMotionAreas_Loads_As_No_Rooms()
	{
		AdaptiveLightingConfig config = LightingConfigDocument.Deserialize(
			$"""
			{LightingConfigDocument.RootKey}:
			  Periods:
			    - Name: morning
			      Start: "06:30"
			      StartsOnMotion: true
			      StartsOnMotionAreas:
			""").Config;

		Assert.IsNotNull(config.Periods.Single().StartsOnMotionAreas, "an emptied key must not come back as null");
		Assert.AreEqual(0, config.Periods.Single().StartsOnMotionAreas.Count);

		// The normaliser is where it threw: it rewrites the list whenever StartsOnMotion is on.
		ConfigNormalizer.Normalize(config);
		Assert.AreEqual(0, config.Periods.Single().StartsOnMotionAreas.Count);
	}

	// The same failure one level down. This list is read by the normaliser on every save, not only by the engine.
	[TestMethod]
	public void An_Emptied_House_Mode_Option_List_Loads_As_No_Options()
	{
		AdaptiveLightingConfig config = LightingConfigDocument.Deserialize(
			$"""
			{LightingConfigDocument.RootKey}:
			  Global:
			    HouseMode:
			      Entity: input_select.husmodus
			      Options:
			""").Config;

		Assert.IsNotNull(config.Global.HouseMode!.Options);
		Assert.AreEqual(0, config.Global.HouseMode.Options.Count);
	}

	// And again under the period select: every area's evaluation goes through this mapping, so a null is one
	// exception per room per tick.
	[TestMethod]
	public void An_Emptied_Period_Select_Option_List_Loads_As_No_Options()
	{
		AdaptiveLightingConfig config = LightingConfigDocument.Deserialize(
			$"""
			{LightingConfigDocument.RootKey}:
			  Global:
			    PeriodSelect:
			      Entity: input_select.tid_pa_dagen
			      Authority: HomeAssistant
			      Options:
			""").Config;

		Assert.IsNotNull(config.Global.PeriodSelect!.Options);
		Assert.AreEqual(0, config.Global.PeriodSelect.Options.Count);
		Assert.IsNull(config.Global.PeriodSelect.PeriodFor("Kveld"), "and the mapping answers rather than throwing");
	}

	[TestMethod]
	public void A_Blank_Period_Select_Row_Is_Dropped()
	{
		AdaptiveLightingConfig config = LightingConfigDocument.Deserialize(
			$"""
			{LightingConfigDocument.RootKey}:
			  Global:
			    PeriodSelect:
			      Entity: input_select.tid_pa_dagen
			      Options:
			        -
			        - Value: Kveld
			          Period: evening
			""").Config;

		Assert.AreEqual(1, config.Global.PeriodSelect!.Options.Count);
		Assert.AreEqual("evening", config.Global.PeriodSelect.PeriodFor("Kveld"));
	}

	// An enum value is the one thing a document can carry that stops the app instead of being ignored.
	[TestMethod]
	public void Period_Select_Authority_Binds_By_Name_And_By_Ordinal()
	{
		static PeriodAuthority Read(string authority) => LightingConfigDocument.Deserialize(
			$"""
			{LightingConfigDocument.RootKey}:
			  Global:
			    PeriodSelect:
			      Entity: input_select.tid_pa_dagen
			      Authority: {authority}
			""").Config.Global.PeriodSelect!.Authority;

		Assert.AreEqual(PeriodAuthority.AdaptiveLighting, Read("AdaptiveLighting"));
		Assert.AreEqual(PeriodAuthority.HomeAssistant, Read("HomeAssistant"));
		Assert.AreEqual(PeriodAuthority.AdaptiveLighting, Read("0"), "Enum.Parse accepts the bare numeral");
		Assert.AreEqual(PeriodAuthority.HomeAssistant, Read("1"));
	}

	// Ordinals are compile-time constants inlined into consuming assemblies. Renumbering silently changes what an
	// already-built binary, and an already-written file, mean.
	[TestMethod]
	public void Period_Authority_Ordinals_Are_Fixed()
	{
		Assert.AreEqual(0, (int)PeriodAuthority.AdaptiveLighting);
		Assert.AreEqual(1, (int)PeriodAuthority.HomeAssistant);
	}

	[TestMethod]
	public void A_Document_Without_A_Period_Select_Loads_As_None()
	{
		AdaptiveLightingConfig config = LightingConfigDocument.Deserialize(
			$"""
			{LightingConfigDocument.RootKey}:
			  Periods:
			    - Name: day
			      Start: "07:00"
			""").Config;

		Assert.IsNull(config.Global.PeriodSelect, "no PeriodSelect block → property null, and OmitNull writes none back");
	}

	// Substituting an empty room or period for a bare dash invents one the file never named, and the validator
	// then stops the document over a stray dash.
	[TestMethod]
	public void A_Blank_List_Entry_Is_Read_As_Absent_Rather_Than_As_A_Nameless_Room()
	{
		AdaptiveLightingConfig config = LightingConfigDocument.Deserialize(
			$"""
			{LightingConfigDocument.RootKey}:
			  Global:
			    Persons:
			      -
			      - person.alex
			  Periods:
			    -
			    - Name: day
			      Start: "06:00"
			      BrightnessPct: 80
			      ColorTempKelvin: 3500
			  Areas:
			    -
			    - Name: Stue
			      AreaId: stue
			      Lights:
			        -
			        - light.stue
			""").Config;

		Assert.AreEqual("day", config.Periods.Single().Name, "the real period survives and the blank one is gone");
		Assert.AreEqual("stue", config.Areas.Single().AreaId);
		Assert.AreEqual("person.alex", config.Global.Persons.Single());
		Assert.AreEqual("light.stue", config.Areas.Single().Lights!.Single());
	}

	// The current key wins, but only a key the binder can read can win. YamlDotNet matches property names
	// case-sensitively, so a hand-edited lower-case areas: binds to nothing.
	[TestMethod]
	public void A_Legacy_Key_Is_Never_Dropped_For_A_Current_Key_The_Binder_Cannot_Read()
	{
		DocumentReadResult read = LightingConfigDocument.Deserialize(
			$"""
			{LightingConfigDocument.RootKey}:
			  Zones:
			    - Name: Stue
			      AreaId: stue
			  areas:
			    - Name: Never bound
			      AreaId: never_bound
			""");

		Assert.IsTrue(read.UsedLegacyKeys);
		Assert.AreEqual("stue", read.Config.Areas.Single().AreaId,
			"the legacy key still binds, because the lower-case one never could");
	}

	// The translation stops at this document's section. A YAML file may hold another NetDaemon app beside this
	// one, and Zones: is a good key for an app managing Home Assistant's GPS zones.
	[TestMethod]
	public void A_Legacy_Key_In_Another_Apps_Section_Is_Neither_Renamed_Nor_Treated_As_A_Migration()
	{
		DocumentReadResult read = LightingConfigDocument.Deserialize(
			$"""
			SomeGpsZoneApp.Configuration.Config:
			  Zones:
			    - home
			    - work
			{LightingConfigDocument.RootKey}:
			  Areas:
			    - Name: Stue
			      AreaId: stue
			""");

		Assert.AreEqual("stue", read.Config.Areas.Single().AreaId, "this document still loads");
		Assert.IsFalse(read.UsedLegacyKeys,
			"and nothing here needs migrating: the only Zones: in the file belongs to somebody else");
	}

	/// <summary>Captures everything logged at warning or above.</summary>
	private sealed class RecordingLogger : ILogger
	{
		private readonly List<string> _warnings = [];

		public IReadOnlyList<string> Warnings => _warnings;

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			ArgumentNullException.ThrowIfNull(formatter);

			if (logLevel >= LogLevel.Warning)
				_warnings.Add(formatter(state, exception));
		}
	}
}
