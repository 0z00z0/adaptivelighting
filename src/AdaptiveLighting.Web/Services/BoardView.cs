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

/// <summary>One room's row on the board: what it is doing now, what it did, and what happens next.</summary>
/// <param name="Key">The stable identity — the area id, or the display name when there is none.</param>
/// <param name="Name">The room's name, as the lane's label.</param>
/// <param name="AreaId">Where the room's own page is, or <c>null</c> when the room has no area to link to.</param>
/// <param name="Latest">The newest report from the room.</param>
/// <param name="Blocks">Its recent past, oldest first.</param>
/// <param name="Next">The one dotted mark ahead of the now-line, or <c>null</c> when nothing is armed.</param>
public sealed record BoardLane(
	string Key,
	string Name,
	string? AreaId,
	AreaSnapshot Latest,
	IReadOnlyList<LaneBlock> Blocks,
	LaneMark? Next)
{
	/// <summary>
	///     Whether this room has nothing to say: no past worth drawing, nothing armed, nothing wrong.
	/// </summary>
	/// <remarks>
	///     The whole dark-cockpit rule reduces to this property. A quiet room's lane is an empty track, and
	///     fourteen empty tracks are a wall of nothing that the three rooms worth reading have to be found in.
	/// </remarks>
	public bool IsQuiet => Blocks.Count == 0 && Next is null && !BoardView.IsException(Latest);
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
	/// <returns>The mark, or <c>null</c>.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static LaneMark? NextMark(AreaSnapshot snapshot, BoardWindow window)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(window);

		if (snapshot.NextChangeAt is not { } at || !window.Contains(at))
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
				? "dark enough, but the house is asleep — movement will not switch the lights on"
				: snapshot.AutoOnBlockingEntity is { Length: > 0 } blocking
					? $"dark enough, but {blocking} is on — movement will not switch the lights on"
					: "dark enough, but something here is on — movement will not switch the lights on";
		}

		return snapshot.State switch
		{
			AreaState.PreOff => snapshot.NextChangeAt is { } off
				? $"warning dim — lights out {Countdown(off, now)} unless someone moves"
				: "warning dim — the lights go out shortly unless someone moves",

			AreaState.OverriddenOn => snapshot.NextChangeAt is { } resumes
				? $"set by hand — automatic control returns at {Clock(resumes)}"
				: "set by hand — the engine stands back until somebody resumes it",

			AreaState.SuppressedOff => snapshot.NextChangeAt is { } listens
				? $"off by hand — movement is ignored until {Clock(listens)}"
				: "off by hand — movement is ignored until the room has been empty long enough",

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
	///         One day's sun times are used for all three days. Over a six-hour window that is a difference of a
	///         couple of minutes at the edges, and the band is context rather than a claim about a boundary.
	///     </para>
	/// </remarks>
	/// <param name="periods">The configured circadian table.</param>
	/// <param name="sun">The day's sun times, for the sun-anchored boundaries.</param>
	/// <param name="window">The board's window.</param>
	/// <returns>The segments, left to right. Empty when no period can be placed.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static IReadOnlyList<BandSegment> Band(IReadOnlyList<TimePeriodConfig> periods, SunTimes sun, BoardWindow window)
	{
		ArgumentNullException.ThrowIfNull(periods);
		ArgumentNullException.ThrowIfNull(sun);
		ArgumentNullException.ThrowIfNull(window);

		List<(DateTimeOffset At, TimePeriodConfig Period)> boundaries = [];

		foreach (TimePeriodConfig period in periods)
		{
			if (!PeriodStart.TryParse(period.Start, out PeriodStart? start) || start!.Resolve(sun) is not { } time)
				continue;

			for (int day = -1; day <= 1; day++)
				boundaries.Add((new DateTimeOffset(window.Start.Date.AddDays(day) + time.ToTimeSpan(), window.Start.Offset), period));
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
