using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     The stretch of time the board draws, and the arithmetic that turns an instant into a position on it.
/// </summary>
/// <remarks>
///     Snapped to whole hours at both ends so the axis carries labelled ticks and the tracks can draw their
///     gridlines as one repeating background rather than as a hairline element per room per hour. The window
///     therefore steps once an hour instead of sliding continuously, which is also what stops the labels
///     shuffling under somebody's eyes while they read.
/// </remarks>
/// <param name="Start">The oldest instant on the board, on the hour.</param>
/// <param name="End">The newest, on the hour — in the future, because the board shows what is coming.</param>
public sealed record BoardWindow(DateTimeOffset Start, DateTimeOffset End)
{
	/// <summary>The window that ends <paramref name="ahead"/> after <paramref name="now"/> and begins <paramref name="back"/> before it.</summary>
	/// <param name="now">The reader's present.</param>
	/// <param name="back">How much past to show.</param>
	/// <param name="ahead">How much future to show.</param>
	public static BoardWindow Around(DateTimeOffset now, TimeSpan back, TimeSpan ahead)
	{
		DateTimeOffset start = FloorToHour(now - back);
		DateTimeOffset end = CeilingToHour(now + ahead);

		// A degenerate window would divide by zero below. Cannot happen with sane arguments; cheap to rule out.
		return new BoardWindow(start, end > start ? end : start.AddHours(1));
	}

	/// <summary>How many whole hours the window spans — the number of columns the gridlines make.</summary>
	public int Hours => (int)Math.Round((End - Start).TotalHours);

	/// <summary>The hour boundaries, first and last inclusive: the axis's labelled ticks.</summary>
	public IReadOnlyList<DateTimeOffset> Ticks
	{
		get
		{
			List<DateTimeOffset> ticks = new(Hours + 1);
			for (DateTimeOffset at = Start; at <= End; at = at.AddHours(1))
				ticks.Add(at);

			return ticks;
		}
	}

	/// <summary>Where <paramref name="at"/> falls, as a percentage of the window's width. Outside it, outside 0–100.</summary>
	public double PercentAt(DateTimeOffset at) => (at - Start) / (End - Start) * 100.0;

	/// <summary>Whether <paramref name="at"/> is on the board at all.</summary>
	public bool Contains(DateTimeOffset at) => at >= Start && at <= End;

	private static DateTimeOffset FloorToHour(DateTimeOffset at) =>
		new(at.Year, at.Month, at.Day, at.Hour, 0, 0, at.Offset);

	private static DateTimeOffset CeilingToHour(DateTimeOffset at)
	{
		DateTimeOffset floored = FloorToHour(at);
		return floored == at ? at : floored.AddHours(1);
	}
}

/// <summary>What a stretch of a room's past is drawn as. Only states worth a mark have one.</summary>
public enum LaneBlockKind
{
	/// <summary>The engine was holding the lights on, in the warmth it commanded.</summary>
	Lit,

	/// <summary>The warning dim — the last call before the lights go out.</summary>
	Dimming,

	/// <summary>Somebody's own levels stood, by hand or by a scene.</summary>
	Hand,

	/// <summary>Switched off by hand, with movement deliberately ignored.</summary>
	Held
}

/// <summary>One coloured stretch of a lane, positioned as percentages of the board's width.</summary>
/// <param name="Kind">What was happening.</param>
/// <param name="LeftPct">Where it starts.</param>
/// <param name="WidthPct">How wide it is — never narrower than a stretch a person can see.</param>
/// <param name="Kelvin">The warmth the engine commanded, for a lit stretch; <c>null</c> otherwise.</param>
public sealed record LaneBlock(LaneBlockKind Kind, double LeftPct, double WidthPct, int? Kelvin);

/// <summary>A dotted mark in a lane's future: when the room's armed timer will act, and what it will do.</summary>
/// <param name="LeftPct">Where it falls on the board.</param>
/// <param name="Label">The time and the verb — <c>22:20 auto resumes</c>.</param>
public sealed record LaneMark(double LeftPct, string Label);

/// <summary>
///     A moment in a lane's past when the room saw movement and did not light: the instant, and the gate that
///     turned it down.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this is a third shape rather than a fifth <see cref="LaneBlockKind"/>.</b> A block is a stretch —
///         it says the room was in some state from one time to another. A refusal has no duration: the room was
///         already dark before it and stays dark after, and drawing it as a stretch would claim a state that never
///         existed. It is not a <see cref="LaneMark"/> either, because those are the future.
///     </para>
///     <para>
///         <b>Why it earns a place on the board at all.</b> "Why didn't the light come on?" is the question this
///         application exists to answer, and until now the lane for a room that refused looked exactly like the
///         lane for a room nobody walked through — an empty track. The refusal was in the log, but only for
///         somebody who already suspected there was something to look for. A mark makes the empty track say
///         <em>something happened here and I decided against it</em>, which is what sends a reader to the row that
///         explains it.
///     </para>
/// </remarks>
/// <param name="LeftPct">Where the refusal falls on the board.</param>
/// <param name="Label">What it was and why — <c>18:04 movement, too bright</c>.</param>
public sealed record LaneRefusal(double LeftPct, string Label);

/// <summary>One room's row on the board: what it is doing now, what it did, and what happens next.</summary>
/// <param name="Key">The stable identity — the area id, or the display name when there is none.</param>
/// <param name="Name">The room's name, as the lane's label.</param>
/// <param name="AreaId">Where the room's own page is, or <c>null</c> when the room has no area to link to.</param>
/// <param name="Latest">The newest report from the room.</param>
/// <param name="Blocks">Its recent past, oldest first.</param>
/// <param name="Next">The one dotted mark ahead of the now-line, or <c>null</c> when nothing is armed.</param>
/// <param name="Refusals">
///     Moments it saw movement and did not light, oldest first. Defaulted so the lane can be built without
///     them; the board passes <see cref="BoardView.Refusals"/> and a caller that does not care may leave it.
/// </param>
public sealed record BoardLane(
	string Key,
	string Name,
	string? AreaId,
	AreaSnapshot Latest,
	IReadOnlyList<LaneBlock> Blocks,
	LaneMark? Next,
	IReadOnlyList<LaneRefusal>? Refusals = null)
{
	/// <summary>
	///     Whether this room has nothing to say: no past worth drawing, nothing armed, nothing wrong.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The whole dark-cockpit rule reduces to this property. A quiet room's lane is an empty track, and
	///         fourteen empty tracks are a wall of nothing that the three rooms worth reading have to be found in.
	///     </para>
	///     <para>
	///         <b>A refused movement makes a room un-quiet.</b> It draws no stretch and arms no timer, so without
	///         this it would hide behind the same emptiness as a room nobody entered — and those two are precisely
	///         the pair a reader comes to the board to tell apart.
	///     </para>
	/// </remarks>
	public bool IsQuiet =>
		Blocks.Count == 0 && Next is null && Refusals is null or { Count: 0 } && !BoardView.IsException(Latest);
}

/// <summary>One period of the day's schedule, drawn as a band above the lanes.</summary>
/// <param name="Name">The period's configured name.</param>
/// <param name="LeftPct">Where it begins on the board.</param>
/// <param name="WidthPct">How much of the board it covers.</param>
/// <param name="Kelvin">Its target warmth, which is what colours the band.</param>
public sealed record BandSegment(string Name, double LeftPct, double WidthPct, int Kelvin);

/// <summary>
///     Everything the board decides, as pure functions: where a span of time lands, which rooms are exceptions,
///     which lanes are worth a row of their own, and what each of those says in words.
/// </summary>
/// <remarks>
///     <para>
///         This repo has no Razor render harness and deliberately does not gain one, so the board's judgement
///         lives here and the markup only arranges it. The judgement is where the board can be wrong in ways a
///         screenshot would not catch: a block off by a percent is invisible, a room wrongly called quiet is a
///         room the owner never sees, and a countdown that says the wrong minute is a confident wrong answer.
///     </para>
///     <para>
///         Read-only throughout. Nothing here touches the configuration document, and nothing on the board it
///         feeds writes one either.
///     </para>
/// </remarks>
public static class BoardView
{
	/// <summary>How much past the board shows.</summary>
	public static readonly TimeSpan LookBack = TimeSpan.FromHours(4);

	/// <summary>How much future the board shows — enough to hold the next period boundary and a vacancy timeout.</summary>
	public static readonly TimeSpan LookAhead = TimeSpan.FromHours(2);

	/// <summary>
	///     How many rooms may each keep a lane of their own before the quiet ones are folded onto a shelf.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Six, because six is the number the design was drawn at and the number an eye reads as a group. Below
	///         it an empty track is information — it says "this room did nothing", which is the whole answer in a
	///         two-room house and is why the first render an owner sees must not be a board with two lanes hidden
	///         on a shelf.
	///     </para>
	///     <para>
	///         Above it the same empty track is noise: seventeen rows, fourteen of them blank, is the scrolling
	///         wall the swim-lane design is prone to. So past six the quiet rooms become chips, and the board
	///         beneath the tray is only the rooms that did something.
	///     </para>
	/// </remarks>
	public const int LaneBudget = 6;

	/// <summary>How many log lines the board carries before handing over to the Activity page.</summary>
	public const int LogPreview = 12;

	/// <summary>The narrowest a block may be drawn, so a five-second visit is still a mark and not a hairline.</summary>
	private const double MinBlockPct = 0.4;

	/// <summary>
	///     How far apart two refusals for the same reason must be before both are drawn.
	/// </summary>
	/// <remarks>
	///     One per cent of a seven-hour board is about four minutes, and on a 320 px phone track it is a little
	///     over three pixels — enough for two 2 px ticks to read as two. Chosen in <i>screen</i> terms rather than
	///     clock terms because the fault it answers is visual: reports minutes apart are perfectly meaningful, they
	///     simply cannot be drawn apart at this scale, and every one of them is still a row in the log.
	/// </remarks>
	private const double MinMarkGapPct = 1.0;

	/// <summary>The narrowest band segment that gets its name written in it.</summary>
	private const double MinLabelledBandPct = 9;

	// ===================== the lanes =====================

	/// <summary>
	///     One room's past, as blocks clipped to the board's window.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Each report covers the stretch from itself to the next one, and the newest covers the stretch up to
	///         <paramref name="now"/> — the engine publishes on transitions, so a report <i>is</i> a statement that
	///         the room stayed that way until it said otherwise. Nothing is drawn before the oldest report the log
	///         still holds: what the room was doing before that is genuinely unknown, and a block reaching back to
	///         the board's left edge would be an invention.
	///     </para>
	///     <para>
	///         Adjacent stretches that say the same thing are merged before they become percentages, so a room the
	///         engine retuned to the same warmth twice is one block rather than two with a hairline seam between
	///         them, and the minimum width is applied once to the merged whole rather than to each half.
	///     </para>
	/// </remarks>
	/// <param name="entries">The log's entries for one room, in any order.</param>
	/// <param name="window">The board's window.</param>
	/// <param name="now">The reader's present, which is where the newest report's stretch ends.</param>
	/// <returns>The blocks, oldest first. Empty when the room did nothing worth drawing.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static IReadOnlyList<LaneBlock> Blocks(IEnumerable<ActivityEntry> entries, BoardWindow window, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(entries);
		ArgumentNullException.ThrowIfNull(window);

		List<ActivityEntry> ordered = [.. entries.OrderBy(entry => entry.At).ThenBy(entry => entry.Sequence)];
		List<(LaneBlockKind Kind, int? Kelvin, DateTimeOffset From, DateTimeOffset To)> spans = [];

		for (int index = 0; index < ordered.Count; index++)
		{
			AreaSnapshot snapshot = ordered[index].Snapshot;
			if (KindOf(snapshot.State) is not { } kind)
				continue;

			DateTimeOffset from = ordered[index].At;
			DateTimeOffset to = index + 1 < ordered.Count ? ordered[index + 1].At : now;
			if (to < from)
				to = from;

			if (to <= window.Start || from >= window.End)
				continue;

			DateTimeOffset clippedFrom = from < window.Start ? window.Start : from;
			DateTimeOffset clippedTo = to > window.End ? window.End : to;
			int? kelvin = kind == LaneBlockKind.Lit ? snapshot.ColorTempKelvin : null;

			if (spans.Count > 0
				&& spans[^1].Kind == kind
				&& spans[^1].Kelvin == kelvin
				&& spans[^1].To >= clippedFrom)
			{
				spans[^1] = spans[^1] with { To = clippedTo };
				continue;
			}

			spans.Add((kind, kelvin, clippedFrom, clippedTo));
		}

		return
		[
			.. spans.Select(span =>
			{
				double left = Math.Clamp(window.PercentAt(span.From), 0, 100 - MinBlockPct);
				double width = Math.Max(MinBlockPct, window.PercentAt(span.To) - left);

				return new LaneBlock(span.Kind, left, Math.Min(width, 100 - left), span.Kelvin);
			})
		];
	}

	/// <summary>
	///     The moments this room saw movement and did not light, as marks on its lane.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>The reason the board carries these at all.</b> A room that refuses draws no stretch — it was dark
	///         before the movement and dark after — so its lane was identical to the lane of a room nobody walked
	///         through. Those are the two cases a reader is trying to tell apart when they come to the board asking
	///         why a light did not come on, and the board answered both with the same emptiness.
	///     </para>
	///     <para>
	///         <b>The reports are already thinned by the engine</b>, which publishes one per <i>change of the
	///         refusing gate</i> rather than one per movement — so somebody pacing under an unchanged block is one
	///         mark, not forty. That is the same property <see cref="ActivityView.IsDeclinedMotion"/> relies on, and
	///         it is why this needs no de-duplication of its own beyond the identical-instant guard below.
	///     </para>
	///     <para>
	///         The test for a refusal is <see cref="ActivityView.IsDeclinedMotion"/> itself, not a copy of it. Two
	///         copies would let the timeline and the log disagree about whether a movement was turned down, and a
	///         mark with no row to explain it is worse than no mark.
	///     </para>
	/// </remarks>
	/// <param name="entries">The log's entries for one room, in any order.</param>
	/// <param name="window">The board's window; a refusal outside it is not drawn.</param>
	/// <returns>The marks, oldest first. Empty when the room turned nothing down.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static IReadOnlyList<LaneRefusal> Refusals(IEnumerable<ActivityEntry> entries, BoardWindow window)
	{
		ArgumentNullException.ThrowIfNull(entries);
		ArgumentNullException.ThrowIfNull(window);

		List<LaneRefusal> marks = [];
		double? previousPercent = null;

		foreach (ActivityEntry entry in entries
			.Where(entry => ActivityView.IsDeclinedMotion(entry.Snapshot))
			.Where(entry => IsAboutThisRoom(entry.Snapshot))
			.Where(entry => entry.At >= window.Start && entry.At <= window.End)
			.OrderBy(entry => entry.At)
			.ThenBy(entry => entry.Sequence))
		{
			double percent = Math.Clamp(window.PercentAt(entry.At), 0, 100);

			// Close enough that the two ticks would touch, whatever turned each one down. The engine publishes
			// once per CHANGE of gate on the ordinary path, but the suppressed-off path republishes on every
			// movement — a bedroom sensor re-firing under a hand-set off produces dozens of reports minutes
			// apart, which at a phone's scale land inside a pixel of one another and paint a continuous smear.
			//
			// Deliberately not keyed on the gate. Two marks a pixel apart cannot be told apart by a reader even
			// when they mean different things, so keeping the second buys an unreadable tick and loses the
			// first's hover to it. Every report is still its own row in the log, with its own reason.
			if (previousPercent is { } last && percent - last < MinMarkGapPct)
				continue;

			previousPercent = percent;
			marks.Add(new LaneRefusal(percent, $"{Clock(entry.At)} movement, {ActivityView.RefusalReason(entry.Snapshot)}"));
		}

		return marks;
	}

	/// <summary>
	///     Whether a refusal is this room's own business, or the house's — already said once, above the lanes.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>Without this the board becomes the wall it was built to avoid.</b> The master switch and an empty
	///         house turn every room down at once, so a night with the switch off gave all seventeen lanes the same
	///         tick reading "the master switch is off" — and since a refusal makes a lane un-quiet, the quiet shelf
	///         emptied and every room claimed a row. Seventeen lanes repeating one house fact is precisely what
	///         <see cref="BoardLane.IsQuiet"/> exists to prevent.
	///     </para>
	///     <para>
	///         It is the same rule <see cref="IsException"/> already applies to the tray, for the same reason
	///         recorded there: these conditions are announced house-wide, and repeating them once per room buries
	///         the rooms that need reading. The house bar says the switch is off in one place, at the top.
	///     </para>
	///     <para>
	///         This is a judgement about which refusals earn a <i>mark</i>, not a second opinion about what a
	///         refusal is — <see cref="ActivityView.IsDeclinedMotion"/> remains the only answer to that, and every
	///         one of these still has its row in the log with its reason.
	///     </para>
	/// </remarks>
	private static bool IsAboutThisRoom(AreaSnapshot snapshot) =>
		snapshot.AutoOnBlockedBy is not (AutoOnBlock.KillSwitch or AutoOnBlock.Away);

	/// <summary>
	///     The one dotted mark ahead of the now-line: when this room's armed timer fires, and what it will do.
	/// </summary>
	/// <remarks>
	///     The verb comes from the state rather than from the snapshot, because the snapshot deliberately does not
	///     carry one — <c>NextChangeAt</c> is documented as "what it will do is implied by State". A state with no
	///     armed timer, or one whose deadline has already passed unheard, gets no mark: a dotted line in the past
	///     would read as a prediction the board got wrong.
	/// </remarks>
	/// <param name="snapshot">The room's newest report.</param>
	/// <param name="window">The board's window; a deadline beyond its right edge is not drawn.</param>
	/// <param name="now">
	///     The reader's present. A deadline already behind it is not drawn: the window reaches four hours back, so
	///     without this a stale snapshot — every snapshot round-trips through Home Assistant, so a connection blip
	///     is enough — put a confident "20:57 auto resumes" to the <i>left</i> of the now-line, which is the one
	///     thing the remark above says this must never do.
	/// </param>
	/// <returns>The mark, or <c>null</c>.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static LaneMark? NextMark(AreaSnapshot snapshot, BoardWindow window, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(window);

		if (snapshot.NextChangeAt is not { } at || at < now || !window.Contains(at))
			return null;

		return NextWord(snapshot.State) is { } word
			? new LaneMark(window.PercentAt(at), $"{Clock(at)} {word}")
			: null;
	}

	/// <summary>
	///     Splits the lanes into the ones that earn a row and the ones that become a chip on the quiet shelf.
	/// </summary>
	/// <remarks>
	///     Below <see cref="LaneBudget"/> nothing is folded, whatever the rooms are doing. A house with two rooms
	///     switched on has to look like a board about two rooms, not like a board with nothing on it and a footnote.
	/// </remarks>
	/// <param name="lanes">Every visible room's lane, in display order.</param>
	/// <returns>The lanes that keep a row, and the ones that do not — each in the order given.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="lanes"/> is <c>null</c>.</exception>
	public static (IReadOnlyList<BoardLane> Busy, IReadOnlyList<BoardLane> Quiet) Partition(IReadOnlyList<BoardLane> lanes)
	{
		ArgumentNullException.ThrowIfNull(lanes);

		if (lanes.Count <= LaneBudget)
			return (lanes, []);

		return ([.. lanes.Where(lane => !lane.IsQuiet)], [.. lanes.Where(lane => lane.IsQuiet)]);
	}

	// ===================== the exception tray =====================

	/// <summary>
	///     Whether a state is an exception — something a person would want hoisted out of the board and named.
	/// </summary>
	/// <remarks>
	///     Exactly the states where the engine is not simply following the schedule: the warning dim, which is
	///     about to act, and the three ways somebody else's decision is standing. <see cref="AreaState.AutoVacant"/>
	///     and <see cref="AreaState.Away"/> are the nominal cases and are the whole point of the tray existing —
	///     a tray that listed them would be the card grid again.
	/// </remarks>
	/// <param name="state">The room's last published state.</param>
	public static bool IsException(AreaState state) =>
		state is AreaState.PreOff or AreaState.OverriddenOn or AreaState.SuppressedOff or AreaState.SceneHold;

	/// <summary>
	///     Whether a whole report is an exception — the question the tray and the quiet split both ask.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Today this is its state and nothing else, so it forwards. It exists as a separate overload because
	///         the interesting case is about to stop being a state: a room that is dark, vacant and <i>blocked</i>
	///         from switching on — by a sleeping house, or by a named entity such as a television — is exactly the
	///         "why didn't that light come on" the whole page exists to answer, and it is a nominal
	///         <see cref="AreaState.AutoVacant"/>.
	///     </para>
	///     <para>
	///         So a blocked room is hoisted too, on top of the states. Only the two blocks that can surprise
	///         somebody qualify: the engine refuses in that order — kill switch, disabled, away, sleep, entity —
	///         so <see cref="AutoOnBlock.Sleep"/> and <see cref="AutoOnBlock.EntityOn"/> arise <i>only</i> where
	///         the room would otherwise have lit, which is exactly what makes them worth saying. The earlier
	///         refusals are already announced house-wide by the master switch and the mode, and a tray repeating
	///         them once per room would bury the rooms that need reading.
	///     </para>
	///     <para>
	///         Gated on <see cref="AreaSnapshot.IsDark"/> because the block is only news when light was wanted:
	///         those two are judged before the darkness gate, so a sleeping house reports
	///         <see cref="AutoOnBlock.Sleep"/> at noon as readily as at midnight. A <c>null</c> field is an older
	///         report that never carried one and must not be read as "nothing was blocking" — it simply fails the
	///         test, leaving the room to be judged on its state as before.
	///     </para>
	/// </remarks>
	/// <param name="snapshot">The room's newest report.</param>
	/// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <c>null</c>.</exception>
	public static bool IsException(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return IsException(snapshot.State) || IsBlockedFromLighting(snapshot);
	}

	/// <summary>
	///     Whether the room is dark and waiting, and would light on movement but for a block worth naming.
	/// </summary>
	/// <param name="snapshot">The room's newest report.</param>
	internal static bool IsBlockedFromLighting(AreaSnapshot snapshot) =>
		snapshot.State is AreaState.AutoVacant
		&& snapshot.IsDark is true
		&& snapshot.AutoOnBlockedBy is AutoOnBlock.Sleep or AutoOnBlock.EntityOn;

	/// <summary>
	///     The rooms the tray carries, most urgent first.
	/// </summary>
	/// <remarks>
	///     The warning dim leads because it is the only one with a deadline measured in seconds; the rest are
	///     standing conditions and are named alphabetically so the tray does not reshuffle itself on every tick.
	/// </remarks>
	/// <param name="snapshots">The reports from rooms the board is showing.</param>
	/// <exception cref="ArgumentNullException"><paramref name="snapshots"/> is <c>null</c>.</exception>
	public static IReadOnlyList<AreaSnapshot> Exceptions(IEnumerable<AreaSnapshot> snapshots)
	{
		ArgumentNullException.ThrowIfNull(snapshots);

		return
		[
			.. snapshots
				.Where(IsException)
				.OrderBy(snapshot => snapshot.State == AreaState.PreOff ? 0 : 1)
				.ThenBy(snapshot => snapshot.AreaName, StringComparer.CurrentCulture)
		];
	}

	/// <summary>
	///     What a tray chip says after the room's name: what is happening, and when it ends.
	/// </summary>
	/// <remarks>
	///     Every branch has a wording for a room with no armed deadline, because that is a real state and not an
	///     error — an override with no expiry configured stands until somebody resumes it, and a chip that trailed
	///     off after "auto resumes" would leave the reader waiting for a time that is never coming.
	/// </remarks>
	/// <param name="snapshot">The room's newest report.</param>
	/// <param name="now">The reader's present, for the one countdown that is worth counting.</param>
	/// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <c>null</c>.</exception>
	public static string ExceptionLine(AreaSnapshot snapshot, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		// Ahead of the switch: a blocked room is a nominal AutoVacant, so the states cannot express it. Worded to
		// match the activity page's line for the same condition — two surfaces answering one question in two
		// vocabularies is how a reader ends up believing they are two different conditions.
		if (IsBlockedFromLighting(snapshot))
		{
			return snapshot.AutoOnBlockedBy is AutoOnBlock.Sleep
				? "dark enough, but the house is asleep — movement won't light the room"
				: snapshot.AutoOnBlockingEntity is { Length: > 0 } blocking
					? $"dark enough, but {blocking} is on — movement won't light the room"
					: "dark enough, but something here is on — movement won't light the room";
		}

		return snapshot.State switch
		{
			AreaState.PreOff => snapshot.NextChangeAt is { } off
				? $"warning dim — lights out {Countdown(off, now)} unless someone moves"
				: "warning dim — the lights go out shortly unless someone moves",

			AreaState.OverriddenOn => snapshot.NextChangeAt is { } resumes
				? $"set manually — automatic control returns at {Clock(resumes)}"
				: "set manually — the engine stands back until somebody resumes it",

			AreaState.SuppressedOff => snapshot.NextChangeAt is { } listens
				? $"off manually — movement is ignored until {Clock(listens)}"
				: "off manually — movement is ignored until the room has been empty long enough",

			AreaState.SceneHold => "held by a scene — the engine stands back until the house leaves this mode",

			_ => StateGlyph.For(snapshot.State).Word
		};
	}

	/// <summary>
	///     The tray's closing line — the entire reassurance budget this design allows itself.
	/// </summary>
	/// <remarks>
	///     Worded against how many rooms the tray already named, so it reads as the remainder of a sentence the
	///     chips started rather than as a second, contradictory count.
	/// </remarks>
	/// <param name="rooms">How many rooms the board is showing.</param>
	/// <param name="exceptions">How many of them the tray named.</param>
	public static string QuietRoomsLine(int rooms, int exceptions)
	{
		int quiet = Math.Max(0, rooms - exceptions);

		if (quiet == 0)
			return exceptions == 1 ? "That is the only room switched on." : "Every room switched on is in the tray.";

		string subject = exceptions == 0
			? quiet switch
			{
				1 => "The one room switched on is",
				2 => "Both rooms are",
				_ => $"All {quiet} rooms are"
			}
			: quiet == 1 ? "The other room is" : $"The other {quiet} rooms are";

		return $"{subject} doing what the schedule says.";
	}

	/// <summary>
	///     What the board's activity summary is showing, out of what it is holding — and what it is holding back.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The summary shows <see cref="ActivityView.SummaryCategories"/>, which is a good deal less than the
	///         Activity page opens with: the routine — movement, ordinary light changes, darkness readings and the
	///         housekeeping — is left out, because the board directly above has already drawn it. That is the whole
	///         point of the filter, but a filter nobody is told about is indistinguishable from reports that were
	///         never recorded, which is the one failure this project treats as worse than showing too much. So the
	///         hidden ones are counted here, and the link beneath the log is where they can be read.
	///     </para>
	///     <para>
	///         <b><paramref name="shown"/> counts rows and <paramref name="kept"/> counts reports, and the line has
	///         to say which is which.</b> A house-wide event that reached the record from six rooms is six reports
	///         and one row, so the summary regularly carries every report it kept in fewer lines than it has
	///         reports. "Newest 11 of 63 reports" would then be a false statement — it invites the subtraction, and
	///         the answer to that subtraction is fifty-two reports that were never left out. So the two counts are
	///         never set against each other: a summary that fits says how many reports it is showing, and one that
	///         has run out of room says how many <i>rows</i> it drew and out of how many reports.
	///     </para>
	/// </remarks>
	/// <param name="held">Every report the log is holding, before any filter.</param>
	/// <param name="kept">How many of them fall into the categories the summary shows.</param>
	/// <param name="shown">How many rows the board actually drew.</param>
	/// <param name="capacity">The log's cap, so the line can say when the oldest reports have started falling off.</param>
	public static string LogFoot(int held, int kept, int shown, int capacity)
	{
		if (held <= 0)
			return "nothing recorded yet";

		int hidden = Math.Max(0, held - kept);

		string lead = shown >= LogPreview
			? $"newest {shown} rows of {Count(kept, "report")}"
			: Count(kept, "report");

		// "0 reports" beside a count of hidden ones reads as a contradiction — the log plainly has something in it.
		// When the filter has taken everything, the hidden count is the whole answer.
		//
		// "everyday", not "background task": since the summary narrowed to SummaryCategories the held-back pile is
		// mostly movement and ordinary light changes, and naming it after the one small category it used to be
		// would undercount what the Activity page has waiting.
		if (hidden > 0)
		{
			lead = kept == 0
				? $"{Count(hidden, "everyday report")} on the Activity page"
				: $"{lead} — {Count(hidden, "everyday report")} on the Activity page";
		}

		return held >= capacity
			? $"{lead}; the most recent {capacity} are kept"
			: lead;
	}

	private static string Count(int count, string singular) => $"{count} {(count == 1 ? singular : singular + "s")}";

	// ===================== the schedule band =====================

	/// <summary>
	///     The day's periods as segments of the board's width.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Boundaries are placed for the day before, the day of and the day after the window's start, then
	///         sorted and walked, which is how a window that straddles midnight gets a band that does too. Periods
	///         whose <c>Start</c> cannot be parsed, or whose sun anchor the day cannot place, are left out — the
	///         same treatment the engine's own <see cref="CircadianCalculator"/> gives them, so the band shows the
	///         table the engine is actually running.
	///     </para>
	///     <para>
	///         <b>Each boundary is placed through the zone, never at the window's own offset.</b> A period's
	///         <c>Start</c> is a wall-clock time, and the offset that turns it into an instant is the one in force
	///         on <i>its</i> day — which is not the window's twice a year. Reading the offset off
	///         <c>window.Start</c> put every boundary on the far side of a clock change exactly an hour out, in
	///         whichever direction the clocks had moved, on the two Sundays a year the household would most notice.
	///         The window itself is untouched: it is a span of absolute time, so its width, its ticks and
	///         <c>PercentAt</c> stay right through a change of its own accord.
	///     </para>
	///     <para>
	///         One day's sun times are used for all three days. Over a six-hour window that is a difference of a
	///         couple of minutes at the edges, and the band is context rather than a claim about a boundary.
	///     </para>
	/// </remarks>
	/// <param name="periods">The configured circadian table.</param>
	/// <param name="sun">The day's sun times, for the sun-anchored boundaries.</param>
	/// <param name="window">The board's window.</param>
	/// <param name="zone">
	///     The household's time zone, which is what turns a period's wall-clock <c>Start</c> into an instant.
	///     Defaults to <see cref="TimeZoneInfo.Local"/> — the same premise the rest of this class makes with
	///     <c>ToLocalTime</c> — and is named explicitly only by tests, which must not depend on the machine they
	///     run on.
	/// </param>
	/// <returns>The segments, left to right. Empty when no period can be placed.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static IReadOnlyList<BandSegment> Band(
		IReadOnlyList<TimePeriodConfig> periods,
		SunTimes sun,
		BoardWindow window,
		TimeZoneInfo? zone = null)
	{
		ArgumentNullException.ThrowIfNull(periods);
		ArgumentNullException.ThrowIfNull(sun);
		ArgumentNullException.ThrowIfNull(window);

		TimeZoneInfo local = zone ?? TimeZoneInfo.Local;

		// The day the window opens on, as the household's clock reads it. Taken through the zone rather than off
		// window.Start.Date: the window carries the offset it was built at, which on the morning of a clock change
		// names the wrong hour and can name the wrong day with it.
		DateTime firstDay = TimeZoneInfo.ConvertTime(window.Start, local).Date;

		List<(DateTimeOffset At, TimePeriodConfig Period)> boundaries = [];

		foreach (TimePeriodConfig period in periods)
		{
			if (!PeriodStart.TryParse(period.Start, out PeriodStart? start) || start!.Resolve(sun) is not { } time)
				continue;

			for (int day = -1; day <= 1; day++)
				boundaries.Add((Instant(firstDay.AddDays(day) + time.ToTimeSpan(), local), period));
		}

		if (boundaries.Count == 0)
			return [];

		boundaries.Sort((left, right) => left.At.CompareTo(right.At));

		List<BandSegment> segments = [];

		for (int index = 0; index < boundaries.Count; index++)
		{
			DateTimeOffset from = boundaries[index].At;
			DateTimeOffset to = index + 1 < boundaries.Count ? boundaries[index + 1].At : window.End;

			if (to <= window.Start || from >= window.End)
				continue;

			double left = Math.Max(0, window.PercentAt(from));
			double right = Math.Min(100, window.PercentAt(to));
			if (right - left <= 0)
				continue;

			segments.Add(new BandSegment(boundaries[index].Period.Name, left, right - left, boundaries[index].Period.ColorTempKelvin));
		}

		return segments;
	}

	/// <summary>
	///     The instant a wall-clock time falls at in <paramref name="zone"/>.
	/// </summary>
	/// <remarks>
	///     The two hours a year that have no single answer are left to <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/>,
	///     which reads both as standard time: a boundary inside the hour the clocks skip is drawn where that clock
	///     lands, and one inside the hour lived twice is drawn on its second pass. Both are defensible readings of a
	///     wall-clock time the household's own clock never showed once, and either way it is one boundary on one day
	///     — where the fault this replaces moved <i>every</i> boundary on the far side of the change.
	/// </remarks>
	private static DateTimeOffset Instant(DateTime wallClock, TimeZoneInfo zone) =>
		new(wallClock, zone.GetUtcOffset(wallClock));

	/// <summary>Whether a band segment is wide enough to carry its own name without the name outgrowing it.</summary>
	/// <param name="segment">The segment.</param>
	/// <exception cref="ArgumentNullException"><paramref name="segment"/> is <c>null</c>.</exception>
	public static bool IsLabelled(BandSegment segment)
	{
		ArgumentNullException.ThrowIfNull(segment);

		return segment.WidthPct >= MinLabelledBandPct;
	}

	// ===================== words =====================

	/// <summary>A wall-clock time, in the reader's culture — the format every surface in this UI uses.</summary>
	/// <param name="at">The instant.</param>
	public static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

	/// <summary>
	///     A deadline, counted in seconds while that is the honest unit and named as a time once it is not.
	/// </summary>
	private static string Countdown(DateTimeOffset at, DateTimeOffset now)
	{
		TimeSpan left = at - now;

		if (left <= TimeSpan.Zero)
			return "any moment now";

		return left < TimeSpan.FromMinutes(2)
			? $"in {left.TotalSeconds:0} s"
			: $"at {Clock(at)}";
	}

	/// <summary>
	///     What a state is drawn as, or <c>null</c> for the states that draw nothing.
	/// </summary>
	/// <remarks>
	///     The two quiet states are the omission that makes the board readable: a vacant room's track is empty,
	///     and an empty track next to a busy one is the comparison the whole design exists to offer.
	/// </remarks>
	private static LaneBlockKind? KindOf(AreaState state) => state switch
	{
		AreaState.AutoActive => LaneBlockKind.Lit,
		AreaState.PreOff => LaneBlockKind.Dimming,
		AreaState.OverriddenOn or AreaState.SceneHold => LaneBlockKind.Hand,
		AreaState.SuppressedOff => LaneBlockKind.Held,
		_ => null
	};

	/// <summary>What the armed timer of a state will do when it fires, or <c>null</c> when the state arms none.</summary>
	private static string? NextWord(AreaState state) => state switch
	{
		AreaState.AutoActive => "dim",
		AreaState.PreOff => "off",
		AreaState.OverriddenOn => "auto resumes",
		AreaState.SuppressedOff => "listens again",
		_ => null
	};
}
