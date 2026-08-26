using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>What the validator refuses outright, what it reports against one area, and the difference.</summary>
/// <remarks>
///     A document error stops the engine; a referential error, such as a renamed entity, costs that one area. The
///     validator is pure: it cannot run discovery, so it never refuses over a sensor a room may find at runtime.
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

	// ===================== the outdoor sensor is not a silent fallback =====================

	/// <summary>The outdoor sensor is opt-in, so a document written before it was looks identical and behaves differently.</summary>
	[TestMethod]
	public void An_Outdoor_Sensor_No_Room_Follows_Is_Warned_About_As_A_Change_Of_Meaning()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Global.OutdoorLuxSensor = "sensor.outdoor_lux";

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid, "the document still runs — it just does something else than it did");
		Assert.IsTrue(result.Warnings.Any(w => w.Contains("no room follows it", StringComparison.Ordinal)),
			"the one place an upgraded house is told what changed under it");
		Assert.IsTrue(result.Warnings.Any(w => w.Contains("FollowOutdoorLux", StringComparison.Ordinal)),
			"and a warning without the name of the fix is a warning nobody can act on");
	}

	[TestMethod]
	public void A_Room_That_Follows_The_Outdoor_Sensor_Silences_That_Warning()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Global.OutdoorLuxSensor = "sensor.outdoor_lux";
		config.Areas[0].FollowOutdoorLux = true;

		Assert.IsFalse(
			ConfigValidator.Validate(config).Warnings.Any(w => w.Contains("no room follows it", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void Following_An_Outdoor_Sensor_The_House_Does_Not_Name_Warns_Per_Room()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Areas[0].FollowOutdoorLux = true;

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid);
		Assert.IsTrue(result.Warnings.Any(w =>
			w.Contains("Stue", StringComparison.Ordinal) && w.Contains("counts as dark", StringComparison.Ordinal)),
			"the room believes itself gated and is not, which is worth naming the room over");
	}

	[TestMethod]
	public void A_Document_With_No_Outdoor_Sensor_Is_Untouched_By_The_Opt_In()
	{
		Assert.AreEqual(0, ConfigValidator.Validate(Minimal()).Warnings.Count);
	}

	[TestMethod]
	public void An_Empty_Document_Fails_On_Periods_But_Only_Warns_On_Areas()
	{
		var result = ConfigValidator.Validate(new AdaptiveLightingConfig());

		Assert.IsFalse(result.IsValid, "with no periods the engine could never pick a target");
		Assert.IsTrue(result.Errors.Any(e => e.Contains("Periods", StringComparison.Ordinal)));

		// An empty area list is where a new installation starts, before discovery has run.
		Assert.IsFalse(result.Errors.Any(e => e.Contains("areas", StringComparison.OrdinalIgnoreCase)),
			"an empty area list is a warning, not a document error");
		Assert.IsTrue(result.Warnings.Any(w => w.Contains("No rooms yet", StringComparison.Ordinal)));
	}

	/// <summary>Empty is the default and means <see cref="GlobalConfig.DefaultMotionDeviceClasses"/>.</summary>
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
	public void Two_Periods_May_Share_A_Display_Name()
	{
		var config = Minimal();
		config.Periods =
		[
			new() { Id = "day-a1b2", Name = "day", Start = "07:00" },
			new() { Id = "day-c3d4", Name = "day", Start = "08:00" }
		];

		var result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid, "nothing resolves by a period's name any more");
	}

	[TestMethod]
	public void Two_Periods_Sharing_An_Id_Are_Rejected()
	{
		var config = Minimal();
		config.Periods =
		[
			new() { Id = "day-a1b2", Name = "morning", Start = "07:00" },
			new() { Id = "day-a1b2", Name = "afternoon", Start = "08:00" }
		];

		var result = ConfigValidator.Validate(config);

		Assert.IsFalse(result.IsValid);
		StringAssert.Contains(result.ToString(), "share the id 'day-a1b2'");
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

	/// <summary>An id no room answers to costs that room its trigger and nothing else; matched ordinally, as the engine matches area ids.</summary>
	[TestMethod]
	public void StartsOnMotionAreas_NamingNoRoomInTheDocument_Warns_AndQuotesTheId()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Periods[0].StartsOnMotion = true;
		config.Periods[0].StartsOnMotionAreas = ["kjokken"];

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid, "one room that never triggers the period is a degraded feature, not an unrunnable document");
		Assert.IsTrue(result.Warnings.Any(warning => warning.Contains("kjokken", StringComparison.Ordinal)),
			"the warning has to quote the id, because the id is the typo");
	}

	[TestMethod]
	public void StartsOnMotionAreas_NamingConfiguredRooms_SaysNothing()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Periods[0].StartsOnMotion = true;
		config.Periods[0].StartsOnMotionAreas = ["stue"];

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid);
		Assert.AreEqual(0, result.Warnings.Count);
	}

	[TestMethod]
	public void StartsOnMotion_WithNoAreasNamed_MeansAnyRoom_AndSaysNothing()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Periods[0].StartsOnMotion = true;

		Assert.AreEqual(0, ConfigValidator.Validate(config).Warnings.Count,
			"an empty list is the default and means any room the engine watches");
	}

	// Nothing begins on the clock, so from midnight the table places nothing and the rooms are commanded nothing.
	[TestMethod]
	public void StartsOnMotion_OnEveryPeriod_WarnsThatNothingBeginsOnTheClock()
	{
		AdaptiveLightingConfig config = Minimal();
		foreach (TimePeriodConfig period in config.Periods)
			period.StartsOnMotion = true;

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid, "it is a house that waits, not a document that cannot run");
		Assert.IsTrue(result.Warnings.Any(warning => warning.Contains("Every period sets StartsOnMotion", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void StartsOnMotion_OnAPeriodWithAnUnparseableStart_AddsNothingToTheOneError()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Periods =
		[
			new() { Name = "morning", Start = "half past tea", StartsOnMotion = true, StartsOnMotionAreas = ["ghost"] }
		];

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsFalse(result.IsValid, "the Start is the error");
		Assert.AreEqual(0, result.Warnings.Count,
			"the period is dropped whole, so its room list has earned no second sentence");
	}


	// ===================== the include label =====================

	/// <summary>Each skipped room says so itself; only this warning can say that one typo at the top of the file is behind all of them.</summary>
	[TestMethod]
	public void An_Include_Label_Nothing_Carries_Is_A_Warning_Not_An_Error()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Global.IncludeLabel = "adpative";   // the typo this warning exists for

		ValidationResult result = ConfigValidator.Validate(config, labelsInUse: ["adaptive", "adaptive-exclude"]);

		Assert.IsTrue(result.IsValid, "an unmatched include label must never stop the document being saved");
		Assert.IsTrue(result.Warnings.Any(warning => warning.Contains("adpative", StringComparison.Ordinal)),
			"the warning has to quote the label, because the label is the typo");
	}

	[TestMethod]
	public void An_Include_Label_Something_Carries_Is_Silent()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Global.IncludeLabel = "adaptive";

		ValidationResult result = ConfigValidator.Validate(config, labelsInUse: ["adaptive"]);

		Assert.AreEqual(0, result.Warnings.Count);
	}

	[TestMethod]
	public void No_Include_Label_Is_Never_Warned_About()
	{
		ValidationResult result = ConfigValidator.Validate(Minimal(), labelsInUse: []);

		Assert.AreEqual(0, result.Warnings.Count,
			"saying nothing is the default, not an omission — a house with no labels must hear nothing about them");
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

	// ===================== the two per-room scenes =====================

	// A room whose scene is wrong still lights, by its own levels, so nothing here may stop the house.
	[TestMethod]
	public void A_Room_Scene_Outside_The_Scene_Domain_Warns_And_Never_Errors()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Areas = [new() { Name = "Stue", AreaId = "stue", SceneOnMotion = "light.stue_tak" }];

		ValidationResult result = ConfigValidator.Validate(config, knownEntityIds: ["light.stue_tak"]);

		Assert.IsTrue(result.IsValid);
		Assert.AreEqual(0, result.AreaErrors.Count);
		Assert.IsTrue(result.Warnings.Any(w => w.Contains("SceneOnMotion", StringComparison.Ordinal)
			&& w.Contains("not a scene entity", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void A_Room_Scene_Home_Assistant_Does_Not_Know_Warns_And_Never_Errors()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Areas = [new() { Name = "Stue", AreaId = "stue", SceneWhenEmpty = "scene.gone" }];

		ValidationResult result = ConfigValidator.Validate(config, knownEntityIds: ["scene.still_here"]);

		Assert.IsTrue(result.IsValid);
		Assert.AreEqual(0, result.AreaErrors.Count, "a renamed scene costs the atmosphere, not the room");
		Assert.IsTrue(result.Warnings.Any(w => w.Contains("SceneWhenEmpty", StringComparison.Ordinal)
			&& w.Contains("does not know", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void Two_Good_Room_Scenes_Warn_About_Nothing()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Areas =
		[
			new()
			{
				Name = "Stue",
				AreaId = "stue",
				SceneOnMotion = "scene.stue_kveld",
				SceneWhenEmpty = "scene.stue_natt"
			}
		];

		ValidationResult result = ConfigValidator.Validate(config, knownEntityIds: ["scene.stue_kveld", "scene.stue_natt"]);

		Assert.IsTrue(result.IsValid);
		Assert.AreEqual(0, result.Warnings.Count);
	}

	// The domain check needs no registry, so it still runs when Home Assistant has not answered.
	[TestMethod]
	public void The_Domain_Check_Runs_With_No_Known_Entities_At_All()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Areas = [new() { Name = "Stue", AreaId = "stue", SceneWhenEmpty = "script.stue_natt" }];

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.Warnings.Any(w => w.Contains("not a scene entity", StringComparison.Ordinal)));
	}

	// ===================== the daylight brightness curve =====================

	[TestMethod]
	public void Inverted_Anchors_Are_Rejected_Even_With_The_Feature_Off()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Defaults.LuxBrightnessStartLux = 10000;
		config.Defaults.LuxBrightnessFullLux = 100;

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsFalse(result.IsValid);
		Assert.IsTrue(result.Errors.Any(e => e.Contains("LuxBrightnessFullLux", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void Equal_Anchors_Are_Rejected()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Defaults.LuxBrightnessStartLux = 1000;
		config.Defaults.LuxBrightnessFullLux = 1000;

		Assert.IsFalse(ConfigValidator.Validate(config).IsValid, "equal anchors leave no range to interpolate across");
	}

	[TestMethod]
	public void A_Start_Anchor_At_Or_Below_Zero_Is_Rejected()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Defaults.LuxBrightnessStartLux = 0;

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsFalse(result.IsValid);
		Assert.IsTrue(result.Errors.Any(e => e.Contains("log10", StringComparison.Ordinal)),
			"the message must say why zero is impossible rather than merely disallowed");
	}

	[TestMethod]
	public void A_Ceiling_Outside_The_Brightness_Range_Is_Rejected()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Defaults.LuxBrightnessMaxPct = 140;

		Assert.IsFalse(ConfigValidator.Validate(config).IsValid);
	}

	[TestMethod]
	public void A_Non_Positive_Gamma_Is_Rejected()
	{
		AdaptiveLightingConfig zero = Minimal();
		zero.Defaults.LuxBrightnessGamma = 0;

		AdaptiveLightingConfig negative = Minimal();
		negative.Defaults.LuxBrightnessGamma = -1;

		Assert.IsFalse(ConfigValidator.Validate(zero).IsValid, "pow(0, 0) is 1 — a zero exponent reads as full daylight in the dark");
		Assert.IsFalse(ConfigValidator.Validate(negative).IsValid);
	}

	[TestMethod]
	public void A_Rooms_Own_Bad_Curve_Is_Caught_Under_Its_Name()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Areas = [new() { Name = "Gang", AreaId = "gang", LuxBrightnessFullLux = 10 }];

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsFalse(result.IsValid);
		StringAssert.Contains(result.ToString(), "Gang", "the household needs to know which room to go and fix");
	}

	/// <summary>The curve reads the house's outdoor sensor, so a period claiming it with none named has nothing to read.</summary>
	[TestMethod]
	public void A_Period_On_The_Curve_With_No_Outdoor_Sensor_Only_Warns()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Periods[0].UseDaylightCurve = true;

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid, "a missing reading is a level nobody chose, not a broken document");
		Assert.IsTrue(result.Warnings.Any(w => w.Contains("daylight curve", StringComparison.Ordinal)));
		StringAssert.Contains(result.Warnings.Single(w => w.Contains("daylight curve", StringComparison.Ordinal)), "Stue",
			"the household needs to know which room has nothing to read");
	}

	[TestMethod]
	public void Naming_The_Houses_Outdoor_Sensor_Answers_That_Warning()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Periods[0].UseDaylightCurve = true;

		Assert.IsTrue(ConfigValidator.Validate(config).Warnings.Any(w => w.Contains("daylight curve", StringComparison.Ordinal)));

		config.Global.OutdoorLuxSensor = "sensor.outdoor_lux";

		Assert.IsFalse(ConfigValidator.Validate(config).Warnings.Any(w => w.Contains("daylight curve", StringComparison.Ordinal)),
			"one outdoor sensor covers every room that did not name one of its own");
	}

	[TestMethod]
	public void A_Rooms_Own_Daylight_Sensor_Answers_It_Too()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Periods[0].UseDaylightCurve = true;
		config.Areas = [new() { Name = "Stue", AreaId = "stue", DaylightSensor = "sensor.stue_lux" }];

		Assert.IsFalse(ConfigValidator.Validate(config).Warnings.Any(w => w.Contains("daylight curve", StringComparison.Ordinal)),
			"the only room in the house reads a sensor it named itself");

		config.Areas.Add(new AreaConfig { Name = "Gang", AreaId = "gang" });

		Assert.IsTrue(ConfigValidator.Validate(config).Warnings.Any(w => w.Contains("daylight curve", StringComparison.Ordinal)),
			"and a second room that named none brings the warning back");
	}

	/// <summary>The darkness sensors are a separate question, and answering that one does not answer this one.</summary>
	[TestMethod]
	public void A_Rooms_Darkness_Sensor_Does_Not_Answer_The_Curves_Warning()
	{
		AdaptiveLightingConfig config = Minimal();
		config.Periods[0].UseDaylightCurve = true;
		config.Areas = [new() { Name = "Stue", AreaId = "stue", LuxSensor = "sensor.stue_lux", FollowOutdoorLux = true }];

		Assert.IsTrue(ConfigValidator.Validate(config).Warnings.Any(w => w.Contains("daylight curve", StringComparison.Ordinal)),
			"an indoor sensor measures the room's own lamps, so the curve does not read it");
	}

	[TestMethod]
	public void No_Period_On_The_Curve_Raises_Nothing()
	{
		AdaptiveLightingConfig config = Minimal();

		Assert.IsFalse(ConfigValidator.Validate(config).Warnings.Any(w => w.Contains("daylight curve", StringComparison.Ordinal)),
			"nothing is waiting on a sensor while every period states its own brightness");
	}

	/// <summary>A brightness end outside 0-100 is refused at either end of the curve.</summary>
	[TestMethod]
	public void Both_Ends_Of_The_Curve_Are_Held_Inside_The_Physical_Range()
	{
		AdaptiveLightingConfig low = Minimal();
		low.Defaults.LuxBrightnessMinPct = -5;

		Assert.IsFalse(ConfigValidator.Validate(low).IsValid);
		StringAssert.Contains(ConfigValidator.Validate(low).ToString(), "LuxBrightnessMinPct");

		AdaptiveLightingConfig high = Minimal();
		high.Defaults.LuxBrightnessMaxPct = 140;

		Assert.IsFalse(ConfigValidator.Validate(high).IsValid);
		StringAssert.Contains(ConfigValidator.Validate(high).ToString(), "LuxBrightnessMaxPct");

		AdaptiveLightingConfig falling = Minimal();
		falling.Defaults.LuxBrightnessMinPct = 90;
		falling.Defaults.LuxBrightnessMaxPct = 20;

		Assert.IsTrue(ConfigValidator.Validate(falling).IsValid,
			"a curve that falls is a choice, not an error");
	}

	[TestMethod]
	public void A_Document_That_Never_Heard_Of_The_Feature_Is_Untouched_By_It()
	{
		ValidationResult result = ConfigValidator.Validate(Minimal());

		Assert.IsTrue(result.IsValid);
		Assert.IsFalse(result.Warnings.Any(w => w.Contains("LuxBrightness", StringComparison.Ordinal)));
		Assert.IsTrue(ConfigValidator.Validate(AdaptiveLightingConfig.CreateDefault()).IsValid,
			"and the seed a fresh installation starts from still validates");
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
	public void An_Unknown_Blocking_Or_Holding_Entity_Costs_The_Area_The_Same_Way()
	{
		var config = Minimal();
		config.Areas =
		[
			new() { Name = "Z", AreaId = "stue", IgnoreWhenOn = ["input_boolean.ghost"] },
			new() { Name = "Y", AreaId = "stue", KeepLitWhenOn = ["input_boolean.ghost"] }
		];

		var result = ConfigValidator.Validate(config, knownEntityIds: ["input_boolean.real"], knownAreaIds: ["stue"]);

		Assert.IsTrue(result.IsValid, "neither list can take the house down");
		Assert.AreEqual(2, result.AreaErrors.Count, "the room that holds its lights on is checked like the one that blocks them");
		Assert.AreEqual(result.AreaErrors[0].Message, result.AreaErrors[1].Message, "and says the same thing about the same id");
	}

	[TestMethod]
	public void An_Unknown_Global_Entity_Is_A_Document_Error()
	{
		// Asymmetric with the kill switch, which fails open and only warns.
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

	// ===================== a room's own levels =====================

	[TestMethod]
	public void Levels_Naming_A_Missing_Period_Warn_And_Are_Never_Refused()
	{
		var config = Minimal();
		config.Areas[0].Levels = [new() { PeriodId = "kveld", BrightnessPct = 40 }];

		var result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid, "renaming a period is itself a save; refusing here would deadlock the file");
		Assert.IsTrue(result.Warnings.Any(w =>
			w.Contains("Stue", StringComparison.Ordinal) && w.Contains("kveld", StringComparison.Ordinal)),
			"and the warning must name both the room and the period, or nobody can find the row");

		Assert.AreEqual(1, config.Areas[0].Levels.Count, "the row is kept: the validator reports, it does not edit");
	}

	[TestMethod]
	public void Two_Rows_For_One_Period_In_One_Room_Warn_And_The_First_Wins()
	{
		var config = Minimal();
		config.Areas[0].Levels =
		[
			new() { PeriodId = "night", BrightnessPct = 8 },
			new() { PeriodId = "night", BrightnessPct = 60 }
		];

		var result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid, "the same non-blocking treatment two Normal house-mode rows get");
		Assert.IsTrue(result.Warnings.Any(w =>
			w.Contains("Stue", StringComparison.Ordinal)
			&& w.Contains("night", StringComparison.Ordinal)
			&& w.Contains("first", StringComparison.Ordinal)),
			"a duplicate means the file says two things, and the reader has to be told which one runs");
	}

	/// <summary>An empty row is skipped here as <c>CircadianCalculator.LevelsOf</c> skips it; reachable only on a hand-edited file.</summary>
	[TestMethod]
	public void A_Cleared_Row_Does_Not_Shadow_The_Real_One_Below_It()
	{
		var config = Minimal();
		config.Areas[0].Levels =
		[
			new() { PeriodId = "night" },
			new() { PeriodId = "night", BrightnessPct = 8 }
		];

		var result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid);
		Assert.IsFalse(result.Warnings.Any(w => w.Contains("first", StringComparison.Ordinal)),
			"the engine runs this room at 8 %, so a warning saying the first row wins would be a false statement");
	}

	[TestMethod]
	public void A_Levels_Row_Naming_No_Period_At_All_Warns()
	{
		var config = Minimal();
		config.Areas[0].Levels = [new() { BrightnessPct = 40 }];

		var result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid);
		Assert.IsTrue(result.Warnings.Any(w => w.Contains("naming no period", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void A_Rooms_Brightness_Outside_The_Physical_Range_Is_A_Document_Error()
	{
		var config = Minimal();
		config.Areas[0].Levels = [new() { PeriodId = "night", BrightnessPct = 150 }];

		var result = ConfigValidator.Validate(config);

		Assert.IsFalse(result.IsValid, "checked exactly as the schedule's own levels are — same range, same severity");
		Assert.IsTrue(result.Errors.Any(e => e.Contains("BrightnessPct 150", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void A_Rooms_Colour_Temperature_Outside_A_Sane_Kelvin_Range_Is_A_Document_Error()
	{
		var config = Minimal();
		config.Areas[0].Levels = [new() { PeriodId = "night", ColorTempKelvin = 42 }];

		var result = ConfigValidator.Validate(config);

		Assert.IsFalse(result.IsValid);
		Assert.IsTrue(result.Errors.Any(e => e.Contains("ColorTempKelvin 42", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void A_Rooms_Out_Of_Range_Value_Is_Still_An_Error_When_Its_Period_Has_Been_Renamed()
	{
		var config = Minimal();
		config.Areas[0].Levels = [new() { PeriodId = "kveld", BrightnessPct = 150 }];

		Assert.IsFalse(ConfigValidator.Validate(config).IsValid);
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
		unmatched.Periods.Add(new TimePeriodConfig { Name = "film", SetsModeId = "Film", Start = "20:00" });
		Assert.IsFalse(ConfigValidator.Validate(unmatched).IsValid, "a SetsModeId matching no configured option value");

		var noSelect = Minimal();
		noSelect.Periods.Add(new TimePeriodConfig { Name = "late", SetsModeId = "Sover", Start = "23:00" });
		Assert.IsFalse(ConfigValidator.Validate(noSelect).IsValid, "a SetsModeId while no HouseMode is configured matches nothing");
	}

	[TestMethod]
	public void SetsMode_MatchingALiveSelectOption_IsAccepted_EvenWhenNotYetTagged()
	{
		var config = WithHouseMode();
		config.Periods.Add(new TimePeriodConfig { Name = "film", SetsModeId = "Film", Start = "20:00" });

		// Requiring the option be tagged first would deadlock the save: tagging is itself a save.
		Assert.IsTrue(ConfigValidator.Validate(config, liveSelectOptions: ["Film"]).IsValid,
			"a SetsModeId naming a live select option is legitimate even before it is tagged a Kind");

		Assert.IsFalse(ConfigValidator.Validate(config).IsValid, "unknown to both configured and live options → error");
	}

	[TestMethod]
	public void SetsMode_NamingANormalOption_Warns_ButDoesNotRefuse()
	{
		var config = WithHouseMode();
		config.Periods.Add(new TimePeriodConfig { Name = "reset", SetsModeId = "Normal", Start = "06:00" });

		var result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid, "SetsModeId naming a Normal option is a scheduled reset — legal, but probably a mistake");
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
		missing.Global.HouseMode!.OptionFor("Sover")!.ClampPeriodId = "nope";
		Assert.IsFalse(ConfigValidator.Validate(missing).IsValid, "a ClampPeriodId naming no period is an error");

		var offSleep = WithHouseMode();
		offSleep.Global.HouseMode!.OptionFor("Borte")!.ClampPeriodId = "night";   // Away, not Sleep — inert
		var result = ConfigValidator.Validate(offSleep);
		Assert.IsTrue(result.IsValid, "a ClampPeriodId on a non-sleep option is inert, a warning not an error");
		Assert.IsTrue(result.Warnings.Any(w => w.Contains("ClampPeriodId", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void ResetOnPeriodStart_MustNameAPeriod()
	{
		var config = WithHouseMode();
		config.Global.HouseMode!.OptionFor("Borte")!.ResetOnPeriodStartId = "ghost";
		Assert.IsFalse(ConfigValidator.Validate(config).IsValid);
	}

	[TestMethod]
	public void StaleResetOnPeriodStart_OnANormalOption_Warns_NotErrors()
	{
		var config = WithHouseMode();
		config.Global.HouseMode!.OptionFor("Normal")!.ResetOnPeriodStartId = "gone";

		var result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid, "a dangling reset field on a Normal option is inert — a warning, not a document error");
	}

	[TestMethod]
	public void AnAwayOrGuestOption_WithNoResetTrigger_Warns()
	{
		// WithHouseMode's Borte carries no reset trigger.
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
		var resolvable = WithHouseMode();
		resolvable.Areas[0].RespectSleepMode = true;
		Assert.IsTrue(ConfigValidator.Validate(resolvable).IsValid, "the 'night' period resolves the clamp");

		// Nothing resolves the clamp: no ClampPeriodId, no SetsModeId period, no "night".
		var unresolvable = WithHouseMode();
		unresolvable.Areas[0].RespectSleepMode = true;
		unresolvable.Periods = [new() { Name = "day", Start = "07:00" }, new() { Name = "evening", Start = "18:00" }];
		Assert.IsFalse(ConfigValidator.Validate(unresolvable).IsValid, "nothing resolves the clamp for a load-bearing sleep path");

		var notBearing = WithHouseMode();
		notBearing.Periods = [new() { Name = "day", Start = "07:00" }, new() { Name = "evening", Start = "18:00" }];
		Assert.IsTrue(ConfigValidator.Validate(notBearing).IsValid, "sleep is not load-bearing, so the missing clamp is inert");

		var viaClamp = WithHouseMode();
		viaClamp.Areas[0].RespectSleepMode = true;
		viaClamp.Periods = [new() { Name = "day", Start = "07:00" }, new() { Name = "dim", Start = "22:00" }];
		viaClamp.Global.HouseMode!.OptionFor("Sover")!.ClampPeriodId = "dim";
		Assert.IsTrue(ConfigValidator.Validate(viaClamp).IsValid, "an explicit ClampPeriodId resolves the clamp");
	}

	[TestMethod]
	public void KillSwitch_Unknown_OnlyWarns_NeverStopsTheDocument()
	{
		// The engine fails open on a missing kill switch, so an unknown one never stops the document.
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
		// One shared period table, so there is no per-mode partitioning to excuse a collision.
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

	private static AdaptiveLightingConfig UnderHomeAssistantAuthority()
	{
		AdaptiveLightingConfig config = WithHouseMode();
		config.Global.HouseMode!.Authority = HouseModeAuthority.HomeAssistant;
		return config;
	}

	/// <summary>An authority with no entity behind it decides nothing, as <see cref="HouseModeConfig.HomeAssistantDecides"/> already says.</summary>
	[TestMethod]
	public void HouseMode_HomeAssistantAuthorityWithNoEntity_Warns()
	{
		AdaptiveLightingConfig config = UnderHomeAssistantAuthority();
		config.Global.HouseMode!.Entity = null;

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid, "an authority with nothing behind it is a misconfiguration, not an unsaveable document");
		Assert.IsTrue(result.Warnings.Any(warning => warning.Contains("no Entity is named", StringComparison.Ordinal)));
	}

	/// <summary>Under Home Assistant's authority the engine never writes the select, so every <c>SetsModeId</c> is dormant.</summary>
	[TestMethod]
	public void HouseMode_HomeAssistantAuthority_NamesThePeriodsWhoseSetsModeWentDormant()
	{
		AdaptiveLightingConfig config = UnderHomeAssistantAuthority();
		config.Periods[1].SetsModeId = "Sover";   // the "night" period

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid, "the house still runs — the dropdown simply owns the mode now");
		Assert.IsTrue(result.Warnings.Any(warning =>
			warning.Contains("dormant", StringComparison.Ordinal) && warning.Contains("'night'", StringComparison.Ordinal)),
			"naming the period is the whole point: it is the line somebody would otherwise go looking for");
	}

	[TestMethod]
	public void HouseMode_HomeAssistantAuthority_WarnsThatTheActivationRulesAreDormant()
	{
		AdaptiveLightingConfig config = UnderHomeAssistantAuthority();
		config.Global.HouseMode!.OptionFor("Sover")!.ActivateAfterNoMotionMinutes = 45;
		config.Global.HouseMode.OptionFor("Borte")!.ActivateWhileOn = ["input_boolean.ferie"];

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid, "both degrade to a rule that no longer fires");
		Assert.IsTrue(result.Warnings.Any(warning => warning.Contains("ActivateAfterNoMotionMinutes", StringComparison.Ordinal)));
		Assert.IsTrue(result.Warnings.Any(warning => warning.Contains("ActivateWhileOn", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void HouseMode_AdaptiveLightingAuthority_FiresNoDormancyRuleAtAll()
	{
		AdaptiveLightingConfig config = WithHouseMode();
		config.Periods[1].SetsModeId = "Sover";
		config.Global.HouseMode!.OptionFor("Sover")!.ActivateAfterNoMotionMinutes = 45;
		config.Global.HouseMode.OptionFor("Borte")!.ActivateWhileOn = ["input_boolean.ferie"];

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid);
		Assert.AreEqual(0, result.Warnings.Count(warning => warning.Contains("dormant", StringComparison.Ordinal)),
			"the default authority is what every document written before it says, and it must notice nothing");
	}

	// ===================== the period select =====================

	private static AdaptiveLightingConfig WithPeriodSelect(
		PeriodAuthority authority = PeriodAuthority.HomeAssistant,
		string? entity = "input_select.tid_pa_dagen",
		params (string Value, string Period)[] options)
	{
		AdaptiveLightingConfig config = Minimal();
		config.Global.PeriodSelect = new PeriodSelectConfig
		{
			Entity = entity,
			Authority = authority,
			Options = [.. options.Select(row => new PeriodSelectOptionConfig { Value = row.Value, PeriodId = row.Period })]
		};

		return config;
	}

	[TestMethod]
	public void PeriodSelect_MappingTheConfiguredPeriods_Passes()
	{
		ValidationResult result = ConfigValidator.Validate(
			WithPeriodSelect(options: [("Dag", "day"), ("Natt", "night")]));

		Assert.IsTrue(result.IsValid);
		Assert.AreEqual(0, result.Warnings.Count);
	}

	/// <summary>Under Adaptive-lighting authority the mirror writes the period to this entity every tick, over what <c>SetsModeId</c> wrote.</summary>
	[TestMethod]
	public void PeriodSelect_OnTheHouseModesOwnHelper_IsADocumentError()
	{
		AdaptiveLightingConfig config = WithPeriodSelect(
			PeriodAuthority.AdaptiveLighting,
			"input_select.husmodus",
			("Dag", "day"));

		config.Global.HouseMode = new HouseModeConfig
		{
			Entity = "input_select.husmodus",
			Options = [new HouseModeOptionConfig { Value = "Hjemme", Kind = ModeKind.Normal }]
		};

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsFalse(result.IsValid, "the two would overwrite each other every tick");
		Assert.IsTrue(result.Errors.Any(error => error.Contains("house-mode select", StringComparison.Ordinal)));
	}

	/// <summary><c>IsEmpty</c> needs blank Value and blank Period, so a row emptied of only its period must still normalise away.</summary>
	[TestMethod]
	public void PeriodSelect_AClearedRow_NormalisesAwayInsteadOfBlockingEverySave()
	{
		AdaptiveLightingConfig config = WithPeriodSelect(options: [("Dag", "day")]);

		config.Global.PeriodSelect!.Options.Clear();

		ConfigNormalizer.Normalize(config);
		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid, "a cleared mapping must not make the document unsaveable");
	}

	/// <summary>Every other branch is gated on the entity being present, so without this rule the reader is never built and nothing says so.</summary>
	[TestMethod]
	public void PeriodSelect_WithMappingsButNoEntity_Warns()
	{
		ValidationResult result = ConfigValidator.Validate(
			WithPeriodSelect(entity: null, options: [("Dag", "day")]));

		Assert.IsTrue(result.IsValid, "it is a misconfiguration, not an unsaveable document");
		Assert.IsTrue(result.Warnings.Any(warning => warning.Contains("names no Entity", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void PeriodSelect_NotAnInputSelect_IsADocumentError()
	{
		ValidationResult result = ConfigValidator.Validate(
			WithPeriodSelect(entity: "sensor.tid_pa_dagen", options: [("Dag", "day")]));

		Assert.IsFalse(result.IsValid, "nothing but an input_select has options to read or write");
	}

	/// <summary>Harsher than the same shape on a room's levels: an unresolvable mapping here leaves every room unable to place the time of day.</summary>
	[TestMethod]
	public void PeriodSelect_MappingToNoConfiguredPeriod_IsADocumentError()
	{
		ValidationResult result = ConfigValidator.Validate(
			WithPeriodSelect(options: [("Dag", "day"), ("Middag", "siesta")]));

		Assert.IsFalse(result.IsValid);
		Assert.IsTrue(result.Errors.Any(e => e.Contains("siesta", StringComparison.Ordinal)),
			"the error names the period nobody configured, or the reader has to go looking for it");
	}

	[TestMethod]
	public void PeriodSelect_DuplicateValues_AreADocumentError()
	{
		ValidationResult result = ConfigValidator.Validate(
			WithPeriodSelect(options: [("Dag", "day"), (" dag ", "night")]));

		Assert.IsFalse(result.IsValid,
			"two rows for one option string means the file says two things and only the first would ever be read");
	}

	[TestMethod]
	public void PeriodSelect_BlankHalves_AreADocumentError()
	{
		Assert.IsFalse(ConfigValidator.Validate(WithPeriodSelect(options: [("", "day")])).IsValid,
			"a row with no Value can never be matched by any select option");

		Assert.IsFalse(ConfigValidator.Validate(WithPeriodSelect(options: [("Dag", "")])).IsValid,
			"a row with no Period means nothing when it is selected");
	}

	[TestMethod]
	public void PeriodSelect_ValueTheLiveSelectNoLongerOffers_IsAWarning()
	{
		ValidationResult result = ConfigValidator.Validate(
			WithPeriodSelect(options: [("Dag", "day"), ("Natt", "night")]),
			livePeriodSelectOptions: ["Dag", "Kveld"]);

		Assert.IsTrue(result.IsValid, "the row is inert, not dangerous");
		Assert.IsTrue(result.Warnings.Any(w => w.Contains("Natt", StringComparison.Ordinal)));
	}

	/// <summary>Both helpers describe a dropped option the same way, and neither guesses at the cause.</summary>
	/// <remarks>A rename is a removal and an addition, so nothing here can tell the two apart.</remarks>
	[TestMethod]
	public void A_Dropped_Option_Reads_The_Same_On_Both_Helpers()
	{
		string periodWarning = ConfigValidator.Validate(
				WithPeriodSelect(options: [("Dag", "day"), ("Natt", "night")]),
				livePeriodSelectOptions: ["Dag"])
			.Warnings.Single(w => w.StartsWith("PeriodSelect option", StringComparison.Ordinal));

		AdaptiveLightingConfig house = Minimal();
		house.Global.HouseMode = new HouseModeConfig
		{
			Entity = "input_select.house_state",
			// A reset trigger on Ferie only so it earns no second warning of its own; this test is about the first.
			Options =
			[
				new() { Value = "Hjemme", Kind = ModeKind.Normal },
				new() { Value = "Ferie", Kind = ModeKind.Away, ResetOnPresence = true }
			]
		};

		string modeWarning = ConfigValidator.Validate(house, liveSelectOptions: ["Hjemme"])
			.Warnings.Single(w => w.StartsWith("HouseMode option", StringComparison.Ordinal));

		Assert.IsTrue(periodWarning.Contains(HelperOrphan.NoLongerOffered("Natt"), StringComparison.Ordinal), periodWarning);
		Assert.IsTrue(modeWarning.Contains(HelperOrphan.NoLongerOffered("Ferie"), StringComparison.Ordinal), modeWarning);

		foreach (string guess in (string[])["renamed", "removed"])
		{
			Assert.IsFalse(periodWarning.Contains(guess, StringComparison.OrdinalIgnoreCase), periodWarning);
			Assert.IsFalse(modeWarning.Contains(guess, StringComparison.OrdinalIgnoreCase), modeWarning);
		}

		// The consequence is the half that must differ: losing a period mapping is not losing a mode's triggers.
		Assert.AreNotEqual(periodWarning, modeWarning);
	}

	[TestMethod]
	public void PeriodSelect_LiveOptionsAreNotCheckedAgainstTheHouseModeSelect()
	{
		// Two separate live lists. Reusing one for both reports every row of each as renamed.
		ValidationResult result = ConfigValidator.Validate(
			WithPeriodSelect(options: [("Dag", "day"), ("Natt", "night")]),
			liveSelectOptions: ["Hjemme", "Borte"],
			livePeriodSelectOptions: ["Dag", "Natt"]);

		Assert.AreEqual(0, result.Warnings.Count(w => w.Contains("PeriodSelect", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void PeriodSelect_HomeAssistantAuthorityWithNoMappings_IsAWarning()
	{
		ValidationResult result = ConfigValidator.Validate(WithPeriodSelect(PeriodAuthority.HomeAssistant));

		Assert.IsTrue(result.IsValid, "the house keeps working — it simply never follows the select");
		Assert.IsTrue(result.Warnings.Any(w => w.Contains("Authority", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void PeriodSelect_AdaptiveLightingAuthorityWithNoMappings_SaysNothing()
	{
		ValidationResult result = ConfigValidator.Validate(WithPeriodSelect(PeriodAuthority.AdaptiveLighting));

		Assert.IsTrue(result.IsValid);
		Assert.AreEqual(0, result.Warnings.Count,
			"the engine owns the periods and simply mirrors nothing; that is not a misconfiguration");
	}

	[TestMethod]
	public void PeriodSelect_EntityHomeAssistantDoesNotKnow_IsAWarning()
	{
		ValidationResult result = ConfigValidator.Validate(
			WithPeriodSelect(options: [("Dag", "day")]),
			knownEntityIds: ["input_select.husmodus"]);

		Assert.IsTrue(result.IsValid);
		Assert.IsTrue(result.Warnings.Any(w => w.Contains("tid_pa_dagen", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void PeriodSelect_Absent_FiresNoRuleAtAll()
	{
		ValidationResult result = ConfigValidator.Validate(Minimal(), livePeriodSelectOptions: ["Dag"]);

		Assert.IsTrue(result.IsValid);
		Assert.AreEqual(0, result.Warnings.Count, "every document today has no period select and must notice nothing");
	}
}
