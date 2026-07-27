using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The activity page's buffer and its words, tested where they live rather than in markup.
/// </summary>
/// <remarks>
///     <para>
///         This repo has no Razor render-test harness and deliberately does not gain one, so the timeline's
///         judgement was written as pure functions and only its arrangement lives in the page. Five things are
///         worth being sure about: the buffer really is bounded (an unbounded one is a leak that only shows up
///         on the houses that have been running longest), it evicts oldest-first (or the page silently loses the
///         wrong end of the history), the entries and the count that goes with them come back from one read (or a
///         report landing between two is counted as shown while missing from what was shown), the room filter
///         matches what the dropdown offered, and the line built from a report says what the engine actually saw.
///     </para>
///     <para>
///         That last one is the feature's reason for existing. The owner's question was why a light did not come
///         on, and the answer was a lux reading against a threshold; a page that shows a confident wrong sentence
///         there is worse than no page.
///     </para>
/// </remarks>
[TestClass]
public sealed class ActivityLogTests
{
	private static readonly DateTimeOffset Noon = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

	private static AreaSnapshot Report(
		string area,
		AreaState state = AreaState.AutoVacant,
		TransitionReason reason = TransitionReason.CircadianTick,
		DateTimeOffset? at = null,
		bool? isDark = null,
		string? darknessDetail = null,
		bool killSwitch = false,
		double? brightness = null,
		int? kelvin = null,
		HouseMode mode = HouseMode.Home,
		string? houseModeValue = null,
		AutoOnBlock? autoOnBlockedBy = null,
		string? autoOnBlockingEntity = null) =>
		new(
			area,
			state,
			reason,
			mode,
			killSwitch,
			isDark,
			null,
			brightness,
			kelvin,
			at ?? Noon,
			null,
			null,
			null,
			null,
			houseModeValue,
			darknessDetail,
			null,
			autoOnBlockedBy,
			autoOnBlockingEntity);

	private static ActivityEntry Entry(long sequence, AreaSnapshot snapshot) => new(sequence, snapshot);

	// ===================== the bounded buffer =====================

	/// <summary>
	///     The cap is real. This process runs for months in a house, so a buffer that only grew would be a leak
	///     nobody notices until the installation that has been up longest falls over.
	/// </summary>
	[TestMethod]
	public void The_Log_Never_Holds_More_Than_Its_Capacity()
	{
		ActivityLog log = new();

		for (int i = 0; i < ActivityLog.Capacity + 25; i++)
			log.Record(Report("Stue"));

		Assert.AreEqual(ActivityLog.Capacity, log.Count,
			"the timeline is bounded; the whole point of the cap is that it holds under load");
		Assert.AreEqual(ActivityLog.Capacity, log.Entries.Count);
	}

	/// <summary>
	///     Full, it drops the oldest report — never the newest. Getting this backwards would leave the page
	///     showing ancient history and silently discarding the transition somebody came to look for.
	/// </summary>
	[TestMethod]
	public void A_Full_Log_Drops_Its_Oldest_Report_First()
	{
		ActivityLog log = new();
		int overflow = 3;

		for (int i = 1; i <= ActivityLog.Capacity + overflow; i++)
			log.Record(Report($"Room {i}"));

		IReadOnlyList<ActivityEntry> entries = log.Entries;

		Assert.AreEqual($"Room {ActivityLog.Capacity + overflow}", entries[0].AreaName, "newest first");
		Assert.AreEqual($"Room {overflow + 1}", entries[^1].AreaName,
			"the three oldest fell off, so the tail is the fourth report ever recorded");
	}

	/// <summary>Newest first is the page's order, and the log hands it over already in that order.</summary>
	[TestMethod]
	public void Entries_Come_Back_Newest_First()
	{
		ActivityLog log = new();

		log.Record(Report("Stue", at: Noon));
		log.Record(Report("Kjøkken", at: Noon.AddMinutes(1)));
		log.Record(Report("Bad", at: Noon.AddMinutes(2)));

		IReadOnlyList<ActivityEntry> entries = log.Entries;

		CollectionAssert.AreEqual(
			new[] { "Bad", "Kjøkken", "Stue" },
			entries.Select(entry => entry.AreaName).ToArray());
	}

	/// <summary>
	///     Sequences count every report ever recorded, not every report still held. That is what lets the page
	///     say "4 new reports" by subtraction and stay right after the oldest have been evicted.
	/// </summary>
	[TestMethod]
	public void Sequences_Keep_Counting_After_Eviction()
	{
		ActivityLog log = new();

		for (int i = 0; i < ActivityLog.Capacity + 5; i++)
			log.Record(Report("Stue"));

		Assert.AreEqual(ActivityLog.Capacity + 5, log.Newest,
			"the buffer forgets; the counter must not, or 'n new' would go backwards");
		Assert.AreEqual(ActivityLog.Capacity + 5, log.Entries[0].Sequence);
	}

	/// <summary>An empty log says so, which is what the page's honest empty state hangs on.</summary>
	[TestMethod]
	public void A_Fresh_Log_Is_Empty_And_Has_No_Sequence_Yet()
	{
		ActivityLog log = new();

		Assert.IsTrue(log.IsEmpty);
		Assert.AreEqual(0, log.Count);
		Assert.AreEqual(0L, log.Newest);

		log.Record(Report("Stue"));

		Assert.IsFalse(log.IsEmpty);
		Assert.AreEqual(1L, log.Newest, "sequences count from one, so zero can mean 'nothing yet'");
	}

	/// <summary>
	///     The timeline and the count that goes with it come back together, and mean the same instant.
	/// </summary>
	[TestMethod]
	public void A_Read_Hands_Over_The_Entries_And_The_Count_As_One()
	{
		ActivityLog log = new();

		ActivityTimeline nothing = log.Read();

		Assert.AreEqual(0, nothing.Entries.Count);
		Assert.AreEqual(0L, nothing.Newest, "sequences count from one, so zero still means 'nothing yet'");

		log.Record(Report("Stue"));
		log.Record(Report("Bad"));

		ActivityTimeline timeline = log.Read();

		Assert.AreEqual(2, timeline.Entries.Count);
		Assert.AreEqual("Bad", timeline.Entries[0].AreaName, "newest first, exactly as Entries hands them over");
		Assert.AreEqual(2L, timeline.Newest);
	}

	/// <summary>
	///     <b>The report that used to vanish.</b> Reading the entries and the sequence as two separately-locked
	///     calls leaves a gap, and a report landing in it is counted as shown while being absent from what was
	///     shown: no "new reports" button appears for it and the row stays invisible until some later report
	///     happens to arrive, which in a quiet house is hours. So the two come back from one lock, and the
	///     invariant that proves it is that the newest entry held is the newest sequence counted.
	/// </summary>
	[TestMethod]
	public void A_Report_Arriving_Mid_Read_Is_Never_Counted_As_Shown_While_Missing()
	{
		// Reader-driven: a fixed number of reads against a writer that runs until they are done, so the window
		// the race needs is never closed early by the writer finishing first. The reads only begin once the
		// writer has filed one report, because reads of an empty log cost nothing and five thousand of them can
		// otherwise be over before the pool has started the writer at all.
		ActivityLog log = new();
		const int Reads = 5_000;

		using CancellationTokenSource stop = new();
		using ManualResetEventSlim running = new();

		Task writer = Task.Run(() =>
		{
			log.Record(Report("Stue"));
			running.Set();

			while (!stop.IsCancellationRequested)
				log.Record(Report("Stue"));
		});

		Assert.IsTrue(running.Wait(TimeSpan.FromSeconds(30)), "the writer never started, so nothing was raced");

		long torn = 0;

		for (int read = 0; read < Reads; read++)
		{
			ActivityTimeline timeline = log.Read();

			if (timeline.Entries.Count > 0 && timeline.Entries[0].Sequence != timeline.Newest)
				torn++;
		}

		stop.Cancel();
		writer.GetAwaiter().GetResult();

		Assert.AreEqual(0L, torn,
			$"{torn} of {Reads} reads counted a report that was not in the list they came with");

		ActivityTimeline settled = log.Read();

		Assert.AreEqual(settled.Newest, settled.Entries[0].Sequence, "and the two still agree once the house goes quiet");
	}

	// ===================== the room filter =====================

	/// <summary>No room chosen shows the whole house — the filter's resting state, not a special case.</summary>
	[TestMethod]
	public void No_Room_Chosen_Shows_Every_Room()
	{
		ActivityEntry[] entries = [Entry(2, Report("Stue")), Entry(1, Report("Kjøkken"))];

		Assert.AreEqual(2, ActivityView.InRoom(entries, ActivityView.AllRooms).Count);
		Assert.AreEqual(2, ActivityView.InRoom(entries, null).Count);
		Assert.AreEqual(2, ActivityView.InRoom(entries, "   ").Count);
	}

	/// <summary>A chosen room shows only that room, and in the order it was given — the filter never re-sorts.</summary>
	[TestMethod]
	public void A_Chosen_Room_Keeps_Only_Its_Own_Reports_In_Order()
	{
		ActivityEntry[] entries =
		[
			Entry(4, Report("Stue", at: Noon.AddMinutes(3))),
			Entry(3, Report("Kjøkken", at: Noon.AddMinutes(2))),
			Entry(2, Report("Stue", at: Noon.AddMinutes(1))),
			Entry(1, Report("Bad", at: Noon))
		];

		IReadOnlyList<ActivityEntry> filtered = ActivityView.InRoom(entries, "Stue");

		CollectionAssert.AreEqual(new long[] { 4, 2 }, filtered.Select(entry => entry.Sequence).ToArray());
	}

	/// <summary>
	///     Matched without regard to case, because the name comes back from a form control and a house that
	///     renamed a room is not a house that wants its history to disappear on a capital letter.
	/// </summary>
	[TestMethod]
	public void The_Room_Filter_Ignores_Case()
	{
		ActivityEntry[] entries = [Entry(1, Report("Stue"))];

		Assert.AreEqual(1, ActivityView.InRoom(entries, "stue").Count);
	}

	/// <summary>A room with nothing in the buffer filters to nothing, which the page answers with its own words.</summary>
	[TestMethod]
	public void A_Room_With_Nothing_Held_Filters_To_Nothing()
	{
		ActivityEntry[] entries = [Entry(1, Report("Stue"))];

		Assert.AreEqual(0, ActivityView.InRoom(entries, "Loft").Count);
	}

	/// <summary>
	///     The dropdown offers each room once, alphabetically, and only rooms that have actually reported —
	///     a filter whose every choice leads to an empty page is worse than no filter.
	/// </summary>
	[TestMethod]
	public void The_Filter_Offers_Each_Reporting_Room_Once()
	{
		ActivityEntry[] entries =
		[
			Entry(4, Report("Stue")),
			Entry(3, Report("Bad")),
			Entry(2, Report("Stue")),
			Entry(1, Report("Kjøkken"))
		];

		CollectionAssert.AreEqual(
			new[] { "Bad", "Kjøkken", "Stue" },
			ActivityView.Rooms(entries).ToArray());
	}

	/// <summary>
	///     One option per filter. The dropdown is built from the names in the buffer and the filter matches them
	///     ignoring case, so the two have to agree about what "the same room" is: a room renamed only in its
	///     capitalisation must not become two options that both show the same entries — and, the way round that
	///     actually loses history, one option must never stand for names the filter will then decline to match.
	/// </summary>
	[TestMethod]
	public void The_Filter_Offers_A_Room_Once_However_Its_Name_Was_Capitalised()
	{
		ActivityEntry[] entries = [Entry(2, Report("Stue")), Entry(1, Report("stue"))];

		IReadOnlyList<string> rooms = ActivityView.Rooms(entries);

		Assert.AreEqual(1, rooms.Count, "the dropdown offers a room once, not once per spelling");
		Assert.AreEqual(2, ActivityView.InRoom(entries, rooms[0]).Count,
			"and choosing it finds everything the room reported under either spelling");
	}

	// ===================== grouping by day =====================

	/// <summary>
	///     Days a person thinks of by name get one; anything older is dated, because counting back three days
	///     is arithmetic the reader has to do and then check.
	/// </summary>
	[TestMethod]
	public void Only_Today_And_Yesterday_Are_Named()
	{
		DateOnly today = new(2026, 7, 22);

		Assert.AreEqual("Today", ActivityView.DayHeading(today, today));
		Assert.AreEqual("Yesterday", ActivityView.DayHeading(today.AddDays(-1), today));

		string older = ActivityView.DayHeading(today.AddDays(-3), today);

		Assert.AreNotEqual("Today", older);
		Assert.AreNotEqual("Yesterday", older);
		Assert.IsTrue(older.Contains("19", StringComparison.Ordinal),
			$"an older day is dated rather than counted back from, but read '{older}'");
	}

	/// <summary>
	///     Grouping cuts the timeline where the day changes and keeps the order it was given: a newest-first
	///     list visits each day once, so a group is a run, not a bucket to be re-sorted into.
	/// </summary>
	[TestMethod]
	public void Grouping_Cuts_The_Timeline_By_Day_Without_Reordering_It()
	{
		DateTimeOffset now = DateTimeOffset.Now;
		ActivityEntry[] entries =
		[
			Entry(4, Report("Stue", at: now.AddMinutes(-5))),
			Entry(3, Report("Bad", at: now.AddMinutes(-30))),
			Entry(2, Report("Stue", at: now.AddDays(-1))),
			Entry(1, Report("Kjøkken", at: now.AddDays(-4)))
		];

		IReadOnlyList<ActivityDay> days = ActivityView.GroupByDay(entries, now);

		Assert.AreEqual(3, days.Count);
		Assert.AreEqual("Today", days[0].Heading);
		Assert.AreEqual(2, days[0].Entries.Count);
		CollectionAssert.AreEqual(new long[] { 4, 3 }, days[0].Entries.Select(entry => entry.Sequence).ToArray());
		Assert.AreEqual("Yesterday", days[1].Heading);
		Assert.AreEqual(1, days[2].Entries.Count);
	}

	/// <summary>Nothing to group is no groups, not one empty one the page would then head.</summary>
	[TestMethod]
	public void Grouping_Nothing_Yields_No_Days()
	{
		Assert.AreEqual(0, ActivityView.GroupByDay([], DateTimeOffset.Now).Count);
	}

	// ===================== the words =====================

	/// <summary>
	///     <b>The line this whole page exists for.</b> A room that stayed dark because it was too bright must say
	///     the measured reading and the threshold it was compared against, in that order and in the gate's own
	///     words. "Too bright" on its own is the answer that sent the owner looking for a fault in the first place.
	/// </summary>
	[TestMethod]
	public void A_Room_Too_Bright_To_Light_Names_The_Reading_And_The_Threshold()
	{
		ActivityLine line = ActivityView.Describe(Report(
			"Stue",
			AreaState.AutoVacant,
			TransitionReason.CircadianTick,
			isDark: false,
			darknessDetail: "lux 86, dark below 40"));

		Assert.AreEqual("Too bright to switch the lights on", line.What);
		Assert.AreEqual("lux 86, dark below 40", line.Why);
	}

	/// <summary>Dusk is news too: the moment a room becomes eligible is the other half of the same question.</summary>
	[TestMethod]
	public void A_Room_That_Just_Became_Dark_Enough_Says_So()
	{
		ActivityLine line = ActivityView.Describe(Report(
			"Stue",
			AreaState.AutoVacant,
			TransitionReason.CircadianTick,
			isDark: true,
			darknessDetail: "lux 12, dark below 40"));

		Assert.AreEqual("Dark enough now — movement will switch the lights on", line.What);
		Assert.AreEqual("lux 12, dark below 40", line.Why);
	}

	/// <summary>
	///     <b>The line that used to lie.</b> Dark enough is only half the question. An area set not to light
	///     itself while the house sleeps sits in exactly the state of an area merely waiting for someone to walk
	///     in, so the row promised a light that was never going to come on — and it appeared at dusk, which is
	///     when somebody is reading this page to find out why the room stayed dark. Auto-discovery sets that
	///     setting on every bedroom it finds, so this was every bedroom in the house, every night.
	/// </summary>
	[TestMethod]
	public void A_Sleeping_House_Does_Not_Promise_A_Light_That_Will_Not_Come_On()
	{
		ActivityLine line = ActivityView.Describe(Report(
			"Soverom",
			AreaState.AutoVacant,
			TransitionReason.CircadianTick,
			isDark: true,
			darknessDetail: "lux 12, dark below 40",
			mode: HouseMode.Sleep,
			autoOnBlockedBy: AutoOnBlock.Sleep));

		Assert.AreEqual(
			"Dark enough now, but the house is asleep — movement will not switch the lights on",
			line.What);
		Assert.AreEqual("lux 12, dark below 40", line.Why,
			"the reading is still the measurement the verdict was reached on");
	}

	/// <summary>
	///     A television on at dusk is the everyday version of the same lie, and the row has to say which entity:
	///     "something is on" leaves the reader walking the room looking for it, which is the dead end this page
	///     exists to end.
	/// </summary>
	[TestMethod]
	public void A_Blocking_Entity_Is_Named_Rather_Than_Alluded_To()
	{
		ActivityLine line = ActivityView.Describe(Report(
			"Stue",
			AreaState.AutoVacant,
			TransitionReason.CircadianTick,
			isDark: true,
			darknessDetail: "lux 12, dark below 40",
			autoOnBlockedBy: AutoOnBlock.EntityOn,
			autoOnBlockingEntity: "media_player.stue_tv"));

		Assert.AreEqual(
			"Dark enough now, but media_player.stue_tv is on — movement will not switch the lights on",
			line.What);
		Assert.AreEqual("lux 12, dark below 40", line.Why);
	}

	/// <summary>
	///     Nothing in the way keeps the original sentence, and so does a report from a build that never carried
	///     the verdict: absent means "this report cannot say", and a row may no more invent a refusal than it
	///     may invent a promise.
	/// </summary>
	[TestMethod]
	public void An_Open_Gate_And_An_Older_Report_Both_Keep_The_Original_Words()
	{
		const string Promise = "Dark enough now — movement will switch the lights on";

		Assert.AreEqual(Promise, ActivityView.Describe(Report(
			"Stue", AreaState.AutoVacant, TransitionReason.CircadianTick,
			isDark: true, autoOnBlockedBy: AutoOnBlock.None)).What);

		Assert.AreEqual(Promise, ActivityView.Describe(Report(
			"Stue", AreaState.AutoVacant, TransitionReason.CircadianTick,
			isDark: true)).What,
			"a report from a build that predates the field says exactly what it always said");
	}

	/// <summary>
	///     The reading rides along on transitions too, not only on the quiet re-check — a suppression lifting
	///     into a room the engine still will not light has to explain the second half as well as the first.
	/// </summary>
	[TestMethod]
	public void A_Transition_Into_A_Too_Bright_Room_Still_Carries_The_Reading()
	{
		ActivityLine line = ActivityView.Describe(Report(
			"Stue",
			AreaState.AutoVacant,
			TransitionReason.SuppressionLifted,
			isDark: false,
			darknessDetail: "lux 86, dark below 40"));

		Assert.AreEqual("Quiet long enough — back under automatic control", line.What);
		Assert.AreEqual("Too bright to switch on — lux 86, dark below 40", line.Why);
	}

	/// <summary>
	///     A report from a build that predates the darkness detail says less rather than inventing numbers. The
	///     field deserialises as <c>null</c> from an older payload, exactly as <c>area_id</c> once did.
	/// </summary>
	[TestMethod]
	public void Without_A_Reading_The_Line_Says_Less_Rather_Than_Guessing()
	{
		ActivityLine line = ActivityView.Describe(Report(
			"Stue",
			AreaState.AutoVacant,
			TransitionReason.SuppressionLifted,
			isDark: false));

		Assert.AreEqual("Too bright to switch on.", line.Why);
	}

	/// <summary>A gate that has never been consulted says that, rather than reporting a verdict it never reached.</summary>
	[TestMethod]
	public void An_Unchecked_Room_Does_Not_Claim_A_Verdict()
	{
		ActivityLine line = ActivityView.Describe(Report("Stue", AreaState.AutoVacant, TransitionReason.Startup));

		Assert.AreEqual("Started up and took the room as it was", line.What);
		Assert.AreEqual("Darkness hasn't been checked here yet.", line.Why);
	}

	/// <summary>Movement that lit a room names the levels it was lit at — what happened, not which field moved.</summary>
	[TestMethod]
	public void Movement_That_Lit_A_Room_Names_The_Levels()
	{
		ActivityLine line = ActivityView.Describe(Report(
			"Stue",
			AreaState.AutoActive,
			TransitionReason.Motion,
			isDark: true,
			brightness: 60,
			kelvin: 2700));

		Assert.AreEqual("Movement — lights on at 60 %, 2700 K", line.What);
		Assert.IsNull(line.Why, "the event says everything; a condition line under it would only repeat itself");
	}

	/// <summary>
	///     Lights the engine adopted at start-up have no command behind them, so there are no levels to report
	///     and the line does not report any.
	/// </summary>
	[TestMethod]
	public void A_Room_With_No_Commanded_Levels_Reports_None()
	{
		ActivityLine line = ActivityView.Describe(Report(
			"Stue",
			AreaState.AutoActive,
			TransitionReason.Motion,
			isDark: true));

		Assert.AreEqual("Movement — lights on", line.What);
	}

	/// <summary>The house-level events read as house-level events, with the mode the house actually names.</summary>
	[TestMethod]
	public void House_Events_Read_As_House_Events()
	{
		Assert.AreEqual(
			"The house changed mode to Sover",
			ActivityView.Describe(Report(
				"Stue",
				AreaState.AutoVacant,
				TransitionReason.HouseModeChanged,
				isDark: true,
				mode: HouseMode.Sleep,
				houseModeValue: "Sover")).What);

		ActivityLine empty = ActivityView.Describe(Report(
			"Stue",
			AreaState.Away,
			TransitionReason.EveryoneLeft,
			mode: HouseMode.Away));

		Assert.AreEqual("The last person left the house", empty.What);
		Assert.AreEqual("Nobody home — the room waits for the first arrival.", empty.Why);
	}

	/// <summary>
	///     The master switch outranks every other explanation. A row that described a transition without saying
	///     the engine was muzzled would send somebody hunting a room-level fault that is not there.
	/// </summary>
	[TestMethod]
	public void The_Master_Switch_Outranks_Every_Other_Explanation()
	{
		ActivityLine line = ActivityView.Describe(Report(
			"Stue",
			AreaState.Disabled,
			TransitionReason.EnablementChanged,
			isDark: true,
			killSwitch: true));

		Assert.AreEqual("Paused by the master switch", line.What);
		Assert.AreEqual("No lights will change until it is turned back on.", line.Why);
	}

	/// <summary>A room switched off in the document says so, and says it as the room's own setting.</summary>
	[TestMethod]
	public void A_Switched_Off_Room_Names_Its_Own_Setting()
	{
		ActivityLine line = ActivityView.Describe(Report(
			"Stue",
			AreaState.Disabled,
			TransitionReason.EnablementChanged));

		Assert.AreEqual("Automatic lighting was switched off for this room", line.What);
		Assert.AreEqual("Automatic lighting is switched off for this room.", line.Why);
	}

	/// <summary>Every reason the engine can publish has words of its own; none falls through to an enum name.</summary>
	[TestMethod]
	public void Every_Transition_Reason_Has_Words()
	{
		foreach (TransitionReason reason in Enum.GetValues<TransitionReason>())
		{
			foreach (AreaState state in Enum.GetValues<AreaState>())
			{
				ActivityLine line = ActivityView.Describe(Report("Stue", state, reason, isDark: true));

				Assert.AreNotEqual(reason.ToString(), line.What,
					$"{reason} in {state} fell through to its enum name instead of a sentence");
				Assert.IsFalse(string.IsNullOrWhiteSpace(line.What));
			}
		}
	}

	/// <summary>
	///     The timeline borrows the dashboard's colour families rather than defining its own, so one room is
	///     painted one way on both pages.
	/// </summary>
	[TestMethod]
	public void Rows_Take_The_Dashboards_Colour_Families()
	{
		Assert.AreEqual("machine", AreaView.Family(AreaState.AutoActive));
		Assert.AreEqual("human", AreaView.Family(AreaState.OverriddenOn));
		Assert.AreEqual("idle", AreaView.Family(AreaState.Away));
	}
}
