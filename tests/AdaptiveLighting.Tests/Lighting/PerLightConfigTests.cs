using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The new key on an area: what the file keeps, what a save drops, and what the validator says about it.</summary>
[TestClass]
public sealed class PerLightConfigTests
{
	private const string Lamp = "light.stue_leselampe";

	private static AdaptiveLightingConfig Document(AreaConfig area) => new()
	{
		Periods =
		[
			new() { Id = "evening", Name = "evening", Start = "18:00", Brightness = 179, ColorTempKelvin = 2700 },
			new() { Id = "night", Name = "night", Start = "22:30", Brightness = 38, ColorTempKelvin = 2200 }
		],
		Areas = [area]
	};

	private static AreaConfig Room(params LightLevelOverride[] lights) =>
		new() { AreaId = "stue", LightLevels = [.. lights] };

	// ===================== the normaliser =====================

	[TestMethod]
	public void An_Empty_Row_Is_Dropped_And_The_Light_With_It()
	{
		AdaptiveLightingConfig config = Document(Room(
			new LightLevelOverride { EntityId = Lamp, Levels = [new RoomLevelOverride { PeriodId = "evening" }] }));

		ConfigNormalizer.Normalize(config);

		Assert.IsNull(config.Areas[0].LightLevels, "a light with no row left is gone, and so is the list it was alone in");
	}

	[TestMethod]
	public void A_Light_Keeps_The_Rows_That_Say_Something()
	{
		AdaptiveLightingConfig config = Document(Room(
			new LightLevelOverride
			{
				EntityId = Lamp,
				Levels =
				[
					new RoomLevelOverride { PeriodId = "evening" },
					new RoomLevelOverride { PeriodId = "night", Brightness = 26 }
				]
			}));

		ConfigNormalizer.Normalize(config);

		RoomLevelOverride kept = config.Areas[0].LightLevels!.Single().Levels.Single();

		Assert.AreEqual("night", kept.PeriodId);
		Assert.AreEqual(26, kept.Brightness);
	}

	[TestMethod]
	public void A_Light_Pinned_To_Nothing_Is_Not_An_Empty_Row()
	{
		AdaptiveLightingConfig config = Document(Room(
			new LightLevelOverride { EntityId = Lamp, Levels = [new RoomLevelOverride { PeriodId = "night", Brightness = 0 }] }));

		ConfigNormalizer.Normalize(config);

		Assert.AreEqual(
			0,
			config.Areas[0].LightLevels!.Single().Levels.Single().Brightness,
			"nothing is a level a light can hold, and it means this light is off for that period");
	}

	[TestMethod]
	public void An_Empty_List_Becomes_Null_So_The_Key_Leaves_The_File()
	{
		AdaptiveLightingConfig config = Document(Room());

		ConfigNormalizer.Normalize(config);

		Assert.IsNull(config.Areas[0].LightLevels);
		Assert.IsFalse(
			LightingConfigDocument.Serialize(config).Contains("LightLevels", StringComparison.Ordinal),
			"null is omitted and an empty list is not, so an empty one would put the key into every room");
	}

	[TestMethod]
	public void A_Document_With_No_Light_Levels_Serialises_Exactly_As_It_Did_Before()
	{
		AdaptiveLightingConfig config = Document(new AreaConfig
		{
			AreaId = "stue",
			Levels = [new RoomLevelOverride { PeriodId = "evening", Brightness = 179 }]
		});

		string before = LightingConfigDocument.Serialize(config);

		ConfigNormalizer.Normalize(config);

		Assert.AreEqual(before, LightingConfigDocument.Serialize(config));
		Assert.IsFalse(before.Contains("LightLevels", StringComparison.Ordinal));
	}

	[TestMethod]
	public void The_Byte_And_Its_Percentage_Are_Bound_On_A_Light_Row_As_On_A_Rooms()
	{
		AdaptiveLightingConfig config = Document(Room(
			new LightLevelOverride { EntityId = Lamp, Levels = [new RoomLevelOverride { PeriodId = "night", Brightness = 26 }] }));

		ConfigNormalizer.Normalize(config);

		Assert.AreEqual(
			RawBrightness.ToPercent(26),
			config.Areas[0].LightLevels!.Single().Levels.Single().BrightnessPct);
	}

	// ===================== round trip =====================

	[TestMethod]
	public void A_Light_Row_Survives_A_Save_And_A_Load()
	{
		AdaptiveLightingConfig config = Document(Room(
			new LightLevelOverride
			{
				EntityId = Lamp,
				Levels = [new RoomLevelOverride { PeriodId = "night", Brightness = 26, ColorTempKelvin = 2000, FollowDaylightCurve = true }]
			}));

		AdaptiveLightingConfig reloaded =
			LightingConfigDocument.Deserialize(LightingConfigDocument.Serialize(config)).Config;

		LightLevelOverride light = reloaded.Areas[0].LightLevels!.Single();

		Assert.AreEqual(Lamp, light.EntityId);
		Assert.AreEqual(26, light.Levels.Single().Brightness);
		Assert.AreEqual(2000, light.Levels.Single().ColorTempKelvin);
		Assert.IsTrue(light.Levels.Single().FollowDaylightCurve);
	}

	// The key is unknown to a build that predates it, and an unknown key is silence: such a build commands every
	// light at the room's level, which is what it did before. Both directions are asserted, because a check that
	// only looks for what should be gone passes happily while it is still there.
	[TestMethod]
	public void An_Older_Build_Reading_The_New_Key_Keeps_The_Rooms_Rows_And_Ignores_The_Lights()
	{
		string handWritten = $"""
			{LightingConfigDocument.RootKey}:
			  Periods:
			    - Id: evening
			      Name: evening
			      Start: "18:00"
			      Brightness: 179
			  Areas:
			    - AreaId: stue
			      Levels:
			        - PeriodId: evening
			          Brightness: 153
			      LightLevels:
			        - EntityId: light.stue_leselampe
			          Levels:
			            - PeriodId: evening
			              Brightness: 77
			""";

		DocumentReadResult read = LightingConfigDocument.Deserialize(handWritten);
		AreaConfig room = read.Config.Areas.Single();

		Assert.AreEqual(153, room.Levels.Single().Brightness, "the room's own rows are untouched by the new key");
		Assert.AreEqual(77, room.LightLevels!.Single().Levels.Single().Brightness);

		Assert.AreEqual(
			0,
			read.Config.RetiredKeysInDocument.Count,
			"neither LightLevels nor EntityId is a retired key, so the raw-text pre-pass leaves them alone");
	}

	// ===================== the validator =====================

	private static ValidationResult Check(AdaptiveLightingConfig config, params string[] knownEntityIds) =>
		ConfigValidator.Validate(config, knownEntityIds.Length > 0 ? knownEntityIds : null, knownAreaIds: ["stue"]);

	[TestMethod]
	public void A_Brightness_Outside_The_Byte_Is_An_Error_On_A_Light_Row_Too()
	{
		ValidationResult result = Check(Document(Room(
			new LightLevelOverride { EntityId = Lamp, Levels = [new RoomLevelOverride { PeriodId = "night", Brightness = 300 }] })));

		Assert.IsTrue(
			result.Errors.Any(error => error.Contains(Lamp, StringComparison.Ordinal) && error.Contains("300", StringComparison.Ordinal)),
			"the sentence must name the light, or a room of ten lamps says nothing about which one");
	}

	[TestMethod]
	public void A_Kelvin_Outside_The_Range_Is_An_Error_On_A_Light_Row_Too()
	{
		ValidationResult result = Check(Document(Room(
			new LightLevelOverride { EntityId = Lamp, Levels = [new RoomLevelOverride { PeriodId = "night", ColorTempKelvin = 99000 }] })));

		Assert.IsTrue(result.Errors.Any(error => error.Contains(Lamp, StringComparison.Ordinal) && error.Contains("99000", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void A_Light_Entry_Naming_No_Light_Warns()
	{
		ValidationResult result = Check(Document(Room(
			new LightLevelOverride { EntityId = "  ", Levels = [new RoomLevelOverride { PeriodId = "night", Brightness = 26 }] })));

		Assert.IsTrue(result.Warnings.Any(warning => warning.Contains("naming no light", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void A_Light_Named_Twice_Warns_And_The_First_Entry_Wins()
	{
		ValidationResult result = Check(Document(Room(
			new LightLevelOverride { EntityId = Lamp, Levels = [new RoomLevelOverride { PeriodId = "night", Brightness = 26 }] },
			new LightLevelOverride { EntityId = Lamp, Levels = [new RoomLevelOverride { PeriodId = "evening", Brightness = 77 }] })));

		Assert.IsTrue(result.Warnings.Any(warning =>
			warning.Contains(Lamp, StringComparison.Ordinal) && warning.Contains("more than once", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void A_Light_Row_Naming_No_Period_Warns()
	{
		ValidationResult result = Check(Document(Room(
			new LightLevelOverride { EntityId = Lamp, Levels = [new RoomLevelOverride { PeriodId = "", Brightness = 26 }] })));

		Assert.IsTrue(result.Warnings.Any(warning =>
			warning.Contains(Lamp, StringComparison.Ordinal) && warning.Contains("naming no period", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void One_Light_Naming_A_Period_Twice_Warns()
	{
		ValidationResult result = Check(Document(Room(
			new LightLevelOverride
			{
				EntityId = Lamp,
				Levels =
				[
					new RoomLevelOverride { PeriodId = "night", Brightness = 26 },
					new RoomLevelOverride { PeriodId = "night", Brightness = 77 }
				]
			})));

		Assert.IsTrue(result.Warnings.Any(warning =>
			warning.Contains(Lamp, StringComparison.Ordinal) && warning.Contains("more than one levels row", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void A_Light_Row_For_A_Period_No_Schedule_Has_Warns_And_Is_Kept()
	{
		AdaptiveLightingConfig config = Document(Room(
			new LightLevelOverride { EntityId = Lamp, Levels = [new RoomLevelOverride { PeriodId = "gone", Brightness = 26 }] }));

		ValidationResult result = Check(config);

		Assert.IsTrue(result.Warnings.Any(warning =>
			warning.Contains(Lamp, StringComparison.Ordinal) && warning.Contains("matches no configured period", StringComparison.Ordinal)));

		Assert.AreEqual(1, config.Areas[0].LightLevels!.Single().Levels.Count, "the row is kept so the levels are not lost");
	}

	[TestMethod]
	public void A_Light_Home_Assistant_Does_Not_Know_Is_The_Same_Area_Error_Every_Other_Unknown_Id_Gets()
	{
		ValidationResult result = Check(
			Document(Room(new LightLevelOverride
			{
				EntityId = "light.gone",
				Levels = [new RoomLevelOverride { PeriodId = "night", Brightness = 26 }]
			})),
			"light.stue_leselampe");

		Assert.IsTrue(
			result.AreaErrors.Any(error =>
				error.Message.Contains("light.gone", StringComparison.Ordinal)
				&& error.Message.Contains("Home Assistant does not know", StringComparison.Ordinal)),
			"an area error and not a document one: a light renamed in HA costs that room, never the house");
	}

	[TestMethod]
	public void A_Room_Levels_Warning_Still_Reads_Exactly_As_It_Did()
	{
		AdaptiveLightingConfig config = Document(new AreaConfig
		{
			AreaId = "stue",
			Name = "Stue",
			Levels = [new RoomLevelOverride { PeriodId = "gone", Brightness = 26 }]
		});

		Assert.IsTrue(
			Check(config).Warnings.Any(warning => warning.StartsWith("[Stue] has levels for period 'gone'", StringComparison.Ordinal)),
			"the shared row check must not have changed the sentence a room already produced");
	}
}
