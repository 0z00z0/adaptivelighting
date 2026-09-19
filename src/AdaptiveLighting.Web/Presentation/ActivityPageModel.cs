using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>
///     Everything the activity page holds and decides: the buffered reports, the room and category filters, the
///     rows they leave, and the sentences under them.
/// </summary>
/// <remarks>
///     A plain class with no component base, so a second design puts its own markup over the same rules. It names
///     meanings, never class names: a chip's <see cref="ActivityFilterChip.IsOn"/> and
///     <see cref="ActivityFilterChip.Count"/> are what a chip means, and turning that into a CSS class is the
///     page's own <c>ChipClass</c>. This page never writes the document.
/// </remarks>
public sealed class ActivityPageModel : IDisposable
{
	private readonly ActivityLog _log;
	private readonly AreaSnapshotCache _cache;
	private readonly ILogger _logger;
	private readonly Func<Action, Task> _dispatch;

	private IReadOnlyList<ActivityEntry> _entries = [];
	private IReadOnlyList<ActivityEntry> _inRoom = [];
	private IReadOnlyList<ActivityEntry> _visible = [];

	// What the timeline draws. Kept beside _visible, not replacing it: the counts and the hidden note are about
	// reports, only the rendering is about rows.
	private IReadOnlyList<ActivityRow> _rows = [];

	private IReadOnlyList<ActivityFilterChip> _chips = [];

	/// <summary>The chosen room, held as its area id so a rename in Home Assistant does not clear the filter.</summary>
	private string _room = ActivityView.AllRooms;

	private ActivityCategory _categories = ActivityView.DefaultCategories;

	// What the buffer held at the last read, before non-events were sifted out. Only the footer reads it, to say
	// whether the buffer has started dropping its oldest.
	private int _recorded;

	private long _shownThrough;
	private IDisposable? _subscription;

	/// <summary>Builds the model over the services the activity page needs.</summary>
	/// <param name="dispatch">
	///     Runs work on whatever thread the interface renders on. The redraw subscription fires off a report
	///     landing on a Home Assistant event-loop thread, so state must not move under a render in progress. A
	///     test passes one that runs the work inline.
	/// </param>
	public ActivityPageModel(ActivityLog log, AreaSnapshotCache cache, ILogger logger, Func<Action, Task> dispatch)
	{
		_log = log ?? throw new ArgumentNullException(nameof(log));
		_cache = cache ?? throw new ArgumentNullException(nameof(cache));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
	}

	/// <summary>Raised when the buffer has moved and the page should redraw.</summary>
	/// <remarks>Only moves the "new reports" count; the render it causes must not re-read the log itself.</remarks>
	public event Action? Changed;

	/// <summary>Reads what the log is holding and starts the redraw subscription.</summary>
	public void Start()
	{
		ShowNew();

		// Sampled, and it must not re-read the log: the render it causes only moves the "new reports" count.
		_subscription = _cache.Changes
			.Sample(TimeSpan.FromSeconds(1))
			.SubscribeSafe(_ => OnReport(), _logger);
	}

	private void OnReport() => _ = _dispatch(() => Changed?.Invoke());

	/// <summary>Every report worth a row, after <see cref="ActivityView.Shown"/>, before either filter.</summary>
	public IReadOnlyList<ActivityEntry> Entries => _entries;

	/// <summary>What the timeline draws: the rows left once the room and category filters are both applied.</summary>
	public IReadOnlyList<ActivityRow> Rows => _rows;

	/// <summary>The category chips. Each chip's count is within the chosen room, not the house.</summary>
	public IReadOnlyList<ActivityFilterChip> Chips => _chips;

	/// <summary>The rooms that have reported, for the room filter's options.</summary>
	public IReadOnlyList<ActivityRoomOption> RoomOptions => ActivityView.RoomOptions(_entries);

	/// <summary>The chosen room, or <see cref="ActivityView.AllRooms"/> for every room.</summary>
	public string Room => _room;

	/// <summary>How many reports have arrived since the timeline was last drawn.</summary>
	/// <remarks>Sequence numbers, not a count of entries, so eviction from the buffer cannot skew it.</remarks>
	public long Pending => _log.Newest - _shownThrough;

	public string PendingWord => Pending == 1 ? "1 new report — show it" : $"{Pending} new reports — show them";

	/// <summary>What to call the chosen room in a sentence: the name it reported most recently.</summary>
	public string RoomName =>
		ActivityView.RoomOptions(_entries).FirstOrDefault(option => string.Equals(option.Key, _room, StringComparison.OrdinalIgnoreCase))?.Name
		?? _room;

	/// <summary>What the filters are keeping off the page, or <c>null</c> when they keep nothing off it.</summary>
	public string? HiddenNote => ActivityView.HiddenNote(_entries.Count, _visible.Count, _room, _categories);

	/// <summary>What the page is holding, and the cap it is held to.</summary>
	public string FootLine
	{
		get
		{
			string held = _entries.Count == 1 ? "1 report" : $"{_entries.Count} reports";
			string shown = _visible.Count == _entries.Count ? held : $"{_visible.Count} of {held}";

			return _recorded < ActivityLog.Capacity
				? $"Showing {shown} since adaptive lighting started."
				: $"Showing {shown}. Only the most recent {ActivityLog.Capacity} are kept; older ones have been dropped.";
		}
	}

	/// <summary>Draws everything the log is holding and moves the read mark to the newest report.</summary>
	/// <remarks>One <see cref="ActivityLog.Read"/>, never the two properties in turn: a report landing between
	/// two separately locked reads would be marked shown while absent from the list that was shown.</remarks>
	public void ShowNew()
	{
		ActivityTimeline timeline = _log.Read();

		_recorded = timeline.Entries.Count;
		_entries = ActivityView.Shown(timeline.Entries);
		_shownThrough = timeline.Newest;

		ApplyFilter();
	}

	/// <summary>Chooses the room the filter narrows to, then re-applies both filters.</summary>
	/// <remarks><c>null</c> is what the bound select reports for its own "All rooms" option's round trip.</remarks>
	public void SelectRoom(string? room)
	{
		_room = room ?? ActivityView.AllRooms;
		ApplyFilter();
	}

	/// <summary>Switches one category on or off, then re-applies both filters.</summary>
	public void Toggle(ActivityCategory category)
	{
		_categories ^= category;
		ApplyFilter();
	}

	/// <summary>Every category on and every room shown, without re-reading the log.</summary>
	public void ShowEverything()
	{
		_room = ActivityView.AllRooms;
		_categories = ActivityView.AllCategories;
		ApplyFilter();
	}

	/// <summary>Both filters, applied to what is already drawn, never re-reading the log.</summary>
	/// <remarks>Room narrows first; the chips then count within that room.</remarks>
	private void ApplyFilter()
	{
		_inRoom = ActivityView.InRoomByKey(_entries, _room);
		_visible = ActivityView.InCategories(_inRoom, _categories);
		_rows = ActivityView.Rows(_visible);
		_chips = ActivityView.Chips(_inRoom, _categories);
	}

	/// <inheritdoc/>
	public void Dispose() => _subscription?.Dispose();
}
