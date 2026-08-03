using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     The stretch of time the board draws, and the arithmetic that turns an instant into a position on it.
/// </summary>
/// <remarks>
///     Both ends are snapped to whole hours, so the window steps once an hour instead of sliding: the gridlines
///     can be one repeating background and the axis labels hold still while somebody reads them.
/// </remarks>
/// <param name="End">The newest instant, on the hour, in the future; the board shows what is coming.</param>
public sealed record BoardWindow(DateTimeOffset Start, DateTimeOffset End)
{
	public static BoardWindow Around(DateTimeOffset now, TimeSpan back, TimeSpan ahead)
	{
		DateTimeOffset start = FloorToHour(now - back);
		DateTimeOffset end = CeilingToHour(now + ahead);

		// PercentAt divides by the width, so a degenerate window must never leave this method.
		return new BoardWindow(start, end > start ? end : start.AddHours(1));
	}

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

	/// <summary>A position as a percentage of the window's width. An instant outside it falls outside 0-100.</summary>
	public double PercentAt(DateTimeOffset at) => (at - Start) / (End - Start) * 100.0;

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

	/// <summary>The warning dim: the last call before the lights go out.</summary>
	Dimming,

	/// <summary>Somebody's own levels stood, by hand or by a scene.</summary>
	Hand,

	/// <summary>Switched off by hand, with movement ignored.</summary>
	Held
}

/// <param name="WidthPct">How wide it is, never narrower than a stretch a person can see.</param>
/// <param name="Kelvin">The warmth the engine commanded, for a lit stretch; <c>null</c> otherwise.</param>
public sealed record LaneBlock(LaneBlockKind Kind, double LeftPct, double WidthPct, int? Kelvin);

/// <summary>A dotted mark in a lane's future: when the room's armed timer will act, and what it will do.</summary>
public sealed record LaneMark(double LeftPct, string Label);

/// <summary>
///     A moment in a lane's past when the room saw movement and did not light: the instant, and the gate that
///     turned it down.
/// </summary>
/// <remarks>
///     A refusal has no duration, so it is neither a <see cref="LaneBlock"/> nor a <see cref="LaneMark"/>: the room
///     was dark before it and dark after, and a stretch would claim a state that never existed.
/// </remarks>
/// <param name="Label">What it was and why: <c>18:04 movement, too bright</c>.</param>
public sealed record LaneRefusal(double LeftPct, string Label);

/// <summary>One room's row on the board: what it is doing now, what it did, and what happens next.</summary>
/// <param name="Key">The stable identity: the area id, or the display name when there is none.</param>
/// <param name="AreaId">Where the room's own page is, or <c>null</c> when the room has no area to link to.</param>
/// <param name="Refusals">Moments it saw movement and did not light, oldest first.</param>
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
	///     A refusal has to count. It draws no stretch and arms no timer, so a room that turned movement down
	///     would otherwise be indistinguishable from a room nobody walked through.
	/// </remarks>
	public bool IsQuiet =>
		Blocks.Count == 0 && Next is null && Refusals is null or { Count: 0 } && !BoardView.IsException(Latest);
}

/// <param name="Kelvin">Its target warmth, which colours the band.</param>
public sealed record BandSegment(string Name, double LeftPct, double WidthPct, int Kelvin);

/// <summary>
///     Everything the board decides, as pure functions. Read-only throughout: nothing here touches the
///     configuration document.
/// </summary>
public static class BoardView
{
	public static readonly TimeSpan LookBack = TimeSpan.FromHours(4);

	/// <summary>How much future the board shows: enough to hold the next period boundary and a vacancy timeout.</summary>
	public static readonly TimeSpan LookAhead = TimeSpan.FromHours(2);

	public const int LaneBudget = 6;

	/// <summary>How many log lines the board carries before handing over to the Activity page.</summary>
	public const int LogPreview = 12;

	/// <summary>The narrowest a block may be drawn, so a five-second visit is still a mark and not a hairline.</summary>
	private const double MinBlockPct = 0.4;

	/// <summary>
	///     How far apart two refusal marks must be before both are drawn. A screen bound, not a clock one: 1 % of
	///     a seven-hour board is about three pixels on a phone track, the width of two ticks.
	/// </summary>
	private const double MinMarkGapPct = 1.0;

	/// <summary>The narrowest band segment that gets its name written in it.</summary>
	private const double MinLabelledBandPct = 9;

	// ===================== the lanes =====================

	/// <summary>
	///     One room's past, as blocks clipped to the board's window.
	/// </summary>
	/// <remarks>
	///     The engine publishes on transitions, so each report covers the stretch to the next one and the newest
	///     runs to <paramref name="now"/>. Nothing is drawn before the oldest report the log holds. Adjacent
	///     stretches merge before they become percentages, so the minimum width applies to the merged whole.
	/// </remarks>
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
	///     <see cref="ActivityView.IsDeclinedMotion"/> is the only test for a refusal; a copy here would let the
	///     board show a mark the timeline has no row for. On the ordinary path the engine publishes one report per
	///     change of the refusing gate, so nothing more than the gap guard below is needed.
	/// </remarks>
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

			// Not keyed on the gate, on purpose. The suppressed-off path republishes on every movement, so a
			// sensor re-firing under a hand-set off paints a smear of ticks inside a pixel of each other. Every
			// report is still its own row in the log.
			if (previousPercent is { } last && percent - last < MinMarkGapPct)
				continue;

			previousPercent = percent;
			marks.Add(new LaneRefusal(percent, $"{Clock(entry.At)} movement, {ActivityView.RefusalReason(entry.Snapshot)}"));
		}

		return marks;
	}

	/// <summary>
	///     Whether a refusal is this room's own business, or the house's, already said once above the lanes. The
	///     master switch and an empty house turn every room down at once, and a refusal makes a lane un-quiet.
	/// </summary>
	private static bool IsAboutThisRoom(AreaSnapshot snapshot) =>
		snapshot.AutoOnBlockedBy is not (AutoOnBlock.KillSwitch or AutoOnBlock.Away);

	/// <summary>
	///     The one dotted mark ahead of the now-line: when this room's armed timer fires, and what it will do.
	/// </summary>
	/// <remarks>
	///     The verb comes from the state; <c>NextChangeAt</c> carries none. The window reaches four hours back, so
	///     a snapshot stale from a connection blip would otherwise put a confident "auto resumes" to the left of
	///     the now-line. Both a passed deadline and an unarmed state get no mark.
	/// </remarks>
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
	///     Below <see cref="LaneBudget"/> nothing is folded, whatever the rooms are doing.
	/// </summary>
	public static (IReadOnlyList<BoardLane> Busy, IReadOnlyList<BoardLane> Quiet) Partition(IReadOnlyList<BoardLane> lanes)
	{
		ArgumentNullException.ThrowIfNull(lanes);

		if (lanes.Count <= LaneBudget)
			return (lanes, []);

		return ([.. lanes.Where(lane => !lane.IsQuiet)], [.. lanes.Where(lane => lane.IsQuiet)]);
	}

	// ===================== the exception tray =====================

	/// <summary>
	///     A state where the engine is not following the schedule. <see cref="AreaState.AutoVacant"/> and
	///     <see cref="AreaState.Away"/> are nominal and must stay out.
	/// </summary>
	public static bool IsException(AreaState state) =>
		state is AreaState.PreOff or AreaState.OverriddenOn or AreaState.SuppressedOff or AreaState.SceneHold;

	public static bool IsException(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return IsException(snapshot.State) || IsBlockedFromLighting(snapshot);
	}

	/// <summary>
	///     Whether the room is dark and waiting, and would light on movement but for a block worth naming.
	/// </summary>
	/// <remarks>
	///     Only Sleep and EntityOn, because the engine refuses in the order kill switch, disabled, away, sleep,
	///     entity, so those two arise only where the room would otherwise have lit. Internal because the activity
	///     timeline asks the same question and two copies would let the two surfaces disagree.
	/// </remarks>
	internal static bool IsBlockedFromLighting(AreaSnapshot snapshot) =>
		snapshot.State is AreaState.AutoVacant
		&& snapshot.IsDark is true
		&& snapshot.AutoOnBlockedBy is AutoOnBlock.Sleep or AutoOnBlock.EntityOn;

	/// <summary>
	///     The rooms the tray carries, most urgent first. The rest are alphabetical so the tray does not reshuffle
	///     itself on every tick.
	/// </summary>
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
	///     What a tray chip says after the room's name. Every branch has a wording for a room with no armed
	///     deadline: an override with no expiry configured stands until somebody resumes it.
	/// </summary>
	public static string ExceptionLine(AreaSnapshot snapshot, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		// Ahead of the switch: a blocked room is a nominal AutoVacant, so the states cannot express it. The
		// wording matches ActivityView.DarkEnough for the same condition.
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
	///     What the board's activity summary is showing, out of what it holds, and what it is holding back.
	/// </summary>
	/// <remarks>
	///     <paramref name="shown"/> counts rows and <paramref name="kept"/> counts reports, and the two are never
	///     set against each other. A house-wide event from six rooms is six reports and one row, so "newest 11 of
	///     63 reports" invites a subtraction whose answer is reports that were never left out.
	/// </remarks>
	/// <param name="held">Every report the log is holding, before any filter.</param>
	/// <param name="reachable">
	///     How many of them the Activity page would draw. The held-back count is measured against this, not
	///     against <paramref name="held"/>, because the line names that page as where they are.
	/// </param>
	/// <param name="capacity">The log's cap, so the line can say when the oldest reports started falling off.</param>
	public static string LogFoot(int held, int reachable, int kept, int shown, int capacity)
	{
		if (held <= 0)
			return "nothing recorded yet";

		int hidden = Math.Max(0, reachable - kept);

		string lead = shown >= LogPreview
			? $"newest {shown} rows of {Count(kept, "report")}"
			: Count(kept, "report");

		// When the filter has taken everything, the hidden count is the answer: "0 reports" beside a count of
		// hidden ones reads as a contradiction.
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
	///     Every boundary is placed through the zone, never at the window's own offset. A period's <c>Start</c> is
	///     a wall-clock time and the offset that turns it into an instant is the one in force on its own day, which
	///     twice a year is not the window's. Boundaries are placed for the day before, the day of and the day after,
	///     then sorted, so a window straddling midnight gets a band that does too. One day's sun times serve all
	///     three. A period the engine's own calculator cannot place is left out here as well.
	/// </remarks>
	/// <param name="sun">The day's sun times, for the sun-anchored boundaries.</param>
	/// <param name="zone">
	///     The household's time zone. Defaults to <see cref="TimeZoneInfo.Local"/> and is named explicitly only by
	///     tests, which must not depend on the machine they run on.
	/// </param>
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

		// Through the zone, not window.Start.Date: the window carries the offset it was built at, which on the
		// morning of a clock change names the wrong hour and can name the wrong day with it.
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
	///     The instant a wall-clock time falls at in <paramref name="zone"/>. The two ambiguous hours a year are
	///     left to <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/>, which reads both as standard time.
	/// </summary>
	private static DateTimeOffset Instant(DateTime wallClock, TimeZoneInfo zone) =>
		new(wallClock, zone.GetUtcOffset(wallClock));

	public static bool IsLabelled(BandSegment segment)
	{
		ArgumentNullException.ThrowIfNull(segment);

		return segment.WidthPct >= MinLabelledBandPct;
	}

	// ===================== words =====================

	/// <summary>A wall-clock time, in the reader's culture: the format every surface in this UI uses.</summary>
	public static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

	/// <summary>A deadline, counted in seconds while that is the honest unit and named as a time once it is not.</summary>
	private static string Countdown(DateTimeOffset at, DateTimeOffset now)
	{
		TimeSpan left = at - now;

		if (left <= TimeSpan.Zero)
			return "any moment now";

		return left < TimeSpan.FromMinutes(2)
			? $"in {left.TotalSeconds:0} s"
			: $"at {Clock(at)}";
	}

	/// <summary>What a state is drawn as, or <c>null</c> for the quiet states, which draw nothing.</summary>
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
