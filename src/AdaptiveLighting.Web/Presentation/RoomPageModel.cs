using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Hosting;
using AdaptiveLighting.Web.Components;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>What the save line is saying, as a meaning rather than a class name.</summary>
public enum RoomSaveState
{
	/// <summary>Nothing to say.</summary>
	Settled,

	/// <summary>An edit is waiting for the quiet window to run out.</summary>
	Waiting,

	/// <summary>The last write landed, and the confirmation is still up.</summary>
	Saved,

	/// <summary>The last write was refused and the file is not what is on screen.</summary>
	Refused
}

/// <summary>The header lamp's reading: whether a hand set it, the warmth it was commanded to, and how far the
/// glow spreads.</summary>
/// <param name="HandHeld">A hand set the levels, or no warmth was commanded. Either way there is no warmth to
/// paint the lamp with.</param>
/// <param name="Kelvin">The commanded warmth, or <c>null</c> when there is none.</param>
/// <param name="Spread">How far the glow reaches, in pixels, read off the brightness.</param>
public readonly record struct RoomGlow(bool HandHeld, int? Kelvin, int Spread);

/// <summary>
///     Everything one room page holds, decides and writes: its document, its live report, its clock, its
///     actions and every sentence and count it shows.
/// </summary>
/// <remarks>
///     A plain class with no component base, so a second design can put its own markup over the same rules. It
///     names meanings and never class names or custom properties; turning a meaning into a colour or a class is
///     the design's half.
/// </remarks>
public sealed class RoomPageModel : IPageClock, IDisposable
{
	/// <summary>How long the page waits for the hand to settle before writing.</summary>
	/// <remarks>A save rebuilds every area controller in the house, so a write per press would restart every
	/// room's vacancy timer while a stepper is held down.</remarks>
	private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(1200);

	/// <summary>How long the write confirmation stays on the save line.</summary>
	private static readonly TimeSpan ConfirmationLingers = TimeSpan.FromSeconds(3.5);

	/// <summary>How many of this room's log entries the card holds; the Activity page has the rest.</summary>
	private const int LogRows = 6;

	/// <summary>What the pickers call the second block of each list.</summary>
	public const string OthersLabel = "Everything else in this room";

	// No default for this: only the page knows whether a pick waits behind a save bar or applies itself.
	/// <summary>What the sentence tokens say about when a pick reaches the file.</summary>
	public const string TokenNote = "Applied about a second after you pick.";

	/// <summary>What the interface asks before leaving with an edit that could not be written.</summary>
	public const string LeaveQuestion = "Leave this room? The change that could not be saved will be lost.";

	private readonly LightingEngineHost _engine;
	private readonly HaCatalog _catalog;
	private readonly AreaSnapshotCache _cache;
	private readonly ActivityLog _log;
	private readonly ILogger _logger;
	private readonly Func<Action, Task> _dispatch;

	private string _areaId = "";
	private string? _loadedFor;

	// Fields, not getters: each of these parses the schedule or reads Home Assistant, and the page renders once a
	// second and again on every frame of a chart drag.
	private TimePeriodConfig? _activePeriod;
	private double? _daylightLux;

	// The one slot of the document this page may write, and that slot as it stood on disk when it was read.
	private RoomWriteToken _write = new(null, "");

	// Whether the pending write is the room's removal. The model still holds the object, so the intent has to be
	// carried separately.
	private bool _removed;

	private TransientMessage _saved = TransientMessage.None;
	private CancellationTokenSource? _saveCts;

	// Per-visit, never persisted: the note is advice about this house right now.
	private bool _switchOnDismissed;

	// The level test running on the real lights, from this visit's own click. The engine owns the return, so
	// these only draw it, and only until the snapshot carries the same news (see TestingPeriod).
	private string? _testingPeriodId;
	private string? _testingLightId;
	private DateTimeOffset? _testEndsAt;

	private readonly HashSet<string> _openedGroups = new(StringComparer.Ordinal);

	// Everything Home Assistant has, read once per document load. The pickers' fallback, not what they show.
	private IReadOnlyList<EntityOption> _lights = [];
	private IReadOnlyList<EntityOption> _motionSensors = [];
	private IReadOnlyList<EntityOption> _sunEntities = [];

	private EntityLookup? _lookup;
	private Func<string, string>? _nameOf;
	private Func<string, string>? _areaNameOf;

	private IDisposable? _subscription;
	private IDisposable? _ticker;

	/// <summary>Builds the model over the services one room page needs.</summary>
	/// <param name="dispatch">
	///     Runs work on whatever thread the interface renders on. The subscription and the ticker fire off a
	///     timer, so state must not move under a render in progress. A test passes one that runs the work inline.
	/// </param>
	public RoomPageModel(
		LightingEngineHost engine,
		HaCatalog catalog,
		AreaSnapshotCache cache,
		ActivityLog log,
		ILogger logger,
		Func<Action, Task> dispatch)
	{
		_engine = engine ?? throw new ArgumentNullException(nameof(engine));
		_catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
		_cache = cache ?? throw new ArgumentNullException(nameof(cache));
		_log = log ?? throw new ArgumentNullException(nameof(log));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
	}

	/// <summary>Raised when a value the still parts of the page show has moved: a report, a delayed write, a
	/// refusal the engine changed its mind about.</summary>
	/// <remarks>Not raised by the clock alone. A page that redrew its editors every second would hand a changed
	/// parameter to every slider and picker for nothing.</remarks>
	public event Action? Changed;

	/// <inheritdoc/>
	public event Action? Tick;

	/// <summary>Raised with the address the page should move to. The interface performs the navigation.</summary>
	public event Action<string>? Navigated;

	/// <summary>Starts the report subscription and the one-second clock.</summary>
	public void Start()
	{
		// Sampled: a house waking up pushes a burst of transitions, and the page redraws a lot of markup.
		_subscription = _cache.Changes
			.Sample(TimeSpan.FromMilliseconds(400))
			.SubscribeSafe(_ => OnRenderThread(() => ReadLiveState()), _logger);

		// Once a second, because the relative times and the countdown ring are only honest while they move. The
		// beat alone redraws only what listens to it; the rest of the page waits for a value to actually move.
		_ticker = Observable.Interval(TimeSpan.FromSeconds(1))
			.SubscribeSafe(beat => Beat(), _logger);
	}

	private void Beat() => _ = _dispatch(() =>
	{
		Now = DateTimeOffset.Now;

		if (ReadLiveState())
			Changed?.Invoke();

		Tick?.Invoke();
	});

	/// <summary>Points the model at a room, reloading only when the route names a different one.</summary>
	/// <remarks>Blazor re-runs its parameter step for reasons unrelated to the URL, and re-reading the document
	/// on each would drop an edit the save timer has not written.</remarks>
	public void Show(string areaId)
	{
		_areaId = areaId ?? "";

		if (string.Equals(_loadedFor, _areaId, StringComparison.Ordinal))
			return;

		Load();
	}

	// ---- what the page is looking at -------------------------------------------------------------------

	/// <summary>The whole document, for the controls that still draw from it.</summary>
	public AdaptiveLightingConfig Document { get; private set; } = new();

	/// <summary>This room's slot in the document, or <c>null</c> when the route names no room it holds.</summary>
	public AreaConfig? Area { get; private set; }

	/// <summary>Why the document could not be read, when it could not be.</summary>
	public string? LoadError { get; private set; }

	/// <summary>The schedule, which the levels table draws.</summary>
	public List<TimePeriodConfig> Periods => Document.Periods;

	/// <summary>The house default settings this room inherits from.</summary>
	public AreaSettings Defaults => Document.Defaults;

	/// <summary>What discovery resolves in this room right now.</summary>
	public AreaPreview Preview { get; private set; } = new(null, null);

	/// <summary>The lights and sensors this room resolves to, or <c>null</c> when discovery could not answer.</summary>
	public ResolvedArea? Resolved => Preview.Resolved;

	/// <summary>Why discovery could not answer.</summary>
	public string? PreviewError => Preview.Error;

	/// <summary>This room's newest report from the engine.</summary>
	public AreaSnapshot? Snapshot { get; private set; }

	/// <summary>Whether a report has arrived at all.</summary>
	public bool HasReport => Snapshot is not null;

	/// <summary>The page's own clock, moved once a second so every relative time on one render agrees.</summary>
	public DateTimeOffset Now { get; private set; } = DateTimeOffset.Now;

	/// <summary>The validator's verdict on the document as it stands.</summary>
	public ValidationResult? Validation { get; private set; }

	/// <summary>The last refused write, which stands until it is resolved.</summary>
	public SaveResult? Failure { get; private set; }

	/// <summary>Whether Home Assistant came back with anything.</summary>
	public bool HomeAssistantIsAnswering { get; private set; }

	/// <summary>Every area Home Assistant knows, for the area picker.</summary>
	public IReadOnlyList<AreaOption> Areas { get; private set; } = [];

	/// <summary>What this room is called, everywhere on this page.</summary>
	/// <remarks>Through <see cref="AreaNaming"/> and never off the document, so a room stating no name of its
	/// own is called what Home Assistant calls its area. That is also what the engine puts in every snapshot,
	/// which is what makes the two name-based joins find the same room.</remarks>
	public string RoomName => Area is { } room ? AreaNaming.DisplayName(room, _catalog.AreaRegistry) : _areaId;

	/// <summary>What the browser tab is called.</summary>
	public string PageName => Area is null ? _areaId : RoomName;

	/// <summary>The route's area id, for the page that has to say which room was not found.</summary>
	public string RequestedAreaId => _areaId;

	/// <summary>Whether this app runs the lighting in this room at all.</summary>
	public bool IsEnabled => Area is { } room && AreaView.IsEnabled(room, Defaults);

	/// <summary>The room's settings with its inheritance resolved.</summary>
	public AreaSettings Effective => Area is { } room ? room.Effective(Defaults) : Defaults;

	/// <summary>A friendly name for an entity id, with the id as the fallback.</summary>
	// Built once: a fresh delegate each render would hand every control a changed parameter on every tick.
	public Func<string, string> NameOf => _nameOf ??= entityId => _catalog.FriendlyNameOrId(entityId);

	/// <summary>An area's name from its id.</summary>
	public Func<string, string> AreaNameOf => _areaNameOf ??= areaId =>
		Areas.FirstOrDefault(area => string.Equals(area.Id, areaId, StringComparison.Ordinal))?.Name ?? areaId;

	/// <summary>What every picker is told about an id it did not offer.</summary>
	public EntityLookup Lookup => _lookup ??= new EntityLookup(NameOf, _catalog.Knows);

	// ---- the header ------------------------------------------------------------------------------------

	/// <summary>The header lamp's reading, or <c>null</c> when nothing is lit.</summary>
	public RoomGlow? Glow =>
		Snapshot is { IsLit: true } snapshot
			? new RoomGlow(
				snapshot.State is AreaState.OverriddenOn || snapshot.ColorTempKelvin is null,
				snapshot.ColorTempKelvin,
				(int)Math.Round(6 + ((snapshot.BrightnessPct ?? 50) / 6), MidpointRounding.AwayFromZero))
			: null;

	/// <summary>How much of the armed countdown is left, from one down to zero, or <c>null</c> when none is armed.</summary>
	public double? Countdown => Snapshot is { } snapshot ? RoomFacts.Countdown(snapshot, Now) : null;

	/// <summary>When the room changes next, for the countdown's title.</summary>
	public DateTimeOffset? NextChangeAt => Snapshot?.NextChangeAt;

	/// <summary>How long the room has been in the state it is in.</summary>
	public string Since => Snapshot is { } snapshot ? RoomFacts.Since(snapshot, Now) : string.Empty;

	/// <summary>What the room is doing, in one line.</summary>
	public string Headline => Snapshot is { } snapshot ? RoomFacts.Headline(snapshot) : string.Empty;

	/// <summary>What happens next, or nothing when nothing is due.</summary>
	public string? NextLine => Snapshot is { } snapshot ? RoomFacts.NextLine(snapshot, Now, NameOf) : null;

	/// <summary>Whether the change that was due has not arrived.</summary>
	public bool IsOverdue => Snapshot is { } snapshot && RoomFacts.IsOverdue(snapshot, Now);

	/// <summary>How many of the room's lights have stopped answering, when any have.</summary>
	public string? NotResponding => Snapshot is { } snapshot ? RoomFacts.NotResponding(snapshot) : null;

	/// <summary>One sentence per motion sensor with a low battery.</summary>
	public IReadOnlyList<string> LowBatteries => Snapshot is { } snapshot ? RoomFacts.LowBatteries(snapshot, NameOf) : [];

	/// <summary>The facts table beside the header.</summary>
	public IReadOnlyList<RoomFact> Facts =>
		Snapshot is { } snapshot ? RoomFacts.For(snapshot, Now, NameOf) : [];

	/// <summary>Why the Light on button is closed, or <c>null</c> when it can be pressed.</summary>
	public string? LightOnRefusal { get; private set; }

	/// <summary>What the last press was actually told.</summary>
	/// <remarks>Not the same question as <see cref="LightOnRefusal"/>: a room can pass the button's own check and
	/// still be refused under the lock a moment later.</remarks>
	public string? LightOnRefused { get; private set; }

	/// <summary>What the button promises, with this room's own timeout in it instead of a vague "for a while".</summary>
	public string LightOnTitle =>
		LightOnRefusal is { Length: > 0 } refusal
			? refusal
			: $"Switches these lights on now, at the levels this room would use anyway, and off again after "
				+ $"{TokenFormat.Duration(Effective.VacancyTimeoutSeconds)} without movement — the same as walking in.";

	// Not a light switch, and the wording says so: switching it off changes nothing about the lamps right now.
	/// <summary>What the adaptive-lighting switch promises.</summary>
	public string SwitchTitle => IsEnabled
		? "On — this app runs the lighting in this room. Switching it off leaves the lamps exactly as they are."
		: "Off — this room is watched, but its lamps are never changed. Its settings are kept and take effect the moment it is enabled again.";

	/// <summary>The advice raised by switching the room on, until it is put away.</summary>
	public SwitchOnNote? SwitchOnNote { get; private set; }

	/// <summary>Asks the engine to light this room by hand.</summary>
	/// <remarks>The engine owns what happens next: the room lands in its ordinary active state on its ordinary
	/// vacancy countdown, so nothing here has to be undone when the page is closed.</remarks>
	public void LightOn()
	{
		LightOnRefused = _engine.LightNow(Area?.AreaId);
		LightOnRefusal = _engine.LightNowRefusal(Area?.AreaId);
	}

	/// <summary>Turns adaptive lighting on or off for this room.</summary>
	public async Task ToggleEnabled()
	{
		if (Area is not { } room)
			return;

		// An explicit true or false, never null; inheritance stays for documents that predate the switch.
		bool switchingOn = !IsEnabled;
		room.Enabled = switchingOn;

		// A refusal that named the disabled room would outlive the thing it named.
		LightOnRefused = null;

		// After MarkDirty, never before: MarkDirty re-runs discovery, so the note is built from the same preview
		// the gear card shows.
		await MarkDirty().ConfigureAwait(false);

		SwitchOnNote = switchingOn && !_switchOnDismissed
			? SwitchOnWarning.For(RoomName, _catalog.LightsIn(room, Defaults, Document.Global), Document.Global.IncludeLabel)
			: null;
	}

	/// <summary>Puts the switch-on note away for the rest of this visit.</summary>
	public void DismissSwitchOnNote()
	{
		SwitchOnNote = null;
		_switchOnDismissed = true;
	}

	// ---- saving ----------------------------------------------------------------------------------------

	/// <summary>Whether the in-memory document has moved ahead of the file.</summary>
	public bool Dirty { get; private set; }

	/// <summary>Everything this page has to say about saving, in one line.</summary>
	/// <remarks>The confirmation clears against the page's own clock, with no timer of its own, since the line is
	/// drawn again on every beat of it.</remarks>
	public string SaveLine => SaveState switch
	{
		RoomSaveState.Refused => "not saved",
		RoomSaveState.Waiting => "saving in a moment…",
		RoomSaveState.Saved => _saved.Text,
		_ => string.Empty
	};

	/// <summary>The confirmation of the last write, and the moment it stops being made.</summary>
	/// <remarks>The save line resolves it against <see cref="Now"/>; this is the message itself, for a design
	/// that wants to place it elsewhere.</remarks>
	public TransientMessage SaveConfirmation => _saved;

	/// <summary>The same answer as a meaning, for a design to paint how it likes.</summary>
	// Order is the ranking: a refusal stands until it is resolved, and a fresh edit outranks the confirmation of
	// the one before it.
	public RoomSaveState SaveState =>
		Failure is not null ? RoomSaveState.Refused
		: Dirty ? RoomSaveState.Waiting
		: _saved.IsShownAt(Now) ? RoomSaveState.Saved
		: RoomSaveState.Settled;

	/// <summary>Whether an edit is on screen that the file does not have.</summary>
	public bool HasRefusedEdit => Dirty && Failure is not null;

	/// <summary>The validator's complaint about this room, when it has one.</summary>
	/// <remarks>Matched on <see cref="AreaConfig.DisplayName"/>, never <see cref="RoomName"/>: the validator is
	/// pure and files errors under the document's own name, so joining on the resolved name finds nothing.</remarks>
	public AreaError? AreaError =>
		Validation is { } validation && Area is { } room
			? validation.AreaErrors.FirstOrDefault(error =>
				string.Equals(error.AreaName, room.DisplayName, StringComparison.OrdinalIgnoreCase))
			: null;

	/// <summary>Records that the document has moved ahead of the file, and starts the timer that catches it up.</summary>
	/// <remarks>Re-validates here so a problem is on screen before the write is attempted. Nothing on this path
	/// writes; <see cref="Commit"/> is the only thing that does.</remarks>
	public Task MarkDirty()
	{
		Dirty = true;
		Refresh();

		_saveCts?.Cancel();
		_saveCts?.Dispose();
		_saveCts = new CancellationTokenSource();

		// Fire and forget. SaveAfterQuiet swallows its own cancellation and logs the rest, so nothing here can
		// surface as an unobserved exception on a thread-pool thread.
		_ = SaveAfterQuiet(_saveCts.Token);

		return Task.CompletedTask;
	}

	private async Task SaveAfterQuiet(CancellationToken token)
	{
		try
		{
			await Task.Delay(SaveDelay, token).ConfigureAwait(false);

			await _dispatch(() =>
			{
				Commit();
				Changed?.Invoke();
			}).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// A newer edit superseded this timer, or the page went away. Either way there is nothing to write here.
		}
		catch (ObjectDisposedException)
		{
			// The circuit closed while the timer was running; Dispose has already written whatever was pending.
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "The room page could not save after an edit.");
		}
	}

	/// <summary>Writes this room into the document as it is on disk now, through the one save pipeline.</summary>
	/// <remarks>This room and nothing else: the document is read once, on open, and the page stays open, so
	/// writing its own copy back would send every other room as it stood at page load. The object graph is not
	/// swapped afterwards, since reloading it here would pull the document out from under an edit in progress.</remarks>
	public void Commit()
	{
		if (!Dirty || LoadError is { Length: > 0 })
			return;

		_saveCts?.Cancel();

		try
		{
			RoomWriteResult write = RoomWrite.Save(_engine, _write, _removed ? null : Area, RoomName);
			SaveResult result = write.Result;
			_write = write.Token;

			// A conflict carries no verdict on this document, so it must not replace the one on screen.
			if (result.Status is not SaveStatus.Conflicted)
				Validation = result.Validation;

			if (result.Written)
			{
				DateTimeOffset saved = DateTimeOffset.Now;

				Dirty = false;
				_saved = TransientMessage.For(
					$"Saved {saved.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)}",
					saved,
					ConfirmationLingers);
				Failure = null;
			}
			else
			{
				// Drops any lingering confirmation, so a refusal never stands beside a stale "Saved".
				_saved = TransientMessage.None;
				Failure = result;
			}
		}
		catch (LightingConfigException exception)
		{
			LoadError = exception.Message;
		}
	}

	/// <summary>Throws away everything unsaved by re-reading the file.</summary>
	public void Discard()
	{
		_loadedFor = null;
		Show(_areaId);
	}

	// ---- the sentences ---------------------------------------------------------------------------------

	/// <summary>How this room behaves, said as editable sentences.</summary>
	public IReadOnlyList<Sentence> Sentences =>
		Area is { } room ? AreaSentences.ForArea(room, Defaults) : [];

	/// <summary>Applies one edit made in a sentence.</summary>
	public Task OnEdited(SentenceEdit edit)
	{
		if (Area is not { } room)
			return Task.CompletedTask;

		// A key the page cannot apply means the sentence and the page have drifted apart.
		if (!RoomSettings.Apply(room, edit))
		{
			_logger.LogWarning("A sentence offered an edit to {Key}, which this page does not know how to apply.", edit.Key);

			return Task.CompletedTask;
		}

		return MarkDirty();
	}

	/// <summary>Sends a value back to following the house by clearing the room's property, never by copying the
	/// house's current number into it.</summary>
	public Task Revert(string key)
	{
		if (Area is not { } room || !RoomSettings.Clear(room, key))
			return Task.CompletedTask;

		return MarkDirty();
	}

	/// <summary>The same, for a setting the all-settings panel handed back.</summary>
	public Task RevertSetting(RoomSetting setting) => Revert(setting.Key);

	// ---- brightness and warmth -------------------------------------------------------------------------

	/// <summary>How many periods this room sets for itself.</summary>
	public int OwnPeriodCount => Area is { } room ? RoomLevels.OwnCount(Periods, room) : 0;

	/// <summary>The line under the card's head: how much of the schedule this room has taken over, in words.</summary>
	public string LevelsLede
	{
		get
		{
			if (Area is not { } room)
				return string.Empty;

			int periods = Periods.Count;
			int chosen = RoomLevels.OwnCount(Periods, room);
			int orphaned = RoomLevels.Orphans(Periods, room).Count;

			// Orphans are counted apart, never folded in: they are not among the periods on screen.
			string kept = orphaned switch
			{
				0 => string.Empty,
				1 => " 1 kept from a period that is gone.",
				_ => $" {orphaned} kept from periods that are gone."
			};

			if (periods == 0)
				return "The schedule has no periods." + kept;

			if (chosen == 0)
				return "All follow the schedule." + kept;

			return chosen == periods
				? $"All {periods} set here." + kept
				: $"{chosen} of {periods} set here." + kept;
		}
	}

	/// <summary>The period in force right now by id, which is what the levels table matches its rows on.</summary>
	public string? ActivePeriodId => _activePeriod?.Key;

	/// <summary>Why a level test is closed, or <c>null</c> when one can be run.</summary>
	public string? TestRefusal { get; private set; }

	/// <summary>How long a level test holds the lights.</summary>
	public static int TestSeconds => (int)AreaController.LevelTestSeconds;

	/// <summary>The period whose test is still holding the lights, or <c>null</c> when none is.</summary>
	/// <remarks>
	///     Local state first, for the click that just armed it: the report has not had time to reach the cache
	///     yet. A fresh model carries none, so it falls back to the last snapshot, which the engine publishes the
	///     moment a test starts; that is what redraws the countdown after a reload or a navigate-back instead of
	///     showing plain Test buttons while the engine's own return is still pending.
	/// </remarks>
	public string? TestingPeriod =>
		_testEndsAt is { } ends && ends > Now ? _testingPeriodId
		: Snapshot is { } snapshot ? RoomFacts.TestingPeriod(snapshot, Now)
		: null;

	/// <summary>How many seconds a running test has left.</summary>
	public int TestSecondsLeft =>
		_testEndsAt is { } ends && ends > Now ? Math.Max(0, (int)Math.Ceiling((ends - Now).TotalSeconds))
		: Snapshot is { } snapshot ? RoomFacts.TestSecondsLeft(snapshot, Now)
		: 0;

	/// <summary>The light a running test is showing alone, read the same way as <see cref="TestingPeriod"/>.</summary>
	public string? TestingLight =>
		_testEndsAt is { } ends && ends > Now ? _testingLightId
		: Snapshot is { } snapshot && RoomFacts.TestingPeriod(snapshot, Now) is not null ? snapshot.TestingLightId
		: null;

	/// <summary>Asks the engine to put one period on this room's real lights for a few seconds.</summary>
	/// <remarks>A press while one is already running moves the test to the new row: the engine drops the pending
	/// return and schedules one return from the newest press, so the room is never owed two.</remarks>
	public void TestPeriod(string periodId)
	{
		TestRefusal = _engine.TestPeriod(Area?.AreaId, periodId);

		if (TestRefusal is { Length: > 0 })
		{
			_testingPeriodId = null;
			_testEndsAt = null;

			return;
		}

		Now = DateTimeOffset.Now;
		_testingPeriodId = periodId;
		_testingLightId = null;
		_testEndsAt = Now.AddSeconds(AreaController.LevelTestSeconds);
	}

	/// <summary>Asks the engine to put one period on one light of this room for a few seconds.</summary>
	public void TestLight(LightTest test)
	{
		TestRefusal = _engine.TestLight(Area?.AreaId, test.EntityId, test.PeriodId);

		if (TestRefusal is { Length: > 0 })
		{
			_testingPeriodId = null;
			_testingLightId = null;
			_testEndsAt = null;

			return;
		}

		Now = DateTimeOffset.Now;
		_testingPeriodId = test.PeriodId;
		_testingLightId = test.EntityId;
		_testEndsAt = Now.AddSeconds(AreaController.LevelTestSeconds);
	}

	/// <summary>The periods this room follows the daylight curve for, in schedule order.</summary>
	/// <remarks>Empty hides the chart: with no period of this room's own on the curve its handles would edit
	/// numbers nothing reads.</remarks>
	public IReadOnlyList<string> CurvePeriodNames =>
	[
		.. RoomLevels.CurvePeriods(Periods, Area)
			.Select(period => period.Name is { Length: > 0 } name ? name : "unnamed period")
	];

	/// <summary>The daylight reading behind the chart. Read on the tick, never in a getter: the page redraws
	/// every second.</summary>
	public double? DaylightLux => _daylightLux;

	/// <summary>Where the curve's reading comes from, said under the chart.</summary>
	// Names the fold the Daylight sensor row is actually in. All settings is a different section and holds no
	// such row, so a reader sent there finds nothing.
	public string DaylightSourceNote =>
		Area?.DaylightSensor is { Length: > 0 } own
			? $"Reading {NameOf(own)}, this room's own choice. Change it under “Not right? Pick by hand”."
			: Document.Global.OutdoorLuxSensor is { Length: > 0 } house
				? $"Reading {NameOf(house)}, the house's outdoor sensor."
				: "No outdoor sensor is named, so the curve holds the level at its dark end. Name one under House, or pick one for this room under “Not right? Pick by hand”.";

	/// <summary>Applies one setting a drag or an arrow key changed, through the path the stepper uses.</summary>
	public Task OnCurveEdited(CurveEdit edit) => SetNumber(edit.Key, edit.Value);

	// ---- all settings ----------------------------------------------------------------------------------

	/// <summary>Whether the all-settings fold is open.</summary>
	public bool ShowAll { get; private set; }

	/// <summary>Opens or closes the all-settings fold.</summary>
	public void ToggleAll() => ShowAll = !ShowAll;

	/// <summary>How many settings this room states for itself, in words.</summary>
	public string OwnLine
	{
		get
		{
			if (Area is not { } room)
				return string.Empty;

			int own = RoomSettings.OwnCount(room);

			// Short: see the note's markup for the 390px measurement that set the length.
			return own == 0
				? $"All {AreaView.OverridableSettingCount} follow the"
				: $"{own} of {AreaView.OverridableSettingCount} are set for this room; the rest follow the";
		}
	}

	/// <summary>The all-settings folds as the panel draws them: this room's values, each said to be its own or
	/// the house's, with the house's wording for the road back.</summary>
	public AllSettingsPanel.Input SettingsInput => new(
	[
		.. RoomSettings.Groups.Select(group => new AllSettingsPanel.Group(
			group,
			_openedGroups.Contains(group.Title),
			OwnCountIn(group),
			[
				.. group.Settings
					.Where(setting => setting.AppliesTo(Effective))
					.Select(SettingItem)
			]))
	]);

	/// <summary>Opens or closes one section of the all-settings panel.</summary>
	public void Toggle(RoomSettingGroup group)
	{
		if (!_openedGroups.Remove(group.Title))
			_openedGroups.Add(group.Title);
	}

	/// <summary>Applies one edit from the all-settings panel, by the kind of control that made it.</summary>
	public Task SetSetting(AllSettingsPanel.Change change) => change.Setting.Control switch
	{
		RoomControl.Steps => SetStep(change.Text ?? ""),
		RoomControl.Flag => SetFlag(change.Setting.Key, change.Flag),
		RoomControl.Choice => SetChoice(change.Setting.Key, change.Text ?? ""),
		RoomControl.Entity => SetEntity(change.Setting.Key, change.Text),
		_ => SetNumber(change.Setting.Key, change.Number)
	};

	// Counts keys, not rows, so the badge and the "n of 23" line above it are the same number counted twice. A
	// stepped row folds two keys and would otherwise report one while the denominator reports two.
	private int OwnCountIn(RoomSettingGroup group) =>
		Area is { } room
			? group.Settings.SelectMany(setting => setting.AllKeys).Count(key => RoomSettings.IsOwn(room, key))
			: 0;

	// Read by the control that draws it and no other: Shown throws on a setting whose value is not a number.
	private AllSettingsPanel.Item SettingItem(RoomSetting setting)
	{
		AllSettingsPanel.Item item = new(setting)
		{
			IsOwn = Area is { } row && RoomSettings.IsOwn(row, setting),
			ValueIsOwn = Area is { } owner && RoomSettings.IsOwn(owner, setting.Key),
			HouseText = RoomSettings.Describe(null, Defaults, setting.Key)
		};

		return setting.Control switch
		{
			RoomControl.Steps => item with { StepValue = SleepSteps.Of(Area, Defaults).ToString() },
			RoomControl.Flag => item with { Flag = RoomSettings.Flag(Area, Defaults, setting.Key) },
			RoomControl.Choice => item with { ChoiceValue = RoomSettings.ChoiceName(Area, Defaults, setting.Key) },
			RoomControl.Entity => item with
			{
				Text = RoomSettings.Entity(Area, Defaults, setting.Key),
				Picker = new AllSettingsPanel.PickerField
				{
					Options = _sunEntities,
					NoneLabel = "(the house's sun entity)",
					Placeholder = "sun.sun"
				}
			},
			_ => item with
			{
				Text = RoomSettings.Describe(Area, Defaults, setting.Key),
				Number = RoomSettings.Shown(Area, Defaults, setting.Key)
			}
		};
	}

	// ---- the movement rules that have no twin in the house defaults ------------------------------------

	/// <summary>Sensors outside this room that light it ahead of time.</summary>
	public IReadOnlyList<string> LeadInSensors => Area?.LeadInSensors ?? [];

	/// <summary>Everything that could be one.</summary>
	public IReadOnlyList<EntityOption> LeadInOptions { get; private set; } = [];

	/// <summary>Sets the lead-in sensors.</summary>
	public Task SetLeadInSensors(IReadOnlyList<string> value) =>
		SetList(value, list => Area!.LeadInSensors = list);

	/// <summary>What stops this room lighting itself.</summary>
	public IReadOnlyList<string> IgnoreWhenOn => Area?.IgnoreWhenOn ?? [];

	/// <summary>Whether that list holds while its entities are off instead.</summary>
	public bool IgnoreWhenOnInverted => Area?.IgnoreWhenOnInverted is true;

	/// <summary>What holds this room's lights on.</summary>
	public IReadOnlyList<string> KeepLitWhenOn => Area?.KeepLitWhenOn ?? [];

	/// <summary>Whether that list holds while its entities are off instead.</summary>
	public bool KeepLitWhenOnInverted => Area?.KeepLitWhenOnInverted is true;

	/// <summary>Everything that could block or hold a room.</summary>
	public IReadOnlyList<EntityOption> BlockerOptions { get; private set; } = [];

	/// <summary>Sets what stops this room lighting itself.</summary>
	public Task SetIgnoreWhenOn(IReadOnlyList<string> value) => SetList(value, list => Area!.IgnoreWhenOn = list);

	/// <summary>Sets that list's polarity.</summary>
	public Task SetIgnoreWhenOnInverted(bool inverted) => SetInvert(inverted, value => Area!.IgnoreWhenOnInverted = value);

	/// <summary>Sets what holds this room's lights on.</summary>
	public Task SetKeepLitWhenOn(IReadOnlyList<string> value) => SetList(value, list => Area!.KeepLitWhenOn = list);

	/// <summary>Sets that list's polarity.</summary>
	public Task SetKeepLitWhenOnInverted(bool inverted) => SetInvert(inverted, value => Area!.KeepLitWhenOnInverted = value);

	/// <summary>The room's own answer on automations, or the house's while it states none.</summary>
	public bool AutomationsCountAsManual => Area?.TreatAutomationsAsManual ?? Document.Global.TreatAutomationsAsManual;

	/// <summary>Whether that answer is this room's own.</summary>
	public bool AutomationsAreOwn => Area?.TreatAutomationsAsManual is not null;

	/// <summary>What the house says, for the road back.</summary>
	public string AutomationsHouseText => Document.Global.TreatAutomationsAsManual ? "yes" : "no";

	/// <summary>Sets the room's answer on automations, or clears it with <c>null</c>.</summary>
	public Task SetAutomationsAsManual(bool? value) => Set(() => Area!.TreatAutomationsAsManual = value);

	/// <summary>The scene this room runs instead of lighting itself on movement.</summary>
	public string? SceneOnMotion => Area?.SceneOnMotion;

	/// <summary>The scene this room runs when it has been empty long enough to go off.</summary>
	public string? SceneWhenEmpty => Area?.SceneWhenEmpty;

	/// <summary>Every scene Home Assistant knows.</summary>
	public IReadOnlyList<EntityOption> SceneOptions { get; private set; } = [];

	/// <summary>Sets the movement scene.</summary>
	public Task SetSceneOnMotion(string? value) => Set(() => Area!.SceneOnMotion = value);

	/// <summary>Sets the empty-room scene.</summary>
	public Task SetSceneWhenEmpty(string? value) => Set(() => Area!.SceneWhenEmpty = value);

	// ---- room identity ---------------------------------------------------------------------------------

	/// <summary>Whether the identity fold is open.</summary>
	public bool IdentityOpen { get; private set; }

	/// <summary>Opens or closes the identity fold.</summary>
	public void ToggleIdentity() => IdentityOpen = !IdentityOpen;

	/// <summary>The name this room states for itself, empty while it follows the area's.</summary>
	public string? OwnName => Area?.Name;

	/// <summary>The Home Assistant area this room is.</summary>
	public string? AreaId => Area?.AreaId;

	/// <summary>Renames the room, or clears the name so it follows the area's again.</summary>
	public Task SetOwnName(string? typed)
	{
		if (Area is not { } room)
			return Task.CompletedTask;

		room.Name = string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();

		return MarkDirty();
	}

	/// <summary>Moves the room to a different Home Assistant area, which is also a change of address.</summary>
	/// <remarks>The new area's name is not copied into the document: names resolve from the registry at the point
	/// of use, so writing one freezes this room at today's name. The navigation stays behind the save check, or a
	/// refused save has the next parameter pass reload from disk and clear the edit.</remarks>
	public void ChangeArea(string? areaId)
	{
		if (Area is not { } room || areaId is not { Length: > 0 })
			return;

		room.AreaId = areaId;
		Dirty = true;
		Commit();

		if (Failure is not null || LoadError is { Length: > 0 })
		{
			// Everything derived from the document still describes the old area. Other edits re-derive through
			// MarkDirty; this path saves at once, so it re-derives here before the failure renders.
			Refresh();

			return;
		}

		Navigated?.Invoke(HouseView.RoomHref(areaId)!);
	}

	// ---- what is in the room ---------------------------------------------------------------------------

	/// <summary>Whether the room counts as dark because it has no light-level sensor.</summary>
	public bool CountsAsDarkForWantOfASensor =>
		Area is { } room && Resolved is { } resolved
		&& CommissioningVerdicts.CountsAsDarkForWantOfASensor(
			room, Defaults, resolved.LuxSensors.Count, resolved.MotionSensors.Count);

	/// <summary>What has been left out of this room by hand.</summary>
	public IReadOnlyList<string> Excluded => Area?.ExcludeEntities ?? [];

	/// <summary>Drops one discovered entity from this room without switching the room to a hand-picked list, so
	/// discovery keeps finding everything else.</summary>
	public Task Exclude(string entityId)
	{
		if (Area is not { } room)
			return Task.CompletedTask;

		List<string> excluded = room.ExcludeEntities is { } existing ? [.. existing] : [];

		if (!excluded.Contains(entityId, StringComparer.Ordinal))
			excluded.Add(entityId);

		room.ExcludeEntities = excluded;

		return MarkDirty();
	}

	/// <summary>The way back from <see cref="Exclude"/>.</summary>
	public Task Include(string entityId)
	{
		if (Area is not { } room || room.ExcludeEntities is not { } excluded)
			return Task.CompletedTask;

		List<string> kept = [.. excluded.Where(existing => !string.Equals(existing, entityId, StringComparison.Ordinal))];
		room.ExcludeEntities = kept.Count == 0 ? null : kept;

		return MarkDirty();
	}

	/// <summary>Whether the pick-by-hand fold is open.</summary>
	public bool PickersOpen { get; private set; }

	/// <summary>Opens or closes the pick-by-hand fold.</summary>
	public void TogglePickers() => PickersOpen = !PickersOpen;

	/// <summary>What has been picked by hand here, in words.</summary>
	public string PickedByHandNote
	{
		get
		{
			if (Area is not { } room)
				return string.Empty;

			List<string> parts = [];

			if (room.Lights is { Count: > 0 } lights)
				parts.Add($"{lights.Count} {(lights.Count == 1 ? "light" : "lights")}");

			if (room.MotionSensors is { Count: > 0 } motion)
				parts.Add($"{motion.Count} motion {(motion.Count == 1 ? "sensor" : "sensors")}");

			if (room.LuxSensor is { Length: > 0 })
				parts.Add("a light-level sensor");

			// Blockers are not counted: that list lives in Movement & timing, not in this fold.
			return parts.Count == 0
				? "nothing is picked by hand here"
				: $"picked by hand: {string.Join(", ", parts)}";
		}
	}

	/// <summary>Whether the pickers are filtered to this room's own entities; a room naming no Home Assistant
	/// area has nothing to filter to.</summary>
	public bool ScopeToArea => !ShowAllEntities && Area?.AreaId is { Length: > 0 };

	/// <summary>Whether the pickers offer the whole house.</summary>
	public bool ShowAllEntities { get; private set; }

	/// <summary>Widens or narrows the pickers; changes what is offered, never what is configured.</summary>
	public void SetShowAllEntities(bool showAll)
	{
		ShowAllEntities = showAll;
		Refresh();
	}

	/// <summary>The line under the tick-box: what the lists are showing right now.</summary>
	public string ScopeNote => ScopeToArea
		? "Showing what Home Assistant puts in this room. Anything you have already picked stays listed even if it is elsewhere."
		: "Showing every one in the house.";

	/// <summary>The house's include label as stored, or empty when it manages every light it finds.</summary>
	private string IncludeLabel => Document.Global.IncludeLabel ?? string.Empty;

	/// <summary>The include label as a reader knows it: a stored label id shows the label's name.</summary>
	// The document may hold an id or a name. A name is shown as it stands, so an unreadable registry changes
	// nothing on screen.
	public string IncludeLabelName =>
		_catalog.LabelOptions().FirstOrDefault(label => string.Equals(label.Id, IncludeLabel, StringComparison.Ordinal))
			is { } known
			? known.Name
			: IncludeLabel;

	/// <summary>Whether the label filter is worth offering: with no include label both states of the control show
	/// the same list.</summary>
	public bool LabelFilterApplies => ScopeToArea && IncludeLabel.Length > 0;

	/// <summary>Whether the second block of each list is held to the house's include label.</summary>
	// On by default, matching the rule the engine applies, so the short list and the running house agree.
	public bool LabelledOnly { get; private set; } = true;

	/// <summary>The line under the filter: what the second block of each list is holding right now.</summary>
	public string LabelNote => LabelledOnly
		? $"Everything else in this room that carries the “{IncludeLabelName}” label — what adaptive lighting manages on its own."
		: "Everything else in this room, labelled or not. Naming one here manages it whatever labels it carries.";

	/// <summary>Narrows or widens the second block; changes what is offered, never what is configured.</summary>
	public void SetLabelFilter(bool labelledOnly)
	{
		LabelledOnly = labelledOnly;
		Refresh();
	}

	/// <summary>Why a picker has nothing to offer; an empty scoped list is ordinary.</summary>
	public string EmptyNote(string what) =>
		!HomeAssistantIsAnswering ? "Waiting for Home Assistant — type an id for now."
		: ScopeToArea ? $"No {what} in this room. Tick “offer entities from the whole house” to pick from anywhere."
		: $"No {what} anywhere in Home Assistant — type an id if one is coming.";

	/// <summary>The lights this room names for itself.</summary>
	public IReadOnlyList<string> Lights => Area?.Lights ?? [];

	/// <summary>The motion sensors this room names for itself.</summary>
	public IReadOnlyList<string> MotionSensors => Area?.MotionSensors ?? [];

	/// <summary>The sensor the daylight curve follows here.</summary>
	public string? DaylightSensor => Area?.DaylightSensor;

	/// <summary>The sensor that decides whether this room is dark.</summary>
	public string? LuxSensor => Area?.LuxSensor;

	/// <summary>Sets the room's lights.</summary>
	public Task SetLights(IReadOnlyList<string> value) => SetList(value, list => Area!.Lights = list);

	/// <summary>Sets the room's motion sensors.</summary>
	public Task SetMotionSensors(IReadOnlyList<string> value) => SetList(value, list => Area!.MotionSensors = list);

	/// <summary>Sets the room's daylight sensor.</summary>
	public Task SetDaylightSensor(string? value) => Set(() => Area!.DaylightSensor = value);

	/// <summary>Sets the room's light-level sensor.</summary>
	public Task SetLuxSensor(string? value) => Set(() => Area!.LuxSensor = value);

	/// <summary>What the lights picker offers first.</summary>
	public IReadOnlyList<EntityOption> LightChoices { get; private set; } = [];

	/// <summary>What the motion picker offers first.</summary>
	public IReadOnlyList<EntityOption> MotionChoices { get; private set; } = [];

	/// <summary>What the light-level picker offers first.</summary>
	public IReadOnlyList<EntityOption> LuxChoices { get; private set; } = [];

	/// <summary>Everything else in the room, for the lights picker.</summary>
	public IReadOnlyList<EntityOption> LightOthers { get; private set; } = [];

	/// <summary>Everything else in the room, for the motion picker.</summary>
	public IReadOnlyList<EntityOption> MotionOthers { get; private set; } = [];

	/// <summary>Everything else in the room, for the light-level picker.</summary>
	public IReadOnlyList<EntityOption> LuxOthers { get; private set; } = [];

	/// <summary>Every light-level sensor in the house, for the daylight picker.</summary>
	public IReadOnlyList<EntityOption> LuxSensorOptions { get; private set; } = [];

	/// <summary>What "say nothing" means on the daylight picker, which is the house's own choice.</summary>
	public string OutdoorSensorLabel =>
		Document.Global.OutdoorLuxSensor is { Length: > 0 } house
			? $"(the house's outdoor sensor — {NameOf(house)})"
			: "(the house's outdoor sensor — none is named)";

	// ---- setting up again, and removal -----------------------------------------------------------------

	/// <summary>Whether the set-up-again panel is open.</summary>
	public bool SetupOpen { get; private set; }

	/// <summary>Whether the removal question is on screen.</summary>
	public bool Removing { get; private set; }

	/// <summary>The scope the set-up-again panel starts with: this room alone.</summary>
	public IReadOnlyCollection<string> SetupScope => Area?.AreaId is { Length: > 0 } areaId ? [areaId] : [];

	/// <summary>Opens the set-up-again panel.</summary>
	public void OpenSetup()
	{
		Removing = false;
		SetupOpen = true;
	}

	/// <summary>Closes the set-up-again panel without changing anything.</summary>
	public void CancelSetup() => SetupOpen = false;

	/// <summary>Asks whether to remove the room.</summary>
	public void AskRemove() => Removing = true;

	/// <summary>Takes the removal question away.</summary>
	public void KeepRoom() => Removing = false;

	/// <summary>The plan for setting this room up again, carrying nothing but this room.</summary>
	/// <remarks><see cref="AreaSetupService.Plan"/> proposes every unconfigured area it finds, whatever the scope.
	/// The extras are dropped here and not at apply time, so the panel's warning stays true to what confirming
	/// does.</remarks>
	public SetupPlan PlanSetup(IReadOnlyCollection<string> scope) =>
		_catalog.PlanSetup(Document, scope) with { NewAreas = [], NotQualifying = [] };

	/// <summary>Carries out a confirmed rebuild, saved at once instead of after the usual pause.</summary>
	/// <remarks><see cref="AreaSetupService.Apply"/> replaces the room object, so the model has to find it again.</remarks>
	public void ApplySetup(SetupPlan plan)
	{
		AreaSetupService.Apply(Document, plan);
		SetupOpen = false;

		Area = Document.Areas.FirstOrDefault(area => string.Equals(area.AreaId, _areaId, StringComparison.Ordinal));

		Dirty = true;
		Commit();
		Refresh();
	}

	/// <summary>Removes the room and leaves for the board; this page's subject no longer exists.</summary>
	public void Remove()
	{
		if (Area is not { } room)
			return;

		Document.Areas.Remove(room);
		_removed = true;
		Dirty = true;
		Commit();

		if (Failure is null)
			Navigated?.Invoke("");
		else
			Removing = false;
	}

	// ---- what happened here ----------------------------------------------------------------------------

	/// <summary>This room's reports, sifted; the card and its footer both read it, so the two counts agree.</summary>
	private IReadOnlyList<ActivityEntry> RoomReports =>
		Area is null ? [] : ActivityView.Shown(ActivityView.InRoom(_log.Entries, RoomName));

	/// <summary>The rows the log card draws.</summary>
	public IReadOnlyList<ActivityRow> RoomLog => ActivityView.Rows(RoomReports, LogRows);

	/// <summary>What the log card is holding back.</summary>
	public string LogFoot
	{
		get
		{
			int total = RoomReports.Count;
			int shown = RoomLog.Count;

			// Only the row budget earns the second sentence; a collapsed run is still on this card.
			return shown >= LogRows && total > shown
				? $"The newest {shown} of {total} reports from this room. The rest are on the Activity page."
				: "Everything this room has reported since the engine started.";
		}
	}

	// ---- loading and deriving --------------------------------------------------------------------------

	private void Load()
	{
		_loadedFor = _areaId;
		Failure = null;
		Dirty = false;
		_saved = TransientMessage.None;
		SetupOpen = false;
		Removing = false;
		_removed = false;
		SwitchOnNote = null;
		_switchOnDismissed = false;

		try
		{
			Document = _engine.Store.Load();
			LoadError = null;
		}
		catch (LightingConfigException exception)
		{
			LoadError = exception.Message;
			Area = null;

			return;
		}

		Area = Document.Areas.FirstOrDefault(area => string.Equals(area.AreaId, _areaId, StringComparison.Ordinal));
		_write = RoomWrite.Open(Document, _areaId);

		LoadCatalog();
		Refresh();
	}

	/// <summary>Re-reads the registry once per document load, never once per render.</summary>
	/// <remarks>These lists change when the house changes; a getter would hit the registry sixty times a minute.</remarks>
	private void LoadCatalog()
	{
		_catalog.Invalidate();

		Areas = _catalog.Areas(Document.Global);
		_sunEntities = _catalog.EntitiesInDomains("sun");
		BlockerOptions = _catalog.EntitiesInDomains("binary_sensor", "input_boolean", "switch", "media_player");
		LeadInOptions = _catalog.EntitiesInDomains("binary_sensor");
		SceneOptions = _catalog.EntitiesInDomains("scene");
		_motionSensors = _catalog.EntitiesWithDeviceClass("binary_sensor", [.. Document.Global.EffectiveMotionDeviceClasses]);
		LuxSensorOptions = _catalog.EntitiesWithDeviceClass("sensor", [Document.Global.IlluminanceDeviceClass]);
		_lights = _catalog.EntitiesInDomains("light");
		HomeAssistantIsAnswering = _catalog.IsHomeAssistantResponding(Document.Global);
	}

	/// <summary>Re-derives everything that follows from the document: the validator's verdict, what discovery now
	/// resolves in this room, and what the room is doing.</summary>
	private void Refresh()
	{
		if (Area is not { } room)
			return;

		Validation = _engine.Validate(Document);
		Preview = _catalog.PreviewArea(room, Defaults, Document.Global);

		AreaEntities inArea = _catalog.EntitiesInArea(room.AreaId, Document.Global);

		LightChoices = Scope(inArea.Lights, _lights, room.Lights);
		MotionChoices = Scope(inArea.MotionSensors, _motionSensors, room.MotionSensors);
		LuxChoices = Scope(inArea.LuxSensors, LuxSensorOptions, room.LuxSensor is { Length: > 0 } lux ? [lux] : null);

		// Only while the lists are scoped to the room. Widened to the house they already hold everything.
		AreaEntities others = ScopeToArea
			? _catalog.OtherEntitiesInArea(room.AreaId, Document.Global, LabelledOnly)
			: AreaEntities.Empty;

		LightOthers = Beyond(others.Lights, LightChoices);
		MotionOthers = Beyond(others.MotionSensors, MotionChoices);
		LuxOthers = Beyond(others.LuxSensors, LuxChoices);

		ReadLiveState();
	}

	/// <summary>Everything else, minus whatever the first block already offers.</summary>
	/// <remarks>A hand-picked entity reaches both lists, since <see cref="Scope"/> carries it into the first and
	/// it is in the room; listing it twice would let one entity be picked from two places in one dropdown.</remarks>
	private static IReadOnlyList<EntityOption> Beyond(
		IReadOnlyList<EntityOption> others,
		IReadOnlyList<EntityOption> offered)
	{
		HashSet<string> already = new(offered.Select(option => option.EntityId), StringComparer.Ordinal);

		return [.. others.Where(option => !already.Contains(option.EntityId))];
	}

	/// <summary>What one picker offers: this room's entities, unless the lists have been widened.</summary>
	/// <remarks>The in-area list comes from the same resolver the gear card runs, so the scoped list cannot
	/// disagree with what the room would resolve, and a configured value survives the filter.</remarks>
	private IReadOnlyList<EntityOption> Scope(
		IReadOnlyList<EntityOption> inArea,
		IReadOnlyList<EntityOption> everywhere,
		IReadOnlyList<string>? configured)
	{
		if (!ScopeToArea)
			return everywhere;

		// An entity picked from outside the area stays listed and stays selected: the filter is a default, and
		// must never drop a chosen value.
		IEnumerable<EntityOption> outsiders = configured is null
			? []
			: everywhere.Where(option =>
				configured.Contains(option.EntityId, StringComparer.Ordinal)
				&& !inArea.Any(local => string.Equals(local.EntityId, option.EntityId, StringComparison.Ordinal)));

		return [.. inArea.Concat(outsiders).OrderBy(option => option.FriendlyName, StringComparer.CurrentCulture)];
	}

	/// <summary>This room's newest report, by area id first and display name second, which is the same join the
	/// snapshot cache and the settings list use.</summary>
	/// <returns>Whether anything drawn outside the clock's reach has moved, so the page is redrawn only when it
	/// would come out different.</returns>
	private bool ReadLiveState()
	{
		if (Area is not { } room)
			return false;

		StillState before = Still;

		Snapshot = _cache.Find(room.AreaId, RoomName);

		// Resolved here and never taken from the snapshot's PeriodName: the levels table has to be right for a
		// room that has never reported, and a stale snapshot would leave the now badge on last night's period.
		// Through Schedule, so this badge, the schedule editor's and the mode cards come out of one calculator,
		// and the engine's latch is handed over where one is running, or a period that waits for movement would
		// be badged from its start time.
		//
		// The select is read only when it is allowed to decide. Under the default authority InForceNow discards
		// the value, so an unguarded read on the one-second ticker is a Home Assistant lookup per second per open
		// room page, thrown away.
		(TimeOnly? sunrise, TimeOnly? sunset) = _catalog.SunTimesToday();
		string? selectValue = Schedule.HomeAssistantDecides(Document.Global)
			? _catalog.CurrentStateOf(Document.Global.PeriodSelect?.EntityId)
			: null;

		_activePeriod = Schedule.InForceNow(
			Periods,
			Document.Global,
			new SunTimes(sunrise, sunset),
			Now,
			selectValue,
			Schedule.PeriodHoldRule(_engine)).Period;

		_daylightLux = _catalog.LuxOf(DaylightSensorId);

		// Asked of the engine on the ticker, not composed here, so a room that falls under somebody's hand while
		// the page sits open has its Test buttons closed within the second.
		TestRefusal = _engine.LevelTestRefusal(room.AreaId);
		LightOnRefusal = _engine.LightNowRefusal(room.AreaId);

		return Still != before;
	}

	/// <summary>Everything the levels table, the curve and the pickers draw that the clock can change under them.</summary>
	// The header, the save line and the log are on the clock and redraw themselves, so nothing they alone show
	// belongs here.
	private StillState Still => new(
		Snapshot,
		ActivePeriodId,
		_daylightLux,
		TestRefusal,
		TestingPeriod,
		TestSecondsLeft,
		TestingLight);

	private readonly record struct StillState(
		AreaSnapshot? Snapshot,
		string? ActivePeriodId,
		double? DaylightLux,
		string? TestRefusal,
		string? TestingPeriod,
		int TestSecondsLeft,
		string? TestingLight);

	/// <summary>Which sensor the curve reads here: this room's own, or the house's outdoor one.</summary>
	private string? DaylightSensorId =>
		Area?.DaylightSensor is { Length: > 0 } own ? own
		: Document.Global.OutdoorLuxSensor is { Length: > 0 } house ? house
		: null;

	// ---- the edit paths --------------------------------------------------------------------------------

	private Task SetNumber(string key, double value)
	{
		if (Area is not { } room)
			return Task.CompletedTask;

		RoomSettings.SetShown(room, key, value);

		return MarkDirty();
	}

	private Task SetFlag(string key, bool value)
	{
		if (Area is not { } room)
			return Task.CompletedTask;

		RoomSettings.SetFlag(room, key, value);

		return MarkDirty();
	}

	private Task SetStep(string value)
	{
		if (Area is not { } room || !SleepSteps.TryParse(value, out SleepStep step))
			return Task.CompletedTask;

		SleepSteps.Set(room, step);

		return MarkDirty();
	}

	// Through RoomSettings.Apply, the same path the sentence tokens take, so the two choice rows cannot end up
	// writing different properties from the same words.
	private Task SetChoice(string key, string value)
	{
		if (Area is not { } room || !RoomSettings.Apply(room, new SentenceEdit(key, TokenKind.Choice, value)))
			return Task.CompletedTask;

		return MarkDirty();
	}

	private Task SetEntity(string key, string? value)
	{
		if (Area is not { } room)
			return Task.CompletedTask;

		RoomSettings.SetEntity(room, key, value);

		return MarkDirty();
	}

	private Task Set(Action mutate)
	{
		if (Area is null)
			return Task.CompletedTask;

		mutate();

		return MarkDirty();
	}

	private Task SetList(IReadOnlyList<string> value, Action<List<string>?> assign)
	{
		if (Area is null)
			return Task.CompletedTask;

		// An empty list and a null list are different instructions: null means "discover", [] would mean "this
		// room has no lights", which is never what clearing the last chip meant.
		assign(value.Count == 0 ? null : [.. value]);

		return MarkDirty();
	}

	/// <summary>Sets a gate's polarity, storing <c>null</c> and not <c>false</c> for the ordinary reading.</summary>
	// Null keeps the key out of the file for every room that never inverted one, the same rule SetList follows.
	private Task SetInvert(bool inverted, Action<bool?> assign)
	{
		if (Area is null)
			return Task.CompletedTask;

		assign(inverted ? true : null);

		return MarkDirty();
	}

	private void OnRenderThread(Action work) => _ = _dispatch(() =>
	{
		work();
		Changed?.Invoke();
	});

	/// <inheritdoc/>
	/// <remarks>An edit still inside its quiet window is written on the way out. The flush stays inside the try: a
	/// Dispose that throws part way through is not retried, so an unguarded throw strands the one-second clock and
	/// the snapshot subscription for the life of the process, per browser tab.</remarks>
	public void Dispose()
	{
		try
		{
			_saveCts?.Cancel();

			if (Dirty)
				Commit();
		}
		finally
		{
			_saveCts?.Dispose();
			_subscription?.Dispose();
			_ticker?.Dispose();
		}
	}
}
