using System.Text.RegularExpressions;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     What the room page says a room is doing, and what the engine measured to decide it.
/// </summary>
/// <remarks>
///     There is no Razor render harness in this repo, so every one of these strings is built outside the markup
///     and asserted here.
/// </remarks>
[TestClass]
public sealed class RoomFactsTests
{
	private static readonly DateTimeOffset Now = new(2026, 7, 27, 21, 40, 0, TimeSpan.FromHours(2));

	private static AreaSnapshot Report(
		AreaState state = AreaState.AutoVacant,
		bool killSwitch = false,
		bool? isDark = true,
		double? brightness = null,
		int? kelvin = null,
		DateTimeOffset? lastCommand = null,
		DateTimeOffset? lastMotion = null,
		DateTimeOffset? nextChange = null,
		DateTimeOffset? nextFrom = null,
		string? darknessDetail = null,
		AutoOnBlock? blockedBy = null,
		string? blockingEntity = null,
		string? houseModeValue = null,
		bool? isAnyoneHome = null,
		ForcedMode? forced = null,
		bool? isHeldLit = null,
		string? heldLitBy = null) =>
		new(
			"Stue",
			state,
			TransitionReason.CircadianTick,
			HouseMode.Home,
			killSwitch,
			isDark,
			"evening",
			brightness,
			kelvin,
			Now.AddMinutes(-1),
			lastCommand,
			lastMotion,
			nextChange,
			nextFrom,
			houseModeValue,
			darknessDetail,
			"stue",
			blockedBy,
			blockingEntity,
			null,
			isAnyoneHome,
			forced,
			isHeldLit,
			heldLitBy);

	private static string ValueOf(IReadOnlyList<RoomFact> facts, string label) =>
		facts.Single(fact => fact.Label == label).Value;

	// ===================== the table =====================

	[TestMethod]
	public void The_Table_Leads_With_Darkness_And_Never_Repeats_The_State_Chip()
	{
		IReadOnlyList<RoomFact> facts = RoomFacts.For(Report(), Now);

		CollectionAssert.AreEqual(
			new[] { "Dark enough?", "Lights", "Last movement", "Last changed", "Time of day" },
			facts.Select(fact => fact.Label).ToArray());
	}

	/// <summary>While the master switch is off the engine commands nothing anywhere, so no room-level row means much.</summary>
	[TestMethod]
	public void The_Master_Switch_Leads_When_It_Is_Off()
	{
		IReadOnlyList<RoomFact> facts = RoomFacts.For(Report(killSwitch: true), Now);

		Assert.AreEqual("Master switch", facts[0].Label);
		StringAssert.Contains(facts[0].Value, "nothing will change");
	}

	/// <summary>The detail line is the gate's own reading; it is the only thing that knows which source it consulted.</summary>
	[TestMethod]
	public void Darkness_Answers_First_And_Shows_The_Engines_Reading_Beneath()
	{
		IReadOnlyList<RoomFact> facts = RoomFacts.For(Report(isDark: false, darknessDetail: "lux 86, dark below 40"), Now);
		RoomFact darkness = facts.Single(fact => fact.Label == "Dark enough?");

		Assert.AreEqual("no — too bright", darkness.Value);
		Assert.AreEqual("lux 86, dark below 40", darkness.Detail);

		RoomFact lit = RoomFacts.For(Report(isDark: true, darknessDetail: "lux 4, dark below 40"), Now)
			.Single(fact => fact.Label == "Dark enough?");

		Assert.AreEqual("yes", lit.Value);

		RoomFact bare = RoomFacts.For(Report(isDark: null), Now).Single(fact => fact.Label == "Dark enough?");

		Assert.AreEqual("not checked yet", bare.Value);
		Assert.IsNull(bare.Detail);
	}

	/// <summary>
	///     The clock half is asserted by shape. <c>RoomFacts.Stamp</c> renders through <c>ToLocalTime()</c>, so a
	///     pinned "21:37" passes on this Europe/Oslo box and fails on the UTC build agent with "19:37".
	/// </summary>
	[TestMethod]
	public void A_Stamp_Leads_With_The_Age_And_Drops_The_Seconds()
	{
		IReadOnlyList<RoomFact> facts = RoomFacts.For(Report(lastMotion: Now.AddMinutes(-2).AddSeconds(-10)), Now);
		string stamp = ValueOf(facts, "Last movement");

		StringAssert.StartsWith(stamp, "2 min ago · ", "the age is the fact, so it comes first");
		StringAssert.Matches(stamp, new Regex(@"^2 min ago · \d{2}:\d{2}$"),
			"hours and minutes only — 17:42:10 asks to be compared digit by digit with the row below it");
	}

	/// <summary>After a restart no command has been sent while the ceiling light may well be on.</summary>
	[TestMethod]
	public void An_Uncommanded_Room_Is_Not_Reported_As_Off()
	{
		Assert.AreEqual("not commanded yet", ValueOf(RoomFacts.For(Report(), Now), "Lights"));

		Assert.AreEqual("off", ValueOf(RoomFacts.For(Report(lastCommand: Now.AddHours(-1)), Now), "Lights"));

		// The warmth is named in the value and numbered in the hover.
		RoomFact lit = RoomFacts.For(Report(state: AreaState.AutoActive, brightness: 70, kelvin: 2700, lastCommand: Now), Now)
			.Single(fact => fact.Label == "Lights");

		Assert.AreEqual("70 % · warm white", lit.Value);
		StringAssert.Contains(lit.Title!, "2700 K");
	}

	// ===================== would movement light this room? =====================

	/// <summary>
	///     A bedroom that will not light itself while the house sleeps sits in the same state as a room waiting
	///     for somebody to walk in, so the state alone cannot answer this.
	/// </summary>
	[TestMethod]
	public void A_Sleeping_House_Is_Not_Promised_A_Light()
	{
		AreaSnapshot asleep = Report(blockedBy: AutoOnBlock.Sleep);

		Assert.AreEqual("The house is asleep — movement won't light the room.", RoomFacts.AutoOnNote(asleep));
		Assert.AreEqual("The house is asleep — movement won't light the room.", RoomFacts.NextLine(asleep, Now));

		StringAssert.Contains(ValueOf(RoomFacts.For(asleep, Now), "If someone walks in"), "won't light the room");
	}

	[TestMethod]
	public void A_Blocking_Entity_Is_Named()
	{
		AreaSnapshot blocked = Report(blockedBy: AutoOnBlock.EntityOn, blockingEntity: "media_player.tv");

		Assert.AreEqual("media_player.tv is on — movement won't light the room.", RoomFacts.AutoOnNote(blocked));

		AreaSnapshot unnamed = Report(blockedBy: AutoOnBlock.EntityOn);
		StringAssert.StartsWith(RoomFacts.AutoOnNote(unnamed)!, "Something here is on");
	}

	// ===================== away, and the two things it can mean =====================

	/// <summary>
	///     Regression: an Away option held by <c>ActivateWhileOn</c> over an occupied house. The page said
	///     "Nobody home" while both person entities read <c>home</c>.
	/// </summary>
	[TestMethod]
	public void An_Away_Mode_Over_An_Occupied_House_Names_What_Is_Forcing_It()
	{
		ForcedMode forced = new(
			ModeKind.Away, "Borte", ModeForceSource.WhileEntityOn, "input_boolean.occupancy", "on");

		AreaSnapshot held = Report(
			state: AreaState.Away, blockedBy: AutoOnBlock.Away, isAnyoneHome: true, forced: forced);

		// ForcedMode.Describe is the single wording; the log reads the same one. Never re-word it here.
		Assert.AreEqual(forced.Describe(), RoomFacts.AutoOnNote(held));
		Assert.AreEqual(forced.Describe(), ValueOf(RoomFacts.For(held, Now), "If someone walks in"));

		Assert.AreEqual("The house is in away mode, though somebody is home.", RoomFacts.Headline(held));
		Assert.AreEqual("Wakes when the house leaves away mode.", RoomFacts.NextLine(held, Now));
	}

	[TestMethod]
	public void An_Away_Mode_Nobody_Forced_Names_The_Option_Instead()
	{
		AreaSnapshot chosen = Report(
			state: AreaState.Away, blockedBy: AutoOnBlock.Away, isAnyoneHome: true, houseModeValue: "Borte");

		Assert.AreEqual("Somebody is home, but the house mode is set to Borte.", RoomFacts.AutoOnNote(chosen));

		AreaSnapshot nameless = Report(state: AreaState.Away, blockedBy: AutoOnBlock.Away, isAnyoneHome: true);

		Assert.AreEqual("Somebody is home, but the house is in away mode.", RoomFacts.AutoOnNote(nameless));
	}

	[TestMethod]
	public void An_Empty_House_Still_Says_Nobody_Home()
	{
		AreaSnapshot empty = Report(state: AreaState.Away, blockedBy: AutoOnBlock.Away, isAnyoneHome: false);

		Assert.AreEqual("Nobody home.", RoomFacts.Headline(empty));
		Assert.AreEqual("Wakes when the first person comes home.", RoomFacts.NextLine(empty, Now));

		Assert.IsNull(RoomFacts.AutoOnNote(empty));

		Assert.AreEqual(
			"Nobody home. This room keeps its lights on.",
			RoomFacts.Headline(Report(state: AreaState.Away, isAnyoneHome: false, brightness: 20)));
	}

	/// <summary>A null <c>IsAnyoneHome</c> is a report from a build that predates the field.</summary>
	[TestMethod]
	public void A_Report_That_Cannot_Say_Who_Is_Home_Keeps_The_Old_Words()
	{
		AreaSnapshot older = Report(state: AreaState.Away, blockedBy: AutoOnBlock.Away, isAnyoneHome: null);

		Assert.AreEqual("Nobody home.", RoomFacts.Headline(older));
		Assert.AreEqual("Wakes when the first person comes home.", RoomFacts.NextLine(older, Now));
		Assert.IsNull(RoomFacts.AutoOnNote(older));
	}

	/// <summary>A null <c>BlockedBy</c> is a report from a build that predates the verdict.</summary>
	[TestMethod]
	public void An_Older_Report_Claims_Nothing_About_The_Gate()
	{
		AreaSnapshot older = Report(blockedBy: null);

		Assert.IsNull(RoomFacts.AutoOnNote(older));
		Assert.IsFalse(RoomFacts.For(older, Now).Any(fact => fact.Label == "If someone walks in"));
		Assert.AreEqual("Movement in the dark turns the lights on.", RoomFacts.NextLine(older, Now));
	}

	/// <summary>
	///     Away is in this list because none of these reports says who is home; the reports that do say are
	///     covered above.
	/// </summary>
	[TestMethod]
	public void The_Gates_That_Are_Already_Visible_Are_Not_Repeated()
	{
		foreach (AutoOnBlock quiet in new[] { AutoOnBlock.None, AutoOnBlock.NotDark, AutoOnBlock.Disabled, AutoOnBlock.KillSwitch, AutoOnBlock.Away })
		{
			Assert.IsNull(RoomFacts.AutoOnNote(Report(blockedBy: quiet)), $"{quiet} is already stated elsewhere on the page");
		}

		Assert.AreEqual("Movement in the dark turns the lights on.", RoomFacts.NextLine(Report(blockedBy: AutoOnBlock.None), Now));
	}

	// ===================== the countdown =====================

	/// <summary>The ring needs both ends: an armed instant and the deadline it was armed from.</summary>
	[TestMethod]
	public void The_Countdown_Is_Absent_Rather_Than_Invented()
	{
		Assert.IsNull(RoomFacts.Countdown(Report(nextChange: Now.AddMinutes(5)), Now), "no armed instant, no ring");
		Assert.IsNull(RoomFacts.Countdown(Report(nextFrom: Now.AddMinutes(-5)), Now), "no deadline, no ring");

		Assert.AreEqual(
			0.5,
			RoomFacts.Countdown(Report(nextChange: Now.AddMinutes(5), nextFrom: Now.AddMinutes(-5)), Now)!.Value,
			0.001);
	}

	[TestMethod]
	public void An_Overdue_Deadline_Says_So_Rather_Than_Counting_Down()
	{
		AreaSnapshot stale = Report(state: AreaState.AutoActive, nextChange: Now.AddMinutes(-10), nextFrom: Now.AddMinutes(-20));

		Assert.IsTrue(RoomFacts.IsOverdue(stale, Now));
		Assert.IsNull(RoomFacts.Countdown(stale, Now));
		StringAssert.Contains(RoomFacts.NextLine(stale, Now)!, "hasn't arrived");

		Assert.IsFalse(RoomFacts.IsOverdue(Report(nextChange: Now.AddSeconds(-30)), Now),
			"a deadline a moment past is a report in flight, not a broken connection");
	}

	// ===================== the present tense =====================

	/// <summary>Lights the engine adopted at start-up have no command behind them; brightness without one is adopted.</summary>
	[TestMethod]
	public void An_Adopted_Room_Does_Not_Claim_Its_Levels()
	{
		StringAssert.Contains(
			RoomFacts.Headline(Report(state: AreaState.AutoActive, brightness: 100)),
			"already on when the engine started");

		StringAssert.StartsWith(
			RoomFacts.Headline(Report(state: AreaState.AutoActive, brightness: 70, kelvin: 2700, lastCommand: Now)),
			"Lit at 70 %");
	}

	[TestMethod]
	public void A_Paused_House_Says_So_Before_Anything_Else()
	{
		StringAssert.StartsWith(
			RoomFacts.Headline(Report(state: AreaState.AutoActive, killSwitch: true, brightness: 70, lastCommand: Now)),
			"Paused by the master switch");
	}

	[TestMethod]
	public void A_Switched_Off_Room_Says_It_Never_Changes_By_Itself()
	{
		Assert.AreEqual("This room never changes by itself.", RoomFacts.Headline(Report(state: AreaState.Disabled)));
	}

	// ===================== relative time =====================

	[TestMethod]
	public void Ages_Are_Written_In_The_Largest_Unit_That_Stays_Useful()
	{
		Assert.AreEqual("just now", RoomFacts.Ago(Now.AddSeconds(-3), Now));
		Assert.AreEqual("42 s ago", RoomFacts.Ago(Now.AddSeconds(-42), Now));
		Assert.AreEqual("5 min ago", RoomFacts.Ago(Now.AddMinutes(-5), Now));
		Assert.AreEqual("2 h ago", RoomFacts.Ago(Now.AddHours(-2), Now));
		Assert.AreEqual("2 h 30 min ago", RoomFacts.Ago(Now.AddMinutes(-150), Now));
		Assert.AreEqual("3 d ago", RoomFacts.Ago(Now.AddDays(-3), Now));
	}

	[TestMethod]
	public void A_Countdown_Never_Reads_As_Negative()
	{
		Assert.AreEqual("any moment now", RoomFacts.In(Now.AddSeconds(-1), Now));
		Assert.AreEqual("in 45 s", RoomFacts.In(Now.AddSeconds(45), Now));
		Assert.AreEqual("in 12 min", RoomFacts.In(Now.AddMinutes(12), Now));
		Assert.AreEqual("in 1 h", RoomFacts.In(Now.AddHours(1), Now));
	}

	[TestMethod]
	public void The_Lamp_Takes_The_Warmth_It_Was_Commanded()
	{
		Assert.AreNotEqual(RoomFacts.KelvinCss(2200), RoomFacts.KelvinCss(4500));
		StringAssert.StartsWith(RoomFacts.KelvinCss(2700), "rgb(255,");
	}

	// ===================== a KeepLitWhenOn hold =====================

	// Lit and dimmed-as-a-warning are the two states a hold can refuse an off from; both null NextChangeAt,
	// which is why the countdown arms below them cannot answer.
	[TestMethod]
	[DataRow(AreaState.AutoActive, 62d)]
	[DataRow(AreaState.PreOff, 25d)]
	public void A_Held_Room_Names_What_Is_Holding_It_Instead_Of_Falling_Silent(AreaState state, double brightness)
	{
		AreaSnapshot held = Report(state, brightness: brightness, isHeldLit: true, heldLitBy: "media_player.stue_tv");

		Assert.AreEqual(
			"Won't switch off while the television is holding the lights on.",
			RoomFacts.NextLine(held, Now, _ => "the television"));
	}

	[TestMethod]
	public void An_Unresolvable_Holder_Falls_Back_To_The_Entity_Id_Then_To_Prose()
	{
		AreaSnapshot named = Report(AreaState.AutoActive, isHeldLit: true, heldLitBy: "media_player.stue_tv");
		StringAssert.Contains(RoomFacts.NextLine(named, Now), "media_player.stue_tv", StringComparison.Ordinal);

		// An older engine reports the hold without naming it, so the sentence must still parse.
		AreaSnapshot anonymous = Report(AreaState.AutoActive, isHeldLit: true);
		Assert.AreEqual(
			"Won't switch off while something in this room is holding the lights on.",
			RoomFacts.NextLine(anonymous, Now));
	}

	[TestMethod]
	public void The_Table_Gains_A_Held_On_By_Row_Only_While_The_Hold_Applies()
	{
		IReadOnlyList<RoomFact> held = RoomFacts.For(
			Report(AreaState.AutoActive, brightness: 62, isHeldLit: true, heldLitBy: "media_player.stue_tv"),
			Now,
			_ => "the television");

		Assert.AreEqual("the television", ValueOf(held, "Held on by"));

		// false is "nothing is holding it"; null is a build that cannot say. Neither earns a row.
		Assert.IsFalse(RoomFacts.For(Report(AreaState.AutoActive, isHeldLit: false), Now).Any(fact => fact.Label == "Held on by"));
		Assert.IsFalse(RoomFacts.For(Report(AreaState.AutoActive), Now).Any(fact => fact.Label == "Held on by"));
	}

	[TestMethod]
	public void A_Hold_Does_Not_Displace_The_Overdue_Warning()
	{
		// Overdue means the connection is suspect, which outranks anything the last snapshot claimed.
		AreaSnapshot stale = Report(
			AreaState.AutoActive,
			nextChange: Now.AddSeconds(-(int)RoomFacts.OverdueAfter.TotalSeconds - 30),
			isHeldLit: true,
			heldLitBy: "media_player.stue_tv");

		StringAssert.Contains(RoomFacts.NextLine(stale, Now), "hasn't arrived", StringComparison.Ordinal);
	}
}
