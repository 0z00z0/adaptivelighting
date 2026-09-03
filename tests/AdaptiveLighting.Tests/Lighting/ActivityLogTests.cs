using System.Numerics;
using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The activity page's buffer and the words it builds, as pure functions outside the markup.</summary>
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
		string? autoOnBlockingEntity = null,
		bool? isAnyoneHome = null,
		ForcedMode? forced = null) =>
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
			autoOnBlockingEntity,
			null,
			isAnyoneHome,
			forced);

	private static ActivityEntry Entry(long sequence, AreaSnapshot snapshot) => new(sequence, snapshot);

	private static ActivityEntry Entry(long sequence, EngineNoticeKind kind, DateTimeOffset? at = null) =>
		new(sequence, new EngineNotice(kind, at ?? Noon));

	// ===================== the bounded buffer =====================

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

	/// <summary>Sequences count every report recorded, not every report held; the page subtracts them to say "4 new".</summary>
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

	/// <remarks>One lock, so the newest entry held is the newest sequence.</remarks>
	[TestMethod]
	public void A_Report_Arriving_Mid_Read_Is_Never_Counted_As_Shown_While_Missing()
	{
		// Gated on the writer's first report: reads of an empty log are free and would otherwise all be over before it starts.
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

	[TestMethod]
	public void No_Room_Chosen_Shows_Every_Room()
	{
		ActivityEntry[] entries = [Entry(2, Report("Stue")), Entry(1, Report("Kjøkken"))];

		Assert.AreEqual(2, ActivityView.InRoom(entries, ActivityView.AllRooms).Count);
		Assert.AreEqual(2, ActivityView.InRoom(entries, null).Count);
		Assert.AreEqual(2, ActivityView.InRoom(entries, "   ").Count);
	}

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

	[TestMethod]
	public void The_Room_Filter_Ignores_Case()
	{
		ActivityEntry[] entries = [Entry(1, Report("Stue"))];

		Assert.AreEqual(1, ActivityView.InRoom(entries, "stue").Count);
	}

	[TestMethod]
	public void A_Room_With_Nothing_Held_Filters_To_Nothing()
	{
		ActivityEntry[] entries = [Entry(1, Report("Stue"))];

		Assert.AreEqual(0, ActivityView.InRoom(entries, "Loft").Count);
	}

	/// <summary>Alphabetical, and only rooms that have reported.</summary>
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

	/// <summary>A group is a run, not a bucket, and it is taken over rows, not reports.</summary>
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

		IReadOnlyList<ActivityDay> days = ActivityView.GroupByDay(ActivityView.Rows(entries), now);

		Assert.AreEqual(3, days.Count);
		Assert.AreEqual("Today", days[0].Heading);
		Assert.AreEqual(2, days[0].Rows.Count);
		CollectionAssert.AreEqual(new long[] { 4, 3 }, days[0].Rows.Select(row => row.Sequence).ToArray());
		Assert.AreEqual("Yesterday", days[1].Heading);
		Assert.AreEqual(1, days[2].Rows.Count);
	}

	[TestMethod]
	public void Grouping_Nothing_Yields_No_Days()
	{
		Assert.AreEqual(0, ActivityView.GroupByDay([], DateTimeOffset.Now).Count);
	}

	// ===================== the words =====================

	[TestMethod]
	public void A_Room_Too_Bright_To_Light_Names_The_Reading_And_The_Threshold()
	{
		ActivityLine line = ActivityView.Describe(Report(
			"Stue",
			AreaState.AutoVacant,
			TransitionReason.CircadianTick,
			isDark: false,
			darknessDetail: "lux 86, dark below 40"));

		Assert.AreEqual("Too bright to switch on", line.What);
		Assert.AreEqual("lux 86, dark below 40", line.Why);
	}

	[TestMethod]
	public void A_Room_That_Just_Became_Dark_Enough_Says_So()
	{
		ActivityLine line = ActivityView.Describe(Report(
			"Stue",
			AreaState.AutoVacant,
			TransitionReason.CircadianTick,
			isDark: true,
			darknessDetail: "lux 12, dark below 40"));

		Assert.AreEqual("Dark enough — movement will light the room", line.What);
		Assert.AreEqual("lux 12, dark below 40", line.Why);
	}

	/// <remarks>A sleep-blocked area sits in the same state as one waiting for movement.</remarks>
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
			"Dark enough, but the house is asleep — movement won't light the room",
			line.What);
		Assert.AreEqual("lux 12, dark below 40", line.Why,
			"the reading is still the measurement the verdict was reached on");
	}

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
			"Dark enough, but media_player.stue_tv is on — movement won't light the room",
			line.What);
		Assert.AreEqual("lux 12, dark below 40", line.Why);
	}

	/// <summary>An absent block field means "this report cannot say", never "nothing was in the way".</summary>
	[TestMethod]
	public void An_Open_Gate_And_An_Older_Report_Both_Keep_The_Original_Words()
	{
		const string Promise = "Dark enough — movement will light the room";

		Assert.AreEqual(Promise, ActivityView.Describe(Report(
			"Stue", AreaState.AutoVacant, TransitionReason.CircadianTick,
			isDark: true, autoOnBlockedBy: AutoOnBlock.None)).What);

		Assert.AreEqual(Promise, ActivityView.Describe(Report(
			"Stue", AreaState.AutoVacant, TransitionReason.CircadianTick,
			isDark: true)).What,
			"a report from a build that predates the field says exactly what it always said");
	}

	[TestMethod]
	public void A_Transition_Into_A_Too_Bright_Room_Still_Carries_The_Reading()
	{
		ActivityLine line = ActivityView.Describe(Report(
			"Stue",
			AreaState.AutoVacant,
			TransitionReason.SuppressionLifted,
			isDark: false,
			darknessDetail: "lux 86, dark below 40"));

		Assert.AreEqual("Quiet long enough — back on automatic", line.What);
		Assert.AreEqual("Too bright to switch on — lux 86, dark below 40", line.Why);
	}

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

	[TestMethod]
	public void An_Unchecked_Room_Does_Not_Claim_A_Verdict()
	{
		ActivityLine line = ActivityView.Describe(Report("Stue", AreaState.AutoVacant, TransitionReason.Startup));

		Assert.AreEqual("Started up — took the room as it was", line.What);
		Assert.AreEqual("Darkness not checked here yet.", line.Why);
	}

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

	/// <summary>Lights adopted at start-up have no command behind them, so there are no levels to report.</summary>
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

	/// <summary>The mode is printed under the house's own name for it, not the enum member.</summary>
	[TestMethod]
	public void House_Events_Read_As_House_Events()
	{
		Assert.AreEqual(
			"Mode changed to Sover",
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

		Assert.AreEqual("Everyone left the house", empty.What);
		Assert.AreEqual("Nobody home — waiting for the first arrival.", empty.Why);
	}

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
		Assert.AreEqual("No lights change until it's turned back on.", line.Why);
	}

	[TestMethod]
	public void A_Switched_Off_Room_Names_Its_Own_Setting()
	{
		ActivityLine line = ActivityView.Describe(Report(
			"Stue",
			AreaState.Disabled,
			TransitionReason.EnablementChanged));

		Assert.AreEqual("Automatic lighting switched off here", line.What);
		Assert.AreEqual("Automatic lighting is off here.", line.Why);
	}

	// ===================== movement the engine turned down =====================

	/// <remarks>Every <see cref="AutoOnBlock"/> value is worded here; the exhaustive test below stops one being missed.</remarks>
	[TestMethod]
	public void A_Refused_Movement_Names_The_Gate_That_Refused_It()
	{
		Assert.AreEqual("Movement, but the room is bright enough",
			Declined(AutoOnBlock.NotDark, isDark: false).What);

		Assert.AreEqual("Movement, but the house is asleep and this room stays dark",
			Declined(AutoOnBlock.Sleep).What);

		Assert.AreEqual("Movement, but media_player.stue_tv is on",
			Declined(AutoOnBlock.EntityOn, blocker: "media_player.stue_tv").What);

		Assert.AreEqual("Movement, but automatic lighting is off here",
			Declined(AutoOnBlock.Disabled, state: AreaState.Disabled).What);

		Assert.AreEqual("Movement, but nobody is home yet",
			Declined(AutoOnBlock.Away, state: AreaState.Away).What);

		Assert.AreEqual("Movement, but a guest scene has this room",
			Declined(AutoOnBlock.SceneHold, state: AreaState.SceneHold).What);
	}

	/// <remarks>Presence and a forced mode are different causes, and the row has to tell them apart.</remarks>
	[TestMethod]
	public void A_Movement_Refused_By_An_Away_Mode_Over_An_Occupied_House_Says_So()
	{
		ForcedMode forced = new(
			ModeKind.Away, "Borte", ModeForceSource.WhileEntityOn, "input_boolean.occupancy", "on");

		ActivityLine held = Declined(
			AutoOnBlock.Away, state: AreaState.Away, isAnyoneHome: true, forced: forced);

		Assert.AreEqual("Movement, but the house is in away mode", held.What);

		// The engine's sentence verbatim: it is the only thing that knows which entity it read.
		Assert.AreEqual("Away mode is forced while input_boolean.occupancy is on.", held.Why);
		Assert.AreEqual(forced.Describe(), held.Why);

		// Nothing forcing it means somebody chose the option, and the row names that, not presence.
		ActivityLine chosen = Declined(
			AutoOnBlock.Away, state: AreaState.Away, isAnyoneHome: true, houseModeValue: "Borte");

		Assert.AreEqual("Somebody is home, but the house mode is set to Borte.", chosen.Why);
	}

	[TestMethod]
	public void An_Away_Refusal_Keeps_Its_Old_Words_Where_Nothing_Contradicts_Them()
	{
		ActivityLine empty = Declined(AutoOnBlock.Away, state: AreaState.Away, isAnyoneHome: false);

		Assert.AreEqual("Movement, but nobody is home yet", empty.What);
		Assert.IsNull(empty.Why);

		ActivityLine older = Declined(AutoOnBlock.Away, state: AreaState.Away, isAnyoneHome: null);

		Assert.AreEqual("Movement, but nobody is home yet", older.What);
		Assert.IsNull(older.Why);
	}

	/// <summary>A forced mode never moves the select, so the select's value is stale for the length of the force.</summary>
	[TestMethod]
	public void A_Forced_Mode_Change_Names_The_Force_Rather_Than_The_Selects_Stale_Value()
	{
		ForcedMode forced = new(
			ModeKind.Away, "Borte", ModeForceSource.WhileEntityOn, "input_boolean.occupancy", "on");

		ActivityLine line = ActivityView.Describe(Report(
			"Stue",
			AreaState.Away,
			TransitionReason.HouseModeChanged,
			mode: HouseMode.Away,
			houseModeValue: "Hjemme",
			isAnyoneHome: true,
			forced: forced));

		Assert.AreEqual("Mode forced to Borte", line.What);
		Assert.AreEqual(forced.Describe(), line.Why);
	}

	/// <summary>The forced sentence is about the house, so the house-wide collapse keeps it.</summary>
	[TestMethod]
	public void A_Forced_Modes_Sentence_Survives_The_House_Wide_Collapse()
	{
		ForcedMode forced = new(
			ModeKind.Away, "Borte", ModeForceSource.WhileEntityOn, "input_boolean.occupancy", "on");

		AreaSnapshot Publisher(string area, AreaState state) => Report(
			area, state, TransitionReason.HouseModeChanged,
			mode: HouseMode.Away, houseModeValue: "Hjemme", isAnyoneHome: true, forced: forced);

		// Two rooms in different states, as a real house is when the mode moves under it.
		IReadOnlyList<ActivityRow> rows = ActivityView.Rows(
		[
			Entry(2, Publisher("Stue", AreaState.Away)),
			Entry(1, Publisher("Kjøkken", AreaState.AutoVacant))
		]);

		Assert.AreEqual(1, rows.Count, "one forced change is one row, whatever the rooms were doing");
		Assert.AreEqual("Mode forced to Borte", rows[0].Line.What);
		Assert.AreEqual(forced.Describe(), rows[0].Line.Why);
		Assert.IsTrue(rows[0].IsAboutTheHouse);
	}

	/// <summary>The one row the master switch does not replace outright; it names the switch inside the refusal.</summary>
	[TestMethod]
	public void A_Refused_Movement_Survives_The_Master_Switch_And_Still_Names_It()
	{
		AreaSnapshot muzzled = Report(
			"Stue", AreaState.Disabled, TransitionReason.Motion,
			killSwitch: true, autoOnBlockedBy: AutoOnBlock.KillSwitch);

		Assert.AreEqual("Movement, but the master switch is off", ActivityView.Describe(muzzled).What);

		Assert.AreEqual(ActivityCategory.Movement, ActivityView.Categorise(muzzled),
			"the row is worded as movement, so switching the movement chip off has to take it away");
	}

	[TestMethod]
	public void Only_A_Darkness_Refusal_Carries_The_Reading_Beneath_It()
	{
		Assert.AreEqual("lux 1700, dark below 1000",
			Declined(AutoOnBlock.NotDark, isDark: false, detail: "lux 1700, dark below 1000").Why);

		Assert.IsNull(
			Declined(AutoOnBlock.Sleep, detail: "lux 3, dark below 1000").Why,
			"the house mode made this decision, and printing the sensor beside it invites blaming the sensor");
	}

	[TestMethod]
	public void A_Movement_That_Lit_The_Room_Is_Not_A_Refusal()
	{
		AreaSnapshot lit = Report(
			"Stue", AreaState.AutoActive, TransitionReason.Motion,
			isDark: true, brightness: 70, kelvin: 2700, autoOnBlockedBy: AutoOnBlock.None);

		Assert.AreEqual("Movement — lights on at 70 %, 2700 K", ActivityView.Describe(lit).What);
	}

	/// <summary>Puts a refused movement into words.</summary>
	private static ActivityLine Declined(
		AutoOnBlock block,
		AreaState state = AreaState.AutoVacant,
		bool? isDark = true,
		string? detail = null,
		string? blocker = null,
		bool? isAnyoneHome = null,
		ForcedMode? forced = null,
		string? houseModeValue = null) =>
		ActivityView.Describe(Report(
			"Stue", state, TransitionReason.Motion,
			isDark: isDark, darknessDetail: detail, houseModeValue: houseModeValue,
			autoOnBlockedBy: block, autoOnBlockingEntity: blocker, isAnyoneHome: isAnyoneHome, forced: forced));

	[TestMethod]
	public void Every_Transition_Reason_Has_Words()
	{
		AutoOnBlock?[] blocks = [null, .. Enum.GetValues<AutoOnBlock>().Cast<AutoOnBlock?>()];

		foreach (TransitionReason reason in Enum.GetValues<TransitionReason>())
		{
			foreach (AreaState state in Enum.GetValues<AreaState>())
			{
				// The block is walked too, so a value added to AutoOnBlock later has to be given a sentence.
				foreach (AutoOnBlock? block in blocks)
				{
					ActivityLine line = ActivityView.Describe(Report("Stue", state, reason, isDark: true, autoOnBlockedBy: block));

					Assert.AreNotEqual(reason.ToString(), line.What,
						$"{reason} in {state} (blocked by {block?.ToString() ?? "nothing recorded"}) fell through to its enum name");
					Assert.IsFalse(string.IsNullOrWhiteSpace(line.What));

					if (reason is TransitionReason.Motion
						&& state is not AreaState.AutoActive
						&& block is { } named and not AutoOnBlock.None)
					{
						Assert.AreNotEqual("Movement", line.What,
							$"a movement refused by {named} must say what refused it, or the row is the silence it replaced");
					}
				}
			}
		}
	}

	/// <summary>The timeline borrows the dashboard's colour families; it defines none of its own.</summary>
	[TestMethod]
	public void Rows_Take_The_Dashboards_Colour_Families()
	{
		Assert.AreEqual("machine", AreaView.Family(AreaState.AutoActive));
		Assert.AreEqual("human", AreaView.Family(AreaState.OverriddenOn));
		Assert.AreEqual("idle", AreaView.Family(AreaState.Away));
	}

	// ===================== the category filter =====================

	/// <summary>
	///     A report in no category is a row no combination of chips can show. A report in two is a row that
	///     survives either of them being switched off, which is a button that does nothing.
	/// </summary>
	[TestMethod]
	public void Every_Report_The_Engine_Can_Publish_Lands_In_Exactly_One_Category()
	{
		bool?[] verdicts = [null, true, false];
		AutoOnBlock?[] blocks = [null, .. Enum.GetValues<AutoOnBlock>().Cast<AutoOnBlock?>()];

		foreach (TransitionReason reason in Enum.GetValues<TransitionReason>())
		{
			foreach (AreaState state in Enum.GetValues<AreaState>())
			{
				foreach (bool? dark in verdicts)
				{
					foreach (bool killSwitch in new[] { false, true })
					{
						// A refused movement is the one row the master switch does not replace, so the block has to be walked.
						foreach (AutoOnBlock? block in blocks)
						{
							AreaSnapshot snapshot = Report(
								"Stue", state, reason, isDark: dark, killSwitch: killSwitch, autoOnBlockedBy: block);
							ActivityCategory categories = ActivityView.Categorise(snapshot);

							Assert.AreNotEqual(ActivityCategory.None, categories,
								$"{reason} in {state} (dark: {dark?.ToString() ?? "unchecked"}, master switch: "
								+ $"{(killSwitch ? "off" : "on")}, blocked by {block?.ToString() ?? "nothing recorded"}) "
								+ "belongs to no category, so no filter can reach it");

							Assert.AreEqual(categories, categories & ActivityView.AllCategories,
								$"{reason} in {state} claimed a category the chips do not offer");

							Assert.AreEqual(1, BitOperations.PopCount((uint)categories),
								$"{reason} in {state} (dark: {dark?.ToString() ?? "unchecked"}, master switch: "
								+ $"{(killSwitch ? "off" : "on")}, blocked by {block?.ToString() ?? "nothing recorded"}) "
								+ $"is filed under {categories}; switching any one of those off leaves the row on "
								+ "the page, which is a filter button that does nothing");
						}
					}
				}
			}
		}
	}

	/// <summary>
	///     The defect the chips were built with: a chip counting reports it cannot remove. Every category is
	///     walked, so a chip that goes dead again fails here rather than in somebody's house.
	/// </summary>
	[TestMethod]
	public void Switching_A_Chip_Off_Removes_Exactly_The_Reports_It_Counts()
	{
		IReadOnlyList<ActivityEntry> entries = EveryShapeOfReport();
		IReadOnlyList<ActivityFilterChip> chips = ActivityView.Chips(entries, ActivityView.AllCategories);

		Assert.AreEqual(8, chips.Count, "every category is offered as a chip");

		foreach (ActivityFilterChip chip in chips)
		{
			Assert.IsTrue(chip.Count > 0,
				$"{chip.Label} counts nothing the engine can publish, so it is a button with no purpose");

			int left = ActivityView.InCategories(entries, ActivityView.AllCategories & ~chip.Category).Count;

			Assert.AreEqual(entries.Count - chip.Count, left,
				$"{chip.Label} says {chip.Count} reports and switching it off removed "
				+ $"{entries.Count - left}; the number beside a chip is what unticking it takes away");
		}
	}

	/// <summary>Counts that overlap cannot be read: the eight beside the chips have to be the whole of it.</summary>
	[TestMethod]
	public void The_Chip_Counts_Add_Up_To_Every_Report_Held()
	{
		IReadOnlyList<ActivityEntry> entries = EveryShapeOfReport();

		Assert.AreEqual(
			entries.Count,
			ActivityView.Chips(entries, ActivityView.AllCategories).Sum(chip => chip.Count));
	}

	/// <summary>The same invariant as above, read from the page's end.</summary>
	[TestMethod]
	public void With_Every_Chip_On_The_Filter_Hides_Nothing()
	{
		List<ActivityEntry> entries = [];
		long sequence = 0;

		foreach (TransitionReason reason in Enum.GetValues<TransitionReason>())
		{
			foreach (AreaState state in Enum.GetValues<AreaState>())
				entries.Add(Entry(++sequence, Report("Stue", state, reason, isDark: true)));
		}

		Assert.AreEqual(entries.Count, ActivityView.InCategories(entries, ActivityView.AllCategories).Count);
	}

	/// <summary>Movement is the sensor speaking; a light change is the engine's own command, in all four shapes.</summary>
	[TestMethod]
	public void Movement_And_The_Engines_Own_Commands_Are_Told_Apart()
	{
		Assert.IsTrue(Has(ActivityCategory.Movement, Report("Stue", AreaState.AutoActive, TransitionReason.Motion)),
			"a motion sensor reported");

		Assert.IsFalse(Has(ActivityCategory.LightChange, Report("Stue", AreaState.AutoActive, TransitionReason.Motion)),
			"the row reads 'Movement — lights on', so it belongs to the movement chip and to that one only");

		Assert.IsTrue(Has(ActivityCategory.LightChange, Report("Stue", AreaState.AutoActive, TransitionReason.CircadianTick)),
			"the circadian re-aim");

		Assert.IsTrue(Has(ActivityCategory.LightChange, Report("Stue", AreaState.PreOff, TransitionReason.VacancyTimeout)),
			"the warning dim");

		Assert.IsTrue(Has(ActivityCategory.LightChange, Report("Stue", AreaState.AutoVacant, TransitionReason.PreOffElapsed)),
			"and the lights going out");

		Assert.IsFalse(Has(ActivityCategory.LightChange, Report("Stue", AreaState.AutoActive, TransitionReason.AdoptedAtStartup)),
			"lights adopted at start-up were never commanded, and the row says so");

		Assert.IsFalse(Has(ActivityCategory.Movement, Report("Stue", AreaState.AutoActive, TransitionReason.CircadianTick)),
			"a tick is not a movement report");
	}

	/// <summary>The two darkness verdicts are one chip each: the room that is ready, and the room that was refused.</summary>
	[TestMethod]
	public void A_Room_Dark_Enough_Is_The_Darkness_Chip_And_One_Too_Bright_Is_The_Refusal()
	{
		AreaSnapshot dusk = Report(
			"Stue", AreaState.AutoVacant, TransitionReason.CircadianTick,
			isDark: true, darknessDetail: "lux 12, dark below 40");

		AreaSnapshot bright = Report(
			"Stue", AreaState.AutoVacant, TransitionReason.CircadianTick,
			isDark: false, darknessDetail: "lux 86, dark below 40");

		Assert.AreEqual(ActivityCategory.Illumination, ActivityView.Categorise(dusk),
			"'Dark enough — movement will light the room' is the verdict itself, and nothing was turned down");

		Assert.AreEqual(ActivityCategory.Declined, ActivityView.Categorise(bright),
			"'Too bright to switch on' is a refusal, and the user guide sends a reader there to find it");

		Assert.AreEqual("lux 86, dark below 40", ActivityView.Describe(bright).Why,
			"the chip and the reading it promises have to be on the same row");

		Assert.IsFalse(Has(ActivityCategory.Illumination, Report("Stue", AreaState.AutoVacant, TransitionReason.Startup)),
			"a room whose darkness has never been checked states no verdict, and must not be offered as though it had");

		Assert.IsFalse(
			Has(ActivityCategory.Illumination, Report("Stue", AreaState.AutoVacant, TransitionReason.SuppressionLifted, isDark: true)),
			"a dark room that got there some other way says nothing about darkness on the row");
	}

	/// <summary>Background opens switched off, and the recheck shares its reason with the two rows the page exists for.</summary>
	[TestMethod]
	public void A_Tick_Is_Housekeeping_Only_When_It_Says_Nothing_Else()
	{
		Assert.IsTrue(
			Has(ActivityCategory.Background, Report("Stue", AreaState.AutoVacant, TransitionReason.CircadianTick)),
			"a recheck that found nothing is the definition of a background task");

		Assert.IsFalse(
			Has(ActivityCategory.Background, Report("Stue", AreaState.AutoVacant, TransitionReason.CircadianTick, isDark: true)),
			"the tick that found the room dark enough is the row somebody came here for");

		Assert.IsFalse(
			Has(ActivityCategory.Background, Report("Stue", AreaState.AutoActive, TransitionReason.CircadianTick)),
			"and the tick that retuned the lights is a light change");
	}

	[TestMethod]
	public void Start_Up_And_A_Rooms_Own_Switch_Are_Background_Tasks()
	{
		AreaSnapshot switchedOff = Report("Stue", AreaState.Disabled, TransitionReason.EnablementChanged);

		Assert.IsTrue(Has(ActivityCategory.Background, switchedOff));
		Assert.AreEqual("Automatic lighting switched off here", ActivityView.Describe(switchedOff).What);

		Assert.IsTrue(Has(ActivityCategory.Background, Report("Stue", AreaState.AutoVacant, TransitionReason.Startup)));
		Assert.IsTrue(Has(ActivityCategory.Background, Report("Stue", AreaState.AutoActive, TransitionReason.AdoptedAtStartup)));
	}

	// ===================== the two fates of a start-up row =====================

	/// <summary>The engine publishes one start-up report per room, so a bare one is noise at the top of a restart.</summary>
	[TestMethod]
	public void A_Start_Up_Row_With_Nothing_Under_It_Is_Not_A_Row()
	{
		AreaSnapshot bare = Report("Stue", AreaState.AutoVacant, TransitionReason.Startup, isDark: true);

		Assert.IsNull(ActivityView.Describe(bare).Why, "the fixture has to be the bare case, or this proves nothing");
		Assert.IsFalse(ActivityView.IsWorthShowing(bare));

		Assert.AreEqual(0, ActivityView.Shown([Entry(1, bare)]).Count);
	}

	/// <summary>Background, not a refusal: at boot the engine has decided nothing, it has only read the room.</summary>
	[TestMethod]
	public void A_Start_Up_Row_That_Carries_A_Reading_Stays_And_Is_Background_Only()
	{
		AreaSnapshot measured = Report(
			"Stue", AreaState.AutoVacant, TransitionReason.Startup,
			isDark: false, darknessDetail: "lux 4096 (mean of 2 of 2 sensors), dark below 40");

		ActivityLine line = ActivityView.Describe(measured);

		Assert.AreEqual("Started up — took the room as it was", line.What);
		Assert.AreEqual("Too bright to switch on — lux 4096 (mean of 2 of 2 sensors), dark below 40", line.Why);

		Assert.IsTrue(ActivityView.IsWorthShowing(measured));
		Assert.AreEqual(1, ActivityView.Shown([Entry(1, measured)]).Count);

		Assert.AreEqual(ActivityCategory.Background, ActivityView.Categorise(measured));
	}

	/// <summary>The mode a rebuild reads off the select reaches every adopted room, and no mode has moved.</summary>
	[TestMethod]
	public void Two_Rebuilds_With_The_House_Asleep_Leave_No_Rows_At_All()
	{
		AreaSnapshot first = Report(
			"Stue", AreaState.AutoActive, TransitionReason.Startup,
			at: Noon, houseModeValue: "Sover", brightness: 15);

		AreaSnapshot second = first with { Timestamp = Noon.AddMinutes(2) };

		Assert.AreEqual(0, ActivityView.Shown([Entry(2, second), Entry(1, first)]).Count);
		Assert.AreEqual(ActivityCategory.Background, ActivityView.Categorise(first));
		Assert.IsFalse(ActivityView.IsAboutTheHouse(first), "start-up says what each room was found in");
	}

	/// <summary>An away-kind mode standing at start-up sweeps the room, so the headline must not claim the lights were left alone.</summary>
	[TestMethod]
	public void A_Room_Swept_At_Start_Up_Says_The_House_Was_Already_Away()
	{
		AreaSnapshot swept = Report(
			"Stue", AreaState.Away, TransitionReason.Startup,
			mode: HouseMode.Away, houseModeValue: "Borte", isAnyoneHome: true, forced: ForcedAway());

		ActivityLine line = ActivityView.Describe(swept);

		Assert.AreEqual("Started up — the house was already away", line.What);
		Assert.AreEqual("Away mode is forced while input_boolean.occupancy is on.", line.Why,
			"what is forcing the mode still has to reach the row");

		Assert.IsTrue(ActivityView.IsWorthShowing(swept));
	}

	private static ForcedMode ForcedAway() =>
		new(ModeKind.Away, "Borte", ModeForceSource.WhileEntityOn, "input_boolean.occupancy", "on");

	/// <summary>A row with no second line is the ordinary shape of most events; only start-up is ever sifted out.</summary>
	[TestMethod]
	public void Only_Start_Up_Rows_Are_Ever_Dropped()
	{
		foreach (TransitionReason reason in Enum.GetValues<TransitionReason>())
		{
			foreach (AreaState state in Enum.GetValues<AreaState>())
			{
				AreaSnapshot snapshot = Report("Stue", state, reason, isDark: true);

				Assert.IsTrue(
					reason is TransitionReason.Startup || ActivityView.IsWorthShowing(snapshot),
					$"{reason} in {state} is an event and must reach the page");
			}
		}

		Assert.IsTrue(
			ActivityView.IsWorthShowing(Report(
				"Stue", AreaState.AutoVacant, TransitionReason.Startup, isDark: true, killSwitch: true)),
			"the master switch replaces the row with a sentence about the whole house, which is news at any time");
	}

	[TestMethod]
	public void Manual_Changes_Cover_A_Persons_Decision_And_Its_Ending()
	{
		Assert.IsTrue(Has(ActivityCategory.ManualChange, Report("Stue", AreaState.OverriddenOn, TransitionReason.ManualOn)));
		Assert.IsTrue(Has(ActivityCategory.ManualChange, Report("Stue", AreaState.SuppressedOff, TransitionReason.ManualOff)));
		Assert.IsTrue(Has(ActivityCategory.ManualChange, Report("Stue", AreaState.AutoVacant, TransitionReason.SuppressionLifted)));

		AreaSnapshot expired = Report("Stue", AreaState.AutoVacant, TransitionReason.OverrideExpired);

		Assert.AreEqual(ActivityCategory.ManualChange, ActivityView.Categorise(expired),
			"the row reads 'The manual change ran its course', which is the end of somebody's decision, not a "
			+ "light change somebody would look for under that chip");

		Assert.IsFalse(Has(ActivityCategory.ManualChange, Report("Stue", AreaState.AutoActive, TransitionReason.Motion)),
			"the engine acting on movement is not somebody's hand");
	}

	/// <summary>The shapes a refusal arrives in, and the one that goes to the movement chip instead.</summary>
	[TestMethod]
	public void The_Refusals_Are_Reachable_On_Their_Own()
	{
		AreaSnapshot ignored = Report("Stue", AreaState.SuppressedOff, TransitionReason.Motion);

		Assert.AreEqual(ActivityCategory.Movement, ActivityView.Categorise(ignored),
			"'Movement, but the lights were switched off manually' is a movement row, and switching the movement "
			+ "chip off must not leave it standing");

		Assert.IsTrue(Has(ActivityCategory.Declined, Report(
			"Soverom", AreaState.AutoVacant, TransitionReason.CircadianTick,
			isDark: true, autoOnBlockedBy: AutoOnBlock.Sleep)),
			"dark enough, but the house is asleep");

		Assert.IsTrue(Has(ActivityCategory.Declined, Report(
			"Stue", AreaState.AutoVacant, TransitionReason.CircadianTick,
			isDark: true, autoOnBlockedBy: AutoOnBlock.EntityOn, autoOnBlockingEntity: "media_player.stue_tv")),
			"dark enough, but something in the room is on");

		Assert.IsTrue(Has(ActivityCategory.Declined, Report(
			"Stue", AreaState.AutoVacant, TransitionReason.CircadianTick, isDark: false)),
			"too bright — the commonest refusal of all, and it has to be in the chip that promises every refusal");

		Assert.IsFalse(Has(ActivityCategory.Declined, Report("Stue", AreaState.AutoVacant, TransitionReason.CircadianTick)),
			"a room whose darkness was never checked has refused nothing");

		Assert.IsFalse(Has(ActivityCategory.Declined, Report("Stue", AreaState.AutoActive, TransitionReason.Motion)),
			"movement that lit the room is not a refusal");
	}

	[TestMethod]
	public void A_Blocked_Room_Names_Its_Block_And_Is_Never_Hidden_When_The_Page_Opens()
	{
		foreach (TransitionReason reason in Enum.GetValues<TransitionReason>())
		{
			AreaSnapshot asleep = Report(
				"Soverom", AreaState.AutoVacant, reason,
				isDark: true, darknessDetail: "lux 12, dark below 40",
				mode: HouseMode.Sleep, autoOnBlockedBy: AutoOnBlock.Sleep);

			// Start-up refused nothing; it only read the room. Every other reason files the row under a chip
			// that is showing when the page opens, so the reason a light did not come on is never hidden.
			if (reason is not TransitionReason.Startup)
			{
				Assert.AreNotEqual(
					ActivityCategory.None, ActivityView.Categorise(asleep) & ActivityView.DefaultCategories,
					$"{reason}: a blocked room is why somebody opened this page, and it opened without the row");
			}

			ActivityLine line = ActivityView.Describe(asleep);

			StringAssert.Contains(
				$"{line.What} {line.Why}",
				"the house is asleep",
				$"{reason}: filed under 'nothing happened' with no word about what was in the way");
		}

		AreaSnapshot television = Report(
			"Stue", AreaState.AutoVacant, TransitionReason.ManualOff,
			isDark: true, autoOnBlockedBy: AutoOnBlock.EntityOn, autoOnBlockingEntity: "media_player.stue_tv");

		Assert.AreEqual(
			"Dark enough, but media_player.stue_tv is on — movement won't light the room.",
			ActivityView.Describe(television).Why,
			"named rather than alluded to, in the same words the dusk row and the board's tray use");
	}

	[TestMethod]
	public void An_Unblocked_Dark_Room_Gains_No_Condition_Line()
	{
		Assert.IsNull(
			ActivityView.Describe(Report("Stue", AreaState.AutoVacant, TransitionReason.SuppressionLifted, isDark: true)).Why,
			"an older report that never carried the verdict must not grow one");

		Assert.IsNull(
			ActivityView.Describe(Report(
				"Stue", AreaState.AutoVacant, TransitionReason.SuppressionLifted,
				isDark: true, autoOnBlockedBy: AutoOnBlock.None)).Why,
			"nothing in the way is nothing to say");

		Assert.AreEqual(
			"Too bright to switch on — lux 86, dark below 40",
			ActivityView.Describe(Report(
				"Stue", AreaState.AutoVacant, TransitionReason.SuppressionLifted,
				isDark: false, darknessDetail: "lux 86, dark below 40",
				autoOnBlockedBy: AutoOnBlock.Sleep)).Why,
			"a room that is not dark enough is answered by the darkness gate, which is the earlier refusal");
	}

	/// <summary>The master switch replaces a row's words outright, so it replaces its categories too.</summary>
	[TestMethod]
	public void House_Events_And_The_Master_Switch_Share_One_Chip()
	{
		Assert.IsTrue(Has(ActivityCategory.House, Report("Stue", AreaState.Away, TransitionReason.EveryoneLeft)));
		Assert.IsTrue(Has(ActivityCategory.House, Report("Stue", AreaState.AutoVacant, TransitionReason.FirstPersonArrived)));
		Assert.IsTrue(Has(ActivityCategory.House, Report("Stue", AreaState.SceneHold, TransitionReason.SceneHold)));

		AreaSnapshot paused = Report(
			"Stue", AreaState.Disabled, TransitionReason.EnablementChanged, isDark: true, killSwitch: true);

		Assert.AreEqual(ActivityCategory.House, ActivityView.Categorise(paused));
		Assert.AreEqual("Paused by the master switch", ActivityView.Describe(paused).What,
			"the row says nothing about darkness or a room's own switch, so it is filed under neither");
	}

	[TestMethod]
	public void A_Mode_Change_Has_A_Chip_Of_Its_Own()
	{
		AreaSnapshot mode = Report("Stue", AreaState.AutoVacant, TransitionReason.HouseModeChanged);

		Assert.IsTrue(Has(ActivityCategory.Mode, mode));
		Assert.IsFalse(Has(ActivityCategory.House, mode),
			"the chip it left has to stop offering it, or the split bought nothing");

		foreach (TransitionReason reason in Enum.GetValues<TransitionReason>())
		{
			if (reason is TransitionReason.HouseModeChanged)
				continue;

			foreach (AreaState state in Enum.GetValues<AreaState>())
			{
				Assert.IsFalse(
					Has(ActivityCategory.Mode, Report("Stue", state, reason, isDark: true)),
					$"{reason} in {state} is not a change of mode, and the chip must hold only what its name says");
			}
		}
	}

	[TestMethod]
	public void The_Mode_Chip_Opens_Switched_On()
	{
		Assert.AreEqual(ActivityCategory.Mode, ActivityView.DefaultCategories & ActivityCategory.Mode);
		Assert.AreEqual(ActivityCategory.Mode, ActivityView.AllCategories & ActivityCategory.Mode);
	}

	/// <summary>Categorising and collapsing are separate; a chip split must not split the runs with it.</summary>
	[TestMethod]
	public void Splitting_The_Chip_Leaves_A_Mode_Change_House_Wide()
	{
		ActivityEntry[] entries =
		[
			Entry(3, Mode("Kontor", "Sover", Noon.AddSeconds(1))),
			Entry(2, Mode("Stue", "Sover", Noon.AddSeconds(1))),
			Entry(1, Mode("Bad", "Sover", Noon.AddSeconds(1)))
		];

		IReadOnlyList<ActivityRow> rows = ActivityView.Rows(
			ActivityView.InCategories(entries, ActivityView.DefaultCategories));

		Assert.AreEqual(1, rows.Count);
		Assert.IsTrue(rows[0].IsAboutTheHouse);
		Assert.AreEqual(3, rows[0].Rooms.Count);
	}

	[TestMethod]
	public void Only_Background_Tasks_Start_Switched_Off()
	{
		Assert.AreEqual(
			ActivityView.AllCategories & ~ActivityCategory.Background,
			ActivityView.DefaultCategories);

		Assert.AreEqual(ActivityCategory.None, ActivityView.DefaultCategories & ActivityCategory.Background);

		foreach (ActivityFilterChip chip in ActivityView.Chips([], ActivityView.DefaultCategories))
			Assert.AreEqual(chip.Category != ActivityCategory.Background, chip.IsOn, $"{chip.Label} opens wrong");
	}

	[TestMethod]
	public void The_Category_Filter_Keeps_Only_What_Is_Chosen_In_Order()
	{
		ActivityEntry[] entries =
		[
			Entry(4, Report("Stue", AreaState.AutoActive, TransitionReason.Motion, at: Noon.AddMinutes(3))),
			Entry(3, Report("Stue", AreaState.AutoVacant, TransitionReason.CircadianTick, at: Noon.AddMinutes(2))),
			Entry(2, Report("Stue", AreaState.SuppressedOff, TransitionReason.ManualOff, at: Noon.AddMinutes(1))),
			Entry(1, Report("Stue", AreaState.AutoVacant, TransitionReason.Startup, at: Noon))
		];

		CollectionAssert.AreEqual(
			new long[] { 4, 2 },
			ActivityView.InCategories(entries, ActivityCategory.Movement | ActivityCategory.ManualChange)
				.Select(entry => entry.Sequence).ToArray());

		CollectionAssert.AreEqual(
			new long[] { 3, 1 },
			ActivityView.InCategories(entries, ActivityCategory.Background)
				.Select(entry => entry.Sequence).ToArray());

		Assert.AreEqual(0, ActivityView.InCategories(entries, ActivityCategory.None).Count,
			"every chip switched off shows nothing, which the page answers with words rather than a blank");
	}

	/// <summary>The two filters compose, and the answer is the same in either order.</summary>
	[TestMethod]
	public void The_Room_And_The_Categories_Narrow_Together()
	{
		ActivityEntry[] entries =
		[
			Entry(4, Report("Stue", AreaState.AutoActive, TransitionReason.Motion)),
			Entry(3, Report("Bad", AreaState.AutoActive, TransitionReason.Motion)),
			Entry(2, Report("Stue", AreaState.AutoVacant, TransitionReason.CircadianTick)),
			Entry(1, Report("Bad", AreaState.SuppressedOff, TransitionReason.ManualOff))
		];

		IReadOnlyList<ActivityEntry> both =
			ActivityView.InCategories(ActivityView.InRoom(entries, "Stue"), ActivityCategory.Movement);

		CollectionAssert.AreEqual(new long[] { 4 }, both.Select(entry => entry.Sequence).ToArray());

		CollectionAssert.AreEqual(
			both.Select(entry => entry.Sequence).ToArray(),
			ActivityView.InRoom(ActivityView.InCategories(entries, ActivityCategory.Movement), "Stue")
				.Select(entry => entry.Sequence).ToArray(),
			"the two filters have to be independent, or the page's order of operations would be a decision");
	}

	/// <summary>A category holding nothing keeps its chip, showing zero, counted over what the room filter left.</summary>
	[TestMethod]
	public void Chips_Count_What_The_Chosen_Room_Did()
	{
		ActivityEntry[] entries =
		[
			Entry(3, Report("Stue", AreaState.AutoActive, TransitionReason.Motion)),
			Entry(2, Report("Stue", AreaState.SuppressedOff, TransitionReason.Motion)),
			Entry(1, Report("Bad", AreaState.SuppressedOff, TransitionReason.ManualOff))
		];

		IReadOnlyList<ActivityFilterChip> whole = ActivityView.Chips(entries, ActivityView.DefaultCategories);

		Assert.AreEqual(8, whole.Count, "every category is offered, whatever the timeline happens to hold");
		Assert.AreEqual(2, Chip(whole, ActivityCategory.Movement).Count);
		Assert.AreEqual(1, Chip(whole, ActivityCategory.ManualChange).Count);
		Assert.AreEqual(0, Chip(whole, ActivityCategory.House).Count);

		IReadOnlyList<ActivityFilterChip> stue =
			ActivityView.Chips(ActivityView.InRoom(entries, "Stue"), ActivityView.DefaultCategories);

		Assert.AreEqual(2, Chip(stue, ActivityCategory.Movement).Count);
		Assert.AreEqual(0, Chip(stue, ActivityCategory.ManualChange).Count,
			"the counts follow the room, or the two filters would each describe a different timeline");

		foreach (ActivityFilterChip chip in whole)
		{
			Assert.IsFalse(string.IsNullOrWhiteSpace(chip.Label), "a chip with no name is a chip nobody can use");
			Assert.IsFalse(string.IsNullOrWhiteSpace(chip.Title));
		}
	}

	/// <summary>The note names which filter is holding them back, not only how many.</summary>
	[TestMethod]
	public void The_Page_Says_How_Much_The_Filters_Are_Holding_Back()
	{
		Assert.IsNull(ActivityView.HiddenNote(12, 12, ActivityView.AllRooms, ActivityView.AllCategories),
			"nothing hidden, nothing said");

		Assert.IsNull(ActivityView.HiddenNote(0, 0, ActivityView.AllRooms, ActivityView.DefaultCategories),
			"an empty timeline is not a filtered one; the page has other words for that");

		string? categories = ActivityView.HiddenNote(40, 12, ActivityView.AllRooms, ActivityView.DefaultCategories);

		StringAssert.Contains(categories, "28 reports are hidden");
		StringAssert.Contains(categories, "categories");
		Assert.IsFalse(categories!.Contains("rooms", StringComparison.Ordinal),
			"no room was chosen, so blaming the room filter would send somebody to the wrong control");

		string? room = ActivityView.HiddenNote(40, 12, "Stue", ActivityView.AllCategories);

		StringAssert.Contains(room, "28 reports are hidden");
		StringAssert.Contains(room, "other rooms");

		string? both = ActivityView.HiddenNote(40, 12, "Stue", ActivityView.DefaultCategories);

		StringAssert.Contains(both, "other rooms");
		StringAssert.Contains(both, "categories");

		StringAssert.Contains(
			ActivityView.HiddenNote(13, 12, ActivityView.AllRooms, ActivityView.DefaultCategories),
			"1 report is hidden");
	}

	// ===================== one row per thing that happened =====================

	[TestMethod]
	public void One_House_Mode_Change_Is_One_Row_Belonging_To_No_Room()
	{
		ActivityEntry[] entries =
		[
			Entry(4, Mode("Kontor", "Home", Noon.AddSeconds(2))),
			Entry(3, Mode("Kjeller - bad", "Home", Noon.AddSeconds(2))),
			Entry(2, Mode("Stue", "Home", Noon.AddSeconds(2))),
			Entry(1, Mode("Kjøkken", "Home", Noon.AddSeconds(2)))
		];

		IReadOnlyList<ActivityRow> rows = ActivityView.Rows(entries);

		Assert.AreEqual(1, rows.Count, "four reports of one event are one row");
		Assert.AreEqual("Mode changed to Home", rows[0].Line.What);
		Assert.IsTrue(rows[0].IsAboutTheHouse);
		Assert.IsNull(rows[0].Room, "no room did this, so the row names none");
		Assert.AreEqual(4, rows[0].Rooms.Count);
		Assert.AreEqual(4, rows[0].Sequence, "the row carries the newest report of the run");

		string? reported = ActivityView.ReportedBy(rows[0]);

		StringAssert.Contains(reported, "4 rooms");
		StringAssert.Contains(reported, "Kjøkken", "the rooms are still reachable, or the row lost the only fact it had");
	}

	/// <summary>The reporting room is the hover, never the column, even when there is only one of them.</summary>
	[TestMethod]
	public void A_Lone_House_Event_Is_Still_Not_A_Rooms_Doing()
	{
		IReadOnlyList<ActivityRow> rows = ActivityView.Rows([Entry(1, Mode("Kontor", "Home", Noon))]);

		Assert.AreEqual(1, rows.Count);
		Assert.IsTrue(rows[0].IsAboutTheHouse);
		Assert.IsNull(rows[0].Room);
		Assert.AreEqual("Reported by Kontor.", ActivityView.ReportedBy(rows[0]));
	}

	[TestMethod]
	public void A_Rooms_Own_Reports_Are_Never_Pooled()
	{
		ActivityEntry[] entries =
		[
			Entry(3, Report("Stue", AreaState.AutoActive, TransitionReason.Motion, at: Noon.AddSeconds(2))),
			Entry(2, Report("Bad", AreaState.AutoActive, TransitionReason.Motion, at: Noon.AddSeconds(1))),
			Entry(1, Report("Kjøkken", AreaState.AutoActive, TransitionReason.Motion, at: Noon))
		];

		IReadOnlyList<ActivityRow> rows = ActivityView.Rows(entries);

		Assert.AreEqual(3, rows.Count);
		CollectionAssert.AreEqual(
			new[] { "Stue", "Bad", "Kjøkken" },
			rows.Select(row => row.Room).ToArray(),
			"three rooms moved; each says so under its own name");

		foreach (ActivityRow row in rows)
		{
			Assert.IsFalse(row.IsAboutTheHouse);
			Assert.IsNull(ActivityView.ReportedBy(row), "a row that already names its room does not need telling");
		}
	}

	/// <summary>Speaking for the house is not the same test as the House chip; a scene hold has one and not the other.</summary>
	[TestMethod]
	public void Only_The_Sentences_That_Speak_For_The_House_Are_Unattributed()
	{
		Assert.IsTrue(ActivityView.IsAboutTheHouse(Mode("Stue", "Home", Noon)));
		Assert.IsTrue(ActivityView.IsAboutTheHouse(Report("Stue", AreaState.Away, TransitionReason.EveryoneLeft)));
		Assert.IsTrue(ActivityView.IsAboutTheHouse(
			Report("Stue", AreaState.AutoVacant, TransitionReason.FirstPersonArrived)));
		Assert.IsTrue(
			ActivityView.IsAboutTheHouse(
				Report("Stue", AreaState.Disabled, TransitionReason.EnablementChanged, killSwitch: true)),
			"the master switch replaces the row's words with a sentence about the whole house");

		Assert.IsFalse(
			ActivityView.IsAboutTheHouse(Report("Stue", AreaState.SceneHold, TransitionReason.SceneHold)),
			"the mode is house-wide, but 'a guest scene took THIS ROOM over' is a claim about one room");
		Assert.IsFalse(
			ActivityView.IsAboutTheHouse(Report("Stue", AreaState.Disabled, TransitionReason.EnablementChanged)),
			"'switched off for this room' is a per-room fact even when a house-wide action caused it");
		Assert.IsFalse(ActivityView.IsAboutTheHouse(Report("Stue", AreaState.AutoVacant, TransitionReason.Startup)));
		Assert.IsFalse(ActivityView.IsAboutTheHouse(Report("Stue", AreaState.AutoActive, TransitionReason.Motion)));
	}

	[TestMethod]
	public void House_Rows_Collapse_Only_When_The_Whole_Sentence_Matches()
	{
		ActivityEntry[] entries =
		[
			Entry(4, Mode("Kontor", "Home", Noon.AddSeconds(3))),
			Entry(3, Mode("Stue", "Home", Noon.AddSeconds(3))),
			Entry(2, Mode("Kontor", "Guests", Noon.AddSeconds(1))),
			Entry(1, Mode("Stue", "Guests", Noon.AddSeconds(1)))
		];

		IReadOnlyList<ActivityRow> rows = ActivityView.Rows(entries);

		Assert.AreEqual(2, rows.Count);
		Assert.AreEqual("Mode changed to Home", rows[0].Line.What);
		Assert.AreEqual("Mode changed to Guests", rows[1].Line.What);
		Assert.AreEqual(2, rows[0].Rooms.Count);
		Assert.AreEqual(2, rows[1].Rooms.Count);
	}

	/// <summary>Dropping the publishing room's condition is what lets one mode change be one row.</summary>
	[TestMethod]
	public void A_House_Row_Carries_The_House_And_Not_The_Room_That_Published_It()
	{
		ActivityEntry[] entries =
		[
			Entry(2, Mode("Kontor", "Home", Noon) with { State = AreaState.Disabled }),
			Entry(1, Mode("Stue", "Home", Noon) with { IsDark = false, DarknessDetail = "lux 214, dark below 40" })
		];

		IReadOnlyList<ActivityRow> rows = ActivityView.Rows(entries);

		Assert.AreEqual(1, rows.Count, "one mode change, whatever each room happened to be doing when it landed");
		Assert.AreEqual("Mode changed to Home", rows[0].Line.What);
		Assert.IsNull(rows[0].Line.Why, "a room's own verdict has no room to belong to on this row");
		Assert.AreEqual(2, rows[0].Rooms.Count);

		// Only the row drops it. Describe is what the room page reads, and there it is still attributed.
		StringAssert.Contains(
			ActivityView.Describe(entries[1]).Why,
			"lux 214",
			"Describe is what the room page reads, and there the condition is attributed and wanted");
	}

	/// <summary>Its second line is about the house, not the publishing room, so the collapse keeps it.</summary>
	[TestMethod]
	public void The_Master_Switch_Keeps_Its_Own_Second_Line()
	{
		IReadOnlyList<ActivityRow> rows = ActivityView.Rows(
		[
			Entry(2, Report("Kontor", AreaState.Disabled, TransitionReason.EnablementChanged, killSwitch: true)),
			Entry(1, Report("Stue", AreaState.Disabled, TransitionReason.EnablementChanged, killSwitch: true))
		]);

		Assert.AreEqual(1, rows.Count);
		Assert.AreEqual("Paused by the master switch", rows[0].Line.What);
		StringAssert.Contains(rows[0].Line.Why, "until it's turned back on");
	}

	[TestMethod]
	public void Only_A_Run_Collapses_Never_A_Scattered_Set()
	{
		ActivityEntry[] entries =
		[
			Entry(3, Mode("Kontor", "Home", Noon.AddSeconds(4))),
			Entry(2, Report("Stue", AreaState.AutoActive, TransitionReason.Motion, at: Noon.AddSeconds(2))),
			Entry(1, Mode("Kontor", "Home", Noon))
		];

		IReadOnlyList<ActivityRow> rows = ActivityView.Rows(entries);

		Assert.AreEqual(3, rows.Count);
		Assert.IsTrue(rows[0].IsAboutTheHouse);
		Assert.IsFalse(rows[1].IsAboutTheHouse);
		Assert.IsTrue(rows[2].IsAboutTheHouse);
	}

	/// <summary>The window bounds how late a report may arrive and still count as part of the same event.</summary>
	[TestMethod]
	public void Reports_Further_Apart_Than_The_Window_Stay_Two_Rows()
	{
		Assert.AreEqual(
			1,
			ActivityView.Rows(
			[
				Entry(2, Mode("Kontor", "Home", Noon + ActivityView.CollapseWindow)),
				Entry(1, Mode("Stue", "Home", Noon))
			]).Count,
			"exactly at the window is still one event");

		IReadOnlyList<ActivityRow> apart = ActivityView.Rows(
		[
			Entry(2, Mode("Kontor", "Home", Noon + ActivityView.CollapseWindow + TimeSpan.FromSeconds(1))),
			Entry(1, Mode("Stue", "Home", Noon))
		]);

		Assert.AreEqual(2, apart.Count, "an hour apart with a silent house between them is two events, not one");
	}

	/// <summary>Otherwise the last row's room count would be a fact about the budget, not about the house.</summary>
	[TestMethod]
	public void A_Limited_Read_Still_Finishes_Its_Last_Run()
	{
		ActivityEntry[] entries =
		[
			Entry(5, Report("Stue", AreaState.AutoActive, TransitionReason.Motion, at: Noon.AddSeconds(9))),
			Entry(4, Mode("Kontor", "Home", Noon.AddSeconds(5))),
			Entry(3, Mode("Stue", "Home", Noon.AddSeconds(5))),
			Entry(2, Mode("Bad", "Home", Noon.AddSeconds(5))),
			Entry(1, Mode("Kjøkken", "Home", Noon.AddSeconds(5)))
		];

		IReadOnlyList<ActivityRow> rows = ActivityView.Rows(entries, 2);

		Assert.AreEqual(2, rows.Count);
		Assert.AreEqual(4, rows[1].Rooms.Count, "the run is finished before the budget is honoured");
		Assert.AreEqual(0, ActivityView.Rows(entries, 0).Count);
	}

	// ===================== the standing darkness verdict =====================

	/// <summary>One row per spell, carrying the newest of the run: a room dark from dusk to dawn reports it hundreds of times.</summary>
	[TestMethod]
	public void A_Room_That_Stays_Dark_Says_So_Once()
	{
		ActivityEntry[] entries =
		[
			Entry(3, Dusk("Stue", dark: true, "lux 9, dark below 40", Noon.AddMinutes(2))),
			Entry(2, Dusk("Stue", dark: true, "lux 11, dark below 40", Noon.AddMinutes(1))),
			Entry(1, Dusk("Stue", dark: true, "lux 12, dark below 40", Noon))
		];

		IReadOnlyList<ActivityRow> rows = ActivityView.Rows(entries);

		Assert.AreEqual(1, rows.Count, "the room became dark once; the rest is the same fact re-measured");
		Assert.AreEqual(3, rows[0].Sequence, "the newest of the run, so the reading under it is the current one");
		Assert.AreEqual("lux 9, dark below 40", rows[0].Line.Why,
			"matching on the whole line would collapse nothing at all — the reading moves every tick");
	}

	[TestMethod]
	public void A_Verdict_That_Changes_Starts_A_New_Row()
	{
		ActivityEntry[] entries =
		[
			Entry(4, Dusk("Stue", dark: true, "lux 8, dark below 40", Noon.AddMinutes(3))),
			Entry(3, Dusk("Stue", dark: false, "lux 96, dark below 40", Noon.AddMinutes(2))),
			Entry(2, Dusk("Stue", dark: false, "lux 120, dark below 40", Noon.AddMinutes(1))),
			Entry(1, Dusk("Stue", dark: true, "lux 12, dark below 40", Noon))
		];

		IReadOnlyList<ActivityRow> rows = ActivityView.Rows(entries);

		Assert.AreEqual(3, rows.Count);
		Assert.AreEqual("Dark enough — movement will light the room", rows[0].Line.What);
		Assert.AreEqual("Too bright to switch on", rows[1].Line.What);
		Assert.AreEqual("Dark enough — movement will light the room", rows[2].Line.What);
	}

	/// <summary>A run is the room's own: every room is re-checked in the same pass, so a room's repeats are never adjacent.</summary>
	[TestMethod]
	public void Other_Rooms_Between_The_Repeats_Do_Not_End_The_Run()
	{
		ActivityEntry[] entries =
		[
			Entry(4, Dusk("Stue", dark: true, "lux 9, dark below 40", Noon.AddMinutes(1))),
			Entry(3, Dusk("Bad", dark: true, "lux 7, dark below 40", Noon.AddMinutes(1))),
			Entry(2, Dusk("Stue", dark: true, "lux 12, dark below 40", Noon)),
			Entry(1, Dusk("Bad", dark: true, "lux 10, dark below 40", Noon))
		];

		IReadOnlyList<ActivityRow> rows = ActivityView.Rows(entries);

		Assert.AreEqual(2, rows.Count, "two rooms went dark; each says so once");
		CollectionAssert.AreEqual(new[] { "Stue", "Bad" }, rows.Select(row => row.Room).ToArray());
	}

	[TestMethod]
	public void Something_Happening_In_The_Room_Ends_The_Run()
	{
		ActivityEntry[] entries =
		[
			Entry(3, Dusk("Stue", dark: true, "lux 9, dark below 40", Noon.AddMinutes(2))),
			Entry(2, Report("Stue", AreaState.AutoActive, TransitionReason.Motion, at: Noon.AddMinutes(1), isDark: true)),
			Entry(1, Dusk("Stue", dark: true, "lux 12, dark below 40", Noon))
		];

		IReadOnlyList<ActivityRow> rows = ActivityView.Rows(entries);

		Assert.AreEqual(3, rows.Count);
		Assert.AreEqual("Movement — lights on", rows[1].Line.What);
	}

	/// <summary>The block is part of the verdict, so dark and then dark-but-blocked is two rows.</summary>
	[TestMethod]
	public void A_Block_Appearing_Is_A_New_Verdict()
	{
		ActivityEntry[] entries =
		[
			Entry(2, Report(
				"Soverom", AreaState.AutoVacant, TransitionReason.CircadianTick, at: Noon.AddMinutes(1),
				isDark: true, darknessDetail: "lux 9, dark below 40", autoOnBlockedBy: AutoOnBlock.Sleep)),
			Entry(1, Dusk("Soverom", dark: true, "lux 12, dark below 40", Noon))
		];

		IReadOnlyList<ActivityRow> rows = ActivityView.Rows(entries);

		Assert.AreEqual(2, rows.Count);
		Assert.AreEqual("Dark enough, but the house is asleep — movement won't light the room", rows[0].Line.What);
		Assert.AreEqual("Dark enough — movement will light the room", rows[1].Line.What);
	}

	[TestMethod]
	public void An_Unmeasured_Recheck_Is_Not_A_Verdict()
	{
		ActivityEntry[] entries =
		[
			Entry(2, Report("Stue", AreaState.AutoVacant, TransitionReason.CircadianTick, at: Noon.AddMinutes(1))),
			Entry(1, Report("Stue", AreaState.AutoVacant, TransitionReason.CircadianTick, at: Noon))
		];

		Assert.AreEqual(2, ActivityView.Rows(entries).Count,
			"'Rechecked the room' is housekeeping and hidden by default; collapsing it would hide it twice");
	}

	// ===================== what the dashboard's summary shows =====================

	/// <summary>The summary reads the activity page's own default set and spends its budget after the filter.</summary>
	[TestMethod]
	public void The_Dashboard_Summary_Applies_The_Activity_Pages_Default_Categories()
	{
		List<ActivityEntry> entries = [];
		long sequence = 40;

		// A quiet house: three switched-off rooms rechecking themselves, four real decisions scattered through.
		for (int tick = 0; tick < 12; tick++)
		{
			foreach (string room in new[] { "Stue", "Kjeller - multimedia", "Kjøkken" })
			{
				entries.Add(Entry(
					sequence--,
					Report(room, AreaState.Disabled, TransitionReason.CircadianTick, at: Noon.AddMinutes(-tick))));
			}

			if (tick % 3 == 0)
			{
				entries.Add(Entry(
					sequence--,
					Report("Kontor", AreaState.AutoActive, TransitionReason.Motion, at: Noon.AddMinutes(-tick))));
			}
		}

		IReadOnlyList<ActivityEntry> kept = ActivityView.InCategories(entries, ActivityView.DefaultCategories);
		IReadOnlyList<ActivityRow> rows = ActivityView.Rows(kept, BoardView.LogPreview);

		Assert.AreEqual(4, kept.Count, "the housekeeping is what the default set leaves out");
		Assert.AreEqual(4, rows.Count);

		foreach (ActivityRow row in rows)
		{
			Assert.AreNotEqual("Rechecked the room", row.Line.What,
				"the rows the owner quoted are exactly the ones the default set hides");
		}
	}

	[TestMethod]
	public void The_Summary_Spends_Its_Budget_On_Rows_It_Will_Show()
	{
		List<ActivityEntry> entries = [];

		for (long sequence = 60; sequence > 0; sequence--)
		{
			entries.Add(sequence % 4 == 0
				? Entry(sequence, Report("Stue", AreaState.AutoActive, TransitionReason.Motion, at: Noon))
				: Entry(sequence, Report("Bad", AreaState.Disabled, TransitionReason.CircadianTick, at: Noon)));
		}

		IReadOnlyList<ActivityRow> rows = ActivityView.Rows(
			ActivityView.InCategories(entries, ActivityView.DefaultCategories),
			BoardView.LogPreview);

		Assert.AreEqual(BoardView.LogPreview, rows.Count, "a full budget of rows worth reading, not of hidden ones");
	}

	/// <summary>Asserted as membership, so a new category forces a decision instead of silently joining the summary.</summary>
	[TestMethod]
	public void The_Summary_Carries_Only_What_The_Board_Cannot_Draw()
	{
		foreach (ActivityCategory carried in new[]
		{
			ActivityCategory.ManualChange, ActivityCategory.Declined, ActivityCategory.Mode, ActivityCategory.House
		})
		{
			Assert.AreNotEqual(ActivityCategory.None, ActivityView.SummaryCategories & carried,
				$"{carried} is something the lanes cannot draw, so the summary has to say it in words");
		}

		foreach (ActivityCategory drawn in new[]
		{
			ActivityCategory.Movement, ActivityCategory.LightChange,
			ActivityCategory.Illumination, ActivityCategory.Background
		})
		{
			Assert.AreEqual(ActivityCategory.None, ActivityView.SummaryCategories & drawn,
				$"the board above already shows {drawn}; repeating it in words spends the whole row budget");
		}

		// A movement the engine turned down is worded as movement, so the summary leaves it to the lane mark
		// BoardView.Refusals already paints for the same report. Pinned, because it reads like a loss otherwise.
		AreaSnapshot refused = Report(
			"Gang", AreaState.AutoVacant, TransitionReason.Motion,
			isDark: false, autoOnBlockedBy: AutoOnBlock.NotDark);

		Assert.AreEqual(ActivityCategory.None, ActivityView.Categorise(refused) & ActivityView.SummaryCategories,
			"the lanes draw this one, and the summary carries only what they cannot");

		Assert.AreNotEqual(ActivityCategory.None, ActivityView.Categorise(refused) & ActivityView.DefaultCategories,
			"the Activity page still opens with it, which is where somebody goes to read the reason");

		// The narrowing is the summary's alone. The page the footer links to still opens on the wide set.
		Assert.AreNotEqual(ActivityCategory.None, ActivityView.DefaultCategories & ActivityCategory.Movement);
		Assert.AreNotEqual(ActivityCategory.None, ActivityView.DefaultCategories & ActivityCategory.LightChange);
		Assert.AreNotEqual(ActivityCategory.None, ActivityView.DefaultCategories & ActivityCategory.Illumination);
	}

	/// <summary>Counted against what the Activity page would draw: the buffer also holds background and start-up reports.</summary>
	[TestMethod]
	public void The_Summarys_Footer_Counts_Only_What_The_Activity_Page_Would_Draw()
	{
		Assert.AreEqual("nothing recorded yet", BoardView.LogFoot(0, 0, 0, 0, ActivityLog.Capacity));

		// 137 held, all reachable; 63 kept as 11 rows. "newest 11 of 63" invites a subtraction of fifty-two rows nobody cut.
		string wholeThing = BoardView.LogFoot(137, 137, 63, 11, ActivityLog.Capacity);

		StringAssert.Contains(wholeThing, "63 reports");
		Assert.IsFalse(wholeThing.Contains("11", StringComparison.Ordinal),
			"nothing was cut, so no row count is set against the report count");
		StringAssert.Contains(wholeThing, "74 everyday reports on the Activity page");

		// 100 held, 5 dropped by Shown() and 30 background, so 65 are reachable. Against the buffer it claims 92.
		StringAssert.Contains(BoardView.LogFoot(100, 65, 8, 8, ActivityLog.Capacity),
			"57 everyday reports on the Activity page");

		string quiet = BoardView.LogFoot(40, 40, 40, 8, ActivityLog.Capacity);

		StringAssert.Contains(quiet, "40 reports");
		Assert.IsFalse(quiet.Contains("everyday", StringComparison.Ordinal),
			"nothing was held back, so nothing is claimed to be");

		// Budget spent: the line says how many rows it drew, and out of how many reports.
		string cut = BoardView.LogFoot(600, 600, 240, BoardView.LogPreview, ActivityLog.Capacity);

		StringAssert.Contains(cut, $"newest {BoardView.LogPreview} rows of 240 reports");
		StringAssert.Contains(cut, "360 everyday reports on the Activity page");

		StringAssert.Contains(BoardView.LogFoot(1, 1, 1, 1, ActivityLog.Capacity), "1 report");
		StringAssert.Contains(BoardView.LogFoot(3, 3, 2, 2, ActivityLog.Capacity), "1 everyday report on the Activity page");

		string routineOnly = BoardView.LogFoot(71, 71, 0, 0, ActivityLog.Capacity);

		Assert.AreEqual("71 everyday reports on the Activity page", routineOnly,
			"'0 reports' beside a count of held-back ones reads as a contradiction: the log plainly holds something");

		StringAssert.Contains(
			BoardView.LogFoot(ActivityLog.Capacity, ActivityLog.Capacity, 300, 12, ActivityLog.Capacity),
			$"the most recent {ActivityLog.Capacity} are kept",
			"a full buffer has started forgetting, and a reader who is not told will read the oldest row as the beginning");
	}

	// ===================== the engine's own rebuilds =====================

	[TestMethod]
	public void A_Save_Is_One_Row_However_Many_Rooms_Were_Rebuilt()
	{
		ActivityLog log = new();

		log.Record(Report("Stue", at: Noon));
		log.Record(Report("Kjøkken", at: Noon));
		log.Record(new EngineNotice(EngineNoticeKind.SettingsSaved, Noon.AddSeconds(1)));

		IReadOnlyList<ActivityRow> rows = ActivityView.Rows(ActivityView.Shown(log.Entries));

		Assert.AreEqual(1, rows.Count(row => row.Line.What == "Settings saved — every room rebuilt"));
		Assert.AreEqual(3, log.Newest, "one running count, shared with the reports either side of it");
	}

	[TestMethod]
	public void A_Rebuild_Row_Belongs_To_The_House_And_Names_No_Room()
	{
		ActivityEntry entry = Entry(1, EngineNoticeKind.Started);
		ActivityRow row = ActivityView.Rows([entry]).Single();

		Assert.AreEqual("Adaptive lighting started", row.Line.What);
		Assert.IsTrue(row.IsAboutTheHouse);
		Assert.IsNull(row.Room);
		Assert.AreEqual(0, row.Rooms.Count, "no room reported it, so none may be named");
		Assert.IsNull(ActivityView.ReportedBy(row), "and nothing claims a room did");
		Assert.IsNull(row.Snapshot, "no state, no period and no darkness verdict were invented for it");
	}

	[TestMethod]
	public void A_Rebuild_Row_Is_Background_So_The_Page_Opens_Without_It()
	{
		ActivityEntry entry = Entry(1, EngineNoticeKind.SettingsSaved);

		Assert.AreEqual(ActivityCategory.Background, ActivityView.Categorise(entry));

		Assert.AreEqual(0, ActivityView.InCategories([entry], ActivityView.DefaultCategories).Count,
			"Background starts switched off, and this row is housekeeping");

		Assert.AreEqual(1, ActivityView.InCategories([entry], ActivityCategory.Background).Count);
		Assert.AreEqual(1, Chip(ActivityView.Chips([entry], ActivityView.DefaultCategories), ActivityCategory.Background).Count);
	}

	[TestMethod]
	public void The_Room_Filter_And_The_Category_Filter_Still_Compose_Over_A_Rebuild_Row()
	{
		IReadOnlyList<ActivityEntry> entries =
		[
			Entry(2, EngineNoticeKind.Started, Noon.AddSeconds(1)),
			Entry(1, Report("Stue", AreaState.OverriddenOn, TransitionReason.ManualOn, at: Noon))
		];

		CollectionAssert.AreEqual(new[] { "Stue" }, ActivityView.Rooms(entries).ToArray(),
			"a row no room reported must not add a nameless option to the dropdown");

		IReadOnlyList<ActivityEntry> inRoom = ActivityView.InRoom(entries, "Stue");

		Assert.AreEqual(1, inRoom.Count, "the rebuild belongs to no room, so choosing one leaves it out");
		Assert.AreEqual(2, ActivityView.InRoom(entries, ActivityView.AllRooms).Count);

		Assert.AreEqual(1, ActivityView.InCategories(entries, ActivityCategory.Background).Count,
			"and the category filter still reaches it when no room is chosen");
	}

	/// <summary>Its own row, not a member of the run beside it: the collapse is about rooms saying one thing.</summary>
	[TestMethod]
	public void A_Rebuild_Row_Is_Never_Swallowed_By_A_House_Wide_Run()
	{
		IReadOnlyList<ActivityEntry> entries =
		[
			Entry(3, Mode("Kjøkken", "Home", Noon.AddSeconds(2))),
			Entry(2, EngineNoticeKind.Started, Noon.AddSeconds(1)),
			Entry(1, Mode("Stue", "Home", Noon))
		];

		IReadOnlyList<ActivityRow> rows = ActivityView.Rows(entries);

		Assert.AreEqual(3, rows.Count,
			"the two mode reports are not consecutive any more, and the rebuild joins neither");
		Assert.AreEqual("Adaptive lighting started", rows[1].Line.What);
	}

	private static AreaSnapshot Mode(string area, string value, DateTimeOffset at) =>
		Report(area, AreaState.AutoVacant, TransitionReason.HouseModeChanged, at: at, houseModeValue: value);

	/// <summary>One quiet re-check that reached a darkness verdict, the report a house publishes once a minute.</summary>
	private static AreaSnapshot Dusk(string area, bool dark, string detail, DateTimeOffset at) =>
		Report(area, AreaState.AutoVacant, TransitionReason.CircadianTick, at: at, isDark: dark, darknessDetail: detail);

	/// <summary>Every report the engine can publish and the page would draw, one entry each.</summary>
	/// <remarks>The six inputs the categoriser reads, walked, then sifted as the page sifts them.</remarks>
	private static IReadOnlyList<ActivityEntry> EveryShapeOfReport()
	{
		bool?[] verdicts = [null, true, false];
		AutoOnBlock?[] blocks = [null, .. Enum.GetValues<AutoOnBlock>().Cast<AutoOnBlock?>()];
		List<ActivityEntry> entries = [];
		long sequence = 0;

		foreach (TransitionReason reason in Enum.GetValues<TransitionReason>())
		{
			foreach (AreaState state in Enum.GetValues<AreaState>())
			{
				foreach (bool? dark in verdicts)
				{
					foreach (bool killSwitch in new[] { false, true })
					{
						foreach (AutoOnBlock? block in blocks)
						{
							entries.Add(Entry(
								++sequence,
								Report(
									"Stue", state, reason, isDark: dark, darknessDetail: "lux 12, dark below 40",
									killSwitch: killSwitch, autoOnBlockedBy: block)));
						}
					}
				}
			}
		}

		return ActivityView.Shown(entries);
	}

	private static bool Has(ActivityCategory category, AreaSnapshot snapshot) =>
		(ActivityView.Categorise(snapshot) & category) != ActivityCategory.None;

	private static ActivityFilterChip Chip(IReadOnlyList<ActivityFilterChip> chips, ActivityCategory category) =>
		chips.Single(chip => chip.Category == category);
}
