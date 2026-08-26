using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The stepped night control against the two flags the document still keeps.</summary>
/// <remarks>
///     The step is a control, not a stored value. Every test here therefore asserts the pair of booleans that
///     reaches the engine, not the step that was picked, and asserts it after a save and a load rather than in
///     memory.
/// </remarks>
[TestClass]
public sealed class SleepStepsTests
{
	/// <summary>What each step must leave in the document, which is what the two flags meant before the ladder.</summary>
	private static IEnumerable<(SleepStep Step, bool Respect, bool Block)> Ladder =>
	[
		(SleepStep.Normal, false, false),
		(SleepStep.Dims, true, false),
		(SleepStep.DimsAndStaysOff, true, true)
	];

	private static AdaptiveLightingConfig Document(AreaConfig room) => new()
	{
		ConfigName = "Adaptive lighting [test]",
		Periods = [new TimePeriodConfig { Id = "night-4d4d", Name = "night", Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200 }],
		Areas = [room]
	};

	private static AreaConfig Reload(AreaConfig room) =>
		LightingConfigDocument.Deserialize(LightingConfigDocument.Serialize(Document(room))).Config.Areas.Single();

	// ===================== the three steps, through save and load =====================

	[TestMethod]
	public void Each_Step_Stores_The_Pair_Of_Flags_It_Always_Meant()
	{
		foreach ((SleepStep step, bool respect, bool block) in Ladder)
		{
			AreaConfig room = new() { Name = "Stue", AreaId = "stue" };
			SleepSteps.Set(room, step);

			AreaConfig reloaded = Reload(room);

			Assert.AreEqual(respect, reloaded.RespectSleepMode, $"{step} must store RespectSleepMode = {respect}");
			Assert.AreEqual(block, reloaded.SleepBlocksAutoOn, $"{step} must store SleepBlocksAutoOn = {block}");
		}
	}

	[TestMethod]
	public void A_Saved_Step_Reads_Back_As_The_Same_Step()
	{
		foreach ((SleepStep step, bool _, bool _) in Ladder)
		{
			AreaConfig room = new() { Name = "Stue", AreaId = "stue" };
			SleepSteps.Set(room, step);

			Assert.AreEqual(step, SleepSteps.Of(Reload(room), new AreaSettings()));
		}
	}

	// A one-sided check passes happily while the flag the step moved away from is still sitting there beside the
	// one it set.
	[TestMethod]
	public void Stepping_Down_Clears_The_Flag_The_Step_Above_Had_Set()
	{
		AreaConfig room = new() { Name = "Soverom", AreaId = "soverom" };
		SleepSteps.Set(room, SleepStep.DimsAndStaysOff);

		AreaConfig top = Reload(room);
		Assert.IsTrue(top.RespectSleepMode);
		Assert.IsTrue(top.SleepBlocksAutoOn);

		SleepSteps.Set(room, SleepStep.Dims);

		AreaConfig middle = Reload(room);
		Assert.IsTrue(middle.RespectSleepMode, "the clamp is what the middle step keeps");
		Assert.IsFalse(middle.SleepBlocksAutoOn, "and the block the step above set has to be gone, not merely unread");

		SleepSteps.Set(room, SleepStep.Normal);

		AreaConfig bottom = Reload(room);
		Assert.IsFalse(bottom.RespectSleepMode, "the first step keeps neither");
		Assert.IsFalse(bottom.SleepBlocksAutoOn);
	}

	[TestMethod]
	public void Both_Keys_Survive_A_Save_And_A_Load()
	{
		AreaConfig room = new() { Name = "Soverom", AreaId = "soverom" };
		SleepSteps.Set(room, SleepStep.DimsAndStaysOff);

		string yaml = LightingConfigDocument.Serialize(Document(room));

		StringAssert.Contains(yaml, nameof(AreaSettings.RespectSleepMode), "the document keeps both fields, and the ladder writes both");
		StringAssert.Contains(yaml, nameof(AreaSettings.SleepBlocksAutoOn));
	}

	// ===================== the fourth combination =====================

	[TestMethod]
	public void No_Step_Blocks_Auto_On_Without_The_Clamp()
	{
		foreach ((SleepStep step, bool _, bool _) in Ladder)
		{
			AreaConfig room = new() { Name = "Stue", AreaId = "stue" };
			SleepSteps.Set(room, step);

			AreaConfig reloaded = Reload(room);

			Assert.IsFalse(
				reloaded.SleepBlocksAutoOn is true && reloaded.RespectSleepMode is not true,
				$"{step} must not produce the combination the ladder exists to remove");
		}
	}

	/// <summary>A file already holding that combination loads unchanged, and reads as the step it always behaved as.</summary>
	[TestMethod]
	public void The_Block_Without_The_Clamp_Still_Loads_And_Reads_As_The_Top_Step()
	{
		AreaConfig room = new() { Name = "Soverom", AreaId = "soverom", SleepBlocksAutoOn = true };

		AreaConfig reloaded = Reload(room);

		Assert.IsNull(reloaded.RespectSleepMode, "reading a document must not write a flag into it");
		Assert.IsTrue(reloaded.SleepBlocksAutoOn);
		Assert.AreEqual(SleepStep.DimsAndStaysOff, SleepSteps.Of(reloaded, new AreaSettings()));
	}

	// ===================== the house, and what a room inherits from it =====================

	[TestMethod]
	public void The_House_Steps_Through_The_Same_Pair_Of_Flags()
	{
		foreach ((SleepStep step, bool respect, bool block) in Ladder)
		{
			AreaSettings house = new();
			SleepSteps.Set(house, step);

			AdaptiveLightingConfig config = new()
			{
				ConfigName = "Adaptive lighting [test]",
				Defaults = house,
				Periods = [new TimePeriodConfig { Id = "night-4d4d", Name = "night", Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200 }]
			};

			AreaSettings reloaded = LightingConfigDocument.Deserialize(LightingConfigDocument.Serialize(config)).Config.Defaults;

			Assert.AreEqual(respect, reloaded.RespectSleepMode, $"{step} on the house must store RespectSleepMode = {respect}");
			Assert.AreEqual(block, reloaded.SleepBlocksAutoOn, $"{step} on the house must store SleepBlocksAutoOn = {block}");
			Assert.AreEqual(step, SleepSteps.Of(reloaded));
		}
	}

	[TestMethod]
	public void A_Room_With_No_Flags_Of_Its_Own_Follows_The_Houses_Step()
	{
		AreaSettings house = new();
		SleepSteps.Set(house, SleepStep.Dims);

		AreaConfig room = new() { Name = "Stue", AreaId = "stue" };

		RoomSetting row = RoomSettings.Of(SleepSteps.Key);

		Assert.AreEqual(SleepStep.Dims, SleepSteps.Of(room, house));
		Assert.IsFalse(RoomSettings.IsOwn(room, row), "following the house is not a decision this room made");

		SleepSteps.Set(room, SleepStep.Normal);

		Assert.AreEqual(SleepStep.Normal, SleepSteps.Of(room, house), "a room may step below the house");
		Assert.IsTrue(RoomSettings.IsOwn(room, row));

		RoomSettings.Clear(room, SleepSteps.Key);

		Assert.AreEqual(SleepStep.Dims, SleepSteps.Of(room, house), "cleared, it follows the house again");
		Assert.IsNull(room.RespectSleepMode);
		Assert.IsNull(room.SleepBlocksAutoOn, "both flags go back, or the room keeps half a rule");
	}

	/// <summary>Either flag pinned makes the row this room's own, so the revert offer is drawn.</summary>
	[TestMethod]
	public void Either_Stored_Flag_Makes_The_Row_The_Rooms_Own()
	{
		RoomSetting row = RoomSettings.Of(SleepSteps.Key);

		Assert.IsTrue(RoomSettings.IsOwn(new AreaConfig { RespectSleepMode = false }, row));
		Assert.IsTrue(RoomSettings.IsOwn(new AreaConfig { SleepBlocksAutoOn = true }, row),
			"the folded flag alone still makes the row this room's own");
		Assert.IsFalse(RoomSettings.IsOwn(new AreaConfig(), row));
	}

	// ===================== the words =====================

	[TestMethod]
	public void The_Ladder_Offers_Three_Steps_In_Rising_Order()
	{
		CollectionAssert.AreEqual(
			new[] { nameof(SleepStep.Normal), nameof(SleepStep.Dims), nameof(SleepStep.DimsAndStaysOff) },
			SleepSteps.Options.Select(option => option.Value).ToArray(),
			"the order carries the meaning, so it is asserted and not merely rendered");

		foreach (TokenChoice option in SleepSteps.Options)
			Assert.IsFalse(string.IsNullOrWhiteSpace(option.Text), $"{option.Value} needs words a person recognises");
	}

	[TestMethod]
	public void Only_The_Steps_Above_Normal_Get_A_Clause()
	{
		Assert.IsNull(SleepSteps.Clause(false, false), "a paragraph reporting an absence is noise");
		Assert.AreEqual("dims while the house sleeps", SleepSteps.Clause(true, false));
		Assert.AreEqual("dims and does not come on while the house sleeps", SleepSteps.Clause(true, true));
	}

	// The engine clamps on RespectSleepMode alone, so the sentence must not credit a room with dimming when only
	// the block is stored. The control still shows the top step, because that is the nearest one it can offer.
	[TestMethod]
	public void The_Block_Without_The_Clamp_Is_Reported_Without_A_Dim()
	{
		Assert.AreEqual("does not come on while the house sleeps", SleepSteps.Clause(false, true));

		Assert.AreEqual(SleepStep.DimsAndStaysOff, SleepSteps.Of(false, true),
			"there is no lower step that covers a room which refuses to come on");
	}

	[TestMethod]
	public void A_Token_That_Names_No_Step_Is_Refused()
	{
		Assert.IsFalse(SleepSteps.TryParse("Quiet", out SleepStep _));
		Assert.IsFalse(SleepSteps.TryParse(null, out SleepStep _));
		Assert.IsTrue(SleepSteps.TryParse(nameof(SleepStep.Dims), out SleepStep parsed));
		Assert.AreEqual(SleepStep.Dims, parsed);
	}

	// ===================== the row the pages draw =====================

	[TestMethod]
	public void One_Row_Carries_Both_Flags()
	{
		RoomSetting row = RoomSettings.Of(SleepSteps.Key);

		Assert.AreEqual(RoomControl.Steps, row.Control);
		CollectionAssert.AreEqual(new[] { SleepSteps.Key, SleepSteps.BlockKey }, row.AllKeys.ToArray());

		Assert.AreSame(row, RoomSettings.Of(SleepSteps.BlockKey),
			"the folded flag has no row of its own, so it answers with the row that writes it");

		Assert.AreEqual(
			1,
			RoomSettings.Groups.SelectMany(group => group.Settings).Count(setting => setting.Control is RoomControl.Steps),
			"two night switches became one control, not two");
	}

	[TestMethod]
	public void The_Row_Is_Described_By_Its_Step_And_Not_By_A_Flag()
	{
		AreaSettings house = new();
		AreaConfig room = new();
		SleepSteps.Set(room, SleepStep.DimsAndStaysOff);

		Assert.AreEqual("Dims and stays off", RoomSettings.Describe(room, house, SleepSteps.Key));
		Assert.AreEqual("Normal", RoomSettings.Describe(null, house, SleepSteps.Key), "the house starts on the first step");

		Assert.AreNotEqual("yes", RoomSettings.Describe(room, house, SleepSteps.Key), "a step is not a switch");
	}

	[TestMethod]
	public void Reverting_The_Row_Clears_Both_Flags()
	{
		AreaConfig room = new();
		SleepSteps.Set(room, SleepStep.DimsAndStaysOff);

		Assert.IsTrue(RoomSettings.IsOwn(room, RoomSettings.Of(SleepSteps.Key)));
		Assert.IsTrue(RoomSettings.Clear(room, SleepSteps.Key));

		Assert.IsNull(room.RespectSleepMode);
		Assert.IsNull(room.SleepBlocksAutoOn, "clearing the row it is filed under has to clear the flag it folds in");
		Assert.IsFalse(RoomSettings.IsOwn(room, RoomSettings.Of(SleepSteps.Key)));
	}

	// The row folds two keys, so a room on the top step counts two of the denominator, exactly as the two
	// switches it replaced did.
	[TestMethod]
	public void A_Stepped_Room_Counts_Both_Flags_Against_The_Denominator()
	{
		AreaConfig room = new();
		SleepSteps.Set(room, SleepStep.Dims);

		Assert.AreEqual(2, RoomSettings.OwnCount(room));

		// The group badge counts the same keys the denominator does. Counting rows would report one here.
		RoomSettingGroup group = RoomSettings.Groups.Single(item => item.Settings.Any(setting => setting.Control is RoomControl.Steps));

		Assert.AreEqual(
			2,
			group.Settings.SelectMany(setting => setting.AllKeys).Count(key => RoomSettings.IsOwn(room, key)),
			"the section badge and the n-of-22 line are one number counted twice");
	}
}
