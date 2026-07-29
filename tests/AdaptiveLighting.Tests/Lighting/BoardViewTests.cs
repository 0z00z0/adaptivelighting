using System.Globalization;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The board's arithmetic and its judgement, tested where they live rather than in markup.
/// </summary>
/// <remarks>
///     <para>
///         Every one of these fails silently on the page. A block off by a percent is invisible; a room wrongly
///         called quiet is a room the owner never sees again; a countdown naming the wrong minute is a confident
///         wrong answer to the one question — "why didn't that light come on" — the board exists to answer.
///     </para>
///     <para>
///         The clock is fixed at 21:37 on a summer evening in +02:00, which is the instant the design was drawn
///         at, so the numbers below can be checked against it by hand.
///     </para>
/// </remarks>
[TestClass]
public sealed class BoardViewTests
{
	private static readonly DateTimeOffset Now = new(2026, 7, 27, 21, 37, 0, TimeSpan.FromHours(2));

	/// <summary>
	///     The house's own time zone, named rather than inherited from the machine.
	/// </summary>
	/// <remarks>
	///     The band turns a period's wall-clock <c>Start</c> into an instant, which cannot be done without a zone.
	///     Every band test therefore says which one, so the numbers below mean the same thing on a build agent in
	///     UTC as on the owner's laptop — and so the two clock-change tests can be about Norway's actual
	///     transitions, which is where this is visible twice a year.
	/// </remarks>
	private static readonly TimeZoneInfo Oslo = TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");

	/// <summary>The board's own window at <see cref="Now"/>: 17:00 to midnight, seven whole hours.</summary>
	private static BoardWindow Window() => BoardWindow.Around(Now, BoardView.LookBack, BoardView.LookAhead);

	private static DateTimeOffset At(int hour, int minute = 0, int second = 0) =>
		new(2026, 7, 27, hour, minute, second, TimeSpan.FromHours(2));

	private static AreaSnapshot Report(
		AreaState state,
		string name = "Stue",
		int? kelvin = null,
		DateTimeOffset? nextChangeAt = null) =>
		new(
			name,
			state,
			TransitionReason.Motion,
			HouseMode.Home,
			false,
			null,
			null,
			null,
			kelvin,
			Now,
			null,
			null,
			nextChangeAt,
			null,
			AreaId: name.ToLowerInvariant());

	private static ActivityEntry Entry(long sequence, DateTimeOffset at, AreaState state, int? kelvin = null) =>
		new(sequence, Report(state, kelvin: kelvin) with { Timestamp = at });

	// ===================== the window =====================

	/// <summary>
	///     Both ends snap to the hour. That is what lets the axis carry labelled ticks and the tracks draw their
	///     gridlines as one repeating background instead of a hairline element per room per hour.
	/// </summary>
	[TestMethod]
	public void The_Window_Snaps_To_Whole_Hours()
	{
		BoardWindow window = Window();

		Assert.AreEqual(At(17), window.Start);
		Assert.AreEqual(At(23).AddHours(1), window.End);
		Assert.AreEqual(7, window.Hours);
	}

	/// <summary>An instant's position is a straight fraction of the window, and the ends are 0 and 100.</summary>
	[TestMethod]
	public void An_Instant_Maps_To_A_Percentage_Of_The_Window()
	{
		BoardWindow window = Window();

		Assert.AreEqual(0, window.PercentAt(window.Start), 1e-9);
		Assert.AreEqual(100, window.PercentAt(window.End), 1e-9);
		Assert.AreEqual(50, window.PercentAt(At(20, 30)), 1e-9);
	}

	/// <summary>One tick per hour boundary, both ends included — eight for a seven-hour window.</summary>
	[TestMethod]
	public void There_Is_One_Tick_Per_Hour_Boundary()
	{
		IReadOnlyList<DateTimeOffset> ticks = Window().Ticks;

		Assert.AreEqual(8, ticks.Count);
		Assert.AreEqual(At(17), ticks[0]);
		Assert.AreEqual(At(23).AddHours(1), ticks[^1]);
	}

	/// <summary>Only instants actually on the board are on the board. A deadline past the right edge is not.</summary>
	[TestMethod]
	public void An_Instant_Outside_The_Window_Is_Not_Contained()
	{
		BoardWindow window = Window();

		Assert.IsTrue(window.Contains(At(21, 37)));
		Assert.IsFalse(window.Contains(At(16, 59)));
		Assert.IsFalse(window.Contains(At(23).AddHours(1).AddMinutes(1)));
	}

	// ===================== the lanes =====================

	/// <summary>
	///     A report covers the stretch from itself to the next one: the engine publishes on transitions, so a
	///     report is a statement that the room stayed that way until it said otherwise.
	/// </summary>
	[TestMethod]
	public void A_Report_Covers_The_Time_Up_To_The_Next_One()
	{
		BoardWindow window = Window();

		IReadOnlyList<LaneBlock> blocks = BoardView.Blocks(
			[Entry(1, At(19), AreaState.AutoActive, 2700), Entry(2, At(20), AreaState.AutoVacant)],
			window,
			Now);

		Assert.AreEqual(1, blocks.Count);
		Assert.AreEqual(LaneBlockKind.Lit, blocks[0].Kind);
		Assert.AreEqual(2.0 / 7 * 100, blocks[0].LeftPct, 1e-6, "the block starts two hours into a seven-hour window");
		Assert.AreEqual(1.0 / 7 * 100, blocks[0].WidthPct, 1e-6, "and runs the one hour until the next report");
	}

	/// <summary>The newest report runs to now, which is what makes the right-hand edge of the board live.</summary>
	[TestMethod]
	public void The_Newest_Report_Runs_To_Now()
	{
		BoardWindow window = Window();

		IReadOnlyList<LaneBlock> blocks = BoardView.Blocks([Entry(1, At(21), AreaState.AutoActive, 2700)], window, Now);

		Assert.AreEqual(1, blocks.Count);
		Assert.AreEqual(window.PercentAt(Now) - window.PercentAt(At(21)), blocks[0].WidthPct, 1e-6);
	}

	/// <summary>
	///     Nothing is drawn before the oldest report the log still holds. What the room was doing earlier is
	///     genuinely unknown, and a block reaching back to the left edge would be an invention.
	/// </summary>
	[TestMethod]
	public void Nothing_Is_Drawn_Before_The_Oldest_Report()
	{
		IReadOnlyList<LaneBlock> blocks = BoardView.Blocks([Entry(1, At(20), AreaState.AutoActive, 2700)], Window(), Now);

		Assert.AreEqual(1, blocks.Count);
		Assert.IsTrue(blocks[0].LeftPct > 40, $"the block began at 20:00, not at the board's left edge (was {blocks[0].LeftPct})");
	}

	/// <summary>A stretch that began before the board did is clipped to it rather than dropped.</summary>
	[TestMethod]
	public void A_Stretch_That_Began_Before_The_Board_Is_Clipped_To_It()
	{
		IReadOnlyList<LaneBlock> blocks = BoardView.Blocks(
			[Entry(1, At(16), AreaState.AutoActive, 2700), Entry(2, At(18), AreaState.AutoVacant)],
			Window(),
			Now);

		Assert.AreEqual(1, blocks.Count);
		Assert.AreEqual(0, blocks[0].LeftPct, 1e-9);
		Assert.AreEqual(1.0 / 7 * 100, blocks[0].WidthPct, 1e-6, "only the 17:00-18:00 hour is on the board");
	}

	/// <summary>
	///     The dark-cockpit rule at the level of one lane: a room that is watching, or a house that is empty,
	///     draws nothing at all. An empty track beside a busy one is the comparison the board exists to offer.
	/// </summary>
	[TestMethod]
	public void Watching_And_House_Empty_Draw_Nothing()
	{
		IReadOnlyList<LaneBlock> blocks = BoardView.Blocks(
			[Entry(1, At(18), AreaState.AutoVacant), Entry(2, At(19), AreaState.Away), Entry(3, At(20), AreaState.AutoVacant)],
			Window(),
			Now);

		Assert.AreEqual(0, blocks.Count);
	}

	/// <summary>
	///     Two reports that say the same thing are one block. Without this a room the engine retuned to the same
	///     warmth twice grows a hairline seam that reads as two separate visits.
	/// </summary>
	[TestMethod]
	public void Adjacent_Stretches_That_Say_The_Same_Thing_Are_One_Block()
	{
		IReadOnlyList<LaneBlock> blocks = BoardView.Blocks(
			[
				Entry(1, At(19), AreaState.AutoActive, 2700),
				Entry(2, At(20), AreaState.AutoActive, 2700),
				Entry(3, At(21), AreaState.AutoVacant)
			],
			Window(),
			Now);

		Assert.AreEqual(1, blocks.Count);
		Assert.AreEqual(2.0 / 7 * 100, blocks[0].WidthPct, 1e-6, "19:00 to 21:00, as one stretch");
	}

	/// <summary>
	///     A retune to a different warmth stays two blocks, because the colour is the fact being reported: the
	///     evening's step from 4300 K to 2700 K should be visible on the board as a change in the block.
	/// </summary>
	[TestMethod]
	public void A_Retune_To_A_Different_Warmth_Stays_Two_Blocks()
	{
		IReadOnlyList<LaneBlock> blocks = BoardView.Blocks(
			[
				Entry(1, At(19), AreaState.AutoActive, 4300),
				Entry(2, At(20), AreaState.AutoActive, 2700),
				Entry(3, At(21), AreaState.AutoVacant)
			],
			Window(),
			Now);

		Assert.AreEqual(2, blocks.Count);
		Assert.AreEqual(4300, blocks[0].Kelvin);
		Assert.AreEqual(2700, blocks[1].Kelvin);
	}

	/// <summary>
	///     A five-second visit to the hall is still a mark. Left to the true arithmetic it would be a fiftieth of
	///     a pixel, which is the same as not drawing it — and the hall is exactly the room somebody checks.
	/// </summary>
	[TestMethod]
	public void A_Moment_Long_Visit_Is_Still_Visible()
	{
		IReadOnlyList<LaneBlock> blocks = BoardView.Blocks(
			[Entry(1, At(20, 0, 0), AreaState.AutoActive, 2700), Entry(2, At(20, 0, 5), AreaState.AutoVacant)],
			Window(),
			Now);

		Assert.AreEqual(1, blocks.Count);
		Assert.IsTrue(blocks[0].WidthPct >= 0.4, $"a five-second block was drawn {blocks[0].WidthPct}% wide");
	}

	/// <summary>
	///     Kelvin rides only on a lit block. A hand-set room's warmth is not the engine's to report, and colouring
	///     its block with it would claim the engine chose that light.
	/// </summary>
	[TestMethod]
	public void Only_A_Lit_Block_Carries_A_Warmth()
	{
		IReadOnlyList<LaneBlock> blocks = BoardView.Blocks(
			[Entry(1, At(20), AreaState.OverriddenOn, 3000), Entry(2, At(21), AreaState.AutoVacant)],
			Window(),
			Now);

		Assert.AreEqual(1, blocks.Count);
		Assert.AreEqual(LaneBlockKind.Hand, blocks[0].Kind);
		Assert.IsNull(blocks[0].Kelvin);
	}

	/// <summary>Off by hand is hatched rather than filled, because nothing was lit — so it is its own kind.</summary>
	[TestMethod]
	public void Off_By_Hand_Is_Its_Own_Kind()
	{
		IReadOnlyList<LaneBlock> blocks = BoardView.Blocks([Entry(1, At(21), AreaState.SuppressedOff)], Window(), Now);

		Assert.AreEqual(1, blocks.Count);
		Assert.AreEqual(LaneBlockKind.Held, blocks[0].Kind);
	}

	/// <summary>Entries arrive newest first from the log; the lane has to draw them oldest first regardless.</summary>
	[TestMethod]
	public void Entries_Are_Ordered_Before_They_Are_Drawn()
	{
		IReadOnlyList<LaneBlock> blocks = BoardView.Blocks(
			[
				Entry(3, At(21), AreaState.AutoVacant),
				Entry(2, At(20), AreaState.AutoActive, 2700),
				Entry(1, At(19), AreaState.PreOff)
			],
			Window(),
			Now);

		Assert.AreEqual(2, blocks.Count);
		Assert.AreEqual(LaneBlockKind.Dimming, blocks[0].Kind, "19:00 came first whatever order the log handed it over in");
		Assert.AreEqual(LaneBlockKind.Lit, blocks[1].Kind);
	}

	// ===================== what happens next =====================

	/// <summary>The dotted mark names the time and what the armed timer will do when it fires.</summary>
	[TestMethod]
	public void The_Future_Mark_Names_The_Time_And_What_Happens()
	{
		BoardWindow window = Window();
		DateTimeOffset resumes = At(22, 20);

		LaneMark? mark = BoardView.NextMark(Report(AreaState.OverriddenOn, nextChangeAt: resumes), window, Now);

		Assert.IsNotNull(mark);
		Assert.AreEqual($"{BoardView.Clock(resumes)} auto resumes", mark.Label);
		Assert.AreEqual(window.PercentAt(resumes), mark.LeftPct, 1e-9);
	}

	/// <summary>Each state's mark says what that state's timer does — the snapshot deliberately does not carry a verb.</summary>
	[TestMethod]
	public void Each_State_Names_Its_Own_Next_Move()
	{
		BoardWindow window = Window();

		Assert.IsTrue(BoardView.NextMark(Report(AreaState.AutoActive, nextChangeAt: At(22)), window, Now)!.Label.EndsWith("dim", StringComparison.Ordinal));
		Assert.IsTrue(BoardView.NextMark(Report(AreaState.PreOff, nextChangeAt: At(22)), window, Now)!.Label.EndsWith("off", StringComparison.Ordinal));
		Assert.IsTrue(BoardView.NextMark(Report(AreaState.SuppressedOff, nextChangeAt: At(22)), window, Now)!.Label.EndsWith("listens again", StringComparison.Ordinal));
	}

	/// <summary>A deadline past the board's right edge is not drawn — the board would be claiming to show it.</summary>
	[TestMethod]
	public void A_Deadline_Off_The_Board_Gets_No_Mark()
	{
		BoardWindow window = Window();

		Assert.IsNull(BoardView.NextMark(Report(AreaState.OverriddenOn, nextChangeAt: At(23).AddHours(2)), window, Now));
		Assert.IsNull(BoardView.NextMark(Report(AreaState.OverriddenOn, nextChangeAt: At(16)), window, Now));
	}

	/// <summary>
	///     A deadline that has already passed gets no mark, even though the board still shows that hour.
	/// </summary>
	/// <remarks>
	///     The window reaches four hours back, so a stale snapshot — every snapshot round-trips through Home
	///     Assistant, and a connection blip is enough — otherwise drew a confident "auto resumes" to the left of
	///     the now-line: a prediction the board can be seen to have got wrong.
	/// </remarks>
	[TestMethod]
	public void A_Deadline_Already_Behind_The_Now_Line_Gets_No_Mark()
	{
		BoardWindow window = Window();
		AreaSnapshot stale = Report(AreaState.OverriddenOn, nextChangeAt: At(20, 57));

		Assert.IsTrue(window.Contains(At(20, 57)), "the board still covers that hour, which is what made this drawable");
		Assert.IsNull(BoardView.NextMark(stale, window, Now));
	}

	/// <summary>A state with nothing armed gets no mark. So does a state whose timer means nothing to a reader.</summary>
	[TestMethod]
	public void A_State_With_No_Armed_Timer_Gets_No_Mark()
	{
		BoardWindow window = Window();

		Assert.IsNull(BoardView.NextMark(Report(AreaState.AutoActive), window, Now), "nothing armed");
		Assert.IsNull(BoardView.NextMark(Report(AreaState.AutoVacant, nextChangeAt: At(22)), window, Now), "a watching room promises nothing");
	}

	// ===================== the tray =====================

	/// <summary>
	///     Exactly the states where the engine is not simply following the schedule. A tray that listed the
	///     nominal ones would be the card grid the board replaced.
	/// </summary>
	[TestMethod]
	public void Only_The_Four_Non_Nominal_States_Are_Exceptions()
	{
		Assert.IsTrue(BoardView.IsException(AreaState.PreOff));
		Assert.IsTrue(BoardView.IsException(AreaState.OverriddenOn));
		Assert.IsTrue(BoardView.IsException(AreaState.SuppressedOff));
		Assert.IsTrue(BoardView.IsException(AreaState.SceneHold));

		Assert.IsFalse(BoardView.IsException(AreaState.AutoActive), "a lit room following the schedule is nominal");
		Assert.IsFalse(BoardView.IsException(AreaState.AutoVacant));
		Assert.IsFalse(BoardView.IsException(AreaState.Away));
		Assert.IsFalse(BoardView.IsException(AreaState.Disabled));
	}

	/// <summary>
	///     A dark room that is waiting but blocked would sit in the board looking like any other quiet room, and
	///     it is the one the reader came for. It is hoisted despite its nominal state.
	/// </summary>
	[TestMethod]
	public void A_Dark_Room_That_Will_Not_Light_Is_An_Exception_Despite_Its_Nominal_State()
	{
		AreaSnapshot asleep = Blocked(AutoOnBlock.Sleep);
		AreaSnapshot television = Blocked(AutoOnBlock.EntityOn, "media_player.stue_tv");

		Assert.IsTrue(BoardView.IsException(asleep));
		Assert.IsTrue(BoardView.IsException(television));

		StringAssert.Contains(BoardView.ExceptionLine(asleep, Now), "the house is asleep");
		StringAssert.Contains(BoardView.ExceptionLine(television, Now), "media_player.stue_tv is on");
	}

	/// <summary>
	///     The refusals that are already announced house-wide, and the ones that are not news, stay out of the
	///     tray — repeating them once per room would bury the rooms worth reading.
	/// </summary>
	[TestMethod]
	public void A_Block_Is_Only_Hoisted_When_It_Is_News_For_That_Room()
	{
		Assert.IsFalse(BoardView.IsException(Blocked(AutoOnBlock.KillSwitch)), "the master switch says this once");
		Assert.IsFalse(BoardView.IsException(Blocked(AutoOnBlock.Away)), "the house mode says this once");
		Assert.IsFalse(BoardView.IsException(Blocked(AutoOnBlock.NotDark)), "daylight is not an exception");
		Assert.IsFalse(BoardView.IsException(Blocked(AutoOnBlock.None)));

		Assert.IsFalse(
			BoardView.IsException(Blocked(AutoOnBlock.Sleep) with { IsDark = false }),
			"the block is only news where light was wanted");

		Assert.IsFalse(
			BoardView.IsException(Blocked(AutoOnBlock.Sleep) with { State = AreaState.AutoActive }),
			"a lit room is not waiting on anything");

		Assert.IsFalse(
			BoardView.IsException(Blocked(AutoOnBlock.Sleep) with { AutoOnBlockedBy = null }),
			"an older report that never carried the field is not a claim that nothing was blocking");
	}

	/// <summary>A blocking entity the wire did not name still says something true.</summary>
	[TestMethod]
	public void A_Blocker_With_No_Entity_Id_Is_Still_Named_Honestly()
	{
		string line = BoardView.ExceptionLine(Blocked(AutoOnBlock.EntityOn), Now);

		StringAssert.Contains(line, "something here is on");
	}

	private static AreaSnapshot Blocked(AutoOnBlock block, string? entity = null) =>
		Report(AreaState.AutoVacant) with
		{
			IsDark = true,
			AutoOnBlockedBy = block,
			AutoOnBlockingEntity = entity
		};

	// ===================== movement the room turned down =====================

	private static ActivityEntry Refused(long sequence, DateTimeOffset at, AutoOnBlock block, string? entity = null) =>
		new(sequence, Blocked(block, entity) with { Timestamp = at });

	/// <summary>
	///     <b>The mark exists so a refusing room stops looking like an empty one.</b> A refusal draws no stretch —
	///     the room was dark before it and dark after — so before this the lane of a room that turned movement
	///     down was pixel-identical to the lane of a room nobody entered. Those are exactly the two cases somebody
	///     opens the board to tell apart.
	/// </summary>
	/// <remarks>
	///     The label is checked against the instant's own local projection rather than against the literal
	///     "20:30". <see cref="BoardView.Clock"/> renders through <c>ToLocalTime()</c>, so the literal held on a
	///     Europe/Oslo machine and failed on the UTC build agent with "18:30" — red CI saying nothing about the
	///     behaviour under test. What the test is really for still holds exactly: one mark, placed where the
	///     refusal happened, carrying <i>that</i> instant's time and the reason.
	/// </remarks>
	[TestMethod]
	public void A_Refused_Movement_Is_Marked_Where_It_Happened()
	{
		DateTimeOffset refusedAt = At(20, 30);

		IReadOnlyList<LaneRefusal> marks = BoardView.Refusals(
			[Refused(1, refusedAt, AutoOnBlock.NotDark)],
			Window());

		Assert.AreEqual(1, marks.Count);

		// 20:30 is three and a half hours into a seven-hour board. The placement is an offset-independent
		// comparison of two instants, so it means the same thing in every zone.
		Assert.AreEqual(50, marks[0].LeftPct, 0.01);
		StringAssert.Contains(marks[0].Label, refusedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));
		StringAssert.Contains(marks[0].Label, "too bright");
	}

	/// <summary>
	///     Every gate that is about <em>this room</em> names itself on its mark. A mark saying only "turned down"
	///     would send the reader to the log for the one word the board had room to carry.
	/// </summary>
	[TestMethod]
	public void Each_Room_Level_Gate_Names_Itself_On_The_Mark()
	{
		(AutoOnBlock Block, string Expected)[] cases =
		[
			(AutoOnBlock.NotDark, "too bright"),
			(AutoOnBlock.Sleep, "the house is asleep"),
			(AutoOnBlock.Disabled, "automatic lighting is off here"),
			(AutoOnBlock.SceneHold, "a guest scene has this room")
		];

		foreach ((AutoOnBlock block, string expected) in cases)
			StringAssert.Contains(
				BoardView.Refusals([Refused(1, At(20), block)], Window()).Single().Label,
				expected,
				$"{block} should name itself");

		StringAssert.Contains(
			BoardView.Refusals([Refused(1, At(20), AutoOnBlock.EntityOn, "media_player.tv")], Window()).Single().Label,
			"media_player.tv is on",
			"the blocking entity is the whole point of that gate");
	}

	/// <summary>
	///     <b>The master switch and an empty house get no mark at all.</b> They turn every room down at once, so
	///     drawing them per room gave all seventeen lanes the same tick — and because a refusal makes a lane
	///     un-quiet, the quiet shelf emptied and every room claimed a row. Seventeen lanes repeating one house
	///     fact is exactly the wall <see cref="BoardLane.IsQuiet"/> exists to prevent, and it is the same rule
	///     <see cref="BoardView.IsException"/> already applies to the tray above.
	/// </summary>
	/// <remarks>
	///     Both are still refusals and both still have their row in the log with their reason. This is a
	///     judgement about which of them earns a mark, not a second opinion about what a refusal is.
	/// </remarks>
	[TestMethod]
	public void A_House_Wide_Refusal_Is_Not_Drawn_On_Every_Lane()
	{
		BoardWindow window = Window();

		Assert.AreEqual(0, BoardView.Refusals([Refused(1, At(20), AutoOnBlock.KillSwitch)], window).Count,
			"the house bar already says the master switch is off, once, at the top");

		Assert.AreEqual(0, BoardView.Refusals([Refused(1, At(20), AutoOnBlock.Away)], window).Count,
			"an empty house is announced house-wide too");

		Assert.AreEqual(1, BoardView.Refusals([Refused(1, At(20), AutoOnBlock.NotDark)], window).Count,
			"a room's own darkness is nobody else's business, and still marked");
	}

	/// <summary>
	///     Movement the room acted on is not a refusal, and neither is a report that carries no gate. The board
	///     asks <see cref="ActivityView.IsDeclinedMotion"/> rather than a copy of it, so the mark and the row it
	///     sends a reader to can never disagree about what was turned down.
	/// </summary>
	[TestMethod]
	public void Movement_That_Lit_The_Room_Is_Not_A_Refusal()
	{
		Assert.AreEqual(0, BoardView.Refusals([Entry(1, At(20), AreaState.AutoActive, 2700)], Window()).Count);

		ActivityEntry noGate = new(2, Report(AreaState.AutoVacant) with { Timestamp = At(20), IsDark = true });
		Assert.AreEqual(0, BoardView.Refusals([noGate], Window()).Count, "a report from before the gate was recorded claims nothing");
	}

	/// <summary>
	///     Refusals outside the board's window are not drawn, and two too close to be told apart are one mark —
	///     whatever turned each of them down.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The gap is deliberately measured on screen rather than on the clock, and deliberately ignores the
	///         gate. Reports minutes apart are perfectly meaningful and still land inside a pixel of each other on
	///         a phone; two ticks a pixel apart cannot be told apart by a reader even when they mean different
	///         things, and keeping the second buys an unreadable mark while stealing the first one's hover.
	///     </para>
	///     <para>
	///         The case that forced it: the suppressed-off path republishes on <i>every</i> movement rather than
	///         once per change of gate, so a bedroom sensor re-firing under a hand-set off drew dozens of ticks
	///         as one continuous amber smear. Every one of them is still a row in the log.
	///     </para>
	/// </remarks>
	[TestMethod]
	public void Marks_Stay_Inside_The_Window_And_Never_Land_On_Top_Of_Each_Other()
	{
		BoardWindow window = Window();

		Assert.AreEqual(0, BoardView.Refusals([Refused(1, At(9), AutoOnBlock.NotDark)], window).Count, "before the window");

		IReadOnlyList<LaneRefusal> together = BoardView.Refusals(
			[Refused(1, At(20), AutoOnBlock.NotDark), Refused(2, At(20), AutoOnBlock.Sleep)],
			window);

		Assert.AreEqual(1, together.Count, "the same instant, even for different reasons");

		// A minute apart on a seven-hour board is a quarter of a per cent — under a pixel on a phone.
		Assert.AreEqual(
			1,
			BoardView.Refusals([Refused(1, At(20), AutoOnBlock.Sleep), Refused(2, At(20, 1), AutoOnBlock.Sleep)], window).Count,
			"a re-firing sensor is one mark, not a smear");

		// Half an hour is seven per cent, and plainly two events.
		Assert.AreEqual(
			2,
			BoardView.Refusals([Refused(1, At(20), AutoOnBlock.Sleep), Refused(2, At(20, 30), AutoOnBlock.Sleep)], window).Count,
			"far enough apart to be read as two");
	}

	/// <summary>
	///     <b>A room that only refused is not quiet.</b> It draws no stretch and arms no timer, so without this it
	///     would be folded away under the dark-cockpit rule — hidden by the very emptiness the mark exists to
	///     break.
	/// </summary>
	[TestMethod]
	public void A_Room_That_Only_Refused_Is_Not_Folded_Away()
	{
		AreaSnapshot latest = Report(AreaState.AutoVacant) with { IsDark = true };

		BoardLane silent = new("stue", "Stue", "stue", latest, [], null);
		BoardLane refused = silent with { Refusals = [new LaneRefusal(50, "20:30 movement, too bright")] };

		Assert.IsTrue(silent.IsQuiet, "nothing happened in this room");
		Assert.IsFalse(refused.IsQuiet, "something happened and the engine decided against it");
	}

	/// <summary>
	///     The warning dim leads: it is the only exception with a deadline measured in seconds. The rest are
	///     standing conditions, named alphabetically so the tray does not reshuffle on every tick.
	/// </summary>
	[TestMethod]
	public void The_Warning_Dim_Leads_The_Tray()
	{
		IReadOnlyList<AreaSnapshot> tray = BoardView.Exceptions(
		[
			Report(AreaState.OverriddenOn, "Kjokken"),
			Report(AreaState.AutoVacant, "Gang"),
			Report(AreaState.SuppressedOff, "Bod"),
			Report(AreaState.PreOff, "Vaskerom")
		]);

		Assert.AreEqual(3, tray.Count, "the watching room is not in the tray");
		Assert.AreEqual("Vaskerom", tray[0].AreaName, "the warning dim comes first however late its name sorts");
		Assert.AreEqual("Bod", tray[1].AreaName);
		Assert.AreEqual("Kjokken", tray[2].AreaName);
	}

	/// <summary>A chip says what is happening and when it ends — the second half is the whole reason to look.</summary>
	[TestMethod]
	public void A_Tray_Chip_Says_When_It_Ends()
	{
		DateTimeOffset resumes = At(22, 54);

		string line = BoardView.ExceptionLine(Report(AreaState.OverriddenOn, nextChangeAt: resumes), Now);

		StringAssert.Contains(line, "set manually");
		StringAssert.Contains(line, BoardView.Clock(resumes));
	}

	/// <summary>The one deadline worth counting is counted, because it is measured in seconds.</summary>
	[TestMethod]
	public void The_Warning_Dim_Is_Counted_In_Seconds()
	{
		string line = BoardView.ExceptionLine(Report(AreaState.PreOff, nextChangeAt: Now.AddSeconds(18)), Now);

		StringAssert.Contains(line, "in 18 s");
		StringAssert.Contains(line, "unless someone moves");
	}

	/// <summary>
	///     A room with no armed deadline is a real state, not a fault: an override with no expiry stands until
	///     somebody resumes it. Every branch has to finish its sentence without one.
	/// </summary>
	[TestMethod]
	public void Every_Exception_Has_Words_For_Having_No_Deadline()
	{
		foreach (AreaState state in (AreaState[])[AreaState.PreOff, AreaState.OverriddenOn, AreaState.SuppressedOff, AreaState.SceneHold])
		{
			string line = BoardView.ExceptionLine(Report(state), Now);

			Assert.IsFalse(string.IsNullOrWhiteSpace(line), $"{state} said nothing");
			Assert.IsFalse(line.EndsWith("at ", StringComparison.Ordinal), $"{state} trailed off waiting for a time");
		}
	}

	/// <summary>The tray's closing line is the entire reassurance budget, and it has to count correctly.</summary>
	[TestMethod]
	public void The_Quiet_Line_Counts_What_The_Tray_Did_Not_Name()
	{
		Assert.AreEqual("The other 12 rooms are doing what the schedule says.", BoardView.QuietRoomsLine(15, 3));
		Assert.AreEqual("The other room is doing what the schedule says.", BoardView.QuietRoomsLine(2, 1));
		Assert.AreEqual("All 15 rooms are doing what the schedule says.", BoardView.QuietRoomsLine(15, 0));
		Assert.AreEqual("The one room switched on is doing what the schedule says.", BoardView.QuietRoomsLine(1, 0));
		Assert.AreEqual("That is the only room switched on.", BoardView.QuietRoomsLine(1, 1));
	}

	// ===================== seventeen lanes =====================

	/// <summary>
	///     Below the budget nothing folds, whatever the rooms are doing. A house with two rooms switched on has
	///     to look like a board about two rooms — an empty track is the answer there, and hiding both behind a
	///     footnote would read as a fault rather than as quiet.
	/// </summary>
	[TestMethod]
	public void Below_The_Budget_Every_Room_Keeps_Its_Lane()
	{
		IReadOnlyList<BoardLane> lanes = [Quiet("Stue"), Quiet("Gang")];

		(IReadOnlyList<BoardLane> busy, IReadOnlyList<BoardLane> quiet) = BoardView.Partition(lanes);

		Assert.AreEqual(2, busy.Count);
		Assert.AreEqual(0, quiet.Count);
	}

	/// <summary>
	///     Past the budget the quiet rooms fold onto the shelf. This is the answer to seventeen lanes: fourteen
	///     rooms cost fourteen chips instead of fourteen rows, and the three worth reading are on screen together.
	/// </summary>
	[TestMethod]
	public void Above_The_Budget_The_Quiet_Rooms_Fold()
	{
		List<BoardLane> lanes = [];
		for (int index = 0; index < 14; index++)
			lanes.Add(Quiet($"Rom {index}"));

		lanes.Add(Busy("Stue"));
		lanes.Add(Exception("Bad", AreaState.PreOff));
		lanes.Add(Awaiting("Uteplass"));

		(IReadOnlyList<BoardLane> busy, IReadOnlyList<BoardLane> quiet) = BoardView.Partition(lanes);

		Assert.AreEqual(3, busy.Count);
		Assert.AreEqual(14, quiet.Count);
		CollectionAssert.AreEqual(
			new[] { "Stue", "Bad", "Uteplass" },
			busy.Select(lane => lane.Name).ToArray(),
			"the busy lanes keep the order they were given");
	}

	/// <summary>
	///     Three ways to earn a lane, and they are all different: something happened, something is about to, or
	///     something is wrong. A room with no history at all but a dotted mark ahead of it still has news.
	/// </summary>
	[TestMethod]
	public void History_A_Future_Mark_Or_A_Fault_Each_Earn_A_Lane()
	{
		Assert.IsFalse(Busy("Stue").IsQuiet, "it did something");
		Assert.IsFalse(Awaiting("Uteplass").IsQuiet, "it is about to do something");
		Assert.IsFalse(Exception("Bad", AreaState.PreOff).IsQuiet, "something is happening now");
		Assert.IsTrue(Quiet("Gang").IsQuiet);
	}

	// ===================== the schedule band =====================

	/// <summary>The band is the day's periods, left to right, clipped to the board.</summary>
	[TestMethod]
	public void The_Band_Is_The_Day_The_Engine_Is_Running()
	{
		IReadOnlyList<BandSegment> band = BoardView.Band(
			[
				new TimePeriodConfig { Name = "Day", Start = "06:00", ColorTempKelvin = 4300 },
				new TimePeriodConfig { Name = "Evening", Start = "20:45", ColorTempKelvin = 2700 },
				new TimePeriodConfig { Name = "Night", Start = "22:30", ColorTempKelvin = 2200 }
			],
			SunTimes.Unknown,
			Window(),
			Oslo);

		CollectionAssert.AreEqual(new[] { "Day", "Evening", "Night" }, band.Select(segment => segment.Name).ToArray());
		Assert.AreEqual(0, band[0].LeftPct, 1e-9, "the day period was already running when the board began");
		Assert.AreEqual(100, band[^1].LeftPct + band[^1].WidthPct, 1e-6, "and the night one runs off the right edge");
		Assert.AreEqual(2200, band[^1].Kelvin);
	}

	/// <summary>
	///     A sun-anchored boundary the day cannot place is left out, exactly as the engine's own calculator drops
	///     it. A band showing a period the engine is not running would be worse than a gap.
	/// </summary>
	[TestMethod]
	public void A_Boundary_The_Day_Cannot_Place_Is_Left_Out()
	{
		IReadOnlyList<BandSegment> band = BoardView.Band(
			[
				new TimePeriodConfig { Name = "Evening", Start = "sunset-01:00", ColorTempKelvin = 2700 },
				new TimePeriodConfig { Name = "Night", Start = "22:30", ColorTempKelvin = 2200 }
			],
			SunTimes.Unknown,
			Window(),
			Oslo);

		Assert.IsTrue(band.All(segment => segment.Name == "Night"), "polar night left only the fixed boundary");
	}

	/// <summary>An unparseable start is left out too, rather than placed at midnight and quietly wrong.</summary>
	[TestMethod]
	public void An_Unparseable_Start_Is_Left_Out()
	{
		IReadOnlyList<BandSegment> band = BoardView.Band(
			[new TimePeriodConfig { Name = "Broken", Start = "half past whenever", ColorTempKelvin = 2700 }],
			SunTimes.Unknown,
			Window(),
			Oslo);

		Assert.AreEqual(0, band.Count);
	}

	/// <summary>
	///     The window ends at midnight, so a night period that starts on the far side of it has to place. The
	///     boundaries are laid out for the day before, of and after precisely so this works.
	/// </summary>
	[TestMethod]
	public void A_Window_That_Reaches_Midnight_Still_Bands()
	{
		IReadOnlyList<BandSegment> band = BoardView.Band(
			[
				new TimePeriodConfig { Name = "Evening", Start = "20:45", ColorTempKelvin = 2700 },
				new TimePeriodConfig { Name = "Small hours", Start = "01:00", ColorTempKelvin = 2200 }
			],
			SunTimes.Unknown,
			Window(),
			Oslo);

		Assert.AreEqual(2, band.Count);
		Assert.AreEqual("Small hours", band[0].Name, "the board opens inside the previous night's period");
		Assert.AreEqual("Evening", band[1].Name);
	}

	// ===================== the schedule band across a clock change =====================

	/// <summary>
	///     <b>Spring forward.</b> Norway's clocks jump from 02:00 to 03:00 on 29 March 2026, so the six hours the
	///     board shows are not all the same distance from UTC. The band used to place every boundary at whatever
	///     offset the window happened to open on, which put a boundary on the far side of the change an hour out.
	/// </summary>
	/// <remarks>
	///     The board opens at 00:00 winter time and runs six hours to 06:00 summer time. "01:30" is still winter
	///     time, so it belongs ninety minutes in — a quarter of the way along. Placed at the window's own summer
	///     offset it landed at 8.3%, an hour early.
	/// </remarks>
	[TestMethod]
	public void A_Boundary_Before_The_Spring_Change_Keeps_Its_Wall_Clock_Time()
	{
		// 05:00 summer time, two hours after the clocks moved: the window reaches back across the change.
		BoardWindow window = BoardWindow.Around(
			new DateTimeOffset(2026, 3, 29, 5, 0, 0, TimeSpan.FromHours(2)), BoardView.LookBack, BoardView.LookAhead);

		IReadOnlyList<BandSegment> band = BoardView.Band(
			[
				new TimePeriodConfig { Name = "Night", Start = "00:00", ColorTempKelvin = 2200 },
				new TimePeriodConfig { Name = "Small hours", Start = "01:30", ColorTempKelvin = 2500 },
				new TimePeriodConfig { Name = "Morning", Start = "05:00", ColorTempKelvin = 4300 }
			],
			SunTimes.Unknown,
			window,
			Oslo);

		CollectionAssert.AreEqual(
			new[] { "Night", "Small hours", "Morning" }, band.Select(segment => segment.Name).ToArray());

		Assert.AreEqual(100.0 * 1.5 / 6, band[1].LeftPct, 1e-6, "01:30 is winter time, ninety minutes into the board");
		Assert.AreEqual(100.0 * 4 / 6, band[2].LeftPct, 1e-6, "05:00 is summer time, four hours into the board");
	}

	/// <summary>
	///     <b>Fall back.</b> On 25 October 2026 the clocks go from 03:00 back to 02:00, and the same fault appears
	///     with the sign reversed: a boundary before the change belongs an hour <i>earlier</i> on the board than the
	///     window's winter offset put it.
	/// </summary>
	/// <remarks>
	///     The board opens at 01:00 summer time and runs six hours to 06:00 winter time. "01:30" is thirty minutes
	///     in; the old arithmetic drew it at a quarter of the way along.
	/// </remarks>
	[TestMethod]
	public void A_Boundary_Before_The_Autumn_Change_Keeps_Its_Wall_Clock_Time()
	{
		// 04:00 winter time, two hours after the clocks went back.
		BoardWindow window = BoardWindow.Around(
			new DateTimeOffset(2026, 10, 25, 4, 0, 0, TimeSpan.FromHours(1)), BoardView.LookBack, BoardView.LookAhead);

		IReadOnlyList<BandSegment> band = BoardView.Band(
			[
				new TimePeriodConfig { Name = "Night", Start = "00:00", ColorTempKelvin = 2200 },
				new TimePeriodConfig { Name = "Small hours", Start = "01:30", ColorTempKelvin = 2500 },
				new TimePeriodConfig { Name = "Morning", Start = "04:00", ColorTempKelvin = 4300 }
			],
			SunTimes.Unknown,
			window,
			Oslo);

		CollectionAssert.AreEqual(
			new[] { "Night", "Small hours", "Morning" }, band.Select(segment => segment.Name).ToArray());

		Assert.AreEqual(100.0 * 0.5 / 6, band[1].LeftPct, 1e-6, "01:30 is summer time, half an hour into the board");
		Assert.AreEqual(100.0 * 4 / 6, band[2].LeftPct, 1e-6, "04:00 is winter time, four hours into the board");
	}

	/// <summary>A sliver of a period is drawn but not named: the name would be wider than the segment it labels.</summary>
	[TestMethod]
	public void A_Sliver_Of_A_Period_Is_Not_Named()
	{
		Assert.IsFalse(BoardView.IsLabelled(new BandSegment("Morning", 0, 2, 4300)));
		Assert.IsTrue(BoardView.IsLabelled(new BandSegment("Evening", 0, 25, 2700)));
	}

	// ===================== warmth =====================

	/// <summary>
	///     One conversion for the chip, the lamp and the board block. A warm light is redder than a cool one, and
	///     the clamp holds at both ends so no lamp can drive the curve off its own range.
	/// </summary>
	[TestMethod]
	public void Warmth_Is_One_Conversion_And_It_Is_Clamped()
	{
		Assert.AreEqual(KelvinColour.Css(KelvinColour.Warmest), KelvinColour.Css(500), "below the range clamps to the warm end");
		Assert.AreEqual(KelvinColour.Css(KelvinColour.Coolest), KelvinColour.Css(20000), "above it clamps to the cool end");
		Assert.AreNotEqual(KelvinColour.Css(2200), KelvinColour.Css(4500), "a night dim and a midday white are different rooms");
		StringAssert.StartsWith(KelvinColour.Css(2700), "rgb(255, ");
	}

	private static BoardLane Quiet(string name) =>
		new(name, name, name, Report(AreaState.AutoVacant, name), [], null);

	private static BoardLane Busy(string name) =>
		new(name, name, name, Report(AreaState.AutoVacant, name), [new LaneBlock(LaneBlockKind.Lit, 10, 5, 2700)], null);

	private static BoardLane Awaiting(string name) =>
		new(name, name, name, Report(AreaState.AutoVacant, name), [], new LaneMark(80, "22:05 dim"));

	private static BoardLane Exception(string name, AreaState state) =>
		new(name, name, name, Report(state, name), [], null);
}
