using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <param name="What">What the engine did, or declined to do. Never empty.</param>
/// <param name="Why">The reading or condition behind it, or <c>null</c> when the event speaks for itself.</param>
public sealed record ActivityLine(string What, string? Why);

/// <summary>
///     One rendered line of the record: the words, when they were said, and who they are attributed to.
/// </summary>
/// <remarks>
///     A row is not a report. The engine publishes one snapshot per area, so a house-wide event reaches the
///     record once per switched-on room; the record renders per thing that happened.
/// </remarks>
/// <param name="Entry">The report the row is drawn from, the newest of the run when several collapsed.</param>
/// <param name="Rooms">The rooms whose reports this row covers, newest first and named once each. Never empty.</param>
public sealed record ActivityRow(
	ActivityEntry Entry,
	ActivityLine Line,
	bool IsAboutTheHouse,
	IReadOnlyList<string> Rooms)
{
	public DateTimeOffset At => Entry.At;

	/// <summary>The row's key. Dense and never reused, so it survives eviction from the buffer.</summary>
	public long Sequence => Entry.Sequence;

	public AreaSnapshot Snapshot => Entry.Snapshot;

	/// <summary>The room the row belongs to, or <c>null</c> when it belongs to the house.</summary>
	public string? Room => IsAboutTheHouse ? null : Entry.AreaName;
}

/// <summary>
///     What a row is about, as the activity page's filter chips divide it.
/// </summary>
/// <remarks>
///     Categories follow the words the row shows, not the report behind it. Flags, because a report forced to
///     pick one category would be hidden from the chip somebody was using.
/// </remarks>
[Flags]
public enum ActivityCategory
{
	/// <summary>No category. Never a report's answer; see <see cref="ActivityView.Categorise"/>.</summary>
	None = 0,

	/// <summary>A motion sensor reported.</summary>
	Movement = 1,

	/// <summary>The engine commanded these lights: on, the circadian re-aim, the warning dim, or off.</summary>
	LightChange = 2,

	/// <summary>The row states where the room stands against the level it counts as dark.</summary>
	Illumination = 4,

	/// <summary>Somebody set or switched these lights themselves, or a manual change ran its course.</summary>
	ManualChange = 8,

	/// <summary>The engine considered lighting the room and did not, and the row says why.</summary>
	Declined = 16,

	/// <summary>The house emptying or filling, or the master switch.</summary>
	House = 32,

	/// <summary>Housekeeping: rechecks, start-up, and a room switched on or off for automatic lighting.</summary>
	Background = 64,

	/// <summary>The house moved to a different mode. Kept apart from <see cref="House"/>, which it would bury.</summary>
	Mode = 128
}

/// <param name="Count">How many of the reports the room filter left, not of the whole buffer.</param>
public sealed record ActivityFilterChip(
	ActivityCategory Category,
	string Label,
	string Title,
	int Count,
	bool IsOn);

/// <param name="Heading">What that date is called: <c>Today</c>, <c>Yesterday</c>, or the date.</param>
public sealed record ActivityDay(DateOnly Day, string Heading, IReadOnlyList<ActivityRow> Rows);

/// <summary>
///     The activity page's decisions, in one testable place: what a report is called, which room it belongs to,
///     and which day it falls under.
/// </summary>
public static class ActivityView
{
	public const string AllRooms = "";

	/// <summary>Every category: what "show everything" puts the chips back to.</summary>
	public const ActivityCategory AllCategories =
		ActivityCategory.Movement
		| ActivityCategory.LightChange
		| ActivityCategory.Illumination
		| ActivityCategory.ManualChange
		| ActivityCategory.Declined
		| ActivityCategory.Mode
		| ActivityCategory.House
		| ActivityCategory.Background;

	/// <summary>What the page opens with. Background is the only category that starts off.</summary>
	public const ActivityCategory DefaultCategories = AllCategories & ~ActivityCategory.Background;

	/// <summary>
	///     What the dashboard's summary carries: the four things the board's lanes cannot draw. Stricter than
	///     <see cref="DefaultCategories"/>, so the summary's footer has to say how many it is holding back.
	/// </summary>
	public const ActivityCategory SummaryCategories =
		ActivityCategory.ManualChange
		| ActivityCategory.Declined
		| ActivityCategory.Mode
		| ActivityCategory.House;

	private sealed record CategoryName(ActivityCategory Category, string Label, string Title);

	private static readonly CategoryName[] Catalogue =
	[
		new(ActivityCategory.Movement, "Movement", "A motion sensor reported."),
		new(ActivityCategory.LightChange, "Light change",
			"The engine commanded the lights: on, retuned, dimmed as a warning, or off."),
		new(ActivityCategory.Illumination, "Darkness",
			"How dark the room measured, against the level it counts as dark."),
		new(ActivityCategory.ManualChange, "Manual changes",
			"Somebody set or switched the lights themselves, and what happened when that ran out."),
		new(ActivityCategory.Declined, "Nothing happened",
			"The engine could have lit the room and did not — with the reason."),
		new(ActivityCategory.Mode, "Mode changes", "The house moved to a different mode."),
		new(ActivityCategory.House, "House",
			"The house emptying and filling, a guest scene, and the master switch."),
		new(ActivityCategory.Background, "Background tasks",
			"Rechecks, start-up, and rooms switched on or off. Starts hidden — the highest volume, the lowest signal.")
	];

	/// <summary>
	///     Puts a report into words.
	/// </summary>
	/// <remarks>
	///     The four branches below are ordered, and <see cref="Categorise"/> and <see cref="IsAboutTheHouse"/>
	///     take the same order: a row's categories and its attribution follow the words it shows, so all three
	///     move together.
	/// </remarks>
	public static ActivityLine Describe(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		// The one thing allowed ahead of the master switch. The engine publishes movement into a blocked room so
		// that this row can exist, and the wording below names the master switch itself when that is what refused.
		if (IsDeclinedMotion(snapshot))
			return new ActivityLine(MovementRefusedBy(snapshot), RefusalDetail(snapshot));

		if (snapshot.KillSwitchActive)
			return new ActivityLine("Paused by the master switch", "No lights change until it's turned back on.");

		// A quiet re-check that found the darkness verdict had moved. That verdict is the news, so it leads.
		if (snapshot is { Reason: TransitionReason.CircadianTick, State: AreaState.AutoVacant, IsDark: { } dark })
			return new ActivityLine(dark ? DarkEnough(snapshot) : TooBright, Reading(snapshot));

		// A forced mode bypasses Condition, which speaks for the publishing room where this speaks for the house.
		if (snapshot is { Reason: TransitionReason.HouseModeChanged, Forced: { } forced })
			return new ActivityLine(Headline(snapshot), forced.Describe());

		return new ActivityLine(Headline(snapshot), Condition(snapshot));
	}

	// Ordinal on the display name. Rooms and the verdict collapse in Rows match the same way; if the three
	// disagree about what "the same room" is, an option stops meaning a filter.
	public static IReadOnlyList<ActivityEntry> InRoom(IEnumerable<ActivityEntry> entries, string? room)
	{
		ArgumentNullException.ThrowIfNull(entries);

		if (string.IsNullOrWhiteSpace(room))
			return [.. entries];

		return [.. entries.Where(entry => string.Equals(entry.AreaName, room, StringComparison.OrdinalIgnoreCase))];
	}

	/// <summary>
	///     The rooms the filter offers: the ones that have reported. De-duplicated ordinally, never by the
	///     culture, which could collapse two names into one option the filter then half-matches. Sorted by culture.
	/// </summary>
	public static IReadOnlyList<string> Rooms(IEnumerable<ActivityEntry> entries)
	{
		ArgumentNullException.ThrowIfNull(entries);

		return
		[
			.. entries
				.Select(entry => entry.AreaName)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(name => name, StringComparer.CurrentCulture)
		];
	}

	// ===================== what is not an event at all =====================

	/// <summary>
	///     Whether a report is worth a row at all. One rule, about start-up. Decided here, not by publishing
	///     fewer snapshots: the board's lanes and the room pages read those reports too.
	/// </summary>
	public static bool IsWorthShowing(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		// The master switch replaces a start-up row's words with a sentence about the whole house, and that one is
		// news whatever else the report carries.
		if (snapshot.Reason is not TransitionReason.Startup || snapshot.KillSwitchActive)
			return true;

		return Describe(snapshot).Why is { Length: > 0 };
	}

	/// <summary>Only the reports worth a row. Never applied where the dashboard reads the log for the board.</summary>
	public static IReadOnlyList<ActivityEntry> Shown(IEnumerable<ActivityEntry> entries)
	{
		ArgumentNullException.ThrowIfNull(entries);

		return [.. entries.Where(entry => IsWorthShowing(entry.Snapshot))];
	}

	// ===================== the category filter =====================

	/// <summary>
	///     What a report is about, as the chips divide it.
	/// </summary>
	/// <remarks>
	///     Tracks <see cref="Describe"/> branch for branch. No catch-all: a reason added to the enum has to be
	///     placed here by hand, and until it is, an exhaustive test fails and the page keeps its rows.
	/// </remarks>
	/// <returns>Every category it belongs to. Never <see cref="ActivityCategory.None"/>.</returns>
	public static ActivityCategory Categorise(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		// Describe replaces the row's words here, so the categories go with them. A refused movement is its one
		// exception, in both places.
		if (snapshot.KillSwitchActive && !IsDeclinedMotion(snapshot))
			return ActivityCategory.House;

		// Start-up decided nothing, so it is housekeeping outright and never the darkness or refusal chips.
		if (snapshot.Reason is TransitionReason.Startup)
			return ActivityCategory.Background;

		ActivityCategory categories = ActivityCategory.None;

		if (snapshot.Reason is TransitionReason.Motion)
			categories |= ActivityCategory.Movement;

		if (CommandedTheLights(snapshot))
			categories |= ActivityCategory.LightChange;

		if (NamesTheDarknessVerdict(snapshot))
			categories |= ActivityCategory.Illumination;

		if (snapshot.Reason is TransitionReason.ManualOn
			or TransitionReason.ManualOff
			or TransitionReason.OverrideExpired
			or TransitionReason.SuppressionLifted)
			categories |= ActivityCategory.ManualChange;

		if (WasDeclined(snapshot))
			categories |= ActivityCategory.Declined;

		if (snapshot.Reason is TransitionReason.HouseModeChanged)
			categories |= ActivityCategory.Mode;

		if (snapshot.Reason is TransitionReason.EveryoneLeft
			or TransitionReason.FirstPersonArrived
			or TransitionReason.SceneHold)
			categories |= ActivityCategory.House;

		// A refused movement past the return above names the master switch, so the house chip must offer it.
		if (snapshot.KillSwitchActive)
			categories |= ActivityCategory.House;

		// A tick is housekeeping only when nothing above claimed it: the same reason carries the circadian re-aim
		// and the dusk verdict, and Background starts switched off, so miscounting either hides it by default.
		if (snapshot.Reason is TransitionReason.AdoptedAtStartup
			or TransitionReason.EnablementChanged
			|| (snapshot.Reason is TransitionReason.CircadianTick && categories == ActivityCategory.None))
			categories |= ActivityCategory.Background;

		return categories;
	}

	/// <summary>
	///     Only the reports in <paramref name="categories"/>. Applied to a list already read out of
	///     <see cref="ActivityLog"/>, never inside its lock, which hands over the entries and the sequence
	///     together.
	/// </summary>
	public static IReadOnlyList<ActivityEntry> InCategories(IEnumerable<ActivityEntry> entries, ActivityCategory categories)
	{
		ArgumentNullException.ThrowIfNull(entries);

		if (categories == AllCategories)
			return [.. entries];

		return [.. entries.Where(entry => (Categorise(entry.Snapshot) & categories) != ActivityCategory.None)];
	}

	/// <summary>
	///     The chips the page draws. Every category is always offered, including the ones holding nothing.
	/// </summary>
	/// <param name="entries">The reports the room filter left, so the counts describe the room.</param>
	public static IReadOnlyList<ActivityFilterChip> Chips(IEnumerable<ActivityEntry> entries, ActivityCategory chosen)
	{
		ArgumentNullException.ThrowIfNull(entries);

		int[] counts = new int[Catalogue.Length];

		foreach (ActivityEntry entry in entries)
		{
			ActivityCategory found = Categorise(entry.Snapshot);

			for (int index = 0; index < Catalogue.Length; index++)
			{
				if ((found & Catalogue[index].Category) != ActivityCategory.None)
					counts[index]++;
			}
		}

		return
		[
			.. Catalogue.Select((name, index) => new ActivityFilterChip(
				name.Category,
				name.Label,
				name.Title,
				counts[index],
				(chosen & name.Category) != ActivityCategory.None))
		];
	}

	/// <summary>
	///     What the filters are keeping off the page, or <c>null</c> when they are keeping nothing off it.
	/// </summary>
	/// <param name="room">The chosen room, or <c>null</c>/empty for every room.</param>
	public static string? HiddenNote(int held, int shown, string? room, ActivityCategory categories)
	{
		int hidden = held - shown;

		if (hidden <= 0)
			return null;

		string count = hidden == 1 ? "1 report is hidden" : $"{hidden} reports are hidden";
		bool byRoom = !string.IsNullOrWhiteSpace(room);
		bool byCategory = categories != AllCategories;

		return (byRoom, byCategory) switch
		{
			(true, true) => $"{count} — they came from other rooms, or fall into categories that are switched off.",
			(true, false) => $"{count} — they came from other rooms.",
			_ => $"{count} — they fall into categories that are switched off."
		};
	}

	/// <summary>
	///     Whether this report is the engine's own command reaching the lights. Read off the reason and the state,
	///     never off <see cref="AreaSnapshot.LastCommandAt"/>, which is set an instant before the snapshot is built.
	/// </summary>
	private static bool CommandedTheLights(AreaSnapshot snapshot) => snapshot.Reason switch
	{
		TransitionReason.VacancyTimeout or TransitionReason.PreOffElapsed => true,

		// An override running out hands the room back by commanding it.
		TransitionReason.OverrideExpired => true,

		TransitionReason.Startup
			or TransitionReason.AdoptedAtStartup
			or TransitionReason.SceneHold
			or TransitionReason.ManualOn
			or TransitionReason.ManualOff
			or TransitionReason.SuppressionLifted
			or TransitionReason.EnablementChanged
			or TransitionReason.EveryoneLeft => false,

		// What is left commands where it left the room lit and aimed. That is what AutoActive means.
		_ => snapshot.State is AreaState.AutoActive
	};

	/// <summary>
	///     The two places <see cref="Describe"/> states the darkness verdict, and no others: a dark vacant room
	///     that arrived some other way says nothing about darkness.
	/// </summary>
	private static bool NamesTheDarknessVerdict(AreaSnapshot snapshot) =>
		snapshot.State is AreaState.AutoVacant
		&& (snapshot.IsDark is false
			|| (snapshot.IsDark is true && snapshot.Reason is TransitionReason.CircadianTick));

	/// <summary>
	///     Whether the engine could have lit the room and did not. The middle test goes through
	///     <see cref="BoardView.IsBlockedFromLighting"/>, which the board reads too.
	/// </summary>
	private static bool WasDeclined(AreaSnapshot snapshot) =>
		(snapshot.Reason is TransitionReason.Motion && snapshot.State is not AreaState.AutoActive)
		|| BoardView.IsBlockedFromLighting(snapshot)
		|| (snapshot.State is AreaState.AutoVacant && snapshot.IsDark is false);

	// ===================== one row per thing that happened =====================

	/// <summary>
	///     How far apart two reports of the same house-wide event may be and still be one row. Not sizing the
	///     burst, which arrives inside one second: it bounds how late a delivery can run before two events merge.
	/// </summary>
	public static readonly TimeSpan CollapseWindow = TimeSpan.FromSeconds(30);

	/// <summary>
	///     Whether this report's words are about the whole house, not about the room that published it.
	/// </summary>
	/// <remarks>
	///     Decided on the words <see cref="Describe"/> prints, not on the cause. Three reasons look house-wide and
	///     are not: SceneHold says "this room" and rooms enter the hold at different moments, EnablementChanged is
	///     raised per area but worded per room, and Startup says what was found in each room.
	/// </remarks>
	public static bool IsAboutTheHouse(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		// The same branch Describe takes, including its exception for a refused movement.
		if (snapshot.KillSwitchActive && !IsDeclinedMotion(snapshot))
			return true;

		return snapshot.Reason is TransitionReason.HouseModeChanged
			or TransitionReason.EveryoneLeft
			or TransitionReason.FirstPersonArrived;
	}

	/// <summary>
	///     A house-wide row's words, minus the publishing room's condition. Dropping it is what lets one mode
	///     change be one row: the rooms differ when the mode moves, so their conditions would split the event.
	/// </summary>
	private static ActivityLine LineFor(AreaSnapshot snapshot)
	{
		ActivityLine line = Describe(snapshot);

		return IsAboutTheHouse(snapshot) && !SpeaksForTheHouse(snapshot)
			? line with { Why = null }
			: line;
	}

	/// <summary>
	///     The two branches of <see cref="Describe"/> that bypass <see cref="Condition"/> on a house-wide report.
	///     A third has to be added here by hand, or it silently loses its second line to the collapse.
	/// </summary>
	private static bool SpeaksForTheHouse(AreaSnapshot snapshot) =>
		snapshot.KillSwitchActive
		|| snapshot is { Reason: TransitionReason.HouseModeChanged, Forced: not null };

	/// <summary>
	///     Whether the report is the standing darkness verdict a quiet re-check republishes on every tick. A
	///     state, not an event, so it collapses.
	/// </summary>
	private static bool IsStandingVerdict(AreaSnapshot snapshot) =>
		snapshot is { Reason: TransitionReason.CircadianTick, State: AreaState.AutoVacant, IsDark: not null }
		&& !snapshot.KillSwitchActive;

	/// <summary>
	///     Turns reports into the rows a page renders: one row per report, except that a run of reports saying the
	///     same thing becomes a single row carrying the newest of them.
	/// </summary>
	/// <remarks>
	///     Two runs collapse, over different sequences. House-wide reports are consecutive in the record. A
	///     standing darkness verdict is not, because every room ticks in one pass, so its run steps over other
	///     rooms. Either way the newest of the run becomes the row, which keeps its reading current.
	///     The match differs too: house-wide reports must match on the whole rendered line, or "mode to Home"
	///     merges with "to Guests", while a verdict matches on its headline alone, because the line underneath is
	///     a live reading that moves every tick. Only a consecutive run collapses.
	///     The run in progress is finished before <paramref name="limit"/> is honoured.
	/// </remarks>
	/// <param name="entries">The reports, newest first, already through whatever filters the page applies.</param>
	/// <param name="limit">How many rows to build at most, or <c>null</c> for all of them.</param>
	public static IReadOnlyList<ActivityRow> Rows(IEnumerable<ActivityEntry> entries, int? limit = null)
	{
		ArgumentNullException.ThrowIfNull(entries);

		if (limit is <= 0)
			return [];

		List<ActivityEntry> ordered = [.. entries];

		// Memoised, because a limited read builds a dozen rows out of five hundred reports once a second and both
		// runs ask for the same lines.
		bool[] swallowed = new bool[ordered.Count];
		ActivityLine?[] words = new ActivityLine?[ordered.Count];
		List<ActivityRow> rows = [];

		for (int index = 0; index < ordered.Count; index++)
		{
			if (swallowed[index])
				continue;

			ActivityEntry head = ordered[index];
			ActivityLine line = LineAt(index);
			bool house = IsAboutTheHouse(head.Snapshot);
			List<string> rooms = [head.AreaName];

			if (house)
				SwallowHouseRun(index, line, rooms);
			else if (IsStandingVerdict(head.Snapshot))
				SwallowRepeatedVerdict(index, line);

			rows.Add(new ActivityRow(head, line, house, rooms));

			if (limit is { } cap && rows.Count >= cap)
				break;
		}

		return rows;

		ActivityLine LineAt(int at) => words[at] ??= LineFor(ordered[at].Snapshot);

		void SwallowHouseRun(int from, ActivityLine line, List<string> rooms)
		{
			DateTimeOffset at = ordered[from].At;

			for (int scan = from + 1; scan < ordered.Count; scan++)
			{
				// Absolute difference, not a subtraction: the list is newest first by sequence, so a report whose
				// timestamp disagrees with its position must not be swept in by an interval that went negative.
				if (!IsAboutTheHouse(ordered[scan].Snapshot)
					|| (at - ordered[scan].At).Duration() > CollapseWindow
					|| LineAt(scan) != line)
				{
					return;
				}

				string room = ordered[scan].AreaName;

				if (!rooms.Contains(room, StringComparer.OrdinalIgnoreCase))
					rooms.Add(room);

				swallowed[scan] = true;
			}
		}

		void SwallowRepeatedVerdict(int from, ActivityLine line)
		{
			// Ordinal on the display name, as InRoom and the room filter are. If the three disagree, a run gets
			// assembled from rooms the filter would not put together.
			string room = ordered[from].AreaName;

			for (int scan = from + 1; scan < ordered.Count; scan++)
			{
				if (!string.Equals(ordered[scan].AreaName, room, StringComparison.OrdinalIgnoreCase))
					continue;

				if (!IsStandingVerdict(ordered[scan].Snapshot)
					|| !string.Equals(LineAt(scan).What, line.What, StringComparison.Ordinal))
				{
					return;
				}

				swallowed[scan] = true;
			}
		}
	}

	/// <summary>Which rooms a house-wide row was assembled from, or <c>null</c> when the row names one.</summary>
	public static string? ReportedBy(ActivityRow row)
	{
		ArgumentNullException.ThrowIfNull(row);

		if (!row.IsAboutTheHouse || row.Rooms.Count == 0)
			return null;

		return row.Rooms.Count == 1
			? $"Reported by {row.Rooms[0]}."
			: $"Reported by {row.Rooms.Count} rooms: {string.Join(", ", row.Rooms)}.";
	}

	/// <summary>
	///     Cuts the timeline into days, keeping the order it was given.
	/// </summary>
	/// <remarks>
	///     Local dates, never UTC. Grouped consecutively, never collected, so the caller's order survives. Rows,
	///     not reports: a house-wide run straddling midnight is one event, and cutting first would call it two.
	/// </remarks>
	public static IReadOnlyList<ActivityDay> GroupByDay(IEnumerable<ActivityRow> rows, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(rows);

		DateOnly today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
		List<ActivityDay> days = [];
		List<ActivityRow> current = [];
		DateOnly day = default;

		foreach (ActivityRow row in rows)
		{
			DateOnly rowDay = DateOnly.FromDateTime(row.At.ToLocalTime().DateTime);

			if (current.Count > 0 && rowDay != day)
			{
				days.Add(new ActivityDay(day, DayHeading(day, today), current));
				current = [];
			}

			day = rowDay;
			current.Add(row);
		}

		if (current.Count > 0)
			days.Add(new ActivityDay(day, DayHeading(day, today), current));

		return days;
	}

	/// <summary>What a day is called on the page. Only today and yesterday get a name.</summary>
	public static string DayHeading(DateOnly day, DateOnly today)
	{
		int back = today.DayNumber - day.DayNumber;

		return back switch
		{
			0 => "Today",
			1 => "Yesterday",
			_ => day.ToString("dddd d MMMM", CultureInfo.CurrentCulture)
		};
	}

	/// <summary>What the room being too bright is called, in the one place it is written down.</summary>
	private const string TooBright = "Too bright to switch on";

	/// <summary>The event itself, from the engine's own reason for publishing.</summary>
	private static string Headline(AreaSnapshot snapshot) => snapshot.Reason switch
	{
		// The away branch is the mode the engine found at start-up, never a mode that moved: the room was swept
		// because the house was already away, and "took the room as it was" would deny the sweep.
		TransitionReason.Startup => snapshot.State == AreaState.Away
			? "Started up — the house was already away"
			: "Started up — took the room as it was",
		TransitionReason.AdoptedAtStartup => "Started up — these lights were already on",
		TransitionReason.Motion => snapshot.State switch
		{
			AreaState.AutoActive => Lit("Movement — lights on", snapshot),
			AreaState.SuppressedOff => "Movement, but the lights were switched off manually",
			AreaState.OverriddenOn => "Movement while the manual levels stand",
			_ => "Movement"
		},
		TransitionReason.VacancyTimeout => Lit("No movement — dimmed as a warning", snapshot),
		TransitionReason.PreOffElapsed => "Dim warning unanswered — lights off",
		TransitionReason.ManualOn => "Lights set manually",
		TransitionReason.ManualOff => "Lights switched off manually",
		TransitionReason.OverrideExpired => "The manual change ran its course",
		TransitionReason.SuppressionLifted => "Quiet long enough — back on automatic",
		TransitionReason.EveryoneLeft => "Everyone left the house",
		TransitionReason.FirstPersonArrived => "First person home",
		TransitionReason.EnablementChanged => snapshot.State == AreaState.Disabled
			? "Automatic lighting switched off here"
			: "Automatic lighting switched on here",
		TransitionReason.CircadianTick => snapshot.State == AreaState.AutoActive
			? Lit("Retuned to the time of day", snapshot)
			: "Rechecked the room",
		// On a forced change the select never moves, so HouseModeValue still reads whatever a person last chose.
		// The option the engine actually put the house on comes off the force.
		TransitionReason.HouseModeChanged => snapshot.Forced is { } forced
			? $"Mode forced to {ForcedOption(forced)}"
			: snapshot.HouseModeValue is { Length: > 0 } value
				? $"Mode changed to {value}"
				: "The house changed mode",
		TransitionReason.SceneHold => snapshot.State == AreaState.SceneHold
			? "A guest scene has this room"
			: "The guest scene let this room go",
		_ => snapshot.Reason.ToString()
	};

	/// <summary>
	///     The condition worth naming under the event, most consequential first. The block is named on every row,
	///     because <see cref="WasDeclined"/> files a blocked room under Declined whatever the report's reason.
	/// </summary>
	private static string? Condition(AreaSnapshot snapshot) => snapshot.State switch
	{
		AreaState.Disabled => "Automatic lighting is off here.",

		// "Waiting for the first arrival" is a promise about people who are already in the room.
		AreaState.Away when snapshot.IsAnyoneHome is true => AwayHold(snapshot),
		AreaState.Away => "Nobody home — waiting for the first arrival.",

		// Through DarkEnough, never worded again, so this row and the dusk row above it say one thing.
		AreaState.AutoVacant when BoardView.IsBlockedFromLighting(snapshot) => $"{DarkEnough(snapshot)}.",

		AreaState.AutoVacant when snapshot.IsDark is false =>
			Reading(snapshot) is { Length: > 0 } reading
				? $"{TooBright} — {reading}"
				: $"{TooBright}.",
		AreaState.AutoVacant when snapshot.IsDark is null => "Darkness not checked here yet.",
		_ => null
	};

	/// <summary>
	///     What "dark enough" is worth to this room. Not always a light: two of the other four auto-on gates leave
	///     the room in <see cref="AreaState.AutoVacant"/> too, so the engine's own verdict is the only answer.
	/// </summary>
	private static string DarkEnough(AreaSnapshot snapshot) => snapshot.AutoOnBlockedBy switch
	{
		AutoOnBlock.Sleep => "Dark enough, but the house is asleep — movement won't light the room",
		AutoOnBlock.EntityOn => snapshot.AutoOnBlockingEntity is { Length: > 0 } blocker
			? $"Dark enough, but {blocker} is on — movement won't light the room"
			: "Dark enough, but something here is on — movement won't light the room",
		_ => "Dark enough — movement will light the room"
	};

	/// <summary>
	///     Whether this report is movement the engine turned down. Internal because the board draws its refusal
	///     marks from this same test; two copies would let the board and the timeline disagree.
	/// </summary>
	internal static bool IsDeclinedMotion(AreaSnapshot snapshot) =>
		snapshot.Reason is TransitionReason.Motion
		&& snapshot.State is not AreaState.AutoActive
		&& snapshot.AutoOnBlockedBy is { } block
		&& block is not AutoOnBlock.None;

	/// <summary>Movement, and the plain sentence for whichever gate stopped it.</summary>
	private static string MovementRefusedBy(AreaSnapshot snapshot) => snapshot.AutoOnBlockedBy switch
	{
		AutoOnBlock.KillSwitch => "Movement, but the master switch is off",
		AutoOnBlock.Disabled => "Movement, but automatic lighting is off here",

		// The one gate with two causes. "Nobody is home yet" is only ever true of an empty house.
		AutoOnBlock.Away => snapshot.IsAnyoneHome is true
			? "Movement, but the house is in away mode"
			: "Movement, but nobody is home yet",
		AutoOnBlock.SceneHold => "Movement, but a guest scene has this room",
		AutoOnBlock.Sleep => "Movement, but the house is asleep and this room stays dark",
		AutoOnBlock.EntityOn => snapshot.AutoOnBlockingEntity is { Length: > 0 } blocker
			? $"Movement, but {blocker} is on"
			: "Movement, but something here is on",
		AutoOnBlock.NotDark => "Movement, but the room is bright enough",

		// Unreachable while IsDeclinedMotion guards this: None and null are both excluded there. Worded rather
		// than thrown, so a bad report costs a vaguer row instead of a page that will not render.
		_ => "Movement"
	};

	/// <summary>
	///     The gate that turned a movement down, as a clause for the board's mark: <c>18:04 movement, too
	///     bright</c>. Off the same <see cref="AutoOnBlock"/> the log's full sentence uses.
	/// </summary>
	/// <param name="snapshot">A report <see cref="IsDeclinedMotion"/> has already accepted.</param>
	internal static string RefusalReason(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return snapshot.AutoOnBlockedBy switch
		{
			AutoOnBlock.KillSwitch => "the master switch is off",
			AutoOnBlock.Disabled => "automatic lighting is off here",
			AutoOnBlock.Away => snapshot.IsAnyoneHome is true ? "the house is in away mode" : "nobody home yet",
			AutoOnBlock.SceneHold => "a guest scene has this room",
			AutoOnBlock.Sleep => "the house is asleep",
			AutoOnBlock.EntityOn => snapshot.AutoOnBlockingEntity is { Length: > 0 } blocker
				? $"{blocker} is on"
				: "something here is on",
			AutoOnBlock.NotDark => "too bright",
			_ => "turned down"
		};
	}

	/// <summary>
	///     What goes under a refused movement. Every other gate has named itself in the headline, and a lux figure
	///     beside "the house is asleep" invites the reader to blame the sensor for a decision the mode made.
	/// </summary>
	private static string? RefusalDetail(AreaSnapshot snapshot) => snapshot.AutoOnBlockedBy switch
	{
		AutoOnBlock.NotDark => Reading(snapshot),
		AutoOnBlock.Away when snapshot.IsAnyoneHome is true => AwayHold(snapshot),
		_ => null
	};

	/// <summary>
	///     Why an away-kind mode is holding a room shut while somebody is home. Internal because
	///     <see cref="RoomFacts"/> says it too; one wording, not two. <see cref="ForcedMode.Describe"/> is called,
	///     never re-worded. Only reached where <see cref="AreaSnapshot.IsAnyoneHome"/> is <c>true</c>.
	/// </summary>
	internal static string AwayHold(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		if (snapshot.Forced is { } forced)
			return forced.Describe();

		return snapshot.HouseModeValue is { Length: > 0 } mode
			? $"Somebody is home, but the house mode is set to {mode}."
			: "Somebody is home, but the house is in away mode.";
	}

	/// <summary>The option a forced mode put the house on, falling back to its kind when the option is nameless.</summary>
	private static string ForcedOption(ForcedMode forced) =>
		forced.OptionValue is { Length: > 0 } value ? value : forced.Kind.ToString();

	/// <summary>
	///     The darkness gate's own words: the reading and the threshold it was compared against. Passed through,
	///     never rebuilt; the gate is the only thing that knows which source is in use.
	/// </summary>
	private static string? Reading(AreaSnapshot snapshot) =>
		snapshot.DarknessDetail is { Length: > 0 } detail ? detail : null;

	/// <summary>
	///     Appends the levels the engine is holding, when it is holding any. Lights adopted at start-up have no
	///     command behind them, so the headline stands alone.
	/// </summary>
	private static string Lit(string headline, AreaSnapshot snapshot)
	{
		if (snapshot.BrightnessPct is not { } brightness)
			return headline;

		return snapshot.ColorTempKelvin is { } kelvin
			? $"{headline} at {brightness:0} %, {kelvin} K"
			: $"{headline} at {brightness:0} %";
	}
}
