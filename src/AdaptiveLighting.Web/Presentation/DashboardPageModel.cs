using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Hosting;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>
///     Everything the dashboard holds, decides and says: the house bar, the timeline, the summary log, its
///     clock and the two things a press changes.
/// </summary>
/// <remarks>
///     A plain class with no component base, so a second design puts its own markup over the same rules. It
///     names meanings and never class names; turning a reading into a colour or a class is the design's half.
///     This page never writes the document.
/// </remarks>
public sealed class DashboardPageModel : IPageClock, IDisposable
{
	/// <summary>How long the house-mode acknowledgement stays on screen.</summary>
	private static readonly TimeSpan ModeConfirmationLingers = TimeSpan.FromSeconds(6);

	private readonly AreaSnapshotCache _cache;
	private readonly ActivityLog _log;
	private readonly LightingEngineHost _engine;
	private readonly DocumentCache _documents;
	private readonly ModeService _service;
	private readonly HaCatalog _catalog;
	private readonly ILogger _logger;
	private readonly Func<Action, Task> _dispatch;

	private IReadOnlyList<AreaSnapshot> _snapshots = [];
	private IReadOnlyList<RoomView> _rooms = [];
	private IReadOnlyList<RoomTile> _roomTiles = [];
	private IReadOnlyList<ActivityEntry> _entries = [];

	// _kept is what the summary categories leave; _reachable is what the Activity page would draw on the same
	// buffer, so the footer's count is one that link can keep.
	private int _kept;
	private int _reachable;

	private AdaptiveLightingConfig? _schedule;
	private TransientMessage _modeConfirmation = TransientMessage.None;

	private IDisposable? _subscription;
	private IDisposable? _ticker;

	/// <summary>Builds the model over the services the dashboard needs.</summary>
	/// <param name="dispatch">
	///     Runs work on whatever thread the interface renders on. The subscription and the clock fire off a
	///     timer, so state must not move under a render in progress. A test passes one that runs the work inline.
	/// </param>
	public DashboardPageModel(
		AreaSnapshotCache cache,
		ActivityLog log,
		LightingEngineHost engine,
		DocumentCache documents,
		ModeService service,
		HaCatalog catalog,
		ILogger logger,
		Func<Action, Task> dispatch)
	{
		_cache = cache ?? throw new ArgumentNullException(nameof(cache));
		_log = log ?? throw new ArgumentNullException(nameof(log));
		_engine = engine ?? throw new ArgumentNullException(nameof(engine));
		_documents = documents ?? throw new ArgumentNullException(nameof(documents));
		_service = service ?? throw new ArgumentNullException(nameof(service));
		_catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
	}

	/// <summary>Raised when a value the house bar shows has moved.</summary>
	/// <remarks>Not raised by the clock alone: the timeline redraws itself on the beat, and the commissioning
	/// board holds a draft that a redraw for nothing would disturb.</remarks>
	public event Action? Changed;

	/// <inheritdoc/>
	public event Action? Tick;

	/// <inheritdoc/>
	public DateTimeOffset Now { get; private set; } = DateTimeOffset.Now;

	/// <summary>Reads the house, builds the board, and starts the report subscription and the clock.</summary>
	public void Start()
	{
		_snapshots = _cache.Snapshots;
		ReadHouseState();
		ReadBoard();

		// Sampled, not raw: a house waking up pushes a burst of transitions.
		_subscription = _cache.Changes
			.Sample(TimeSpan.FromMilliseconds(400))
			.SubscribeSafe(report => OnReport(), _logger);

		// The now-line and every countdown are only honest while they move, so the board is rebuilt once a second
		// whether or not the engine said anything. Everything this beat does must stay cheap; see ReadSchedule.
		_ticker = Observable.Interval(TimeSpan.FromSeconds(1))
			.SubscribeSafe(beat => Beat(), _logger);
	}

	private void OnReport() => _ = _dispatch(() =>
	{
		_snapshots = _cache.Snapshots;
		ReadHouseState();
		ReadBoard();
		Changed?.Invoke();
	});

	private void Beat() => _ = _dispatch(() =>
	{
		Now = DateTimeOffset.Now;

		HouseBarState before = HouseBar;

		ReadHouseState();
		ReadBoard();

		if (HouseBar != before)
			Changed?.Invoke();

		Tick?.Invoke();
	});

	// ---- the house bar ---------------------------------------------------------------------------------

	/// <summary>What Home Assistant says the house mode is, or <c>null</c> before it has said.</summary>
	public HouseModeView? HouseMode { get; private set; }

	/// <summary>The master switch as the document resolves it.</summary>
	public MasterSwitchView? MasterSwitch { get; private set; }

	/// <summary>Who the house counts as home.</summary>
	public IReadOnlyList<PersonView> People { get; private set; } = [];

	/// <summary>Why a mode could not be switched, which stands until the next press.</summary>
	public string? ModeMessage { get; private set; }

	/// <summary>The acknowledgement of a mode press, and the moment it stops being made.</summary>
	/// <remarks>Read against <see cref="Now"/> where it is drawn: the panel must not carry a stale one once the
	/// mode has landed.</remarks>
	public TransientMessage ModeConfirmation => _modeConfirmation;

	/// <summary>The line under the master switch, or <c>null</c> when the switch is simply on.</summary>
	public string? MasterNote
	{
		get
		{
			if (MasterSwitch is not { } master)
				return null;

			if (!master.IsAvailable)
				return master.IsReady ? "Switch not found" : "Waiting for Home Assistant";

			return master.AdaptiveLightingOn ? null : "Paused";
		}
	}

	/// <summary>What the (i) beside <see cref="MasterNote"/> explains.</summary>
	public string? MasterNoteMore
	{
		get
		{
			if (MasterSwitch is not { } master)
				return null;

			if (!master.IsAvailable)
			{
				return master.IsReady
					? "Home Assistant doesn't know the master switch, so its state can't be shown."
					: "Home Assistant hasn't answered yet, so the switch's state is unknown.";
			}

			return master.AdaptiveLightingOn
				? null
				: "Nothing was turned off, but no lights will change until it is turned back on.";
		}
	}

	/// <summary>How much <see cref="MasterNote"/> matters: a paused house needs attention.</summary>
	public InfoSeverity MasterNoteSeverity =>
		MasterSwitch is { IsAvailable: true, AdaptiveLightingOn: false } ? InfoSeverity.Bad : InfoSeverity.Neutral;

	/// <summary>What the master switch says on hover.</summary>
	public string MasterTitle
	{
		get
		{
			if (MasterSwitch is not { } master)
				return string.Empty;

			if (!master.IsAvailable)
			{
				return master.IsReady
					? "Home Assistant doesn't know this switch."
					: "Waiting for Home Assistant.";
			}

			return master.Toggle.CanToggle
				? $"Turn adaptive lighting {(master.AdaptiveLightingOn ? "off" : "on")}"
				: "This entity can't be switched on or off.";
		}
	}

	/// <summary>Flips the master switch through the config-resolved toggle, then re-reads the house state.</summary>
	public void ToggleMaster()
	{
		if (MasterSwitch is { IsAvailable: true, Toggle.CanToggle: true } master)
			_service.Toggle(master.Toggle);

		ReadHouseState();
	}

	/// <summary>Switches the house mode, then re-reads.</summary>
	/// <remarks>The service call is fire and forget, so what is set here is a requested acknowledgement; the
	/// current-mode highlight still comes from what Home Assistant reports on the next read.</remarks>
	public void SelectMode(string option)
	{
		Now = DateTimeOffset.Now;

		if (_service.SelectHouseMode(option))
		{
			ModeMessage = null;
			_modeConfirmation = TransientMessage.For($"Switching to {option}…", Now, ModeConfirmationLingers);
		}
		else
		{
			_modeConfirmation = TransientMessage.None;
			ModeMessage = $"Couldn't switch to '{option}' — Home Assistant offline?";
		}

		ReadHouseState();
	}

	// ---- what the page is looking at -------------------------------------------------------------------

	/// <summary>Whether any room reports the master switch as holding everything.</summary>
	public bool KillSwitchActive => _snapshots.Any(area => area.KillSwitchActive);

	/// <summary>Whether the engine is running.</summary>
	public bool EngineIsRunning => _engine.IsRunning;

	/// <summary>Whether the engine is attached to Home Assistant.</summary>
	public bool EngineIsAttached => _engine.IsAttached;

	/// <summary>Why the engine stopped, when it has.</summary>
	public string? EngineFault => _engine.Fault;

	/// <summary>Whether any room has reported at all.</summary>
	public bool HasData => _cache.HasData;

	/// <summary>Whether the house has rooms but none has been switched on yet.</summary>
	public bool AwaitingRoomChoice => AreaView.IsAwaitingRoomChoice(_rooms, _engine.IsAttached);

	/// <summary>Every report on screen, newest state per room.</summary>
	public IReadOnlyList<ActivityEntry> Entries => _entries;

	/// <summary>The summary log's rows.</summary>
	public IReadOnlyList<ActivityRow> LogRows { get; private set; } = [];

	// ---- the timeline ----------------------------------------------------------------------------------

	/// <summary>One lane per switched-on room.</summary>
	public IReadOnlyList<BoardLane> Lanes { get; private set; } = [];

	/// <summary>The lanes grouped by floor, or one unnamed group where the registry cannot say.</summary>
	public IReadOnlyList<FloorGroup<BoardLane>> Groups { get; private set; } = [];

	/// <summary>The lanes that have done nothing inside the window.</summary>
	public IReadOnlyCollection<string> Quiet => _quiet;

	/// <summary>The rooms worth a chip above the board.</summary>
	public IReadOnlyList<AreaSnapshot> Exceptions { get; private set; } = [];

	/// <summary>The schedule band above the lanes.</summary>
	public IReadOnlyList<BandSegment> Band { get; private set; } = [];

	/// <summary>The window the board covers.</summary>
	public BoardWindow Window { get; private set; } =
		BoardWindow.Around(DateTimeOffset.Now, BoardView.LookBack, BoardView.LookAhead);

	/// <summary>The hour marks across the axis.</summary>
	public IReadOnlyList<DateTimeOffset> Ticks { get; private set; } = [];

	/// <summary>How far across the window the present moment is, from 0 to 100.</summary>
	public double NowPercent { get; private set; }

	/// <summary>Whether any lane carries a mark, which is what the legend explains.</summary>
	public bool HasMarks { get; private set; }

	private HashSet<string> _quiet = new(StringComparer.Ordinal);

	/// <summary>Whether a lane is resting rather than drawn on the board.</summary>
	public bool IsQuiet(BoardLane lane) => _quiet.Contains(lane.Key);

	/// <summary>Whether a floor's heading is drawn at all.</summary>
	public bool ShowsFloorHeader(FloorGroup<BoardLane> group) => AreaView.ShowsHeader(Groups.Count, group.Floor);

	/// <summary>What a floor is called.</summary>
	public static string FloorTitle(AreaFloor? floor) => AreaView.FloorTitle(floor);

	/// <summary>Whether a lane's room has lights that stopped answering or a motion sensor with a low battery.</summary>
	public static bool HasWarning(AreaSnapshot snapshot) =>
		RoomFacts.NotResponding(snapshot) is { Length: > 0 } || snapshot.LowBatteries is { Count: > 0 };

	/// <summary>Whether a band segment is named on screen.</summary>
	public static bool IsLabelled(BandSegment segment) => BoardView.IsLabelled(segment);

	// ---- the sentences ---------------------------------------------------------------------------------

	/// <summary>What a tray chip says about one room.</summary>
	public string ExceptionLine(AreaSnapshot area) => BoardView.ExceptionLine(area, Now);

	/// <summary>How many rooms are quiet, said in words.</summary>
	public string QuietRoomsLine => BoardView.QuietRoomsLine(Lanes.Count, Exceptions.Count);

	/// <summary>The window's start, as a clock reading.</summary>
	public string WindowStartClock => BoardView.Clock(Window.Start);

	/// <summary>The window's end, as a clock reading.</summary>
	public string WindowEndClock => BoardView.Clock(Window.End);

	/// <summary>The present moment, as a clock reading.</summary>
	public string NowClock => BoardView.Clock(Now);

	/// <summary>How far back the board can see; a restart wipes the morning.</summary>
	public string SinceLine =>
		_engine.LastStartedUtc is { } started
			? $"since {BoardView.Clock(started)}"
			: "since start";

	/// <summary>What the log below is showing, what the filter is holding back, and the cap it is held to.</summary>
	public string LogFoot => BoardView.LogFoot(_entries.Count, _reachable, _kept, LogRows.Count, ActivityLog.Capacity);

	/// <summary>The count in front of the nothing-important empty state.</summary>
	public string BackgroundOnlyLine => _reachable == 1
		? "The one report so far is routine. Movement, re-checks and everyday light changes are on the Activity page."
		: $"All {_reachable} reports so far are routine. Movement, re-checks and everyday light changes are on the Activity page.";

	/// <summary>What a lane says on hover: the room, its state, its lights, any that dropped out, and low batteries.</summary>
	public string LaneTitle(BoardLane lane)
	{
		string word = StateGlyph.For(lane.Latest).Word;
		string title = LightReadout.Line(lane.Latest, _catalog.FriendlyNameOrId) is { } lights
			? $"{lane.Name} — {word} — {lights}"
			: $"{lane.Name} — {word}";

		if (RoomFacts.NotResponding(lane.Latest) is { Length: > 0 } dropped)
			title = $"{title} — {dropped}";

		foreach (string battery in RoomFacts.LowBatteries(lane.Latest, _catalog.FriendlyNameOrId))
			title = $"{title} — {battery}";

		return title;
	}

	/// <summary>What the page says about rooms that are switched off, when it says anything.</summary>
	public HiddenRoomsNote? HiddenRooms => AreaView.HiddenNote(AreaView.SwitchedOffCount(_rooms));

	/// <summary>Every enabled room, with what its lamps are doing now — a room that has not reported is dark,
	/// not missing.</summary>
	public IReadOnlyList<RoomTile> Rooms => _roomTiles;

	// ---- reading ---------------------------------------------------------------------------------------

	/// <summary>Re-reads the page the moment the commissioning board's save lands, ahead of the next beat.</summary>
	public void OnCommissioned()
	{
		ReadHouseState();
		ReadBoard();
	}

	/// <summary>Re-reads the three house-state projections, house mode first: its readiness bookkeeping is
	/// clobbered by the master switch's if the two are swapped.</summary>
	private void ReadHouseState()
	{
		HouseMode = _service.GetHouseMode();
		MasterSwitch = _service.GetMasterSwitch();
		People = _service.GetPeople();
	}

	/// <summary>Everything the house bar draws, so it is redrawn only when one of them has moved.</summary>
	private HouseBarState HouseBar =>
		new(HouseMode, MasterSwitch, People.Count, ModeMessage, _modeConfirmation.TextAt(Now));

	private readonly record struct HouseBarState(
		HouseModeView? HouseMode,
		MasterSwitchView? MasterSwitch,
		int PeopleCount,
		string? ModeMessage,
		string? ModeConfirmation);

	/// <summary>Rebuilds the whole board: the window, one lane per switched-on room, the quiet split, the tray
	/// and the schedule band.</summary>
	/// <remarks>Rebuilt on every beat and never cached, since the window steps on the hour and every countdown
	/// goes wrong the moment it stops being recalculated. This page keeps no read mark on the log, so
	/// <c>Entries</c> alone is safe; anything that wants to say "4 new" must switch to <c>ActivityLog.Read()</c>,
	/// which returns the entries and the sequence number under one lock.</remarks>
	private void ReadBoard()
	{
		_rooms = _service.GetRooms();
		_roomTiles =
		[
			.. _rooms.Where(room => room.IsEnabled)
				.Select(room => new RoomTile(room.Name, room.AreaId, RoomLamp.Of(_cache.Find(room.AreaId, room.Name))))
		];
		Window = BoardWindow.Around(Now, BoardView.LookBack, BoardView.LookAhead);
		Ticks = Window.Ticks;
		NowPercent = Math.Clamp(Window.PercentAt(Now), 0, 100);
		_entries = _log.Entries;

		// This sift belongs to the summary alone: the lanes are drawn from every report the engine published,
		// start-up included, because that snapshot is where a lane's first block begins.
		IReadOnlyList<ActivityEntry> shown = ActivityView.Shown(_entries);
		IReadOnlyList<ActivityEntry> kept = ActivityView.InCategories(shown, ActivityView.SummaryCategories);

		// DefaultCategories, because that is what the Activity page opens on. Counting against the raw buffer would
		// promise reports that page also hides.
		_reachable = ActivityView.InCategories(shown, ActivityView.DefaultCategories).Count;
		_kept = kept.Count;
		LogRows = ActivityView.Rows(kept, BoardView.LogPreview);

		IReadOnlyList<AreaSnapshot> visible = AreaView.VisibleCards(_snapshots, _rooms);

		// The engine's own notices are skipped: a lane draws one room's history, and a rebuild belongs to none.
		Dictionary<string, List<ActivityEntry>> byRoom = new(StringComparer.Ordinal);
		foreach (ActivityEntry entry in _entries)
		{
			if (entry.Snapshot is not { } snapshot)
				continue;

			if (!byRoom.TryGetValue(AreaSnapshotCache.KeyOf(snapshot), out List<ActivityEntry>? room))
				byRoom[AreaSnapshotCache.KeyOf(snapshot)] = room = [];

			room.Add(entry);
		}

		List<BoardLane> lanes = new(visible.Count);
		foreach (AreaSnapshot snapshot in visible)
		{
			string key = AreaSnapshotCache.KeyOf(snapshot);
			IReadOnlyList<ActivityEntry> history = byRoom.TryGetValue(key, out List<ActivityEntry>? found) ? found : [];

			lanes.Add(new BoardLane(
				key,
				snapshot.AreaName,
				snapshot.AreaId,
				snapshot,
				BoardView.Blocks(history, Window, Now),
				BoardView.NextMark(snapshot, Window, Now),
				BoardView.Refusals(history, Window)));
		}

		Lanes = lanes;
		// Refusals count here: they are the only mark a lane can carry with no block and nothing armed, so leaving
		// them out hides the legend on the very board the refusal tick was drawn for.
		HasMarks = lanes.Any(lane => lane.Blocks.Count > 0 || lane.Next is not null || lane.Refusals is { Count: > 0 });
		Exceptions = BoardView.Exceptions(visible);

		(IReadOnlyList<BoardLane> _, IReadOnlyList<BoardLane> quiet) = BoardView.Partition(lanes);
		_quiet = new HashSet<string>(quiet.Select(lane => lane.Key), StringComparer.Ordinal);

		// A registry that has not connected throws instead of answering, so GroupOrFlat degrades to one unnamed
		// group and the board renders flat instead of not at all.
		Groups = FloorGrouping.GroupOrFlat(lanes, lane => lane.AreaId, _catalog.AreaRegistry);

		ReadSchedule();
	}

	/// <summary>Re-reads the circadian table for the band above the lanes.</summary>
	/// <remarks>A parse failure keeps the last good table instead of blanking the band mid-save.</remarks>
	private void ReadSchedule()
	{
		if (_documents.Read() is { } document)
			_schedule = document;

		if (_schedule is not { } schedule)
		{
			Band = [];

			return;
		}

		SunTimes sun;
		try
		{
			(TimeOnly? sunrise, TimeOnly? sunset) = _catalog.SunTimesToday();
			sun = new SunTimes(sunrise, sunset);
		}
		catch (InvalidOperationException)
		{
			sun = SunTimes.Unknown;
		}

		// Read only where it is allowed to decide: under the default authority the value is discarded anyway, and
		// an unguarded read on the one-second beat is a Home Assistant lookup per second per open board.
		string? selectValue = Schedule.HomeAssistantDecides(schedule.Global)
			? _catalog.CurrentStateOf(schedule.Global.PeriodSelect?.EntityId)
			: null;

		// Through Schedule, so the band draws the table the engine is running: the dropdown's period where Home
		// Assistant owns the time of day, and the engine's latch where a period waits for movement.
		Band = BoardView.Band(
			Schedule.CalculatorFor(
				schedule.Periods, schedule.Global, sun, selectValue, Schedule.PeriodHoldRule(_engine)),
			Window);
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		_subscription?.Dispose();
		_ticker?.Dispose();
	}
}
