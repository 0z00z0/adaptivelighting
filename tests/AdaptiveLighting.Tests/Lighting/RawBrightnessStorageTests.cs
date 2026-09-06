using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Ha;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Brightness is stored as the 0-255 byte Home Assistant accepts, and a document written before that still
/// puts every lamp exactly where it always put it.</summary>
/// <remarks>
///     The invariant these tests exist for is the byte a lamp lands on, not the number in the payload. Percent and
///     raw are not a lossless pair — 74 of the 101 whole percents do not survive a trip through a byte and back at
///     one decimal — so a design that converted an old document on load would move most houses. The old percentage
///     key is bound unchanged instead, and the byte grid is reached at the next save.
/// </remarks>
[TestClass]
public sealed class RawBrightnessStorageTests
{
	private const string Light = "light.a";
	private const string Room = "stue";

	// The whole percents a document can hold: every one of them, and the awkward ones are in there by construction.
	private static IEnumerable<int> WholePercents => Enumerable.Range(0, 101);

	/// <summary>The byte Home Assistant lands on for a <c>brightness_pct</c> payload.</summary>
	// Its own conversion, which this codebase already assumes in HaLightActuator.AlreadyMatches and
	// AreaController's read-back. Mirrored here so a lamp's landing place can be asserted, not reasoned about.
	private static int LandsOn(double brightnessPct) =>
		(int)Math.Round(brightnessPct / 100.0 * RawBrightness.Max, MidpointRounding.AwayFromZero);

	private static string PeriodDocument(string level) =>
		$"""
		AdaptiveLighting.Configuration.AdaptiveLightingConfig:
		  Periods:
		  - Name: night
		    Start: 22:30
		    {level}
		    ColorTempKelvin: 2700
		""";

	private static string RoomDocument(string level) =>
		$"""
		AdaptiveLighting.Configuration.AdaptiveLightingConfig:
		  Periods:
		  - Id: night
		    Name: night
		    Start: 22:30
		    Brightness: 204
		    ColorTempKelvin: 2700
		  Areas:
		  - Name: Stue
		    AreaId: {Room}
		    Levels:
		    - PeriodId: night
		      {level}
		""";

	/// <summary>What the engine would tell a lamp for the one period in <paramref name="yaml"/>.</summary>
	private static double CommandedPct(string yaml)
	{
		AdaptiveLightingConfig config = LightingConfigDocument.Deserialize(yaml).Config;
		IReadOnlyList<RoomLevelOverride>? levels = config.Areas.Count > 0 ? config.Areas[0].Levels : null;

		CircadianCalculator calculator = new(
			config.Periods,
			new GlobalConfig { SmoothTransitions = false },
			() => SunTimes.Unknown,
			levels,
			zone: TimeZoneInfo.Utc);

		LightTarget target = calculator.GetTarget(new DateTimeOffset(2026, 1, 15, 23, 0, 0, TimeSpan.Zero))!;

		FakeHaContext ha = new();
		ha.SetState(Light, "off");

		new HaLightActuator(ha, new GlobalConfig(), NullLogger.Instance)
			.Apply(Light, new LightCommand(true, target.BrightnessPct));

		return (double)((Dictionary<string, object>)ha.Calls.Single().Data!)["brightness_pct"];
	}

	// ---- the invariant a live house depends on -------------------------------------------------------------

	/// <summary>A document written in percent is read as it always was, so nothing moves on the deploy itself.</summary>
	[TestMethod]
	public void An_Old_Period_Document_Commands_The_Same_Payload_It_Always_Did()
	{
		foreach (int percent in WholePercents)
			Assert.AreEqual(
				(double)percent,
				CommandedPct(PeriodDocument($"BrightnessPct: {percent}")),
				$"{percent} % has to reach the lamp untouched until a save moves it onto the byte grid");
	}

	/// <summary>The same for a room's own level for a period, which is the other value the fine handle can set.</summary>
	[TestMethod]
	public void An_Old_Room_Level_Commands_The_Same_Payload_It_Always_Did()
	{
		foreach (int percent in WholePercents)
			Assert.AreEqual(
				(double)percent,
				CommandedPct(RoomDocument($"BrightnessPct: {percent}")),
				$"a room stating {percent} % has to reach the lamp untouched too");
	}

	/// <summary>The save that moves a document onto the byte grid must not move a lamp.</summary>
	[TestMethod]
	public void Moving_A_Percentage_Onto_The_Byte_Grid_Lands_Every_Lamp_On_The_Same_Byte()
	{
		foreach (int percent in WholePercents)
		{
			AdaptiveLightingConfig config = LightingConfigDocument.Deserialize(
				PeriodDocument($"BrightnessPct: {percent}")).Config;

			ConfigNormalizer.Normalize(config);

			double after = CommandedPct(LightingConfigDocument.Serialize(config));

			Assert.AreEqual(LandsOn(percent), LandsOn(after),
				$"{percent} % landed on byte {LandsOn(percent)} before the save and must land there after it");
		}
	}

	/// <summary>And must not change a single number a person reads.</summary>
	[TestMethod]
	public void Moving_A_Percentage_Onto_The_Byte_Grid_Leaves_Every_Readout_Saying_The_Same_Thing()
	{
		foreach (int percent in WholePercents)
		{
			AdaptiveLightingConfig config = LightingConfigDocument.Deserialize(
				PeriodDocument($"BrightnessPct: {percent}")).Config;

			ConfigNormalizer.Normalize(config);

			Assert.AreEqual((double)percent, ConfigNormalizer.Whole(config.Periods[0].BrightnessPct),
				$"{percent} % has to still read as {percent} % after the save");
		}
	}

	// ---- what the new storage buys -------------------------------------------------------------------------

	/// <summary>The symptom this change exists to remove: a fine-adjusted value used to come back as its neighbour.</summary>
	[TestMethod]
	public void A_Fine_Adjusted_Level_Survives_A_Save()
	{
		AdaptiveLightingConfig config = new()
		{
			Periods = [new TimePeriodConfig { Name = "evening", Start = "18:00", BrightnessPct = RawBrightnessStep.ToPercent(108) }]
		};

		ConfigNormalizer.Normalize(config);

		Assert.AreEqual(108, config.Periods[0].Brightness,
			"a value the fine handle can set has to survive the save that follows it");
	}

	[TestMethod]
	public void The_Saved_Document_Holds_The_Raw_Value()
	{
		AdaptiveLightingConfig config = new()
		{
			Periods = [new TimePeriodConfig { Name = "evening", Start = "18:00", BrightnessPct = RawBrightnessStep.ToPercent(108) }]
		};

		ConfigNormalizer.Normalize(config);
		string yaml = LightingConfigDocument.Serialize(config);

		StringAssert.Contains(yaml, "Brightness: 108");
		Assert.IsFalse(yaml.Contains("BrightnessPct", StringComparison.Ordinal),
			"the percentage is an input the deserialiser still binds, never a key a save writes");
	}

	/// <summary>Every byte, including the ends, survives the document and comes back as itself.</summary>
	[TestMethod]
	public void Every_Byte_Round_Trips_Through_The_Document()
	{
		foreach (int raw in new[] { 0, 1, 2, 3, 107, 108, 128, 253, 254, 255 })
		{
			AdaptiveLightingConfig config = LightingConfigDocument.Deserialize(
				PeriodDocument($"Brightness: {raw}")).Config;

			Assert.AreEqual(raw, config.Periods[0].Brightness, "read back");

			ConfigNormalizer.Normalize(config);
			AdaptiveLightingConfig again = LightingConfigDocument.Deserialize(
				LightingConfigDocument.Serialize(config)).Config;

			Assert.AreEqual(raw, again.Periods[0].Brightness, "and again after a save");
		}
	}

	/// <summary>The whole percent a byte reads as, at both ends and in the middle.</summary>
	[TestMethod]
	public void A_Byte_Reads_As_The_Whole_Percent_It_Is_Nearest()
	{
		Assert.AreEqual(0d, ConfigNormalizer.Whole(RawBrightness.ToPercent(0)));
		Assert.AreEqual(0d, ConfigNormalizer.Whole(RawBrightness.ToPercent(1)));
		Assert.AreEqual(1d, ConfigNormalizer.Whole(RawBrightness.ToPercent(2)));
		Assert.AreEqual(50d, ConfigNormalizer.Whole(RawBrightness.ToPercent(128)));
		Assert.AreEqual(100d, ConfigNormalizer.Whole(RawBrightness.ToPercent(254)));
		Assert.AreEqual(100d, ConfigNormalizer.Whole(RawBrightness.ToPercent(255)));
	}

	// ---- documents nobody meant to write --------------------------------------------------------------------

	/// <summary>Both forms of one level, which only a hand edit can produce: the raw one wins whatever the order.</summary>
	[TestMethod]
	public void A_Level_Written_Twice_Is_Read_As_The_Raw_One()
	{
		DocumentReadResult rawFirst = LightingConfigDocument.Deserialize(
			PeriodDocument("Brightness: 108\n    BrightnessPct: 90"));

		DocumentReadResult percentFirst = LightingConfigDocument.Deserialize(
			PeriodDocument("BrightnessPct: 90\n    Brightness: 108"));

		Assert.AreEqual(108, rawFirst.Config.Periods[0].Brightness);
		Assert.AreEqual(108, percentFirst.Config.Periods[0].Brightness,
			"the winner cannot depend on which line came first");

		Assert.IsTrue(rawFirst.NeedsMigratingWrite, "the file is ambiguous, so it is written back without the percentage");
	}

	/// <summary>A room's level row says nothing when it states no level, whichever key it would have used.</summary>
	[TestMethod]
	public void A_Row_With_No_Level_Is_Still_Empty()
	{
		Assert.IsTrue(new RoomLevelOverride { PeriodId = "night" }.IsEmpty);
		Assert.IsFalse(new RoomLevelOverride { PeriodId = "night", Brightness = 0 }.IsEmpty,
			"nought is a level a room can choose, and is not the same as choosing nothing");
	}
}
