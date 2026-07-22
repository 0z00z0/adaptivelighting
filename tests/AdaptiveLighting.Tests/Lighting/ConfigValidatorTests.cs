using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     What the validator refuses outright, what it merely reports against one area, and the difference.
/// </summary>
/// <remarks>
///     The split is the whole design: a document-level problem means nobody thought about this config and the
///     app should show up dead in HA; a referential problem — an entity renamed under us — must cost that one
///     area rather than the house.
/// </remarks>
[TestClass]
public sealed class ConfigValidatorTests
{
	private static AdaptiveLightingConfig Minimal() => new()
	{
		Periods = [new() { Name = "day", Start = "07:00" }, new() { Name = "night", Start = "22:30" }],
		Areas = [new() { Name = "Stue", AreaId = "stue" }]
	};

	[TestMethod]
	public void A_Reasonable_Config_Passes()
	{
		Assert.IsTrue(ConfigValidator.Validate(Minimal()).IsValid);
	}

	[TestMethod]
	public void An_Empty_Document_Fails_On_Periods_But_Only_Warns_On_Areas()
	{
		var result = ConfigValidator.Validate(new AdaptiveLightingConfig());

		Assert.IsFalse(result.IsValid, "with no periods the engine could never pick a target");
		Assert.IsTrue(result.Errors.Any(e => e.Contains("Periods", StringComparison.Ordinal)));

		// Deliberately NOT an error. An empty area list is where a new installation starts, before discovery has
		// run - and where a household ends up after removing every room on purpose. The engine runs and commands
		// nothing, which is a fine state to be in; refusing the document would stop the app and announce
		// "document-level errors" to somebody whose only sin is not having configured anything yet.
		Assert.IsFalse(result.Errors.Any(e => e.Contains("areas", StringComparison.OrdinalIgnoreCase)),
			"an empty area list is a warning, not a document error");
		Assert.IsTrue(result.Warnings.Any(w => w.Contains("No areas yet", StringComparison.Ordinal)));
	}

	/// <summary>
	///     Empty is the default and means <see cref="GlobalConfig.DefaultMotionDeviceClasses"/>, so it must not be
	///     an error. It used to be, which would now reject every config that did not fight the binder.
	/// </summary>
	[TestMethod]
	public void An_Empty_MotionDeviceClasses_Is_Not_An_Error()
	{
		var config = Minimal();
		config.Global.MotionDeviceClasses = [];

		Assert.IsTrue(ConfigValidator.Validate(config).IsValid);
	}

	// ===================== periods =====================

	[TestMethod]
	public void An_Unparseable_Start_Is_Rejected_With_The_Grammar()
	{
		var config = Minimal();
		config.Periods = [new() { Name = "x", Start = "half past tea" }];

		var result = ConfigValidator.Validate(config);

		Assert.IsFalse(result.IsValid);
		StringAssert.Contains(result.ToString(), "sunset-01:00", "the error should show the household what it may write");
	}

	[TestMethod]
	public void Sun_Anchored_Starts_Are_Accepted()
	{
		var config = Minimal();
		config.Periods =
		[
			new() { Name = "day", Start = "sunrise" },
			new() { Name = "evening", Start = "sunset-01:00" },
			new() { Name = "night", Start = "22:30" }
		];

		Assert.IsTrue(ConfigValidator.Validate(config).IsValid);
	}

	[TestMethod]
	public void Duplicate_Period_Names_Are_Rejected()
	{
		var config = Minimal();
		config.Periods = [new() { Name = "day", Start = "07:00" }, new() { Name = "day", Start = "08:00" }];

		var result = ConfigValidator.Validate(config);

		Assert.IsFalse(result.IsValid);
		StringAssert.Contains(result.ToString(), "Duplicate");
	}

	[TestMethod]
	public void Two_Periods_Starting_At_The_Same_Clock_Time_Are_Rejected()
	{
		var config = Minimal();
		config.Periods = [new() { Name = "a", Start = "07:00" }, new() { Name = "b", Start = "07:00" }];

		Assert.IsFalse(ConfigValidator.Validate(config).IsValid);
	}

	[TestMethod]
	public void Two_Sun_Anchored_Periods_Are_Not_Rejected_As_Overlapping()
	{
		var config = Minimal();
		config.Periods = [new() { Name = "a", Start = "sunrise" }, new() { Name = "b", Start = "sunset" }];

		Assert.IsTrue(ConfigValidator.Validate(config).IsValid,
			"sun boundaries move daily; an overlap between them cannot be decided at validation time");
	}

	[TestMethod]
	public void Brightness_Outside_Zero_To_A_Hundred_Is_Rejected()
	{
		var config = Minimal();
		config.Periods = [new() { Name = "d", Start = "07:00", BrightnessPct = 400 }];

		Assert.IsFalse(ConfigValidator.Validate(config).IsValid);
	}

	[TestMethod]
	public void A_Colour_Temperature_No_Lamp_Can_Make_Is_Rejected()
	{
		var config = Minimal();
		config.Periods = [new() { Name = "d", Start = "07:00", ColorTempKelvin = 42 }];

		Assert.IsFalse(ConfigValidator.Validate(config).IsValid);
	}

	[TestMethod]
	public void A_Floor_Above_Its_Ceiling_Is_Rejected()
	{
		var config = Minimal();
		config.Periods = [new() { Name = "d", Start = "07:00", MinBrightnessPct = 80, MaxBrightnessPct = 20 }];

		Assert.IsFalse(ConfigValidator.Validate(config).IsValid);
	}

	// ===================== settings =====================

	[TestMethod]
	public void A_PreOff_Longer_Than_The_Vacancy_Timeout_Is_Rejected()
	{
		var config = Minimal();
		config.Defaults = new AreaSettings { VacancyTimeoutSeconds = 20, PreOffSeconds = 30 };

		var result = ConfigValidator.Validate(config);

		Assert.IsFalse(result.IsValid, "a warning that arrives after the darkness is not a warning");
	}

	[TestMethod]
	public void Negative_Times_Are_Rejected()
	{
		var config = Minimal();
		config.Global.AwayDebounceMinutes = -1;
		config.Global.CircadianTickSeconds = 0;

		Assert.AreEqual(2, ConfigValidator.Validate(config).Errors.Count);
	}

	[TestMethod]
	public void An_Areas_Own_Bad_Override_Is_Caught_Under_The_Areas_Name()
	{
		var config = Minimal();
		config.Areas = [new() { Name = "Stue", AreaId = "stue", PreOffBrightnessFactor = 4 }];

		var result = ConfigValidator.Validate(config);

		Assert.IsFalse(result.IsValid);
		StringAssert.Contains(result.ToString(), "Stue", "the household needs to know which room to go and fix");
	}

	// ===================== areas =====================

	[TestMethod]
	public void Duplicate_Area_Names_Are_Rejected()
	{
		var config = Minimal();
		config.Areas = [new() { Name = "Z", AreaId = "a" }, new() { Name = "Z", AreaId = "b" }];

		var result = ConfigValidator.Validate(config);

		Assert.IsFalse(result.IsValid);
		StringAssert.Contains(result.ToString(), "Duplicate");
	}

	[TestMethod]
	public void An_Area_With_Nothing_To_Resolve_Is_An_Area_Error_Not_A_Document_Error()
	{
		var config = Minimal();
		config.Areas = [new() { Name = "Nowhere" }];

		var result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid);
		Assert.AreEqual(1, result.AreaErrors.Count);
	}

	[TestMethod]
	public void An_Unknown_Area_Id_Costs_The_Area_And_Not_The_House()
	{
		var config = Minimal();
		config.Areas = [new() { Name = "Z", AreaId = "nope" }];

		var result = ConfigValidator.Validate(config, knownEntityIds: [], knownAreaIds: ["stue"]);

		Assert.IsTrue(result.IsValid, "one renamed area must not take the whole house's lighting down");
		Assert.AreEqual(1, result.AreaErrors.Count);
		StringAssert.Contains(result.AreaErrors[0].Message, "stue", "and the message should name the ids that do exist");
	}

	[TestMethod]
	public void An_Unknown_Entity_Costs_The_Area_And_Not_The_House()
	{
		var config = Minimal();
		config.Areas = [new() { Name = "Z", AreaId = "stue", Lights = ["light.ghost"] }];

		var result = ConfigValidator.Validate(config, knownEntityIds: ["light.real"], knownAreaIds: ["stue"]);

		Assert.IsTrue(result.IsValid);
		Assert.AreEqual(1, result.AreaErrors.Count);
	}

	[TestMethod]
	public void An_Unknown_Global_Entity_Is_A_Document_Error()
	{
		// A watched-person entity HA does not know is a document-level error (unlike the kill switch, which fails
		// open and only warns — see KillSwitch_Unknown_OnlyWarns_NeverStopsTheDocument).
		var config = Minimal();
		config.Global.Persons = ["person.ghost"];

		var result = ConfigValidator.Validate(config, knownEntityIds: ["input_boolean.real"], knownAreaIds: ["stue"]);

		Assert.IsFalse(result.IsValid, "a watched person entity that does not exist is the house's problem, not one area's");
	}

	[TestMethod]
	public void Referential_Checks_Are_Skipped_When_Nothing_Is_Known()
	{
		var config = Minimal();
		config.Areas = [new() { Name = "Z", AreaId = "anything", Lights = ["light.whatever"] }];

		Assert.IsTrue(ConfigValidator.Validate(config).IsValid,
			"a caller with no IHaContext gets the document checks and nothing it cannot answer");
	}

	// ===================== rendering =====================

	[TestMethod]
	public void ToHtml_Renders_A_List_For_The_Persistent_Notification()
	{
		var html = ConfigValidator.Validate(new AdaptiveLightingConfig()).ToHtml();

		StringAssert.StartsWith(html, "<ul>");
	}

	// ===================== house modes =====================

	private static AdaptiveLightingConfig WithHouseMode()
	{
		var config = Minimal();
		config.Global.HouseMode = new HouseModeConfig
		{
			Entity = "input_select.husmodus",
			Options =
			[
				new() { Value = "Normal", Kind = ModeKind.Normal },
				new() { Value = "Borte", Kind = ModeKind.Away },
				new() { Value = "Sover", Kind = ModeKind.Sleep }
			]
		};
		return config;
	}

	[TestMethod]
	public void HouseModeEntity_MustBeInputSelect()
	{
		var wrongDomain = WithHouseMode();
		wrongDomain.Global.HouseMode!.Entity = "input_boolean.husmodus";
		Assert.IsFalse(ConfigValidator.Validate(wrongDomain).IsValid, "the house mode must be an input_select");

		Assert.IsTrue(ConfigValidator.Validate(WithHouseMode()).IsValid, "a correct input_select passes cleanly");
	}

	[TestMethod]
	public void DuplicateOrBlankOptionValues_AreRejected()
	{
		var duplicate = WithHouseMode();
		duplicate.Global.HouseMode!.Options.Add(new HouseModeOptionConfig { Value = "sover" });
		Assert.IsFalse(ConfigValidator.Validate(duplicate).IsValid, "duplicate option values, case-insensitive");

		var blank = WithHouseMode();
		blank.Global.HouseMode!.Options.Add(new HouseModeOptionConfig { Value = "  " });
		Assert.IsFalse(ConfigValidator.Validate(blank).IsValid, "a blank option value");
	}

	[TestMethod]
	public void SetsMode_MustMatchAConfiguredOption_AndRequireASelect()
	{
		var unmatched = WithHouseMode();
		unmatched.Periods.Add(new TimePeriodConfig { Name = "film", SetsMode = "Film", Start = "20:00" });
		Assert.IsFalse(ConfigValidator.Validate(unmatched).IsValid, "a SetsMode matching no configured option value");

		var noSelect = Minimal();
		noSelect.Periods.Add(new TimePeriodConfig { Name = "late", SetsMode = "Sover", Start = "23:00" });
		Assert.IsFalse(ConfigValidator.Validate(noSelect).IsValid, "a SetsMode while no HouseMode is configured matches nothing");
	}

	[TestMethod]
	public void SetsMode_MatchingALiveSelectOption_IsAccepted_EvenWhenNotYetTagged()
	{
		var config = WithHouseMode();
		config.Periods.Add(new TimePeriodConfig { Name = "film", SetsMode = "Film", Start = "20:00" });

		// "Film" is not a configured option, but the select reports it live → valid: the engine can select it, and
		// requiring it be tagged first would deadlock the save (tagging is itself a save).
		Assert.IsTrue(ConfigValidator.Validate(config, liveSelectOptions: ["Film"]).IsValid,
			"a SetsMode naming a live select option is legitimate even before it is tagged a Kind");

		// Without the live option, the configured-only rule still refuses it.
		Assert.IsFalse(ConfigValidator.Validate(config).IsValid, "unknown to both configured and live options → error");
	}

	[TestMethod]
	public void SetsMode_NamingANormalOption_Warns_ButDoesNotRefuse()
	{
		var config = WithHouseMode();
		config.Periods.Add(new TimePeriodConfig { Name = "reset", SetsMode = "Normal", Start = "06:00" });

		var result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid, "SetsMode naming a Normal option is a scheduled reset — legal, but probably a mistake");
		Assert.IsTrue(result.Warnings.Any(w => w.Contains("Normal", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void ExactlyOneNormal_NoneOrManyIsAWarning_NotAnError()
	{
		var none = WithHouseMode();
		none.Global.HouseMode!.OptionFor("Normal")!.Kind = ModeKind.Guest;   // now nothing is Normal
		var noneResult = ConfigValidator.Validate(none);
		Assert.IsTrue(noneResult.IsValid, "no Normal is a warning, not a refusal");
		Assert.IsTrue(noneResult.Warnings.Any(w => w.Contains("Normal", StringComparison.Ordinal)));

		var many = WithHouseMode();
		many.Global.HouseMode!.Options.Add(new HouseModeOptionConfig { Value = "Kveld", Kind = ModeKind.Normal });
		var manyResult = ConfigValidator.Validate(many);
		Assert.IsTrue(manyResult.IsValid, "more than one Normal is a warning too");
		Assert.IsTrue(manyResult.Warnings.Any(w => w.Contains("Normal", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void Scene_MustBeASceneEntity_AndKnownWhenIdsAreGiven()
	{
		var notScene = WithHouseMode();
		notScene.Global.HouseMode!.OptionFor("Borte")!.Scene = "light.not_a_scene";
		Assert.IsFalse(ConfigValidator.Validate(notScene).IsValid, "a non-scene entity in Scene is an error");

		var unknown = WithHouseMode();
		unknown.Global.HouseMode!.OptionFor("Borte")!.Scene = "scene.ghost";
		Assert.IsFalse(ConfigValidator.Validate(unknown, knownEntityIds: ["input_select.husmodus"]).IsValid,
			"a scene HA does not know is an error");

		var known = WithHouseMode();
		known.Global.HouseMode!.OptionFor("Borte")!.Scene = "scene.borte";
		Assert.IsTrue(ConfigValidator.Validate(known, knownEntityIds: ["input_select.husmodus", "scene.borte"]).IsValid);
	}

	[TestMethod]
	public void SceneOrResetOnANormalOption_Warns()
	{
		var config = WithHouseMode();
		config.Global.HouseMode!.OptionFor("Normal")!.ResetOnPresence = true;

		var result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid, "reset fields on a Normal option are inert — a warning, not a document error");
		Assert.IsTrue(result.Warnings.Any(w => w.Contains("Normal", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void ClampPeriod_MustNameAPeriod_AndWarnsOffSleep()
	{
		var missing = WithHouseMode();
		missing.Global.HouseMode!.OptionFor("Sover")!.ClampPeriod = "nope";
		Assert.IsFalse(ConfigValidator.Validate(missing).IsValid, "a ClampPeriod naming no period is an error");

		var offSleep = WithHouseMode();
		offSleep.Global.HouseMode!.OptionFor("Borte")!.ClampPeriod = "night";   // Away, not Sleep — inert
		var result = ConfigValidator.Validate(offSleep);
		Assert.IsTrue(result.IsValid, "a ClampPeriod on a non-sleep option is inert, a warning not an error");
		Assert.IsTrue(result.Warnings.Any(w => w.Contains("ClampPeriod", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void ResetOnPeriodStart_MustNameAPeriod()
	{
		var config = WithHouseMode();
		config.Global.HouseMode!.OptionFor("Borte")!.ResetOnPeriodStart = "ghost";
		Assert.IsFalse(ConfigValidator.Validate(config).IsValid);
	}

	[TestMethod]
	public void StaleResetOnPeriodStart_OnANormalOption_Warns_NotErrors()
	{
		// The field is inert on a Normal option, so a dangling period name must not make the whole document unsaveable.
		var config = WithHouseMode();
		config.Global.HouseMode!.OptionFor("Normal")!.ResetOnPeriodStart = "gone";

		var result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid, "a dangling reset field on a Normal option is inert — a warning, not a document error");
	}

	[TestMethod]
	public void AnAwayOrGuestOption_WithNoResetTrigger_Warns()
	{
		// WithHouseMode's Borte (Away) carries no reset trigger, so it would stay active until a manual change.
		var result = ConfigValidator.Validate(WithHouseMode());

		Assert.IsTrue(result.IsValid, "a triggerless away option is legal");
		Assert.IsTrue(result.Warnings.Any(w => w.Contains("stay active", StringComparison.Ordinal)),
			"but it warns that the mode will stay active until a manual change");
	}

	[TestMethod]
	public void APresenceSensor_OfAWrongDomain_Warns()
	{
		var config = WithHouseMode();
		var borte = config.Global.HouseMode!.OptionFor("Borte")!;
		borte.ResetOnPresence = true;
		borte.ResetPresenceSensors = ["light.not_a_sensor"];   // not binary_sensor / person / device_tracker

		var result = ConfigValidator.Validate(config);   // no knownEntityIds, so the unknown-entity error is skipped

		Assert.IsTrue(result.IsValid, "a wrong-domain presence sensor is inert, not a document error");
		Assert.IsTrue(result.Warnings.Any(w => w.Contains("device_tracker", StringComparison.Ordinal)),
			"it warns that the sensor's presence will not reset the mode");
	}

	[TestMethod]
	public void ResetAtTime_MustBeADateOrTimeEntity_AndKnown()
	{
		var wrongDomain = WithHouseMode();
		wrongDomain.Global.HouseMode!.OptionFor("Borte")!.ResetAtTime = "light.clock";
		Assert.IsFalse(ConfigValidator.Validate(wrongDomain).IsValid, "ResetAtTime must name a date or time entity");

		// Broadened beyond input_datetime: the time/datetime helper domains and timestamp/date sensors all qualify.
		foreach (var id in new[] { "input_datetime.slutt", "time.slutt", "datetime.slutt", "sensor.next_alarm" })
		{
			var accepted = WithHouseMode();
			accepted.Global.HouseMode!.OptionFor("Borte")!.ResetAtTime = id;
			Assert.IsTrue(ConfigValidator.Validate(accepted).IsValid, $"'{id}' is a valid ResetAtTime entity");
		}

		var unknown = WithHouseMode();
		unknown.Global.HouseMode!.OptionFor("Borte")!.ResetAtTime = "input_datetime.ghost";
		Assert.IsFalse(ConfigValidator.Validate(unknown, knownEntityIds: ["input_select.husmodus"]).IsValid,
			"a ResetAtTime HA does not know is an error");
	}

	[TestMethod]
	public void ActivateWhileOn_WrongDomainWarns_UnknownErrors_ValidAccepted()
	{
		var wrongDomain = WithHouseMode();
		wrongDomain.Global.HouseMode!.OptionFor("Borte")!.ActivateWhileOn = ["light.not_a_toggle"];
		var result = ConfigValidator.Validate(wrongDomain);   // no knownEntityIds, so the unknown-entity error is skipped
		Assert.IsTrue(result.IsValid, "a wrong-domain activation entity is inert, not a document error");
		Assert.IsTrue(result.Warnings.Any(w => w.Contains("ActivateWhileOn", StringComparison.Ordinal)),
			"it warns the entity cannot turn the mode on");

		var unknown = WithHouseMode();
		unknown.Global.HouseMode!.OptionFor("Borte")!.ActivateWhileOn = ["input_boolean.ghost"];
		Assert.IsFalse(ConfigValidator.Validate(unknown, knownEntityIds: ["input_select.husmodus"]).IsValid,
			"an ActivateWhileOn entity HA does not know is an error");

		var known = WithHouseMode();
		known.Global.HouseMode!.OptionFor("Borte")!.ActivateWhileOn = ["input_boolean.sover"];
		Assert.IsTrue(ConfigValidator.Validate(known).IsValid, "a valid boolean-ish activation entity is accepted");
	}

	[TestMethod]
	public void ResetPresenceSensors_MustBeKnown_AndGraceNonNegative()
	{
		var unknown = WithHouseMode();
		unknown.Global.HouseMode!.OptionFor("Borte")!.ResetPresenceSensors = ["binary_sensor.ghost"];
		Assert.IsFalse(ConfigValidator.Validate(unknown, knownEntityIds: ["input_select.husmodus"]).IsValid,
			"a reset presence sensor HA does not know is an error");

		var negativeGrace = WithHouseMode();
		negativeGrace.Global.HouseMode!.OptionFor("Borte")!.ResetPresenceGraceMinutes = -1;
		Assert.IsFalse(ConfigValidator.Validate(negativeGrace).IsValid, "a negative grace is an error");
	}

	[TestMethod]
	public void SleepClamp_MustResolveWhenLoadBearing()
	{
		// Load-bearing (an area respects sleep), resolvable via the "night" period Minimal() carries → valid.
		var resolvable = WithHouseMode();
		resolvable.Areas[0].RespectSleepMode = true;
		Assert.IsTrue(ConfigValidator.Validate(resolvable).IsValid, "the 'night' period resolves the clamp");

		// Load-bearing, but nothing resolves the clamp (no ClampPeriod, no SetsMode period, no 'night') → error.
		var unresolvable = WithHouseMode();
		unresolvable.Areas[0].RespectSleepMode = true;
		unresolvable.Periods = [new() { Name = "day", Start = "07:00" }, new() { Name = "evening", Start = "18:00" }];
		Assert.IsFalse(ConfigValidator.Validate(unresolvable).IsValid, "nothing resolves the clamp for a load-bearing sleep path");

		// Not load-bearing (no area respects sleep) → valid even though nothing resolves.
		var notBearing = WithHouseMode();
		notBearing.Periods = [new() { Name = "day", Start = "07:00" }, new() { Name = "evening", Start = "18:00" }];
		Assert.IsTrue(ConfigValidator.Validate(notBearing).IsValid, "sleep is not load-bearing, so the missing clamp is inert");

		// Load-bearing, resolvable via an explicit ClampPeriod → valid.
		var viaClamp = WithHouseMode();
		viaClamp.Areas[0].RespectSleepMode = true;
		viaClamp.Periods = [new() { Name = "day", Start = "07:00" }, new() { Name = "dim", Start = "22:00" }];
		viaClamp.Global.HouseMode!.OptionFor("Sover")!.ClampPeriod = "dim";
		Assert.IsTrue(ConfigValidator.Validate(viaClamp).IsValid, "an explicit ClampPeriod resolves the clamp");
	}

	[TestMethod]
	public void KillSwitch_Unknown_OnlyWarns_NeverStopsTheDocument()
	{
		// The engine fails open on a missing kill switch, so an unknown one — explicit or defaulted — must never be
		// a document-stopping error, only a warning. (An explicit id that no longer exists is exactly what a renamed
		// app switch leaves behind.)
		var explicitUnknown = Minimal();
		explicitUnknown.Global.KillSwitchEntity = "input_boolean.ghost";
		var explicitResult = ConfigValidator.Validate(explicitUnknown, knownEntityIds: ["input_boolean.real"]);
		Assert.IsTrue(explicitResult.IsValid, "an explicit kill switch HA does not know still saves — the engine fails open");
		Assert.IsTrue(explicitResult.Warnings.Any(w => w.Contains("KillSwitchEntity", StringComparison.Ordinal)),
			"but it warns, since it is a likely mistake");

		var defaultedUnknown = Minimal();
		defaultedUnknown.Global.KillSwitchEntity = null;
		defaultedUnknown.Global.DefaultKillSwitchEntity = "input_boolean.netdaemon_builtin";
		var result = ConfigValidator.Validate(defaultedUnknown, knownEntityIds: ["input_boolean.real"]);
		Assert.IsTrue(result.IsValid, "a defaulted built-in switch HA has not created yet is only a warning");
		Assert.IsTrue(result.Warnings.Any(w => w.Contains("master switch", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void PlainDuplicatePeriodStarts_AreRejected_Again()
	{
		// With one shared table there is no per-mode partitioning: a fixed-start collision is a document error.
		var colliding = WithHouseMode();
		colliding.Periods =
		[
			new() { Name = "a", Start = "07:00" },
			new() { Name = "b", Start = "07:00" },
			new() { Name = "night", Start = "22:30" }
		];
		Assert.IsFalse(ConfigValidator.Validate(colliding).IsValid, "a fixed-start collision is rejected");
	}

	[TestMethod]
	public void Warnings_NeverRefuse()
	{
		var config = WithHouseMode();

		// HA offers Natt (unclassified) and no longer offers Sover (configured but gone).
		var result = ConfigValidator.Validate(config, liveSelectOptions: ["Normal", "Borte", "Natt"]);

		Assert.IsTrue(result.IsValid, "live-option mismatches are warnings, never errors");
		Assert.IsTrue(result.Warnings.Count >= 2, "one for the orphaned Sover, one for the unclassified Natt");
	}

	[TestMethod]
	public void Migration_LiveCabinConfig_ValidatesCleanly()
	{
		var config = LightingConfigDocument.Deserialize(
			$"""
			{LightingConfigDocument.RootKey}:
			  ConfigName: "Adaptive lighting [Cabin]"
			  Global:
			    Persons:
			      - person.alex
			  Periods:
			    - Name: day
			      Start: "07:00"
			    - Name: night
			      Start: "22:30"
			      MaxBrightnessPct: 30
			  Areas:
			    - Name: Stue
			      AreaId: stue
			      RespectSleepMode: true
			""").Config;

		Assert.IsNull(config.Global.HouseMode, "no HouseMode block → property null");
		Assert.IsTrue(ConfigValidator.Validate(config).IsValid,
			"the live cabin document validates cleanly — no house-mode rule fires without a HouseMode");
	}
}
