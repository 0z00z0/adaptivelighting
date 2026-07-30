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
///     <para>
///         This is the detective's half of the page: somebody opens a room <i>because</i> a light did not come
///         on. A confident wrong answer there is worse than no page at all, so the two claims most easily got
///         wrong are pinned here — that movement would light the room, and that a countdown is running.
///     </para>
///     <para>
///         There is no Razor render harness in this repo, so every one of these strings is built outside the
///         markup and asserted here rather than screenshotted.
///     </para>
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
		ForcedMode? forced = null) =>
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
			forced);

	private static string ValueOf(IReadOnlyList<RoomFact> facts, string label) =>
		facts.Single(fact => fact.Label == label).Value;

	// ===================== the table =====================

	/// <summary>
	///     The readings, ordered by the question that brought somebody to the page — darkness first, context after.
	/// </summary>
	/// <remarks>
	///     There is deliberately no State row: the header's state chip and headline sentence sit an inch above this
	///     table, and a third telling of the same fact is what made the table unscannable.
	/// </remarks>
	[TestMethod]
	public void The_Table_Leads_With_Darkness_And_Never_Repeats_The_State_Chip()
	{
		IReadOnlyList<RoomFact> facts = RoomFacts.For(Report(), Now);

		CollectionAssert.AreEqual(
			new[] { "Dark enough?", "Lights", "Last movement", "Last changed", "Time of day" },
			facts.Select(fact => fact.Label).ToArray());
	}

	/// <summary>
	///     The master switch outranks everything and leads: while it is off the engine commands nothing anywhere,
	///     and a table reporting a state and a period without saying so sends somebody hunting a room-level fault.
	/// </summary>
	[TestMethod]
	public void The_Master_Switch_Leads_When_It_Is_Off()
	{
		IReadOnlyList<RoomFact> facts = RoomFacts.For(Report(killSwitch: true), Now);

		Assert.AreEqual("Master switch", facts[0].Label);
		StringAssert.Contains(facts[0].Value, "nothing will change");
	}

	/// <summary>
	///     The darkness row answers in two words and carries the engine's own reading beneath, not welded onto the
	///     end of the answer. The gate is still the only thing that knows which source it consulted.
	/// </summary>
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

		// No reading published means no second line at all, rather than an empty one taking the space.
		RoomFact bare = RoomFacts.For(Report(isDark: null), Now).Single(fact => fact.Label == "Dark enough?");

		Assert.AreEqual("not checked yet", bare.Value);
		Assert.IsNull(bare.Detail);
	}

	/// <summary>
	///     Times read ago-first without seconds: the age is the fact, the clock is the corroboration, and this
	///     table is read to the nearest minute.
	/// </summary>
	/// <remarks>
	///     <b>The clock half is asserted by shape, not by value.</b> <c>RoomFacts.Stamp</c> renders through
	///     <c>ToLocalTime()</c>, so pinning "21:37" passed on a Europe/Oslo machine and failed on the UTC build
	///     agent with "19:37" — a red CI that said nothing about the behaviour under test. Both halves of the name
	///     are still checked exactly: the age leads, and the time carries no seconds.
	/// </remarks>
	[TestMethod]
	public void A_Stamp_Leads_With_The_Age_And_Drops_The_Seconds()
	{
		IReadOnlyList<RoomFact> facts = RoomFacts.For(Report(lastMotion: Now.AddMinutes(-2).AddSeconds(-10)), Now);
		string stamp = ValueOf(facts, "Last movement");

		StringAssert.StartsWith(stamp, "2 min ago · ", "the age is the fact, so it comes first");
		StringAssert.Matches(stamp, new Regex(@"^2 min ago · \d{2}:\d{2}$"),
			"hours and minutes only — 17:42:10 asks to be compared digit by digit with the row below it");
	}

	/// <summary>
	///     "No command yet" and "off" are different facts. After a restart the first is true while the ceiling
	///     light may well be on, and the page must not claim otherwise.
	/// </summary>
	[TestMethod]
	public void An_Uncommanded_Room_Is_Not_Reported_As_Off()
	{
		Assert.AreEqual("not commanded yet", ValueOf(RoomFacts.For(Report(), Now), "Lights"));

		Assert.AreEqual("off", ValueOf(RoomFacts.For(Report(lastCommand: Now.AddHours(-1)), Now), "Lights"));

		// The warmth is named rather than numbered — "2700 K" is a unit you have to already know to read, and this
		// table is written for the person asking why a light did not come on. The kelvin is kept in the hover.
		RoomFact lit = RoomFacts.For(Report(state: AreaState.AutoActive, brightness: 70, kelvin: 2700, lastCommand: Now), Now)
			.Single(fact => fact.Label == "Lights");

		Assert.AreEqual("70 % · warm white", lit.Value);
		StringAssert.Contains(lit.Title!, "2700 K");
	}

	// ===================== would movement light this room? =====================

	/// <summary>
	///     <b>The claim this page must never get wrong.</b> A bedroom set not to light itself while the house
	///     sleeps sits in exactly the same state as a room waiting for somebody to walk in, and the page is opened
	///     precisely by somebody asking why the light did not come on.
	/// </summary>
	[TestMethod]
	public void A_Sleeping_House_Is_Not_Promised_A_Light()
	{
		AreaSnapshot asleep = Report(blockedBy: AutoOnBlock.Sleep);

		Assert.AreEqual("The house is asleep — movement won't light the room.", RoomFacts.AutoOnNote(asleep));
		Assert.AreEqual("The house is asleep — movement won't light the room.", RoomFacts.NextLine(asleep, Now));

		StringAssert.Contains(ValueOf(RoomFacts.For(asleep, Now), "If someone walks in"), "won't light the room");
	}

	/// <summary>
	///     A blocking entity is named. "Something is on" leaves somebody hunting through the room, which is the
	///     dead end the published entity id exists to end.
	/// </summary>
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
	///     <b>The hour this page cost.</b> A cabin's Away option listed <c>ActivateWhileOn</c> on an
	///     <c>input_boolean</c> that had been on all evening, so every settings save re-asserted Away and swept the
	///     house dark while the owner stood in it — and this page said "Nobody home" and "wakes when the first
	///     person comes home" at him while both person entities read <c>home</c>.
	/// </summary>
	[TestMethod]
	public void An_Away_Mode_Over_An_Occupied_House_Names_What_Is_Forcing_It()
	{
		ForcedMode forced = new(
			ModeKind.Away, "Borte", ModeForceSource.WhileEntityOn, "input_boolean.occupancy", "on");

		AreaSnapshot held = Report(
			state: AreaState.Away, blockedBy: AutoOnBlock.Away, isAnyoneHome: true, forced: forced);

		// The engine's own sentence, called rather than re-worded: it is the only thing that knows which entity it
		// read, and the log and this page saying it differently is how a reader trusts the wrong one.
		Assert.AreEqual(forced.Describe(), RoomFacts.AutoOnNote(held));
		Assert.AreEqual(forced.Describe(), ValueOf(RoomFacts.For(held, Now), "If someone walks in"));

		Assert.AreEqual("The house is in away mode, though somebody is home.", RoomFacts.Headline(held));
		Assert.AreEqual("Wakes when the house leaves away mode.", RoomFacts.NextLine(held, Now));
	}

	/// <summary>
	///     Nothing is forcing the mode, so somebody chose an away option at the select. The page names it rather
	///     than falling back on a claim about presence that the report has just contradicted.
	/// </summary>
	[TestMethod]
	public void An_Away_Mode_Nobody_Forced_Names_The_Option_Instead()
	{
		AreaSnapshot chosen = Report(
			state: AreaState.Away, blockedBy: AutoOnBlock.Away, isAnyoneHome: true, houseModeValue: "Borte");

		Assert.AreEqual("Somebody is home, but the house mode is set to Borte.", RoomFacts.AutoOnNote(chosen));

		// No select value to name is still not a reason to say nobody is home.
		AreaSnapshot nameless = Report(state: AreaState.Away, blockedBy: AutoOnBlock.Away, isAnyoneHome: true);

		Assert.AreEqual("Somebody is home, but the house is in away mode.", RoomFacts.AutoOnNote(nameless));
	}

	/// <summary>
	///     A genuinely empty house keeps every word it had. The fix is a distinction, not a hedge printed over the
	///     case the page was always right about.
	/// </summary>
	[TestMethod]
	public void An_Empty_House_Still_Says_Nobody_Home()
	{
		AreaSnapshot empty = Report(state: AreaState.Away, blockedBy: AutoOnBlock.Away, isAnyoneHome: false);

		Assert.AreEqual("Nobody home.", RoomFacts.Headline(empty));
		Assert.AreEqual("Wakes when the first person comes home.", RoomFacts.NextLine(empty, Now));

		// And the away gate stays off this table: the chip and the headline have already said it.
		Assert.IsNull(RoomFacts.AutoOnNote(empty));

		Assert.AreEqual(
			"Nobody home. This room keeps its lights on.",
			RoomFacts.Headline(Report(state: AreaState.Away, isAnyoneHome: false, brightness: 20)));
	}

	/// <summary>
	///     A report from a build that predates <c>IsAnyoneHome</c> says what this page always said. An older
	///     payload cannot support "somebody is home" any better than it supports the opposite.
	/// </summary>
	[TestMethod]
	public void A_Report_That_Cannot_Say_Who_Is_Home_Keeps_The_Old_Words()
	{
		AreaSnapshot older = Report(state: AreaState.Away, blockedBy: AutoOnBlock.Away, isAnyoneHome: null);

		Assert.AreEqual("Nobody home.", RoomFacts.Headline(older));
		Assert.AreEqual("Wakes when the first person comes home.", RoomFacts.NextLine(older, Now));
		Assert.IsNull(RoomFacts.AutoOnNote(older));
	}

	/// <summary>
	///     A report from a build that predates the verdict claims nothing in either direction. An older payload
	///     cannot support "nothing is blocking this room" any better than it supports the opposite.
	/// </summary>
	[TestMethod]
	public void An_Older_Report_Claims_Nothing_About_The_Gate()
	{
		AreaSnapshot older = Report(blockedBy: null);

		Assert.IsNull(RoomFacts.AutoOnNote(older));
		Assert.IsFalse(RoomFacts.For(older, Now).Any(fact => fact.Label == "If someone walks in"));
		Assert.AreEqual("Movement in the dark turns the lights on.", RoomFacts.NextLine(older, Now));
	}

	/// <summary>
	///     Nothing blocking, and the refusals that already have their own place on the page, add no row: the
	///     room's switch, the master-switch row, the state chip and the darkness row each say their own piece
	///     once.
	/// </summary>
	/// <remarks>
	///     The away gate is in this list on the strength of what these reports carry — none of them says who is
	///     home, so none of them can support the one telling of away the page is not already right about. The
	///     reports that do say are covered above.
	/// </remarks>
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

	/// <summary>A missing start is a missing ring, never an invented one.</summary>
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

	/// <summary>
	///     A deadline long past that no report replaced means the page has lost touch, and it says so rather than
	///     counting down into the negative.
	/// </summary>
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

	/// <summary>
	///     Lights the engine adopted at start-up have no command behind them, so the page does not describe their
	///     levels as the engine's doing.
	/// </summary>
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

	/// <summary>The master switch outranks the state in the prose too.</summary>
	[TestMethod]
	public void A_Paused_House_Says_So_Before_Anything_Else()
	{
		StringAssert.StartsWith(
			RoomFacts.Headline(Report(state: AreaState.AutoActive, killSwitch: true, brightness: 70, lastCommand: Now)),
			"Paused by the master switch");
	}

	/// <summary>A switched-off room says what it is rather than what it would do.</summary>
	[TestMethod]
	public void A_Switched_Off_Room_Says_It_Never_Changes_By_Itself()
	{
		Assert.AreEqual("This room never changes by itself.", RoomFacts.Headline(Report(state: AreaState.Disabled)));
	}

	// ===================== relative time =====================

	/// <summary>Ages are a function of two instants, so they can be asserted rather than watched.</summary>
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

	/// <summary>A future never reads as negative: the overdue line takes over before it could.</summary>
	[TestMethod]
	public void A_Countdown_Never_Reads_As_Negative()
	{
		Assert.AreEqual("any moment now", RoomFacts.In(Now.AddSeconds(-1), Now));
		Assert.AreEqual("in 45 s", RoomFacts.In(Now.AddSeconds(45), Now));
		Assert.AreEqual("in 12 min", RoomFacts.In(Now.AddMinutes(12), Now));
		Assert.AreEqual("in 1 h", RoomFacts.In(Now.AddHours(1), Now));
	}

	/// <summary>
	///     The lamp's colour comes from the Kelvin the engine commanded, so a night dim and a midday white are
	///     visibly different rooms rather than one palette choice applied twice.
	/// </summary>
	[TestMethod]
	public void The_Lamp_Takes_The_Warmth_It_Was_Commanded()
	{
		Assert.AreNotEqual(RoomFacts.KelvinCss(2200), RoomFacts.KelvinCss(4500));
		StringAssert.StartsWith(RoomFacts.KelvinCss(2700), "rgb(255,");
	}
}
