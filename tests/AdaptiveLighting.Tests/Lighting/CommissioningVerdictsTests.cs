using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The words a roll-call row uses; which flags a room gets belongs to <c>AreaAutoDiscovery</c>.</summary>
[TestClass]
public sealed class CommissioningVerdictsTests
{
	private static AreaSettings Defaults => new();

	private static IReadOnlyList<string> Words(IReadOnlyList<Verdict> notes) => [.. notes.Select(note => note.Text)];

	// ===================== silence =====================

	[TestMethod]
	public void An_Ordinary_Room_Says_Nothing()
	{
		AreaConfig room = new() { AreaId = "gjesterom" };

		Assert.AreEqual(0, CommissioningVerdicts.For(room, Defaults, luxSensorCount: 1, suspectCount: 0, lightCount: 1, motionCount: 1).Count);
	}

	// No lights means no suspects, and every other note is about settings, so nothing else stops the row printing
	// Ready over a room that would never light.
	[TestMethod]
	public void A_Room_With_No_Lights_Is_Never_Called_Ready()
	{
		AreaConfig stranded = new() { AreaId = "loftstue" };

		IReadOnlyList<Verdict> notes =
			CommissioningVerdicts.For(stranded, Defaults, luxSensorCount: 0, suspectCount: 0, lightCount: 0, motionCount: 1);

		Assert.AreNotEqual(0, notes.Count, "an empty list is how the row says Ready");
		Assert.AreEqual(VerdictTone.Warn, notes[0].Tone);
		StringAssert.Contains(notes[0].Text, "no lights found");
	}

	// ===================== the light-level notes =====================

	[TestMethod]
	public void No_Sensor_Is_Not_A_Row_Note()
	{
		AreaConfig room = new() { AreaId = "bod" };

		Assert.AreEqual(0, CommissioningVerdicts.For(room, Defaults, 0, 0, 1, 1).Count);
	}

	// Follows IlluminanceGate: a gate with nothing to read is not a gate, so the room counts as dark.
	[TestMethod]
	public void No_Sensor_On_The_Default_Source_Counts_As_Dark_All_Day()
	{
		Assert.IsTrue(CommissioningVerdicts.CountsAsDarkForWantOfASensor(new AreaConfig { AreaId = "bod" }, Defaults, 0, 1));
	}

	[TestMethod]
	public void A_Room_That_Judges_By_The_Sun_Is_Not_Counted()
	{
		AreaConfig room = new() { AreaId = "uteplass", Darkness = DarknessSource.Sun };

		Assert.IsFalse(CommissioningVerdicts.CountsAsDarkForWantOfASensor(room, Defaults, 0, 1));
		Assert.AreEqual(0, CommissioningVerdicts.For(room, Defaults, 0, 0, 1, 1).Count);
	}

	// Defensive agreement with IlluminanceGate, which answers Lux or Either alike in all three arms. No page can
	// reach this with Either: every web read goes through LightingConfigDocument.Deserialize, whose LegacyValues
	// rewrites the scalar on load. The member survives for NetDaemon's binder, which has no such pre-pass.
	[TestMethod]
	public void The_Retired_Either_Counts_As_Dark_Just_As_Lux_Does()
	{
		AreaConfig legacy = new() { AreaId = "kjellerbod", Darkness = DarknessSource.Either };

		Assert.IsTrue(CommissioningVerdicts.CountsAsDarkForWantOfASensor(legacy, Defaults, 0, 1));

		AreaSettings inherited = new() { Darkness = DarknessSource.Either };
		Assert.IsTrue(CommissioningVerdicts.CountsAsDarkForWantOfASensor(new AreaConfig { AreaId = "bod" }, inherited, 0, 1));
	}

	[TestMethod]
	public void A_Room_With_A_Sensor_Is_Not_Counted()
	{
		Assert.IsFalse(CommissioningVerdicts.CountsAsDarkForWantOfASensor(new AreaConfig { AreaId = "stue" }, Defaults, 1, 1));

		AreaConfig pinned = new() { AreaId = "stue", LuxSensor = "sensor.stue_lys" };
		Assert.IsFalse(CommissioningVerdicts.CountsAsDarkForWantOfASensor(pinned, Defaults, 0, 1));
	}

	[TestMethod]
	public void The_No_Sensor_Line_Is_Said_Once()
	{
		Assert.IsNull(CommissioningVerdicts.NoSensorLine(0));
		StringAssert.StartsWith(CommissioningVerdicts.NoSensorLine(1), "One room has no light-level sensor, so it counts as dark all day");
		StringAssert.StartsWith(CommissioningVerdicts.NoSensorLine(11), "11 rooms have no light-level sensor, so they count as dark all day");
	}

	[TestMethod]
	public void Two_Sensors_Are_Reported_As_An_Average()
	{
		AreaConfig room = new() { AreaId = "kontor" };

		CollectionAssert.Contains(
			(System.Collections.ICollection)Words(CommissioningVerdicts.For(room, Defaults, 2, 0, 3, 1)),
			"reads the average of 2 sensors");
	}

	[TestMethod]
	public void A_Pinned_Sensor_Ends_The_Average_Note()
	{
		AreaConfig room = new() { AreaId = "kontor", LuxSensor = "sensor.kontor_vindu" };

		Assert.AreEqual(0, CommissioningVerdicts.For(room, Defaults, 2, 0, 3, 1).Count);
	}

	// ===================== the role guesses =====================

	[TestMethod]
	public void Any_Step_Above_Normal_Reads_As_Quiet_While_The_House_Sleeps()
	{
		foreach (SleepStep step in new[] { SleepStep.Dims, SleepStep.DimsAndStaysOff })
		{
			AreaConfig room = new() { AreaId = "soverom" };
			SleepSteps.Set(room, step);

			CollectionAssert.AreEqual(
				new[] { "quiet while the house sleeps" },
				(System.Collections.ICollection)Words(CommissioningVerdicts.For(room, Defaults, 1, 0, 2, 1)),
				$"{step} is a night rule the roll call has to report");
		}

		AreaConfig plain = new() { AreaId = "soverom" };
		SleepSteps.Set(plain, SleepStep.Normal);

		CollectionAssert.DoesNotContain(
			(System.Collections.ICollection)Words(CommissioningVerdicts.For(plain, Defaults, 1, 0, 2, 1)),
			"quiet while the house sleeps",
			"a room on the first step has no night rule to report");
	}

	[TestMethod]
	public void The_Other_Two_Guesses_Keep_Their_Own_Words()
	{
		AreaConfig hall = new() { AreaId = "gang", WelcomeHome = true };
		AreaConfig terrace = new() { AreaId = "uteplass", SkipAwaySweep = true, Darkness = DarknessSource.Sun };

		CollectionAssert.Contains((System.Collections.ICollection)Words(CommissioningVerdicts.For(hall, Defaults, 1, 0, 1, 1)), "welcomes you home");
		CollectionAssert.Contains((System.Collections.ICollection)Words(CommissioningVerdicts.For(terrace, Defaults, 0, 0, 2, 1)), "stays on when everyone leaves");
	}

	[TestMethod]
	public void An_Inherited_Flag_Still_Earns_Its_Note()
	{
		AreaConfig room = new() { AreaId = "stue" };
		AreaSettings defaults = new() { WelcomeHome = true };

		CollectionAssert.Contains((System.Collections.ICollection)Words(CommissioningVerdicts.For(room, defaults, 1, 0, 1, 1)), "welcomes you home");
	}

	// ===================== the suspects =====================

	[TestMethod]
	public void The_Warning_Leads_The_Row()
	{
		AreaConfig room = new() { AreaId = "stue", WelcomeHome = true };

		IReadOnlyList<Verdict> notes = CommissioningVerdicts.For(room, Defaults, 1, suspectCount: 2, lightCount: 5, motionCount: 1);

		Assert.AreEqual("2 of 5 lights look like something else", notes[0].Text);
		Assert.AreEqual(VerdictTone.Warn, notes[0].Tone);
		Assert.AreEqual(VerdictTone.Info, notes[1].Tone);
	}

	[TestMethod]
	public void One_Suspect_Reads_As_One()
	{
		AreaConfig room = new() { AreaId = "kjokken" };

		Assert.AreEqual(
			"1 of 3 lights looks like something else",
			CommissioningVerdicts.For(room, Defaults, 1, 1, 3, 1)[0].Text);
	}

	// ===================== the switch-driven room =====================

	[TestMethod]
	public void A_Room_With_No_Motion_Sensor_Says_So_On_Its_Own_Row()
	{
		AreaConfig spisestue = new() { AreaId = "spisestue" };

		IReadOnlyList<Verdict> notes =
			CommissioningVerdicts.For(spisestue, Defaults, luxSensorCount: 1, suspectCount: 0, lightCount: 2, motionCount: 0);

		Assert.AreEqual(CommissioningVerdicts.SwitchDrivenNote, notes[0].Text,
			"a room that never lights itself must not read as an ordinary room");
		Assert.AreEqual(VerdictTone.Warn, notes[0].Tone);
	}

	// The note is about the whole room; a suspect light is about some of them. Leading with the suspects would
	// bury the difference the board exists to show.
	[TestMethod]
	public void The_Switch_Driven_Note_Leads_Even_The_Suspects()
	{
		AreaConfig spisestue = new() { AreaId = "spisestue", WelcomeHome = true };

		IReadOnlyList<Verdict> notes =
			CommissioningVerdicts.For(spisestue, Defaults, 1, suspectCount: 2, lightCount: 5, motionCount: 0);

		Assert.AreEqual(CommissioningVerdicts.SwitchDrivenNote, notes[0].Text);
		Assert.AreEqual("2 of 5 lights look like something else", notes[1].Text);
	}

	[TestMethod]
	public void A_Room_With_A_Motion_Sensor_Carries_No_Such_Note()
	{
		AreaConfig gang = new() { AreaId = "gang" };

		CollectionAssert.DoesNotContain(
			(System.Collections.ICollection)Words(CommissioningVerdicts.For(gang, Defaults, 1, 0, 2, 1)),
			CommissioningVerdicts.SwitchDrivenNote);
	}

	// No lights still returns early, so the row says the one thing that matters and stops.
	[TestMethod]
	public void A_Room_With_Neither_Lights_Nor_A_Sensor_Only_Reports_The_Lights()
	{
		AreaConfig loftstue = new() { AreaId = "loftstue" };

		IReadOnlyList<Verdict> notes = CommissioningVerdicts.For(loftstue, Defaults, 0, 0, 0, 0);

		Assert.AreEqual(1, notes.Count);
		StringAssert.Contains(notes[0].Text, "no lights found");
	}

	// Nothing there ever asks the darkness gate, so the light-level line has no consequence to warn about and
	// would say "movement alone lights it" over a room where movement lights nothing.
	[TestMethod]
	public void A_Switch_Driven_Room_Is_Not_Counted_As_Dark_For_Want_Of_A_Sensor()
	{
		AreaConfig spisestue = new() { AreaId = "spisestue" };

		Assert.IsFalse(CommissioningVerdicts.CountsAsDarkForWantOfASensor(spisestue, Defaults, 0, 0));
		Assert.IsTrue(CommissioningVerdicts.CountsAsDarkForWantOfASensor(spisestue, Defaults, 0, 1),
			"the same room with a sensor is exactly what that line is for");
	}

	[TestMethod]
	public void The_Switch_Driven_Line_Is_Said_Once()
	{
		Assert.IsNull(CommissioningVerdicts.SwitchDrivenLine(0));

		Assert.AreEqual(
			"One room has no motion sensor, so it never lights itself. Switching it on at the wall lights it, "
			+ "and it goes off when that hold runs out.",
			CommissioningVerdicts.SwitchDrivenLine(1));

		Assert.AreEqual(
			"4 rooms have no motion sensor, so they never light themselves. Switching one on at the wall "
			+ "lights it, and it goes off when that hold runs out.",
			CommissioningVerdicts.SwitchDrivenLine(4));
	}

	// ===================== the commit button =====================

	[TestMethod]
	public void The_Button_Counts()
	{
		Assert.AreEqual("Switch on the rooms you pick", CommissioningVerdicts.CommitLabel(0));
		Assert.AreEqual("Switch on 1 room", CommissioningVerdicts.CommitLabel(1));
		Assert.AreEqual("Switch on 9 rooms", CommissioningVerdicts.CommitLabel(9));
	}

	[TestMethod]
	public void The_Rest_Line_Says_Where_They_Went()
	{
		Assert.AreEqual("The other 8 stay listed under House, each with its own switch.", CommissioningVerdicts.RestLine(9, 17));
		Assert.AreEqual("The other room stays listed under House, with its own switch.", CommissioningVerdicts.RestLine(16, 17));
		Assert.IsNull(CommissioningVerdicts.RestLine(17, 17));
	}
}
