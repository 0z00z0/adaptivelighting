using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The move off user-typed names: periods and house-mode options get ids, every reference points at the id, and an older document is migrated once.</summary>
[TestClass]
public sealed class StableKeyMigrationTests
{
	private const string Root = LightingConfigDocument.RootKey;

	/// <summary>A pre-migration document: no ids anywhere, every reference by name.</summary>
	private static string PreMigrationYaml() =>
		$"""
		{Root}:
		  Periods:
		    - Name: dag
		      Start: "09:00"
		      BrightnessPct: 90
		      ColorTempKelvin: 4500
		    - Name: natt
		      Start: "22:30"
		      BrightnessPct: 15
		      ColorTempKelvin: 2200
		      SetsMode: Sover
		  Global:
		    HouseMode:
		      Entity: input_select.husmodus
		      Options:
		        - Value: Normal
		          Kind: Normal
		        - Value: Sover
		          Kind: Sleep
		          ClampPeriod: natt
		          ResetOnPeriodStart: dag
		    PeriodSelect:
		      Entity: input_select.tid
		      Options:
		        - Value: Dag
		          Period: dag
		        - Value: Natt
		          Period: natt
		  Areas:
		    - Name: Stue
		      AreaId: stue
		      Levels:
		        - Period: natt
		          BrightnessPct: 8

		""";

	private static AdaptiveLightingConfig Migrated(out bool minted)
	{
		DocumentReadResult read = LightingConfigDocument.Deserialize(PreMigrationYaml());
		minted = read.MintedStableKeys;

		return read.Config;
	}

	private static TimePeriodConfig PeriodNamed(AdaptiveLightingConfig config, string name) =>
		config.Periods.Single(period => string.Equals(period.Name, name, StringComparison.Ordinal));

	// ===================== what the migration produces =====================

	[TestMethod]
	public void Every_Name_Reference_Becomes_The_Id_It_Resolved_To()
	{
		AdaptiveLightingConfig config = Migrated(out bool minted);

		Assert.IsTrue(minted, "the read has to report the migration, or the file is never written back");

		TimePeriodConfig day = PeriodNamed(config, "dag");
		TimePeriodConfig night = PeriodNamed(config, "natt");
		HouseModeOptionConfig sleep = config.Global.HouseMode!.OptionFor("Sover")!;

		Assert.AreEqual(night.Id, config.Areas[0].Levels[0].PeriodId, "a room's levels row");
		Assert.AreEqual(night.Id, sleep.ClampPeriodId, "a sleep option's clamp");
		Assert.AreEqual(day.Id, sleep.ResetOnPeriodStartId, "a reset trigger");
		Assert.AreEqual(night.Id, config.Global.PeriodSelect!.Options[1].PeriodId, "a select mapping");
		Assert.AreEqual(sleep.Id, night.SetsModeId, "a period's mode switch, onto the option's own id");
	}

	[TestMethod]
	public void An_Id_Reads_As_A_Slug_Of_The_Name_It_Was_Created_Under()
	{
		AdaptiveLightingConfig config = Migrated(out _);

		StringAssert.Matches(
			PeriodNamed(config, "natt").Id!,
			new System.Text.RegularExpressions.Regex("^natt-[a-z0-9]{4}$"),
			"friendly to read in a hand-edited file, and unique without being a GUID");
	}

	[TestMethod]
	public void The_Option_Value_Stays_Home_Assistants_Match_Key()
	{
		AdaptiveLightingConfig config = Migrated(out _);
		HouseModeOptionConfig sleep = config.Global.HouseMode!.Options[1];

		Assert.AreEqual("Sover", sleep.Value, "the select reports this string, so nothing may rewrite it");
		Assert.AreNotEqual(sleep.Value, sleep.Id, "the document's own references use the id instead");
	}

	// ===================== once, and only once =====================

	[TestMethod]
	public void A_Migrated_Document_Reloads_Without_Being_Rewritten()
	{
		AdaptiveLightingConfig once = Migrated(out bool firstMinted);
		string written = LightingConfigDocument.Serialize(once);

		DocumentReadResult second = LightingConfigDocument.Deserialize(written);

		Assert.IsTrue(firstMinted);
		Assert.IsFalse(second.MintedStableKeys, "a second start must not push the pre-migration backup out of its one slot");
		Assert.IsFalse(second.NeedsMigratingWrite);
		Assert.AreEqual(written, LightingConfigDocument.Serialize(second.Config), "and the bytes are unchanged");
	}

	[TestMethod]
	public void Apply_IsIdempotent_OnADocumentItHasAlreadySeen()
	{
		AdaptiveLightingConfig config = Migrated(out _);

		Assert.IsFalse(StableKeyMigration.Apply(config), "nothing left to mint and nothing left to repoint");
	}

	[TestMethod]
	public void A_Period_Added_By_Hand_Gets_An_Id_Without_Orphaning_The_Rest()
	{
		AdaptiveLightingConfig config = Migrated(out _);
		string nightId = PeriodNamed(config, "natt").Id!;

		config.Periods.Add(new TimePeriodConfig { Name = "kveld", Start = "20:00" });

		Assert.IsTrue(StableKeyMigration.Apply(config));
		Assert.IsNotNull(PeriodNamed(config, "kveld").Id);
		Assert.AreEqual(nightId, config.Areas[0].Levels[0].PeriodId, "a reference already on an id is left alone");
	}

	// ===================== renaming is free =====================

	[TestMethod]
	public void Renaming_A_Period_Keeps_Every_Reference_Pointing_At_It()
	{
		AdaptiveLightingConfig config = Migrated(out _);
		TimePeriodConfig night = PeriodNamed(config, "natt");
		string id = night.Id!;

		night.Name = "sengetid";

		HouseModeOptionConfig sleep = config.Global.HouseMode!.OptionFor("Sover")!;

		Assert.AreEqual(id, night.Key, "the id does not move with the name");
		Assert.AreEqual(id, config.Areas[0].Levels[0].PeriodId, "the room's levels row");
		Assert.AreEqual(id, sleep.ClampPeriodId, "the sleep clamp");
		Assert.AreEqual(id, config.Global.PeriodSelect!.Options.Single(o => o.Value == "Natt").PeriodId, "the select mapping");
		Assert.AreEqual(night, HouseModeConfig.SleepClampPeriodFor(sleep, config.Periods), "and the clamp still resolves to it");

		// The reset trigger named the other period; renaming this one must not have disturbed it either.
		Assert.AreEqual(PeriodNamed(config, "dag").Id, sleep.ResetOnPeriodStartId);

		Assert.IsTrue(ConfigValidator.Validate(config).IsValid, "a rename is not a broken reference any more");
	}

	[TestMethod]
	public void Renaming_A_Period_Keeps_The_Rooms_Own_Level()
	{
		AdaptiveLightingConfig config = Migrated(out _);
		TimePeriodConfig night = PeriodNamed(config, "natt");

		night.Name = "sengetid";

		CircadianCalculator calculator = new(
			config.Periods, config.Global, () => SunTimes.Unknown, config.Areas[0].Levels);

		Assert.AreEqual(8d, calculator.GetPeriodTarget(night.Key)!.BrightnessPct, "the room still replaces this period");
	}

	[TestMethod]
	public void Two_Periods_May_Share_A_Display_Name_And_Stay_Distinct()
	{
		List<TimePeriodConfig> periods =
		[
			new() { Id = "hytta-1111", Name = "kveld", Start = "18:00", BrightnessPct = 70 },
			new() { Id = "hytta-2222", Name = "kveld", Start = "21:00", BrightnessPct = 20 }
		];

		CircadianCalculator calculator = new(periods, new GlobalConfig(), () => SunTimes.Unknown);

		Assert.AreEqual(70d, calculator.GetPeriodTarget("hytta-1111")!.BrightnessPct);
		Assert.AreEqual(20d, calculator.GetPeriodTarget("hytta-2222")!.BrightnessPct);
	}

	// ===================== a reference that resolves to nothing =====================

	[TestMethod]
	public void A_Dangling_Levels_Row_Still_Warns_And_Survives()
	{
		AdaptiveLightingConfig config = Migrated(out _);
		config.Areas[0].Levels[0].PeriodId = "gone-9999";

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid, "a room's levels row is worth more than the tidiness");
		Assert.AreEqual(1, config.Areas[0].Levels.Count, "and it is kept");
		StringAssert.Contains(result.ToString(), "gone-9999");
	}

	[TestMethod]
	public void A_Dangling_Select_Mapping_Still_Errors()
	{
		AdaptiveLightingConfig config = Migrated(out _);
		config.Global.PeriodSelect!.Options[1].PeriodId = "gone-9999";

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsFalse(result.IsValid, "an unresolvable mapping costs the whole house its time of day");
		StringAssert.Contains(result.ToString(), "gone-9999");
	}

	[TestMethod]
	public void A_Name_That_Resolves_To_Nothing_Is_Left_Exactly_As_It_Was()
	{
		// No line ending in the needle: a raw string literal carries the source file's, which varies by checkout.
		string yaml = PreMigrationYaml().Replace("        - Period: natt", "        - Period: fjorten", StringComparison.Ordinal);

		AdaptiveLightingConfig config = LightingConfigDocument.Deserialize(yaml).Config;

		Assert.AreEqual("fjorten", config.Areas[0].Levels[0].PeriodId, "never silently dropped, so the validator can report it");
	}

	// ===================== the older key names =====================

	[TestMethod]
	public void The_Superseded_Key_Names_Still_Carry_Their_Values_In()
	{
		DocumentReadResult read = LightingConfigDocument.Deserialize(PreMigrationYaml());

		Assert.IsTrue(read.UsedLegacyKeys, "Period, SetsMode, ClampPeriod and ResetOnPeriodStart were renamed on the way in");
		Assert.IsNotNull(read.Config.Areas[0].Levels[0].PeriodId);
		Assert.IsNotNull(read.Config.Global.HouseMode!.Options[1].ClampPeriodId);
		Assert.IsNotNull(read.Config.Global.HouseMode.Options[1].ResetOnPeriodStartId);
		Assert.IsNotNull(PeriodNamed(read.Config, "natt").SetsModeId);
	}

	// ===================== the id itself =====================

	[TestMethod]
	public void Slug_TransliteratesTheLettersAHouseholdActuallyTypes()
	{
		Assert.AreEqual("kjoekken-kveld", StableId.Slug("Kjøkken kveld"));
		Assert.AreEqual("sma-barn", StableId.Slug("Små  barn!"), "a run of punctuation collapses to one separator");
		Assert.AreEqual("id", StableId.Slug("—"), "and something that slugs to nothing still gets an id");
	}

	[TestMethod]
	public void Create_NeverHandsOutAnIdAlreadyTaken()
	{
		HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);

		for (int i = 0; i < 200; i++)
			Assert.IsTrue(taken.Contains(StableId.Create("natt", taken)), "Create adds what it hands out");

		Assert.AreEqual(200, taken.Count);
	}
}
