using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     One row's words: what happened, and — where there is one — the condition that decided it.
/// </summary>
/// <remarks>
///     Two strings rather than one sentence because the page renders them at two weights: the event reads at full
///     strength down the timeline, the condition sits under it in muted type. Keeping them apart also keeps the
///     part worth being sure about — the measured reading against the configured threshold — a thing a test can
///     assert on its own rather than a substring of a paragraph.
/// </remarks>
/// <param name="What">What the engine did, or declined to do. Never empty.</param>
/// <param name="Why">The reading or condition behind it, or <c>null</c> when the event speaks for itself.</param>
public sealed record ActivityLine(string What, string? Why);

/// <summary>
///     One rendered line of the record: the words, when they were said, and who they are attributed to.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a row is not simply a report.</b> The engine publishes one snapshot per area, which is correct —
///         every area really did re-evaluate itself, and the board, the cards and the room pages all read those
///         per-area reports. But a house-wide event then reaches the record once per switched-on room, so a single
///         change of house mode was rendered as a stack of identical rows, each attributed to a room as though
///         that room had done something. This type is where the two views part company: the engine keeps
///         publishing per area, and the record renders per <i>thing that happened</i>.
///     </para>
///     <para>
///         The fix lives here rather than in the engine on purpose. The per-area snapshots are load-bearing
///         elsewhere, and a presentation problem answered by publishing less would take the board's lanes and the
///         room pages with it.
///     </para>
/// </remarks>
/// <param name="Entry">
///     The report the row is drawn from — the newest of the run when several collapsed, so the row carries the
///     time the event was last seen and a key the page can render against.
/// </param>
/// <param name="Line">The words, identical across every report the row covers.</param>
/// <param name="IsAboutTheHouse">
///     Whether the row's words are about the whole house rather than about one room. Such a row is deliberately
///     <b>not</b> attributed to a room: naming one would say that room changed the house's mode.
/// </param>
/// <param name="Rooms">
///     The rooms whose reports this row covers, newest first and named once each. One entry is the ordinary case;
///     more than one means a house-wide event reached the record from several rooms at once. Never empty — it is
///     what tells a reader which rooms a house-wide row was actually assembled from.
/// </param>
public sealed record ActivityRow(
	ActivityEntry Entry,
	ActivityLine Line,
	bool IsAboutTheHouse,
	IReadOnlyList<string> Rooms)
{
	/// <summary>When the event happened — the newest report's time when several collapsed.</summary>
	public DateTimeOffset At => Entry.At;

	/// <summary>The row's key. Dense and never reused, so it survives eviction from the buffer.</summary>
	public long Sequence => Entry.Sequence;

	/// <summary>The report behind the row, for the colour family and the room link.</summary>
	public AreaSnapshot Snapshot => Entry.Snapshot;

	/// <summary>The room the row belongs to, or <c>null</c> when it belongs to the house rather than to a room.</summary>
	public string? Room => IsAboutTheHouse ? null : Entry.AreaName;
}

/// <summary>
///     What a row is about, as the activity page's filter chips divide it.
/// </summary>
/// <remarks>
///     <para>
///         Flags, because a report is regularly about more than one thing: movement in a room somebody switched
///         off by hand is both movement and a refusal to act, and the quiet re-check that finds a room dark and
///         blocked is both the lux reading and the reason the light will not come on. A report that could only
///         hold one category would have to pick, and every pick would hide it from the chip somebody was using.
///     </para>
///     <para>
///         <b>A row's categories follow the words the row shows.</b> The filter hides rows, and a row is what it
///         says — so the master switch, which replaces a row's wording outright in <see cref="ActivityView.Describe"/>,
///         replaces its categories too. The alternative is a chip that promises a kind of row and then shows one
///         that says something else entirely.
///     </para>
/// </remarks>
[Flags]
public enum ActivityCategory
{
	/// <summary>No category. Never a report's answer — see <see cref="ActivityView.Categorise"/>.</summary>
	None = 0,

	/// <summary>A motion sensor reported.</summary>
	Movement = 1,

	/// <summary>The engine commanded these lights: on, the circadian re-aim, the warning dim, or off.</summary>
	LightChange = 2,

	/// <summary>
	///     The row states where the room stands against the level it counts as dark, with the reading behind it.
	/// </summary>
	Illumination = 4,

	/// <summary>Somebody set or switched these lights themselves, or a manual change ran its course.</summary>
	ManualChange = 8,

	/// <summary>The engine considered lighting the room and did not — and the row says why.</summary>
	Declined = 16,

	/// <summary>The house emptying or filling, or the master switch.</summary>
	House = 32,

	/// <summary>Housekeeping: rechecks, start-up, and a room switched on or off for automatic lighting.</summary>
	Background = 64,

	/// <summary>
	///     The house moved to a different mode.
	/// </summary>
	/// <remarks>
	///     Its own flag rather than a share of <see cref="House"/>, because the two are read for different
	///     reasons: arriving, leaving and the master switch are things that happened <i>to</i> the house, and a
	///     mode change is the one house-wide event somebody chose. Filed together, the chip that answers "when did
	///     the house go to sleep" also carried every arrival and departure, which on a house with people coming and
	///     going is the answer buried in the noise. Still a house-wide row — see <see cref="ActivityView.Rows"/> —
	///     so one mode change is still one row however many rooms published it.
	/// </remarks>
	Mode = 128
}

/// <summary>
///     One filter chip: the category, what it is called, how many of the reports on offer it holds, and whether
///     it is currently letting them through.
/// </summary>
/// <remarks>
///     The count is over the reports the <i>room</i> filter left, not over the whole buffer, because that is what
///     makes the two filters legible together: choose one room and the chips say what that room has done. A zero
///     is worth rendering rather than hiding — "this room has no illumination rows" is an answer, and a chip that
///     disappeared would leave somebody wondering where the category went.
/// </remarks>
/// <param name="Category">Which category the chip switches.</param>
/// <param name="Label">Its name on the chip.</param>
/// <param name="Title">One line on what falls into it, for the chip's hover explanation.</param>
/// <param name="Count">How many of the reports on offer it holds.</param>
/// <param name="IsOn">Whether it is letting them through.</param>
public sealed record ActivityFilterChip(
	ActivityCategory Category,
	string Label,
	string Title,
	int Count,
	bool IsOn);

/// <summary>
///     A day's worth of the timeline, under the heading the page shows.
/// </summary>
/// <param name="Day">The local date the rows fell on.</param>
/// <param name="Heading">What that date is called on the page — <c>Today</c>, <c>Yesterday</c>, or the date.</param>
/// <param name="Rows">The day's rows, newest first.</param>
public sealed record ActivityDay(DateOnly Day, string Heading, IReadOnlyList<ActivityRow> Rows);

/// <summary>
///     The activity page's decisions, in one testable place: what a report is called, which room it belongs to,
///     and which day it falls under.
/// </summary>
/// <remarks>
///     <para>
///         This repo has no Razor render-test harness and deliberately does not gain one, so the page's judgement
///         lives here as pure functions and the markup only arranges them. <see cref="Describe"/> is the reason
///         the page exists at all: the owner's question was "why didn't that light come on", and the answer is a
///         measured lux reading beside the threshold it was compared against. A wrong line there is worse than no
///         page, because it would be a confident wrong answer.
///     </para>
///     <para>
///         The colour language is the dashboard's, through <see cref="AreaView.Family"/>: a person who learned
///         "amber means somebody touched it" on the cards reads the same fact down this timeline.
///     </para>
/// </remarks>
public static class ActivityView
{
	/// <summary>What the room filter is set to when it is not filtering.</summary>
	public const string AllRooms = "";

	/// <summary>Every category — what <i>show everything</i> puts the chips back to.</summary>
	public const ActivityCategory AllCategories =
		ActivityCategory.Movement
		| ActivityCategory.LightChange
		| ActivityCategory.Illumination
		| ActivityCategory.ManualChange
		| ActivityCategory.Declined
		| ActivityCategory.Mode
		| ActivityCategory.House
		| ActivityCategory.Background;

	/// <summary>
	///     What the page opens with: everything the engine decided, and none of the housekeeping.
	/// </summary>
	/// <remarks>
	///     Background is the only category that starts off. It is by a long way the highest volume — every room
	///     re-checks itself on every tick — and by a long way the lowest signal, and a timeline that opens on a
	///     screenful of "Rechecked the room" is one nobody scans a second time. Every other category is a decision
	///     the engine made or refused to make, and a page about decisions that opened with some of them switched
	///     off would be answering a question nobody asked it.
	/// </remarks>
	public const ActivityCategory DefaultCategories = AllCategories & ~ActivityCategory.Background;

	/// <summary>
	///     What the dashboard's summary carries: the exceptions, not the routine.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Stricter than <see cref="DefaultCategories"/> because it is read in a different place. The summary
	///         sits directly under the board, and the board has already <i>drawn</i> the routine — every lit block,
	///         every movement that lit one, is a mark on a lane an inch above. Repeating those in words underneath
	///         spends all twelve rows saying what the picture said, and the one report somebody actually needed
	///         falls off the bottom.
	///     </para>
	///     <para>
	///         So four categories, each one something the lanes cannot draw: somebody overrode the engine, the
	///         engine declined to light a room and only prose can say why, the house changed mode, or the house
	///         emptied, filled or was switched off. <see cref="ActivityCategory.Movement"/>,
	///         <see cref="ActivityCategory.LightChange"/> and <see cref="ActivityCategory.Illumination"/> are out
	///         because the board is a better answer to them than a sentence is.
	///     </para>
	///     <para>
	///         Nothing is lost by it: the Activity page still opens on <see cref="DefaultCategories"/>, and the
	///         summary's footer says how many reports it is holding back and links there.
	///     </para>
	/// </remarks>
	public const ActivityCategory SummaryCategories =
		ActivityCategory.ManualChange
		| ActivityCategory.Declined
		| ActivityCategory.Mode
		| ActivityCategory.House;

	/// <summary>A category's fixed half: what it is called, and one line on what falls into it.</summary>
	private sealed record CategoryName(ActivityCategory Category, string Label, string Title);

	/// <summary>
	///     The chips, in the order they are shown.
	/// </summary>
	/// <remarks>
	///     The four the owner asked for lead, then the two that answer "why did nothing happen", then the two that
	///     are about the house rather than a room — the chosen change first, the ones that merely happened after it
	///     — then the housekeeping, which is last because it is the one that starts switched off, and a control that
	///     is off belongs at the end of a row rather than as a gap in the middle of one.
	/// </remarks>
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
	///     <para>
	///         Driven by <see cref="TransitionReason"/>, with the state deciding the nuance, because the reason is
	///         the engine's own account of why it published. The one place that ordering is overturned is the
	///         periodic re-check of a room nobody is in: "retuned the lights to the time of day" would be a lie
	///         about a room whose lights are off, and the news in that report is the darkness verdict that just
	///         moved — so the verdict becomes the headline and its reading the line beneath.
	///     </para>
	///     <para>
	///         The master switch outranks every transition: while it is off the engine commands nothing, and a row
	///         that described a transition without saying so would send somebody hunting a room-level fault. It
	///         does not outrank a <i>refused movement</i>, which is not a transition but a report published for the
	///         express purpose of naming what refused — and which names the master switch itself when that is the
	///         answer, so the reason the rule exists is served rather than overridden.
	///     </para>
	///     <para>
	///         Where a row would otherwise say what the engine is about to do, it says it only if the engine has
	///         said so itself — see <see cref="DarkEnough"/>. Softening the wording instead was considered and
	///         rejected: a hedge printed over every house that is merely asleep is a different false statement,
	///         not a smaller one.
	///     </para>
	/// </remarks>
	/// <param name="snapshot">The report to describe.</param>
	/// <returns>The row's two lines.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <c>null</c>.</exception>
	public static ActivityLine Describe(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		// Ahead of the master switch, and it is the one thing allowed ahead of it. The engine publishes movement
		// into a blocked room precisely so this row can exist, and "Paused by the master switch" would answer a
		// question nobody asked while dropping the one fact the row was published to carry: somebody moved. The
		// wording below names the master switch itself when that is what refused, so nothing is lost.
		if (IsDeclinedMotion(snapshot))
			return new ActivityLine(MovementRefusedBy(snapshot), RefusalDetail(snapshot));

		if (snapshot.KillSwitchActive)
			return new ActivityLine("Paused by the master switch", "No lights change until it's turned back on.");

		// A quiet re-check that found the darkness verdict had moved. That verdict is the whole news, so it leads.
		if (snapshot is { Reason: TransitionReason.CircadianTick, State: AreaState.AutoVacant, IsDark: { } dark })
			return new ActivityLine(dark ? DarkEnough(snapshot) : TooBright, Reading(snapshot));

		return new ActivityLine(Headline(snapshot), Condition(snapshot));
	}

	/// <summary>
	///     Only the reports from <paramref name="room"/>, or all of them when nothing is selected.
	/// </summary>
	/// <remarks>
	///     Matched on the display name rather than the area id, because the name is what the filter offers and
	///     what the timeline shows. A room renamed mid-run therefore reads as two rooms in the filter, which is
	///     the honest rendering: the older entries really do say the older name.
	/// </remarks>
	/// <param name="entries">The entries to filter, in the order they should stay in.</param>
	/// <param name="room">The room's display name, or <c>null</c>/empty for every room.</param>
	/// <returns>The matching entries, in the order they were given.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="entries"/> is <c>null</c>.</exception>
	public static IReadOnlyList<ActivityEntry> InRoom(IEnumerable<ActivityEntry> entries, string? room)
	{
		ArgumentNullException.ThrowIfNull(entries);

		if (string.IsNullOrWhiteSpace(room))
			return [.. entries];

		return [.. entries.Where(entry => string.Equals(entry.AreaName, room, StringComparison.OrdinalIgnoreCase))];
	}

	/// <summary>
	///     The rooms the filter offers: the ones that have actually reported, named once each.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Built from the log rather than from the configuration document on purpose. A filter listing rooms
	///         with nothing in the timeline would offer choices that all lead to an empty page, and a room the
	///         document no longer mentions still has entries worth finding.
	///     </para>
	///     <para>
	///         Named once each by <see cref="InRoom"/>'s own equality, not by the culture's: the two have to agree
	///         about what "the same room" is or an option stops meaning a filter. Case-sensitive de-duplication
	///         offered a room renamed only in its capitalisation twice, both leading to the same list; and the
	///         culture comparer's looser equality could collapse two names into one option that the filter — which
	///         is ordinal — would then match only half the entries of, quietly hiding the rest. Sorted in the
	///         reader's culture all the same: which names are the same room and what order they read in are
	///         different questions.
	///     </para>
	/// </remarks>
	/// <param name="entries">The entries to read the names from.</param>
	/// <returns>Distinct display names, alphabetical in the reader's culture.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="entries"/> is <c>null</c>.</exception>
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
	///     Whether a report is worth a row at all.
	/// </summary>
	/// <remarks>
	///     <para>
	///         One rule, and it is about start-up. The engine publishes one report per room when it starts, and the
	///         row for it reads "Started up — took the room as it was". Where the room had something to say for
	///         itself the row says it underneath — <i>too bright to switch on, lux 4096 (mean of 2 of 2 sensors),
	///         dark below 40</i> — and that is worth keeping: it is twice this month the answer to what a room saw
	///         at boot. Where it did not, the row is the sentence and nothing else, which is not an event. It is the
	///         engine saying it did nothing, once per room, at the top of every restart.
	///     </para>
	///     <para>
	///         <b>Decided here rather than by publishing fewer snapshots.</b> The board's lanes, the room cards and
	///         the room pages all read those per-area reports, and a start-up snapshot is where a lane's first block
	///         begins. Dropping it at the source would answer a wording problem by losing data three other surfaces
	///         are drawn from.
	///     </para>
	/// </remarks>
	/// <param name="snapshot">The report to judge.</param>
	/// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <c>null</c>.</exception>
	public static bool IsWorthShowing(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		// The master switch replaces a start-up row's words with a sentence about the whole house, and that one is
		// news whatever else the report carries.
		if (snapshot.Reason is not TransitionReason.Startup || snapshot.KillSwitchActive)
			return true;

		return Describe(snapshot).Why is { Length: > 0 };
	}

	/// <summary>
	///     Only the reports worth a row, in the order they were given.
	/// </summary>
	/// <remarks>
	///     Applied where a page reads the log for the <i>record</i> — the activity timeline, the dashboard's summary
	///     and a room's own list — and deliberately not where the dashboard reads it for the board, whose lanes are
	///     drawn from every report the engine published. Applied before the counts, so what the page says it is
	///     holding and what it draws are one number.
	/// </remarks>
	/// <param name="entries">The entries to sift.</param>
	/// <returns>The entries worth showing, in the order they were given.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="entries"/> is <c>null</c>.</exception>
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
	///     <para>
	///         Derived from <see cref="TransitionReason"/> and <see cref="AreaState"/> — the two things the engine
	///         publishes about why it moved — and written to track <see cref="Describe"/> branch for branch, so a
	///         chip and the row it lets through can never be about different things. <b>Every pair lands in at
	///         least one category, and a test walks all of them:</b> a report no chip can reach is a report the
	///         page has hidden, which on the one page that exists to explain a missing light is the same failure
	///         as losing it.
	///     </para>
	///     <para>
	///         There is deliberately no catch-all. A reason added to the enum later has to be placed here by hand,
	///         and until it is, the exhaustive test fails rather than the page quietly swallowing its rows.
	///     </para>
	///     <para>
	///         Categories overlap on purpose. A room too bright to light is both the lux reading somebody came for
	///         and the reason nothing happened, and each chip has to be complete on its own: a person who ticks
	///         only <i>Nothing happened</i> is asking for every refusal, not for the ones that no other chip
	///         happened to claim.
	///     </para>
	/// </remarks>
	/// <param name="snapshot">The report to place.</param>
	/// <returns>Every category it belongs to. Never <see cref="ActivityCategory.None"/>.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <c>null</c>.</exception>
	public static ActivityCategory Categorise(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		// The master switch replaces the row's words outright, so it replaces its categories too: the row says
		// "Paused by the master switch" and nothing about movement, lights or lux, and a chip that offered it as
		// one of those would be pointing at a sentence that does not mention them. A refused movement is the one
		// row it does not replace — Describe keeps that one's words — so it keeps its own categories as well.
		if (snapshot.KillSwitchActive && !IsDeclinedMotion(snapshot))
			return ActivityCategory.House;

		// Start-up decided nothing: the engine took every room as it found it, and the condition under the row is a
		// reading of what boot happened to walk into. Housekeeping outright, therefore, and never the darkness or
		// refusal chips — those promise a room the engine has just made its mind up about, and a person who ticks
		// them to find out why a light stayed off is not asking what the house looked like when the add-on started.
		// The row is only drawn at all when it carries that condition; see IsWorthShowing.
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

		// A refused movement that reached here past the master-switch return says so in its own words, and the
		// words are what the chips follow: the row names the master switch, so the house chip must offer it.
		if (snapshot.KillSwitchActive)
			categories |= ActivityCategory.House;

		// Start-up and a room's own switch are housekeeping outright. A tick is housekeeping only when nothing
		// above wanted it: the same reason carries the circadian re-aim and the dusk verdict, and those are the
		// two rows this page exists for — miscounting either as background would hide them by default.
		if (snapshot.Reason is TransitionReason.AdoptedAtStartup
			or TransitionReason.EnablementChanged
			|| (snapshot.Reason is TransitionReason.CircadianTick && categories == ActivityCategory.None))
			categories |= ActivityCategory.Background;

		return categories;
	}

	/// <summary>
	///     Only the reports in <paramref name="categories"/>, in the order they were given.
	/// </summary>
	/// <remarks>
	///     Applied to a list already read out of <see cref="ActivityLog"/>, never inside its lock. The log hands
	///     over the entries and the sequence together for a reason, and a filtered read would have to choose one
	///     of the two to be right about.
	/// </remarks>
	/// <param name="entries">The entries to filter.</param>
	/// <param name="categories">The categories that are switched on.</param>
	/// <returns>The matching entries, in the order they were given.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="entries"/> is <c>null</c>.</exception>
	public static IReadOnlyList<ActivityEntry> InCategories(IEnumerable<ActivityEntry> entries, ActivityCategory categories)
	{
		ArgumentNullException.ThrowIfNull(entries);

		if (categories == AllCategories)
			return [.. entries];

		return [.. entries.Where(entry => (Categorise(entry.Snapshot) & categories) != ActivityCategory.None)];
	}

	/// <summary>
	///     The chips the page draws: every category, its count within what is on offer, and whether it is on.
	/// </summary>
	/// <remarks>
	///     Every category is always offered, including the ones holding nothing. A chip that vanished when its
	///     count reached zero would take the reader's map of the page with it, and "this room has never reported
	///     a manual change" is an answer worth being able to read.
	/// </remarks>
	/// <param name="entries">The reports on offer — the ones the room filter left, so the counts describe the room.</param>
	/// <param name="chosen">The categories that are switched on.</param>
	/// <returns>One chip per category, in display order.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="entries"/> is <c>null</c>.</exception>
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
	/// <remarks>
	///     A filtered timeline that looks like an empty one is the same failure as a report the log lost: somebody
	///     reads "nothing" and stops looking. So the page says how many reports are behind the filters and which
	///     filter is holding them, and offers the one tap back. <c>null</c> for "nothing hidden" keeps that
	///     decision here rather than as a condition in the markup that could disagree with the wording beside it.
	/// </remarks>
	/// <param name="held">How many reports the timeline is holding.</param>
	/// <param name="shown">How many of them survived both filters.</param>
	/// <param name="room">The chosen room, or <c>null</c>/empty for every room.</param>
	/// <param name="categories">The categories that are switched on.</param>
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
	///     Whether this report is the engine's own command reaching the lights.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Read off the reason and the state rather than off <see cref="AreaSnapshot.LastCommandAt"/>. That
	///         timestamp is set an instant before the snapshot is built, so on a real clock the two differ by
	///         microseconds and any threshold comparing them would be a guess with a number in it.
	///     </para>
	///     <para>
	///         The leaving sweep is the one command deliberately left out. An away scene, or the room's own
	///         opt-out, holds it, and the report does not say which happened — so it stays a house event rather
	///         than a claim about lights that may never have moved.
	///     </para>
	/// </remarks>
	private static bool CommandedTheLights(AreaSnapshot snapshot) => snapshot.Reason switch
	{
		// The vacancy pair is nothing but commands: the warning dim, and then the lights out.
		TransitionReason.VacancyTimeout or TransitionReason.PreOffElapsed => true,

		// An override running out hands the room back by commanding it — retuned if somebody is still in it,
		// off if nobody is.
		TransitionReason.OverrideExpired => true,

		// Start-up takes the room as it found it, a scene hold is the engine standing back, a manual change is
		// somebody else's command, a suppression lifting only stops ignoring movement, and switching a room on
		// or off changes what the engine may do rather than what the lights are doing.
		TransitionReason.Startup
			or TransitionReason.AdoptedAtStartup
			or TransitionReason.SceneHold
			or TransitionReason.ManualOn
			or TransitionReason.ManualOff
			or TransitionReason.SuppressionLifted
			or TransitionReason.EnablementChanged
			or TransitionReason.EveryoneLeft => false,

		// What is left — movement, the tick, a mode switch, the first arrival — commands exactly where it left
		// the room lit and aimed, which is what AutoActive means and what the row then says in words.
		_ => snapshot.State is AreaState.AutoActive
	};

	/// <summary>
	///     Whether the row states where the room stands against the level it counts as dark.
	/// </summary>
	/// <remarks>
	///     The two places <see cref="Describe"/> says it: as the headline, when a quiet re-check found the verdict
	///     had moved, and under the headline, when a vacant room is too bright to light. A dark vacant room that
	///     reached that state some other way says nothing about darkness, and must not be offered as though it
	///     had — this is the chip somebody opens holding "why did the hall light not come on", and it has to be
	///     the rows that answer it rather than the rows that merely could have.
	/// </remarks>
	private static bool NamesTheDarknessVerdict(AreaSnapshot snapshot) =>
		snapshot.State is AreaState.AutoVacant
		&& (snapshot.IsDark is false
			|| (snapshot.IsDark is true && snapshot.Reason is TransitionReason.CircadianTick));

	/// <summary>
	///     Whether the engine could have lit the room and did not.
	/// </summary>
	/// <remarks>
	///     Three refusals, and the row names all three: movement that lit nothing because somebody's own decision
	///     is standing; a room dark and waiting but held off by a sleeping house or a named entity, which
	///     <see cref="BoardView.IsBlockedFromLighting"/> decides for the board as well so the two surfaces cannot
	///     disagree; and a room simply too bright. A room whose darkness has never been checked is not a refusal —
	///     nothing has been decided there yet, and saying otherwise would invent a verdict.
	/// </remarks>
	private static bool WasDeclined(AreaSnapshot snapshot) =>
		(snapshot.Reason is TransitionReason.Motion && snapshot.State is not AreaState.AutoActive)
		|| BoardView.IsBlockedFromLighting(snapshot)
		|| (snapshot.State is AreaState.AutoVacant && snapshot.IsDark is false);

	// ===================== one row per thing that happened =====================

	/// <summary>
	///     How far apart two reports of the same house-wide event may be and still be one row.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Thirty seconds. The reports this collapses are published in one pass over the areas and arrive
	///         inside the same second, so the window is not sizing the burst — it is the bound on how wrong a
	///         delivery can go before two events are treated as one. A house whose event stream is running half a
	///         minute late is still collapsed correctly; two genuinely separate changes half a minute apart are not.
	///     </para>
	///     <para>
	///         It is a smaller guarantee than it looks, because only a <i>run</i> collapses: anything else in the
	///         record between two identical house events already separates them, whatever the clock says. The
	///         window only decides the case where nothing else happened in between — a mode set to Home, and set to
	///         Home again an hour later with a silent house between them, which are two events and read as two.
	///     </para>
	/// </remarks>
	public static readonly TimeSpan CollapseWindow = TimeSpan.FromSeconds(30);

	/// <summary>
	///     Whether this report's words are about the whole house rather than about the room that published it.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>Decided on the words, not on the cause.</b> Every reason in <see cref="TransitionReason"/> was
	///         read against what <see cref="Describe"/> actually prints for it, and only four sentences speak about
	///         the house: the master switch being off, a change of mode, the house emptying, and the first arrival.
	///     </para>
	///     <para>
	///         Three near misses are deliberately left out, and each would have been a worse row than the one it
	///         replaced. <see cref="TransitionReason.SceneHold"/> is filed under the house <i>chip</i> but says "a
	///         guest scene took <b>this room</b> over" — the mode is house-wide, the takeover is not, and rooms
	///         enter and leave the hold at different moments. <see cref="TransitionReason.EnablementChanged"/> is
	///         raised on every area when the master switch moves, but its words are "automatic lighting was
	///         switched on for this room", which is a per-room fact even though a house-wide action caused it — and
	///         the report that <i>does</i> speak for the house on that path, the one taken while the switch is off,
	///         is the first case below. <see cref="TransitionReason.Startup"/> likewise arrives once per room and
	///         says what was found in each: rooms whose lights were already on are not the same news as rooms whose
	///         were not, and merging them would lose exactly the distinction the two sentences exist to draw.
	///     </para>
	/// </remarks>
	/// <param name="snapshot">The report to place.</param>
	/// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <c>null</c>.</exception>
	public static bool IsAboutTheHouse(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		// The master switch replaces the row's words with a sentence about the whole house, and this takes the same
		// branch Describe takes — including its one exception, a refused movement, whose words stay about the room
		// somebody was walking through.
		if (snapshot.KillSwitchActive && !IsDeclinedMotion(snapshot))
			return true;

		return snapshot.Reason is TransitionReason.HouseModeChanged
			or TransitionReason.EveryoneLeft
			or TransitionReason.FirstPersonArrived;
	}

	/// <summary>
	///     A house-wide row's words: the report's own, minus the account of the room that published it.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The second line under a mode change, an emptying or a first arrival is <see cref="Condition"/>'s
	///         verdict on the <i>publishing room</i> — "too bright to switch on", "darkness hasn't been checked
	///         here yet". True of that room, and unattributable the moment the row stops belonging to one: printed
	///         over a row headed "House" it asks the reader which room "here" is, and answering that is the job the
	///         row has just given up.
	///     </para>
	///     <para>
	///         The master switch keeps its own second line. That sentence — no lights will change until it is turned
	///         back on — is about the whole house, and <see cref="Describe"/> does not build it from
	///         <see cref="Condition"/> at all, which is exactly the distinction being drawn here.
	///     </para>
	///     <para>
	///         Dropping it is also what lets one mode change be one row. The rooms of a real house are in different
	///         states when the mode moves, so their conditions differ, and a collapse that kept them would split a
	///         single event into a row per condition — the defect again, with a smaller number.
	///     </para>
	/// </remarks>
	private static ActivityLine LineFor(AreaSnapshot snapshot)
	{
		ActivityLine line = Describe(snapshot);

		return IsAboutTheHouse(snapshot) && !snapshot.KillSwitchActive
			? line with { Why = null }
			: line;
	}

	/// <summary>
	///     Whether the report is the standing darkness verdict a quiet re-check republishes on every tick.
	/// </summary>
	/// <remarks>
	///     Not an event but a state, which is why it is collapsed. The engine re-checks each room on every tick and
	///     publishes what it found, so a room that goes dark at dusk and stays dark says "dark enough now" once a
	///     minute until dawn — which is what made the line meaningless to read. The verdict itself is still worth a
	///     row; the four hundred repeats of it are not.
	/// </remarks>
	private static bool IsStandingVerdict(AreaSnapshot snapshot) =>
		snapshot is { Reason: TransitionReason.CircadianTick, State: AreaState.AutoVacant, IsDark: not null }
		&& !snapshot.KillSwitchActive;

	/// <summary>
	///     Turns reports into the rows a page renders: one row per report, except that a run of reports saying the
	///     same thing becomes a single row carrying the newest of them.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>Two runs collapse, and they are the same idea read over different sequences.</b> House-wide
	///         reports collapse over the whole record: one change of mode is published by every switched-on room,
	///         so the run is consecutive and the row belongs to the house. The standing darkness verdict collapses
	///         over <i>one room's own</i> reports: every room ticks in the same pass, so a room's repeats are never
	///         adjacent in the record, and the other rooms' reports are stepped over rather than counted as
	///         something happening in between. Either way the newest of the run is the row, which is what keeps the
	///         reading on it current.
	///     </para>
	///     <para>
	///         <b>What "the same thing" means differs between the two, and both are the honest test.</b> Two
	///         house-wide reports collapse only when their whole rendered lines match, so the row never says
	///         anything that was not in every report behind it; matching on the reason alone would have merged "the
	///         house changed mode to Home" with "…to Guests". A darkness verdict is matched on its headline alone,
	///         because the line underneath is the live reading and moves by a few lux every tick — requiring that to
	///         match as well would collapse nothing at all, which is the defect rather than the fix.
	///     </para>
	///     <para>
	///         Only a consecutive run collapses, never a scattered set. The record's order is the reader's account
	///         of what followed what, and lifting a row out of the middle of it to join one further up would rewrite
	///         that account — the two rows would have become one in a place where, on the evidence, something else
	///         happened between them. For a room's verdict that means anything else <i>from that room</i> ends the
	///         run: movement, a hand on a switch, a restart. The verdict then reads again below it, which is right —
	///         something happened in that room, and the record says what the room looked like on either side of it.
	///     </para>
	///     <para>
	///         <paramref name="limit"/> exists for the dashboard, which wants a dozen rows out of a buffer of five
	///         hundred reports and re-reads it once a second. The run in progress is always finished before the
	///         count is honoured, so the last row on a limited read names every room it covers rather than however
	///         many happened to fit.
	///     </para>
	/// </remarks>
	/// <param name="entries">The reports, newest first — already through whatever filters the page applies.</param>
	/// <param name="limit">How many rows to build at most, or <c>null</c> for all of them.</param>
	/// <returns>The rows, newest first.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="entries"/> is <c>null</c>.</exception>
	public static IReadOnlyList<ActivityRow> Rows(IEnumerable<ActivityEntry> entries, int? limit = null)
	{
		ArgumentNullException.ThrowIfNull(entries);

		if (limit is <= 0)
			return [];

		List<ActivityEntry> ordered = [.. entries];

		// Swallowed by a run above them, and memoised words. A limited read builds a dozen rows out of five hundred
		// reports once a second, so the line each report renders to is worked out at most once whichever run asks.
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
				// Absolute difference rather than a subtraction: the list is newest first by sequence, and a report
				// whose timestamp disagrees with its position must not be swept in by an interval that went negative.
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
			// Matched on the display name, as InRoom and the room filter are: the three have to agree about what
			// "the same room" is, or a run would be assembled from rooms the filter would then not put together.
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

	/// <summary>
	///     Which rooms a house-wide row was assembled from, or <c>null</c> when the row already names its room.
	/// </summary>
	/// <remarks>
	///     Answered for a single room as well as for several, because a house-wide row shows no room name at all —
	///     so without this, the one thing a reader could no longer find out is which rooms the row was built from.
	///     That is the whole price of dropping the attribution, and it is worth paying only because it is paid
	///     into a hover rather than into nothing.
	/// </remarks>
	/// <param name="row">The row to describe.</param>
	/// <exception cref="ArgumentNullException"><paramref name="row"/> is <c>null</c>.</exception>
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
	///     <para>
	///         Local dates, from both the rows and <paramref name="now"/>, so a heading says the day the person
	///         reading it lived through rather than the day UTC was having. Rows are grouped consecutively, not
	///         collected: a newest-first list visits each day exactly once, and grouping consecutively preserves
	///         the order rather than re-sorting it behind the caller's back.
	///     </para>
	///     <para>
	///         Rows rather than reports, because collapsing has to happen first: a house-wide run that straddles
	///         midnight is one event, and cutting it into days before collapsing it would put half of it under each
	///         heading and call that two.
	///     </para>
	///     <para>
	///         Only the two days a person thinks of by name get one. Anything older is dated, because "3 days ago"
	///         is arithmetic somebody has to do twice — once to read it, once to check it against a clock.
	///     </para>
	/// </remarks>
	/// <param name="rows">The rows, newest first.</param>
	/// <param name="now">The reader's present, for deciding what "today" is.</param>
	/// <returns>One group per day, in the order the rows arrived in.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="rows"/> is <c>null</c>.</exception>
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

	/// <summary>What a day is called on the page.</summary>
	/// <param name="day">The day to name.</param>
	/// <param name="today">The reader's today.</param>
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

	/// <summary>
	///     What the room being too bright is called, in the one place it is written down.
	/// </summary>
	/// <remarks>
	///     It reads as the headline when the verdict is the news and as the condition under someone else's headline,
	///     and it used to be worded twice — "too bright to switch the lights on" above, "too bright to switch on"
	///     below. One condition, one sentence; the shorter one, because the second place it appears is already
	///     under a line that has said what happened.
	/// </remarks>
	private const string TooBright = "Too bright to switch on";

	/// <summary>
	///     The event itself, from the engine's own reason for publishing.
	/// </summary>
	/// <remarks>
	///     Written to be read at a glance down a twelve-row summary, so each line carries the event and stops. What
	///     a line may not trade for brevity is precision: which room, which gate and which measured number are the
	///     whole reason somebody opened the page, and a shorter line that is vaguer about them is worse than a long
	///     one.
	/// </remarks>
	private static string Headline(AreaSnapshot snapshot) => snapshot.Reason switch
	{
		TransitionReason.Startup => "Started up — took the room as it was",
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
		TransitionReason.HouseModeChanged => snapshot.HouseModeValue is { Length: > 0 } value
			? $"Mode changed to {value}"
			: "The house changed mode",
		TransitionReason.SceneHold => snapshot.State == AreaState.SceneHold
			? "A guest scene has this room"
			: "The guest scene let this room go",
		_ => snapshot.Reason.ToString()
	};

	/// <summary>
	///     The condition worth naming under the event, most consequential first: the room being switched off
	///     explains everything after it, an empty house explains the rest, and the darkness gate is what a person
	///     is looking for when they ask why nothing happened.
	/// </summary>
	/// <remarks>
	///     <b>The block is named on every row, not only on the dusk verdict's own.</b>
	///     <see cref="WasDeclined"/> files a room held off by a sleeping house or a blocking entity under
	///     <see cref="ActivityCategory.Declined"/> whatever the reason for the report, and that chip promises the
	///     reason on the row. It was only ever there when the reason happened to be
	///     <see cref="TransitionReason.CircadianTick"/>, because that is the one branch <see cref="Describe"/>
	///     hands to <see cref="DarkEnough"/>; the other fourteen produced a row filed under "nothing happened"
	///     that said nothing about anything happening. <see cref="RoomFacts.NextLine"/> already answers a gated
	///     room ahead of everything its state would otherwise say, for the same reason.
	/// </remarks>
	private static string? Condition(AreaSnapshot snapshot) => snapshot.State switch
	{
		AreaState.Disabled => "Automatic lighting is off here.",
		AreaState.Away => "Nobody home — waiting for the first arrival.",

		// Through DarkEnough rather than worded again, so the chip, this row and the dusk row it sits above all
		// say one thing about one condition.
		AreaState.AutoVacant when BoardView.IsBlockedFromLighting(snapshot) => $"{DarkEnough(snapshot)}.",

		AreaState.AutoVacant when snapshot.IsDark is false =>
			Reading(snapshot) is { Length: > 0 } reading
				? $"{TooBright} — {reading}"
				: $"{TooBright}.",
		AreaState.AutoVacant when snapshot.IsDark is null => "Darkness not checked here yet.",
		_ => null
	};

	/// <summary>
	///     What "dark enough" is worth to this room — which is not always a light.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>The sentence this replaced was false.</b> Darkness is one of five gates on auto-on, and two of
	///         the other four leave the room sitting in <see cref="AreaState.AutoVacant"/>, indistinguishable from
	///         a room merely waiting for somebody to walk in: a sleeping house over a room set not to light itself,
	///         and one of the room's own blocking entities being on. Auto-discovery sets the first on every bedroom
	///         it finds, so "movement will switch the lights on" was wrong in every bedroom in the house every
	///         night — and printed at dusk, which is the moment somebody comes here asking why the room stayed dark.
	///     </para>
	///     <para>
	///         Read from the verdict the engine published rather than worked out again here. The engine is the only
	///         thing that knows which gates it consulted, and a second copy of those rules would drift from the one
	///         it acts on — which is how this class of bug is born. A report from a build that predates the verdict
	///         carries none, and the row then says exactly what it always said: an older payload can support a new
	///         claim no better in the negative than in the positive.
	///     </para>
	/// </remarks>
	private static string DarkEnough(AreaSnapshot snapshot) => snapshot.AutoOnBlockedBy switch
	{
		AutoOnBlock.Sleep => "Dark enough, but the house is asleep — movement won't light the room",
		AutoOnBlock.EntityOn => snapshot.AutoOnBlockingEntity is { Length: > 0 } blocker
			? $"Dark enough, but {blocker} is on — movement won't light the room"
			: "Dark enough, but something here is on — movement won't light the room",
		_ => "Dark enough — movement will light the room"
	};

	/// <summary>
	///     Whether this report is movement the engine turned down, with the gate that turned it down named.
	/// </summary>
	/// <remarks>
	///     The engine publishes one of these per change of the refusing gate rather than one per movement, so a
	///     person pacing under an unchanged block is one row and not forty. A report from a build that predates
	///     <see cref="AreaSnapshot.AutoOnBlockedBy"/> carries none, and reads exactly as it always did — an older
	///     payload cannot support this claim any better than it can support its opposite.
	/// </remarks>
	/// <remarks>
	///     Internal rather than private because the board draws a mark for the same reports, and two copies of
	///     "was this movement turned down" would drift — the timeline and the log would then disagree about a
	///     refusal, which is the one thing both surfaces exist to explain.
	/// </remarks>
	internal static bool IsDeclinedMotion(AreaSnapshot snapshot) =>
		snapshot.Reason is TransitionReason.Motion
		&& snapshot.State is not AreaState.AutoActive
		&& snapshot.AutoOnBlockedBy is { } block
		&& block is not AutoOnBlock.None;

	/// <summary>
	///     Movement, and the plain sentence for whichever gate stopped it.
	/// </summary>
	/// <remarks>
	///     One sentence per value of <see cref="AutoOnBlock"/>, because the whole point of the report is that the
	///     reason reaches the reader. The two refusals a room can hide — a sleeping house and a blocking entity —
	///     are worded as <see cref="RoomFacts"/> and <see cref="BoardView"/> word them, so a person who learned the
	///     phrase on one surface reads the same fact on this one.
	/// </remarks>
	private static string MovementRefusedBy(AreaSnapshot snapshot) => snapshot.AutoOnBlockedBy switch
	{
		AutoOnBlock.KillSwitch => "Movement, but the master switch is off",
		AutoOnBlock.Disabled => "Movement, but automatic lighting is off here",
		AutoOnBlock.Away => "Movement, but nobody is home yet",
		AutoOnBlock.SceneHold => "Movement, but a guest scene has this room",
		AutoOnBlock.Sleep => "Movement, but the house is asleep and this room stays dark",
		AutoOnBlock.EntityOn => snapshot.AutoOnBlockingEntity is { Length: > 0 } blocker
			? $"Movement, but {blocker} is on"
			: "Movement, but something here is on",
		AutoOnBlock.NotDark => "Movement, but the room is bright enough",

		// Unreachable while IsDeclinedMotion guards this: None and null are both excluded there. Worded rather
		// than thrown, because a row that says less is a better failure than a page that does not render.
		_ => "Movement"
	};

	/// <summary>
	///     The gate that turned a movement down, in the fewest words that still name it — for the board's mark,
	///     which has a lane's width rather than a row's.
	/// </summary>
	/// <remarks>
	///     A clause rather than a sentence, because it is read after a time and the word <c>movement</c>:
	///     <c>18:04 movement, too bright</c>. Derived from the same <see cref="AutoOnBlock"/> the log's full
	///     sentence uses, so the mark and the row it sends the reader to can never name different causes — but
	///     deliberately not built by trimming that sentence, which would make every wording change a two-place
	///     edit with one place easy to miss.
	/// </remarks>
	/// <param name="snapshot">A report <see cref="IsDeclinedMotion"/> has already accepted.</param>
	/// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <c>null</c>.</exception>
	internal static string RefusalReason(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return snapshot.AutoOnBlockedBy switch
		{
			AutoOnBlock.KillSwitch => "the master switch is off",
			AutoOnBlock.Disabled => "automatic lighting is off here",
			AutoOnBlock.Away => "nobody home yet",
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
	///     What goes under a refused movement: the reading, and only where the reading is what refused.
	/// </summary>
	/// <remarks>
	///     Every other gate has already named itself in the headline, and repeating the lux figure beside "the
	///     house is asleep" invites the reader to blame the sensor for a decision the house mode made.
	/// </remarks>
	private static string? RefusalDetail(AreaSnapshot snapshot) =>
		snapshot.AutoOnBlockedBy is AutoOnBlock.NotDark ? Reading(snapshot) : null;

	/// <summary>
	///     The darkness gate's own words — the measured reading and the threshold it was compared against.
	/// </summary>
	/// <remarks>
	///     Passed through rather than rebuilt. The gate is the only thing that knows which source is in use, and a
	///     second opinion assembled here would eventually disagree with the one the engine acted on. A report from
	///     a build that predates the field carries none, and the row then says less rather than guessing.
	/// </remarks>
	private static string? Reading(AreaSnapshot snapshot) =>
		snapshot.DarknessDetail is { Length: > 0 } detail ? detail : null;

	/// <summary>
	///     Appends the levels the engine is holding, when it is holding any — the same phrasing the cards use.
	/// </summary>
	/// <remarks>
	///     Lights the engine adopted at start-up have no command behind them, so there are no levels to name and
	///     the headline stands alone rather than reporting numbers nobody chose.
	/// </remarks>
	private static string Lit(string headline, AreaSnapshot snapshot)
	{
		if (snapshot.BrightnessPct is not { } brightness)
			return headline;

		return snapshot.ColorTempKelvin is { } kelvin
			? $"{headline} at {brightness:0} %, {kelvin} K"
			: $"{headline} at {brightness:0} %";
	}
}
