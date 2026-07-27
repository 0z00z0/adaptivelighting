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
///     A day's worth of the timeline, under the heading the page shows.
/// </summary>
/// <param name="Day">The local date the entries fell on.</param>
/// <param name="Heading">What that date is called on the page — <c>Today</c>, <c>Yesterday</c>, or the date.</param>
/// <param name="Entries">The day's entries, newest first.</param>
public sealed record ActivityDay(DateOnly Day, string Heading, IReadOnlyList<ActivityEntry> Entries);

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
	///         The master switch outranks everything: while it is off the engine commands nothing, and a row that
	///         described a transition without saying so would send somebody hunting a room-level fault.
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

		if (snapshot.KillSwitchActive)
			return new ActivityLine("Paused by the master switch", "No lights will change until it is turned back on.");

		// A quiet re-check that found the darkness verdict had moved. That verdict is the whole news, so it leads.
		if (snapshot is { Reason: TransitionReason.CircadianTick, State: AreaState.AutoVacant, IsDark: { } dark })
		{
			return new ActivityLine(
				dark ? DarkEnough(snapshot) : "Too bright to switch the lights on",
				Reading(snapshot));
		}

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

	/// <summary>
	///     Cuts the timeline into days, keeping the order it was given.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Local dates, from both the entries and <paramref name="now"/>, so a heading says the day the person
	///         reading it lived through rather than the day UTC was having. Entries are grouped consecutively, not
	///         collected: a newest-first list visits each day exactly once, and grouping consecutively preserves
	///         the order rather than re-sorting it behind the caller's back.
	///     </para>
	///     <para>
	///         Only the two days a person thinks of by name get one. Anything older is dated, because "3 days ago"
	///         is arithmetic somebody has to do twice — once to read it, once to check it against a clock.
	///     </para>
	/// </remarks>
	/// <param name="entries">The entries, newest first.</param>
	/// <param name="now">The reader's present, for deciding what "today" is.</param>
	/// <returns>One group per day, in the order the entries arrived in.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="entries"/> is <c>null</c>.</exception>
	public static IReadOnlyList<ActivityDay> GroupByDay(IEnumerable<ActivityEntry> entries, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(entries);

		DateOnly today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
		List<ActivityDay> days = [];
		List<ActivityEntry> current = [];
		DateOnly day = default;

		foreach (ActivityEntry entry in entries)
		{
			DateOnly entryDay = DateOnly.FromDateTime(entry.At.ToLocalTime().DateTime);

			if (current.Count > 0 && entryDay != day)
			{
				days.Add(new ActivityDay(day, DayHeading(day, today), current));
				current = [];
			}

			day = entryDay;
			current.Add(entry);
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
	///     The event itself, from the engine's own reason for publishing.
	/// </summary>
	private static string Headline(AreaSnapshot snapshot) => snapshot.Reason switch
	{
		TransitionReason.Startup => "Started up and took the room as it was",
		TransitionReason.AdoptedAtStartup => "Started up and found these lights already on",
		TransitionReason.Motion => snapshot.State switch
		{
			AreaState.AutoActive => Lit("Movement — lights on", snapshot),
			AreaState.SuppressedOff => "Movement, but these lights were switched off by hand",
			AreaState.OverriddenOn => "Movement while the hand-set levels stand",
			_ => "Movement"
		},
		TransitionReason.VacancyTimeout => Lit("No movement for a while — dimmed as a warning", snapshot),
		TransitionReason.PreOffElapsed => "Nobody answered the dim warning, so the lights went off",
		TransitionReason.ManualOn => "Someone set the lights by hand",
		TransitionReason.ManualOff => "Someone switched the lights off by hand",
		TransitionReason.OverrideExpired => "The hand-set levels ran their course",
		TransitionReason.SuppressionLifted => "Quiet long enough — back under automatic control",
		TransitionReason.EveryoneLeft => "The last person left the house",
		TransitionReason.FirstPersonArrived => "The first person came home",
		TransitionReason.EnablementChanged => snapshot.State == AreaState.Disabled
			? "Automatic lighting was switched off for this room"
			: "Automatic lighting was switched on for this room",
		TransitionReason.CircadianTick => snapshot.State == AreaState.AutoActive
			? Lit("Retuned the lights to the time of day", snapshot)
			: "Rechecked the room",
		TransitionReason.HouseModeChanged => snapshot.HouseModeValue is { Length: > 0 } value
			? $"The house changed mode to {value}"
			: "The house changed mode",
		TransitionReason.SceneHold => snapshot.State == AreaState.SceneHold
			? "A guest scene took this room over"
			: "The guest scene let this room go",
		_ => snapshot.Reason.ToString()
	};

	/// <summary>
	///     The condition worth naming under the event, most consequential first: the room being switched off
	///     explains everything after it, an empty house explains the rest, and the darkness gate is what a person
	///     is looking for when they ask why nothing happened.
	/// </summary>
	private static string? Condition(AreaSnapshot snapshot) => snapshot.State switch
	{
		AreaState.Disabled => "Automatic lighting is switched off for this room.",
		AreaState.Away => "Nobody home — the room waits for the first arrival.",
		AreaState.AutoVacant when snapshot.IsDark is false =>
			Reading(snapshot) is { Length: > 0 } reading
				? $"Too bright to switch on — {reading}"
				: "Too bright to switch on.",
		AreaState.AutoVacant when snapshot.IsDark is null => "Darkness hasn't been checked here yet.",
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
		AutoOnBlock.Sleep => "Dark enough now, but the house is asleep — movement will not switch the lights on",
		AutoOnBlock.EntityOn => snapshot.AutoOnBlockingEntity is { Length: > 0 } blocker
			? $"Dark enough now, but {blocker} is on — movement will not switch the lights on"
			: "Dark enough now, but something here is on — movement will not switch the lights on",
		_ => "Dark enough now — movement will switch the lights on"
	};

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
