using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     Round-trip tests for the YAML loader.
/// </summary>
/// <remarks>
///     These exist because the UI now writes this file. A viewer that mis-parses shows a wrong number; a
///     writer that mis-parses destroys the household's configuration and then serialises the damage back over
///     the only copy. The round trip is the property that makes the write path safe to press, so it is tested
///     against a document that has every shape in the schema in it, not a happy-path stub.
/// </remarks>
[TestClass]
public sealed class LightingConfigDocumentTests
{
	/// <summary>A document exercising every shape the schema has: sun-anchored periods, caps, explicit lists, overrides.</summary>
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
			IlluminanceDeviceClass = "illuminance",
			BrightnessTolerancePct = 3.5,
			ColorTempToleranceKelvin = 75
		},
		Defaults = new ZoneSettings
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
			new TimePeriodConfig { Name = "morning", Start = "06:00", BrightnessPct = 60, ColorTempKelvin = 3000 },
			new TimePeriodConfig { Name = "day", Start = "sunrise+00:45", BrightnessPct = 90, ColorTempKelvin = 4500 },
			new TimePeriodConfig { Name = "evening", Start = "sunset-01:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new TimePeriodConfig
			{
				Name = "night", Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200,
				MaxBrightnessPct = 30, MinBrightnessPct = 5
			}
		],
		Zones =
		[
			// The common case: an area and one override, everything else inherited.
			new ZoneConfig { Name = "Stue", AreaId = "stue", RespectSleepMode = true },

			// The awkward case: explicit lists replacing discovery, and a spread of override types.
			new ZoneConfig
			{
				Name = "Uteplass",
				AreaId = "uteplass",
				Lights = ["light.outdoor_front", "light.outdoor_back"],
				MotionSensors = ["binary_sensor.outdoor_mmwave"],
				LuxSensor = "sensor.outdoor_illuminance",
				IgnoreWhenOn = ["binary_sensor.projector"],
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

		var actual = LightingConfigDocument.Deserialize(LightingConfigDocument.Serialize(original)).Global;
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
		Assert.AreEqual(expected.BrightnessTolerancePct, actual.BrightnessTolerancePct);
		Assert.AreEqual(expected.ColorTempToleranceKelvin, actual.ColorTempToleranceKelvin);
	}

	[TestMethod]
	public void RoundTrip_PreservesDefaults()
	{
		var original = Populated();

		var actual = LightingConfigDocument.Deserialize(LightingConfigDocument.Serialize(original)).Defaults;
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

	[TestMethod]
	public void RoundTrip_PreservesPeriods()
	{
		var original = Populated();

		var actual = LightingConfigDocument.Deserialize(LightingConfigDocument.Serialize(original)).Periods;

		Assert.AreEqual(original.Periods.Count, actual.Count);

		for (var index = 0; index < original.Periods.Count; index++)
		{
			Assert.AreEqual(original.Periods[index].Name, actual[index].Name);
			Assert.AreEqual(original.Periods[index].Start, actual[index].Start);
			Assert.AreEqual(original.Periods[index].BrightnessPct, actual[index].BrightnessPct);
			Assert.AreEqual(original.Periods[index].ColorTempKelvin, actual[index].ColorTempKelvin);
			Assert.AreEqual(original.Periods[index].MinBrightnessPct, actual[index].MinBrightnessPct);
			Assert.AreEqual(original.Periods[index].MaxBrightnessPct, actual[index].MaxBrightnessPct);
		}
	}

	[TestMethod]
	public void RoundTrip_PreservesFullyOverriddenZone()
	{
		var original = Populated();

		var actual = LightingConfigDocument.Deserialize(LightingConfigDocument.Serialize(original)).Zones[1];
		var expected = original.Zones[1];

		Assert.AreEqual(expected.Name, actual.Name);
		Assert.AreEqual(expected.AreaId, actual.AreaId);
		CollectionAssert.AreEqual(expected.Lights, actual.Lights);
		CollectionAssert.AreEqual(expected.MotionSensors, actual.MotionSensors);
		Assert.AreEqual(expected.LuxSensor, actual.LuxSensor);
		CollectionAssert.AreEqual(expected.IgnoreWhenOn, actual.IgnoreWhenOn);
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

	/// <summary>
	///     The single most destructive round-trip bug available: null means "inherit Defaults", and a
	///     serialiser that wrote nulls back as concrete values would freeze every zone at whatever the defaults
	///     happened to be on the day somebody first pressed Save. Every later edit to Defaults would then do
	///     nothing, silently, for reasons nobody could see in the file.
	/// </summary>
	[TestMethod]
	public void RoundTrip_LeavesInheritedZoneSettingsNull()
	{
		var original = Populated();

		var zone = LightingConfigDocument.Deserialize(LightingConfigDocument.Serialize(original)).Zones[0];

		Assert.AreEqual("stue", zone.AreaId);
		Assert.IsTrue(zone.RespectSleepMode);

		Assert.IsNull(zone.Lights);
		Assert.IsNull(zone.MotionSensors);
		Assert.IsNull(zone.LuxSensor);
		Assert.IsNull(zone.IgnoreWhenOn);
		Assert.IsNull(zone.VacancyTimeoutSeconds);
		Assert.IsNull(zone.PreOffSeconds);
		Assert.IsNull(zone.PreOffBrightnessFactor);
		Assert.IsNull(zone.OverrideDurationMinutes);
		Assert.IsNull(zone.VacancyResetMinutes);
		Assert.IsNull(zone.Darkness);
		Assert.IsNull(zone.LuxThreshold);
		Assert.IsNull(zone.LuxHysteresis);
		Assert.IsNull(zone.SunElevationThreshold);
		Assert.IsNull(zone.SunEntity);
		Assert.IsNull(zone.DayTransitionSeconds);
		Assert.IsNull(zone.NightTransitionSeconds);
		Assert.IsNull(zone.SleepBlocksAutoOn);
		Assert.IsNull(zone.SkipAwaySweep);
		Assert.IsNull(zone.WelcomeHome);
		Assert.IsNull(zone.Enabled);
	}

	/// <summary>
	///     An empty MotionDeviceClasses list means "the built-in set". Serialising the resolved fallback would
	///     turn "no opinion" into three device classes the operator never chose, permanently, on the first save.
	/// </summary>
	[TestMethod]
	public void Serialize_DoesNotWriteComputedProperties()
	{
		var config = Populated();
		config.Global.MotionDeviceClasses = [];

		var yaml = LightingConfigDocument.Serialize(config);

		StringAssert.DoesNotMatch(yaml, new System.Text.RegularExpressions.Regex(nameof(GlobalConfig.EffectiveMotionDeviceClasses)));
		StringAssert.DoesNotMatch(yaml, new System.Text.RegularExpressions.Regex(nameof(ZoneConfig.DisplayName)));

		var reloaded = LightingConfigDocument.Deserialize(yaml);

		Assert.AreEqual(0, reloaded.Global.MotionDeviceClasses.Count);
		CollectionAssert.AreEqual(
			GlobalConfig.DefaultMotionDeviceClasses.ToList(),
			reloaded.Global.EffectiveMotionDeviceClasses.ToList());
	}

	/// <summary>Round-tripping twice must be a fixed point: a save that keeps changing the file is a save nobody can review.</summary>
	[TestMethod]
	public void Serialize_IsStableAcrossRepeatedRoundTrips()
	{
		var once = LightingConfigDocument.Serialize(Populated());
		var twice = LightingConfigDocument.Serialize(LightingConfigDocument.Deserialize(once));

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
		var config = LightingConfigDocument.Deserialize($"{LightingConfigDocument.RootKey}:\n");

		Assert.AreEqual(0, config.Zones.Count);
		Assert.AreEqual(0, config.Periods.Count);
	}

	/// <summary>The file the household hand-edited before the UI existed must still load in the UI.</summary>
	[TestMethod]
	public void RoundTrip_NoHouseMode_EmitsNoHouseModeOrModeKeys()
	{
		// Populated() carries no HouseMode and no tagged periods, so a save must not acquire either key.
		var yaml = LightingConfigDocument.Serialize(Populated());

		StringAssert.DoesNotMatch(yaml, new System.Text.RegularExpressions.Regex("HouseMode"));
		StringAssert.DoesNotMatch(yaml, new System.Text.RegularExpressions.Regex(@"\bMode:"));
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
		original.Periods.Add(new TimePeriodConfig { Name = "late", SetsMode = "Sover", Start = "23:15", BrightnessPct = 10, ColorTempKelvin = 2000 });

		var reloaded = LightingConfigDocument.Deserialize(LightingConfigDocument.Serialize(original));

		Assert.AreEqual("input_select.husmodus", reloaded.Global.HouseMode!.Entity);
		Assert.AreEqual(3, reloaded.Global.HouseMode.Options.Count, "the option list replaces, it does not append");
		Assert.AreEqual(ModeKind.Sleep, reloaded.Global.HouseMode.OptionFor("Sover")!.Kind, "Kind survives the round trip");
		Assert.AreEqual(ModeKind.Away, reloaded.Global.HouseMode.OptionFor("Borte")!.Kind);
		Assert.AreEqual("scene.borte", reloaded.Global.HouseMode.OptionFor("Borte")!.Scene, "the option's Scene survives");
		Assert.AreEqual("Sover", reloaded.Periods.Single(p => string.Equals(p.Name, "late", StringComparison.Ordinal)).SetsMode,
			"SetsMode survives the round trip");
	}

	/// <summary>
	///     The kill-switch views are in-memory only: <see cref="GlobalConfig.DefaultKillSwitchEntity"/> is the
	///     built-in switch the host injects at start, and <see cref="GlobalConfig.EffectiveKillSwitchEntity"/> is a
	///     read-only view. Serialising either would write the resolved fallback back into the file as if it had been
	///     chosen, so the document must carry neither.
	/// </summary>
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
			  Zones:
			    - Name: Example living room
			      AreaId: REPLACE_ME_living_room_area_id
			      RespectSleepMode: true
			""");

		Assert.AreEqual("Adaptive lighting [Home]", config.ConfigName);
		Assert.AreEqual("person.REPLACE_ME", config.Global.Persons.Single());
		Assert.AreEqual(DarknessSource.Either, config.Defaults.Darkness);
		Assert.AreEqual("22:30", config.Periods.Single().Start);
		Assert.AreEqual(30, config.Periods.Single().MaxBrightnessPct);
		Assert.AreEqual("REPLACE_ME_living_room_area_id", config.Zones.Single().AreaId);
		Assert.IsTrue(config.Zones.Single().RespectSleepMode);
	}

	/// <summary>
	///     The top-level key is a fully qualified type name, so extracting this library for distribution renamed
	///     it. Every document already on disk must keep loading, or an upgrade silently orphans a working house.
	/// </summary>
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

		AdaptiveLightingConfig config = LightingConfigDocument.Deserialize(legacy);

		Assert.AreEqual("Adaptive lighting [Home]", config.ConfigName, "a document under the old namespace key still loads");
		Assert.AreEqual(1, config.Periods.Count);

		StringAssert.Contains(LightingConfigDocument.Serialize(config), LightingConfigDocument.RootKey,
			"and saving rewrites it under the current key, so the file self-migrates");
	}
}
