using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     What a roll-call row says about a room before anybody switches it on.
/// </summary>
/// <remarks>
///     The words rather than the rules. Which flags a room gets is <c>AreaAutoDiscovery</c>'s decision and is
///     tested there; what is asserted here is that a row with nothing to say stays silent, that the amber note
///     comes first, and that the light-level note describes the room's darkness source rather than manufacturing
///     a problem out of a setting.
/// </remarks>
[TestClass]
public sealed class CommissioningVerdictsTests
{
	private static AreaSettings Defaults => new();

	private static IReadOnlyList<string> Words(IReadOnlyList<Verdict> notes) => [.. notes.Select(note => note.Text)];

	// ===================== silence =====================

	/// <summary>
	///     An ordinary room with a sensor and no flags says nothing, so the row shows one muted word. Seventeen
	///     green verdicts would be the reassurance dashboard this design guards against.
	/// </summary>
	[TestMethod]
	public void An_Ordinary_Room_Says_Nothing()
	{
		AreaConfig room = new() { AreaId = "gjesterom" };

		Assert.AreEqual(0, CommissioningVerdicts.For(room, Defaults, luxSensorCount: 1, suspectCount: 0, lightCount: 1).Count);
	}

	// ===================== the light-level notes =====================

	/// <summary>
	///     Having no light-level sensor is not a row note. It was, and on a real house it fired on thirteen of
	///     seventeen rows while restating the muted dash in the column beside it. The consequence is said once,
	///     under the table.
	/// </summary>
	[TestMethod]
	public void No_Sensor_Is_Not_A_Row_Note()
	{
		AreaConfig room = new() { AreaId = "bod" };

		Assert.AreEqual(0, CommissioningVerdicts.For(room, Defaults, 0, 0, 1).Count);
	}

	/// <summary>
	///     A room with no sensor on the default darkness source counts as dark all day — the engine's own rule
	///     (<c>IlluminanceGate</c>: a gate with nothing to read is not a gate), so it is counted for the line.
	/// </summary>
	[TestMethod]
	public void No_Sensor_On_The_Default_Source_Counts_As_Dark_All_Day()
	{
		Assert.IsTrue(CommissioningVerdicts.CountsAsDarkForWantOfASensor(new AreaConfig { AreaId = "bod" }, Defaults, 0));
	}

	/// <summary>A room that judges by the sun is not missing anything by having no sensor, so it is not counted.</summary>
	[TestMethod]
	public void A_Room_That_Judges_By_The_Sun_Is_Not_Counted()
	{
		AreaConfig room = new() { AreaId = "uteplass", Darkness = DarknessSource.Sun };

		Assert.IsFalse(CommissioningVerdicts.CountsAsDarkForWantOfASensor(room, Defaults, 0));
		Assert.AreEqual(0, CommissioningVerdicts.For(room, Defaults, 0, 0, 1).Count);
	}

	/// <summary>A room with a sensor, or with one pinned by hand, is not counted either.</summary>
	[TestMethod]
	public void A_Room_With_A_Sensor_Is_Not_Counted()
	{
		Assert.IsFalse(CommissioningVerdicts.CountsAsDarkForWantOfASensor(new AreaConfig { AreaId = "stue" }, Defaults, 1));

		AreaConfig pinned = new() { AreaId = "stue", LuxSensor = "sensor.stue_lys" };
		Assert.IsFalse(CommissioningVerdicts.CountsAsDarkForWantOfASensor(pinned, Defaults, 0));
	}

	/// <summary>The line is said once, counted, and not at all for a house where every room has a sensor.</summary>
	[TestMethod]
	public void The_No_Sensor_Line_Is_Said_Once()
	{
		Assert.IsNull(CommissioningVerdicts.NoSensorLine(0));
		StringAssert.StartsWith(CommissioningVerdicts.NoSensorLine(1), "One room has no light-level sensor, so it counts as dark all day");
		StringAssert.StartsWith(CommissioningVerdicts.NoSensorLine(11), "11 rooms have no light-level sensor, so they count as dark all day");
	}

	/// <summary>Two sensors are averaged by engine rule, and the row says so rather than leaving it to be found out.</summary>
	[TestMethod]
	public void Two_Sensors_Are_Reported_As_An_Average()
	{
		AreaConfig room = new() { AreaId = "kontor" };

		CollectionAssert.Contains(
			(System.Collections.ICollection)Words(CommissioningVerdicts.For(room, Defaults, 2, 0, 3)),
			"reads the average of 2 sensors");
	}

	/// <summary>A room that pins its own sensor has no ambiguity left to report.</summary>
	[TestMethod]
	public void A_Pinned_Sensor_Ends_The_Average_Note()
	{
		AreaConfig room = new() { AreaId = "kontor", LuxSensor = "sensor.kontor_vindu" };

		Assert.AreEqual(0, CommissioningVerdicts.For(room, Defaults, 2, 0, 3).Count);
	}

	// ===================== the role guesses =====================

	/// <summary>The bedroom guess is the two sleep flags, said once however many of them are set.</summary>
	[TestMethod]
	public void The_Sleep_Flags_Read_As_Bedroom_Manners()
	{
		AreaConfig room = new() { AreaId = "soverom", RespectSleepMode = true, SleepBlocksAutoOn = true };

		CollectionAssert.AreEqual(new[] { "bedroom manners" }, (System.Collections.ICollection)Words(
			CommissioningVerdicts.For(room, Defaults, 1, 0, 2)));
	}

	/// <summary>The hallway guess and the outdoor guess get the words the room's own sentences use.</summary>
	[TestMethod]
	public void The_Other_Two_Guesses_Keep_Their_Own_Words()
	{
		AreaConfig hall = new() { AreaId = "gang", WelcomeHome = true };
		AreaConfig terrace = new() { AreaId = "uteplass", SkipAwaySweep = true, Darkness = DarknessSource.Sun };

		CollectionAssert.Contains((System.Collections.ICollection)Words(CommissioningVerdicts.For(hall, Defaults, 1, 0, 1)), "welcomes you home");
		CollectionAssert.Contains((System.Collections.ICollection)Words(CommissioningVerdicts.For(terrace, Defaults, 0, 0, 2)), "stays on when everyone leaves");
	}

	/// <summary>A flag inherited from the house's defaults counts exactly as a flag the room states itself.</summary>
	[TestMethod]
	public void An_Inherited_Flag_Still_Earns_Its_Note()
	{
		AreaConfig room = new() { AreaId = "stue" };
		AreaSettings defaults = new() { WelcomeHome = true };

		CollectionAssert.Contains((System.Collections.ICollection)Words(CommissioningVerdicts.For(room, defaults, 1, 0, 1)), "welcomes you home");
	}

	// ===================== the suspects =====================

	/// <summary>The amber note comes first, because it is the one thing on the row somebody has to act on.</summary>
	[TestMethod]
	public void The_Warning_Leads_The_Row()
	{
		AreaConfig room = new() { AreaId = "stue", WelcomeHome = true };

		IReadOnlyList<Verdict> notes = CommissioningVerdicts.For(room, Defaults, 1, suspectCount: 2, lightCount: 5);

		Assert.AreEqual("2 of 5 lights look like something else", notes[0].Text);
		Assert.AreEqual(VerdictTone.Warn, notes[0].Tone);
		Assert.AreEqual(VerdictTone.Info, notes[1].Tone);
	}

	/// <summary>One suspect is singular. An off-by-one in a plural reads as a bug in the product.</summary>
	[TestMethod]
	public void One_Suspect_Reads_As_One()
	{
		AreaConfig room = new() { AreaId = "kjokken" };

		Assert.AreEqual(
			"1 of 3 lights looks like something else",
			CommissioningVerdicts.For(room, Defaults, 1, 1, 3)[0].Text);
	}

	// ===================== the near-miss line =====================

	/// <summary>A house where discovery refused nothing gets no line at all.</summary>
	[TestMethod]
	public void No_Near_Misses_Means_No_Line()
	{
		Assert.IsNull(CommissioningVerdicts.NearMiss([]));
	}

	/// <summary>The line names the rooms and the fix, and reads as English in both numbers.</summary>
	[TestMethod]
	public void The_Near_Miss_Line_Names_The_Rooms_And_The_Fix()
	{
		Assert.AreEqual(
			"Bod and Teknisk rom have lights but nothing that senses movement, so they sit this out — "
			+ "give them a motion sensor in Home Assistant and press Set up rooms again.",
			CommissioningVerdicts.NearMiss(["Bod", "Teknisk rom"]));

		Assert.AreEqual(
			"Bod has lights but nothing that senses movement, so it sits this out — "
			+ "give it a motion sensor in Home Assistant and press Set up rooms again.",
			CommissioningVerdicts.NearMiss(["Bod"]));
	}

	// ===================== the commit button =====================

	/// <summary>The button counts, which is the whole progress model — there is no progress bar anywhere.</summary>
	[TestMethod]
	public void The_Button_Counts()
	{
		Assert.AreEqual("Switch on the rooms you pick", CommissioningVerdicts.CommitLabel(0));
		Assert.AreEqual("Switch on 1 room", CommissioningVerdicts.CommitLabel(1));
		Assert.AreEqual("Switch on 9 rooms", CommissioningVerdicts.CommitLabel(9));
	}

	/// <summary>The rest are not lost, and the line says where they went rather than only how many there are.</summary>
	[TestMethod]
	public void The_Rest_Line_Says_Where_They_Went()
	{
		Assert.AreEqual("The other 8 stay listed under House, each with its own switch.", CommissioningVerdicts.RestLine(9, 17));
		Assert.AreEqual("The other room stays listed under House, with its own switch.", CommissioningVerdicts.RestLine(16, 17));
		Assert.IsNull(CommissioningVerdicts.RestLine(17, 17));
	}
}
