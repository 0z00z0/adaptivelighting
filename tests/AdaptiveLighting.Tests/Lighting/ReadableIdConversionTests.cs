using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The one-time move onto readable ids: every period renumbered, every reference following it, nothing else moved.</summary>
[TestClass]
public sealed class ReadableIdConversionTests
{
	private const string Root = LightingConfigDocument.RootKey;

	/// <summary>A document as the slug-and-random ids left it.</summary>
	private static string OldShapeYaml() =>
		$"""
		{Root}:
		  Periods:
		    - Id: early-ab12
		      Name: Early
		      Start: "06:00"
		    - Id: night-cd34
		      Name: night
		      Start: "23:00"
		    - Id: evening-ef56
		      Name: Evening
		      Start: "19:00"
		      SetsModeId: sleeping-gh78
		  Global:
		    HouseMode:
		      Entity: input_select.house_mode
		      Options:
		        - Id: normal-ij90
		          Value: Normal
		          Kind: Normal
		        - Id: sleeping-gh78
		          Value: Sleeping
		          Kind: Sleep
		        - Id: nap-kl12
		          Value: Nap
		          Kind: Sleep
		        - Id: guests-mn34
		          Value: Guests
		          Kind: Guest
		          ResetOnPeriodStartId: early-ab12
		    PeriodSelect:
		      Entity: input_select.time_of_day
		      Options:
		        - Value: Early
		          PeriodId: early-ab12
		        - Value: Night
		          PeriodId: night-cd34
		  Areas:
		    - AreaId: living_room
		      Levels:
		        - PeriodId: night-cd34
		          BrightnessPct: 8
		        - PeriodId: early-ab12
		          BrightnessPct: 50
		      LightLevels:
		        - EntityId: light.reading_lamp
		          Levels:
		            - PeriodId: evening-ef56
		              BrightnessPct: 30
		    - Name: Shed
		      Lights: [light.shed]

		""";

	private static AdaptiveLightingConfig Converted(out bool changed)
	{
		AdaptiveLightingConfig config = LightingConfigDocument.Deserialize(OldShapeYaml()).Config;
		changed = ReadableIdConversion.Apply(config);

		return config;
	}

	private static TimePeriodConfig PeriodNamed(AdaptiveLightingConfig config, string name) =>
		config.Periods.Single(period => string.Equals(period.Name, name, StringComparison.Ordinal));

	[TestMethod]
	public void Every_Period_Reference_Follows_Its_Period_Onto_A_Readable_Id()
	{
		AdaptiveLightingConfig config = Converted(out bool changed);

		Assert.IsTrue(changed, "the read has to report the conversion, or the file is never written back");
		CollectionAssert.AreEqual(new[] { "period_001", "period_002", "period_003" }, config.Periods.Select(p => p.Id).ToArray());
		Assert.AreEqual(3, config.Global.IdCounters.Periods);

		AreaConfig living = config.Areas[0];
		HouseModeConfig mode = config.Global.HouseMode!;

		Assert.AreEqual("period_002", living.Levels[0].PeriodId, "a room's levels row");
		Assert.AreEqual("period_001", living.Levels[1].PeriodId);
		Assert.AreEqual("period_003", living.LightLevels![0].Levels[0].PeriodId, "a single light's row");
		Assert.AreEqual("period_001", config.Global.PeriodSelect!.Options[0].PeriodId, "a select mapping");
		Assert.AreEqual("period_002", config.Global.PeriodSelect.Options[1].PeriodId);
		Assert.AreEqual("period_001", mode.OptionFor("Guests")!.ResetOnPeriodStartId, "a reset trigger");
		Assert.AreEqual(8d, living.Levels[0].BrightnessPct, "and every row still says what it said");
	}

	[TestMethod]
	public void A_Mode_Switch_Names_The_Option_Text_And_No_Option_Keeps_A_Minted_Id()
	{
		AdaptiveLightingConfig config = Converted(out _);

		Assert.AreEqual("Sleeping", PeriodNamed(config, "Evening").SetsModeId);
		Assert.IsTrue(config.Global.HouseMode!.Options.All(option => option.Id is null));

		string written = LightingConfigDocument.Serialize(config);

		foreach (string old in (string[])["ab12", "cd34", "ef56", "gh78", "ij90", "kl12", "mn34"])
			Assert.IsFalse(written.Contains(old, StringComparison.Ordinal), $"old id ending {old} is still in the file");
	}

	[TestMethod]
	public void The_Sleep_Dim_Period_Is_Unchanged_And_The_Night_Name_Link_Becomes_An_Explicit_Id()
	{
		AdaptiveLightingConfig config = Converted(out _);
		HouseModeConfig mode = config.Global.HouseMode!;

		HouseModeOptionConfig sleeping = mode.OptionFor("Sleeping")!;
		HouseModeOptionConfig nap = mode.OptionFor("Nap")!;

		Assert.IsNull(sleeping.ClampPeriodId, "the period that sets this option still names it, so nothing is added");
		Assert.AreEqual("Evening", HouseModeConfig.SleepClampPeriodFor(sleeping, config.Periods)!.Name);

		Assert.AreEqual("period_002", nap.ClampPeriodId, "only a period named night gave this one its dim period");
		Assert.AreEqual("night", HouseModeConfig.SleepClampPeriodFor(nap, config.Periods)!.Name);
	}

	[TestMethod]
	public void A_Room_Without_An_Area_Gets_A_Room_Id_And_A_Room_With_One_Keeps_Its_Area()
	{
		AdaptiveLightingConfig config = Converted(out _);

		Assert.AreEqual("living_room", config.Areas[0].Key);
		Assert.IsNull(config.Areas[0].Id);
		Assert.AreEqual("room_001", config.Areas[1].Key);
		Assert.AreEqual(1, config.Global.IdCounters.Rooms);
	}

	[TestMethod]
	public void A_Converted_Document_Is_Not_Converted_Again()
	{
		AdaptiveLightingConfig config = Converted(out _);
		string written = LightingConfigDocument.Serialize(config);

		AdaptiveLightingConfig reread = LightingConfigDocument.Deserialize(written).Config;

		Assert.IsFalse(ReadableIdConversion.Apply(reread), "a second start must not push the original out of the backup slot");
		Assert.AreEqual(written, LightingConfigDocument.Serialize(reread));
	}

	[TestMethod]
	public void A_Dangling_Levels_Row_Still_Warns_And_Survives()
	{
		AdaptiveLightingConfig config = Converted(out _);
		config.Areas[0].Levels[0].PeriodId = "period_099";

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid, "a room's levels row is worth more than the tidiness");
		StringAssert.Contains(result.ToString(), "period_099");
	}

	[TestMethod]
	public void A_Dangling_Select_Mapping_Still_Errors()
	{
		AdaptiveLightingConfig config = Converted(out _);
		config.Global.PeriodSelect!.Options[1].PeriodId = "period_099";

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsFalse(result.IsValid, "an unresolvable mapping costs the whole house its time of day");
		StringAssert.Contains(result.ToString(), "period_099");
	}
}
