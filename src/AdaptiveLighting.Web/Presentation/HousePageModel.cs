using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Hosting;
using AdaptiveLighting.Web.Components;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>The house page's four sections, one shown at a time.</summary>
public enum HouseSection
{
	Areas,
	Schedule,
	HouseModes,
	House
}

/// <summary>Everything the house page knows and does: its state, its document, its edits, its save and every
/// sentence it shows.</summary>
/// <remarks>A plain class, not a component: a second design puts its own markup over this same model, so a rule
/// left in a razor file is a rule that design writes again. The interface keeps markup, styling and the wiring
/// of events to these methods, and nothing else. No member here names a stylesheet class or a custom
/// property.</remarks>
public sealed class HousePageModel : IPageClock, IDisposable
{
	/// <summary>How long a success confirmation stays on screen.</summary>
	private static readonly TimeSpan ConfirmationLife = TimeSpan.FromSeconds(3.5);

	// SentenceView has no default for this: only the page knows whether a pick waits behind a save bar.
	public const string TokenNote = "Applies when you save. Discard changes puts it back.";

	/// <summary>What the browser asks before leaving with an unsaved edit.</summary>
	public const string LeaveQuestion = "Leave this page? Changes that are not saved will be lost.";

	// The same value the layout's feedback link reports, never a second derivation of it.
	public static string Version { get; } = AppVersion.Text;

	/// <summary>The sections, in the order the interface offers them.</summary>
	public static IReadOnlyList<HouseSection> Sections { get; } =
		[HouseSection.Areas, HouseSection.Schedule, HouseSection.HouseModes, HouseSection.House];

	private readonly LightingEngineHost _engine;
	private readonly HaCatalog _catalog;
	private readonly ConfigLocation _location;
	private readonly HomeLocation _homeGeo;
	private readonly AreaSnapshotCache _cache;
	private readonly ILogger _logger;

	private AdaptiveLightingConfig _config = new();

	// The whole document as this page read it. This page edits a whole draft, so what it must not overwrite is
	// the whole document.
	private string _documentStamp = "";

	// Compared against a serialised snapshot, never set by a flag each edit path remembers: a dozen paths change
	// this document, and one that forgot the flag would leave an edit with no way to save it.
	private bool _dirty;
	private string? _cleanDocument;

	// The confirmation and the beat that clears it. The beat runs only while there is something to clear: this
	// page has nothing else that moves on its own, so a standing clock would redraw for nothing.
	private TransientMessage _confirmation = TransientMessage.None;
	private IDisposable? _clock;

	// Read once per page load, not once per area: the lists are identical for every area.
	private IReadOnlyList<AreaOption> _areas = [];
	private IReadOnlyList<EntityOption> _motionSensors = [];

	// Starts as "cannot compare", which is what a page that has read nothing yet knows.
	private HouseModeOptionsDiff _helperDiff = new(false, [], []);

	private readonly Dictionary<AreaConfig, string> _roomNames = [];

	// Per room, found through AreaSnapshotCache.Find. Read from the cache, never subscribed to: a live re-render
	// mid-edit costs the thing being edited.
	private IReadOnlyDictionary<AreaConfig, AreaState> _liveStates = new Dictionary<AreaConfig, AreaState>();

	// _unsaved holds rooms added since the last save. They cannot be opened: the room page reads the document
	// from disk, not from here.
	private readonly HashSet<string> _unsaved = new(StringComparer.Ordinal);

	// Per visit, never persisted: advice about the house as it is right now.
	private readonly Dictionary<string, SwitchOnNote> _switchOnNotes = new(StringComparer.Ordinal);
	private readonly HashSet<string> _switchOnRead = new(StringComparer.Ordinal);

	private readonly HashSet<string> _openedGroups = new(StringComparer.Ordinal);

	private EntityLookup? _lookup;

	public HousePageModel(
		LightingEngineHost engine,
		HaCatalog catalog,
		ConfigLocation location,
		HomeLocation homeGeo,
		AreaSnapshotCache cache,
		ILogger<HousePageModel> logger)
	{
		_engine = engine ?? throw new ArgumentNullException(nameof(engine));
		_catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
		_location = location ?? throw new ArgumentNullException(nameof(location));
		_homeGeo = homeGeo ?? throw new ArgumentNullException(nameof(homeGeo));
		_cache = cache ?? throw new ArgumentNullException(nameof(cache));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>Raised whenever anything the interface draws has moved.</summary>
	/// <remarks>Raised from whatever thread did the moving, so a design marshals it itself.</remarks>
	public event Action? Changed;

	/// <inheritdoc/>
	public event Action? Tick;

	/// <inheritdoc/>
	public DateTimeOffset Now { get; private set; } = DateTimeOffset.Now;

	/// <summary>Reads the document and opens the section a <c>?section=</c> value names.</summary>
	public void Start(string? sectionQuery)
	{
		Reload();

		if (Resolve(sectionQuery) is { } requested)
			Section = requested;
	}

	// ---- the document, for the shared controls that draw configuration objects ----

	/// <summary>The draft this page edits. Handed to the controls that draw a configuration object; every change
	/// to it goes through a method here.</summary>
	public AdaptiveLightingConfig Config => _config;

	public GlobalConfig Global => _config.Global;

	public AreaSettings Defaults => _config.Defaults;

	public List<TimePeriodConfig> Periods => _config.Periods;

	public List<AreaConfig> Areas => _config.Areas;

	public HouseModeConfig? HouseMode => _config.Global.HouseMode;

	// ---- load, save and what the save bar says ----

	public string? LoadError { get; private set; }

	public ValidationResult? Validation { get; private set; }

	public SaveResult? Result { get; private set; }

	/// <summary>The success confirmation and the moment it stops being made.</summary>
	/// <remarks>Read against <see cref="Now"/> where it is drawn, so it clears itself with no timer behind it.</remarks>
	public TransientMessage SaveConfirmation => _confirmation;

	public bool IsBusy { get; private set; }

	/// <summary>Whether the save bar has anything to say: an unsaved edit, an unresolved refusal, or a
	/// confirmation still on screen.</summary>
	/// <remarks><see cref="UserIdInput"/> is checked separately: it is not part of the document until the save
	/// applies it, so a typed id leaves the document clean and would have no way to be written.</remarks>
	public bool ShowSaveBar =>
		_dirty
		|| !string.IsNullOrWhiteSpace(UserIdInput)
		|| _confirmation.IsShownAt(Now)
		|| Result is { Written: false };

	public bool HasUnsavedEdits => _dirty || !string.IsNullOrWhiteSpace(UserIdInput);

	/// <summary>How many problems the save bar reports, or <c>null</c> when it reports none.</summary>
	public string? SaveBarProblems => Validation is { IsValid: false } validation
		? $"{validation.Errors.Count} {(validation.Errors.Count == 1 ? "problem" : "problems")} to fix before saving."
		: null;

	/// <summary>Whether the validator has anything at all to show above the section rail.</summary>
	public bool HasValidationNotice => Validation is { } validation
		&& (validation.Errors.Count > 0 || validation.AreaErrors.Count > 0 || validation.Warnings.Count > 0);

	public void Reload()
	{
		Result = null;
		UserIdInput = null;
		CloseSetup();
		CloseAdd();
		_unsaved.Clear();
		SetupNote = null;

		try
		{
			_config = _engine.Store.Load();
			_documentStamp = ConfigStamp.OfDocument(_config);
			LoadError = null;
		}
		catch (LightingConfigException exception)
		{
			LoadError = exception.Message;
			Notify();

			return;
		}

		LoadCatalog();
		Revalidate();
		MarkClean();

		// After the catalog, which it needs; after MarkClean, because raising a note must not arm the save bar.
		RaiseStandingNotes();
		Notify();
	}

	public void StartFromScratch()
	{
		_config = new AdaptiveLightingConfig();

		// Nothing was read, so there is nothing this draft could revert. Only reachable over a file that would
		// not parse, and this save is the way out of that.
		_documentStamp = "";
		LoadError = null;
		LoadCatalog();
		Revalidate();
	}

	public void Save()
	{
		IsBusy = true;

		// Drop any lingering success toast, so a refused save is never shown next to a stale confirmation.
		ClearConfirmation();

		try
		{
			// Only if something was typed: an untouched box must leave the stored value alone, since the page
			// never had it to give back.
			if (!string.IsNullOrWhiteSpace(UserIdInput))
			{
				_config.Global.NetDaemonUserId = UserIdInput.Trim();
				UserIdInput = null;
			}

			// Before anything is written: this page sends the whole document, so a file that has moved on since
			// it was read would be overwritten and not edited.
			if (DocumentWrite.ChangedUnderneath(_engine.Store, _documentStamp))
			{
				Result = DocumentWrite.Conflict(Validation);

				return;
			}

			// Read before the save, so the confirmation below can check the file's timestamp actually advanced.
			DateTimeOffset? writtenBefore = _engine.Store.LastWrittenUtc;

			Result = _engine.Save(_config);
			Validation = Result.Validation;

			if (Result.Written)
			{
				DateTimeOffset? writtenAfter = _engine.Store.LastWrittenUtc;
				string savedAt = writtenAfter is { } utc ? utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture) : "now";
				bool advanced = writtenAfter is { } after && (writtenBefore is not { } before || after >= before);

				// Result is cleared so the persistent note never restates the toast. A refused save keeps it.
				ShowSaveConfirmation(advanced
					? $"Saved ✓  Written to {System.IO.Path.GetFileName(_engine.Store.FilePath)} at {savedAt}."
					: "Saved, but the file timestamp did not change — check the log.");
				Result = null;

				// Everything on disk now has a page to open.
				_unsaved.Clear();
				SetupNote = null;

				// Re-read so the page shows what is on disk, formatting and all.
				_config = _engine.Store.Load();
				_documentStamp = ConfigStamp.OfDocument(_config);
				LoadCatalog();
				Revalidate();
				MarkClean();

				// Same order and same reason as Reload: after the catalog, after MarkClean.
				RaiseStandingNotes();
			}
		}
		catch (LightingConfigException exception)
		{
			LoadError = exception.Message;
		}
		finally
		{
			IsBusy = false;
			Notify();
		}
	}

	/// <summary>Shows the success confirmation and starts the beat that clears it.</summary>
	/// <remarks>The first beat falls exactly where the confirmation runs out, so it stays up for the same time
	/// however long the save took. A later save restarts the beat, so rapid saves cannot clear one another.</remarks>
	private void ShowSaveConfirmation(string message)
	{
		Now = DateTimeOffset.Now;
		_confirmation = TransientMessage.For(message, Now, ConfirmationLife);

		_clock?.Dispose();
		_clock = Observable.Timer(ConfirmationLife, TimeSpan.FromSeconds(1))
			.SubscribeSafe(_ => Beat(), _logger);
	}

	private void Beat()
	{
		Now = DateTimeOffset.Now;

		// Nothing else on this page moves on its own, so the beat stops with the message it was started for.
		if (!_confirmation.IsShownAt(Now))
		{
			_confirmation = TransientMessage.None;
			_clock?.Dispose();
			_clock = null;
			Notify();
		}

		Tick?.Invoke();
	}

	private void ClearConfirmation()
	{
		_confirmation = TransientMessage.None;
		_clock?.Dispose();
		_clock = null;
	}

	/// <inheritdoc/>
	public void Dispose() => ClearConfirmation();

	private void Notify() => Changed?.Invoke();

	/// <summary>Records the document as matching the file, after a load and after a save that wrote.</summary>
	private void MarkClean()
	{
		_cleanDocument = Snapshot();
		_dirty = false;
	}

	/// <summary>The document as text, or <c>null</c> when it cannot be serialised.</summary>
	/// <remarks>Null counts as changed, so the bar shows and the save reports the real error.</remarks>
	private string? Snapshot()
	{
		try
		{
			return LightingConfigDocument.Serialize(_config);
		}
		catch (LightingConfigException)
		{
			return null;
		}
	}

	/// <summary>Takes the document's measure again after an edit: dirty, valid, and every sentence built from
	/// it.</summary>
	public void Revalidate()
	{
		string? current = Snapshot();
		_dirty = current is null || _cleanDocument is null || !string.Equals(current, _cleanDocument, StringComparison.Ordinal);

		Validation = _engine.Validate(_config);
		BlendSentence = [HouseSentences.Blend(_config.Global)];
		AwaySentence = [HouseSentences.AwayDebounce(_config.Global)];
		ModeLines = HouseSentences.Modes(_config.Global.HouseMode, _config.Periods);

		// Recomputed here, not at each place that could move it: they all end in a Revalidate, and a comparison
		// refreshed at only some of them outlives what it was calling for.
		_helperDiff = HouseModeSync.Compare(_config.Global.HouseMode, LiveSelectOptions);
		AddableAreas = HouseView.Unconfigured(_areas, _config.Areas);
		RegroupAreas();
		RefreshLiveStates();
		Notify();
	}

	private void LoadCatalog()
	{
		// The catalog caches discovery per load, so a re-read document needs a re-read registry.
		_catalog.Invalidate();

		// Areas are labelled with what discovery resolves in each, so the conventions come from the document
		// being edited, not from a default.
		_areas = _catalog.Areas(_config.Global);
		LabelOptions = _catalog.LabelOptions();
		SunEntities = _catalog.EntitiesInDomains("sun");
		PersonOptions = _catalog.EntitiesInDomains("person", "device_tracker");
		Switches = _catalog.EntitiesInDomains("input_boolean", "switch");
		InputSelects = _catalog.EntitiesInDomains("input_select");
		Scenes = _catalog.EntitiesInDomains("scene");
		DateTimes = _catalog.DateTimeEntities();
		ActivationEntities = _catalog.EntitiesInDomains("input_boolean", "switch", "binary_sensor");
		LiveSelectOptions = _catalog.SelectOptionsOf(_config.Global.HouseMode?.Entity);
		ActiveModeValue = _catalog.CurrentStateOf(_config.Global.HouseMode?.Entity);
		HouseModeOptionValues = ModeOptionValues();
		LivePeriodSelectOptions = _catalog.SelectOptionsOf(_config.Global.PeriodSelect?.EntityId);
		ActivePeriodValue = _catalog.CurrentStateOf(_config.Global.PeriodSelect?.EntityId);
		HomeCoordinates = _homeGeo.TryRead();
		SensorDeviceClasses = _catalog.SensorDeviceClasses();

		// Offer what discovery would accept, so an explicit list corrects discovery instead of listing every
		// binary sensor in the house.
		_motionSensors = _catalog.EntitiesWithDeviceClass("binary_sensor", [.. _config.Global.EffectiveMotionDeviceClasses]);
		LuxSensors = _catalog.EntitiesWithDeviceClass("sensor", [_config.Global.IlluminanceDeviceClass]);
		HomeAssistantIsAnswering = _catalog.IsHomeAssistantResponding(_config.Global);

		// The presence-reset override picker: discovered motion sensors plus person entities.
		PresenceEntities = [.. _motionSensors.Concat(_catalog.EntitiesInDomains("person"))];

		// Union with the engine's defaults, so the built-in classes stay tickable in a house that reports none
		// of them today.
		MotionDeviceClassChoices =
		[
			.. _catalog.BinarySensorDeviceClasses()
				.Concat(GlobalConfig.DefaultMotionDeviceClasses)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(deviceClass => deviceClass, StringComparer.Ordinal)
		];
	}

	// ---- sections ----

	/// <summary>Which section is open.</summary>
	public HouseSection Section { get; private set; } = HouseSection.Areas;

	public bool IsActive(HouseSection section) => Section == section;

	public void Show(HouseSection section)
	{
		Section = section;
		Notify();
	}

	/// <summary>What a section is called.</summary>
	public static string TitleOf(HouseSection section) => section switch
	{
		HouseSection.Areas => "Areas",
		HouseSection.Schedule => "Schedule",
		HouseSection.HouseModes => "House modes",
		_ => "House"
	};

	/// <summary>How many things a section holds, or <c>null</c> when it counts nothing.</summary>
	public int? CountOf(HouseSection section) => section switch
	{
		HouseSection.Areas => _config.Areas.Count,
		HouseSection.Schedule => _config.Periods.Count,
		HouseSection.HouseModes => HouseModeOptionValues.Count > 0 ? HouseModeOptionValues.Count : null,
		_ => null
	};

	/// <summary>The section a <c>?section=</c> value names, or <c>null</c> when it names none.</summary>
	/// <remarks>Renamed sections keep answering to their old names, each alias pointing at wherever its contents
	/// are now, so old bookmarks land somewhere sensible.</remarks>
	public static HouseSection? Resolve(string? query)
	{
		if (string.IsNullOrWhiteSpace(query))
			return null;

		if (Enum.TryParse(query, ignoreCase: true, out HouseSection requested) && Enum.IsDefined(requested))
			return requested;

		return query.Trim().ToLowerInvariant() switch
		{
			"periods" => HouseSection.Schedule,
			"defaults" => HouseSection.House,
			"rooms" => HouseSection.Areas,
			"advanced" => HouseSection.House,
			"people" => HouseSection.House,
			_ => null
		};
	}

	// ---- what the pickers are drawn from ----

	public bool HomeAssistantIsAnswering { get; private set; }

	public IReadOnlyList<AreaOption> AddableAreas { get; private set; } = [];

	public IReadOnlyList<EntityOption> LuxSensors { get; private set; } = [];

	public IReadOnlyList<EntityOption> SunEntities { get; private set; } = [];

	public IReadOnlyList<EntityOption> PersonOptions { get; private set; } = [];

	public IReadOnlyList<EntityOption> Switches { get; private set; } = [];

	public IReadOnlyList<EntityOption> InputSelects { get; private set; } = [];

	public IReadOnlyList<EntityOption> Scenes { get; private set; } = [];

	public IReadOnlyList<EntityOption> PresenceEntities { get; private set; } = [];

	public IReadOnlyList<EntityOption> DateTimes { get; private set; } = [];

	public IReadOnlyList<EntityOption> ActivationEntities { get; private set; } = [];

	public IReadOnlyList<LabelOption> LabelOptions { get; private set; } = [];

	public IReadOnlyList<string> SensorDeviceClasses { get; private set; } = [];

	public IReadOnlyList<string> MotionDeviceClassChoices { get; private set; } = [];

	public (double Latitude, double Longitude)? HomeCoordinates { get; private set; }

	/// <summary>What every picker on this page is told about an id it did not offer.</summary>
	// Built once: a fresh pair each render would hand every picker a changed parameter on every tick.
	public EntityLookup Lookup => _lookup ??= new EntityLookup(_catalog.FriendlyNameOrId, _catalog.Knows);

	/// <summary>The empty-picker note: this page knows why a list is empty and the component does not, so a lost
	/// connection is not reported as an empty domain.</summary>
	public string EmptyNote(string noneYet) => HomeAssistantIsAnswering
		? noneYet
		: "Waiting for Home Assistant — type an id for now.";

	/// <summary>Why the add-a-room picker has nothing to offer: no connection, or no area left unclaimed.</summary>
	public string AddEmptyNote => !HomeAssistantIsAnswering
		? "Waiting for Home Assistant — type an area id for now."
		: "Every area Home Assistant knows already has a room. Type an id if one is coming.";

	// ---- the rooms list ----

	public IReadOnlyList<FloorGroup<AreaConfig>> AreaGroups { get; private set; } = [];

	public bool HasNoRooms => _config.Areas.Count == 0;

	/// <summary>Groups the rooms by floor through the helper the board shares, so the two pages cannot
	/// disagree.</summary>
	/// <remarks>A registry that has not connected throws instead of answering, so GroupOrFlat degrades to one
	/// unnamed group and the list renders flat instead of not at all.</remarks>
	private void RegroupAreas()
	{
		AreaGroups = FloorGrouping.GroupOrFlat(_config.Areas, area => area.AreaId, _catalog.AreaRegistry);

		// Names come from the same registry read as the floors, and go stale at the same moment: the next edit.
		_roomNames.Clear();

		foreach (AreaConfig area in _config.Areas)
			_roomNames[area] = HouseView.DisplayName(area, _catalog.AreaRegistry);
	}

	/// <summary>The live state of each room the cache has heard from, taken with the room names it is matched
	/// by.</summary>
	private void RefreshLiveStates()
	{
		Dictionary<AreaConfig, AreaState> states = [];

		foreach (AreaConfig area in _config.Areas)
		{
			if (_cache.Find(area.AreaId, RoomName(area)) is { } snapshot)
				states[area] = snapshot.State;
		}

		_liveStates = states;
	}

	/// <summary>What a room is doing right now, or <c>null</c> when nothing has been heard about it.</summary>
	public AreaState? LiveStateOf(AreaConfig area) =>
		_liveStates.TryGetValue(area, out AreaState state) ? state : null;

	/// <summary>Whether a room's lights are managed, the house's answer standing in where the room has none.</summary>
	public bool IsEnabled(AreaConfig area) => AreaView.IsEnabled(area, _config.Defaults);

	/// <summary>A room's own page, or <c>null</c> when there is nothing to open yet.</summary>
	/// <remarks>A room with no area has no route, and one added since the last save has none yet: the room page
	/// loads from disk, so the link would land on "no such room".</remarks>
	public string? RoomLink(AreaConfig area) =>
		area.AreaId is { Length: > 0 } areaId && !_unsaved.Contains(areaId) ? HouseView.RoomHref(areaId) : null;

	/// <summary>What a room is called in this list, matching the board, the log and the room's own page.</summary>
	/// <remarks>Read from the map built with the document and never per render: each row asks three times, this
	/// page re-renders on every keystroke, and the registry throws until Home Assistant has connected. A room
	/// added since the last rebuild falls back to the document alone, one render behind.</remarks>
	public string RoomName(AreaConfig area) =>
		_roomNames.TryGetValue(area, out string? name) ? name : HouseView.DisplayName(area, null);

	/// <summary>The one-line summary of what a room is made of.</summary>
	public static string RoomSummary(AreaConfig area) => HouseView.RoomSummary(area);

	/// <summary>What the room's own switch says it does, for a pointer.</summary>
	public static string ToggleTitle(bool enabled) => enabled
		? "On — the lights in this room are managed. Switch off to leave them alone."
		: "Off — this room is watched but its lights are never changed.";

	/// <summary>What a screen reader calls the room's switch.</summary>
	public string ToggleLabel(AreaConfig area) => $"Automatic lighting in {RoomName(area)}";

	/// <summary>The line naming how many rooms are switched off, or <c>null</c> when none are.</summary>
	public string? SwitchedOffLine => HouseView.SwitchedOffLine(_config.Areas, _config.Defaults) is { Length: > 0 } line
		? line
		: null;

	/// <summary>Whether a floor gets a heading of its own: only where there is more than one floor to tell
	/// apart.</summary>
	public bool ShowsFloorHeader(FloorGroup<AreaConfig> floor)
	{
		ArgumentNullException.ThrowIfNull(floor);

		return AreaView.ShowsHeader(AreaGroups.Count, floor.Floor);
	}

	public static string FloorTitle(FloorGroup<AreaConfig> floor)
	{
		ArgumentNullException.ThrowIfNull(floor);

		return AreaView.FloorTitle(floor.Floor);
	}

	/// <summary>What the floor's bulk action offers: switching a floor off once every room on it is already
	/// on.</summary>
	public string FloorAction(FloorGroup<AreaConfig> floor)
	{
		ArgumentNullException.ThrowIfNull(floor);

		return AreaView.AllEnabled(floor.Items, _config.Defaults) ? "Switch off this floor" : "Switch on this floor";
	}

	/// <summary>Switches every room on a floor, as an edit: the save bar arms, nothing reaches disk.</summary>
	public void SwitchFloor(FloorGroup<AreaConfig> floor)
	{
		ArgumentNullException.ThrowIfNull(floor);

		AreaView.SwitchAll(floor.Items, !AreaView.AllEnabled(floor.Items, _config.Defaults));
		Revalidate();
	}

	/// <summary>Flips a room's power switch, writing an explicit true or false and never null.</summary>
	/// <remarks>An edit like any other: it arms the save bar, it does not save.</remarks>
	public void ToggleEnabled(AreaConfig area)
	{
		bool switchingOn = !IsEnabled(area);
		area.Enabled = switchingOn;
		Revalidate();

		if (switchingOn)
			RaiseSwitchOnNote(area);
		else
			_switchOnNotes.Remove(KeyOf(area));
	}

	/// <summary>Rebuilds every standing note: the note for a room already on, when what it commands looks
	/// wrong.</summary>
	/// <remarks>The bar is higher here than on the press, where any room commanding more than one light raises a
	/// note: only <see cref="SwitchOnNote.IsWarning"/> earns a panel. It clears first, and every path that
	/// re-reads the document must call it, since a note held across a re-read is a claim about a file that is no
	/// longer open. The read-and-dismissed set survives the rebuild.</remarks>
	private void RaiseStandingNotes()
	{
		_switchOnNotes.Clear();

		foreach (AreaConfig area in _config.Areas)
		{
			if (!IsEnabled(area))
				continue;

			string key = KeyOf(area);

			if (_switchOnRead.Contains(key))
				continue;

			if (NoteFor(area) is { IsWarning: true } note)
				_switchOnNotes[key] = note;
		}
	}

	/// <summary>What this room's switch has to say for itself, or <c>null</c> when there is nothing to
	/// say.</summary>
	/// <remarks>One place: the standing pass and the press ask the same question and differ only in which
	/// answers they surface. A second copy of the argument list drifts on the first change to how lights
	/// resolve.</remarks>
	private SwitchOnNote? NoteFor(AreaConfig area) => SwitchOnWarning.For(
		RoomName(area),
		_catalog.LightsIn(area, _config.Defaults, _config.Global),
		_config.Global.IncludeLabel);

	/// <summary>Works out what a room now commands and, when there is something to say, says it under the room's
	/// row.</summary>
	/// <remarks>Advisory and after the fact: the switch has already taken effect, and a room switched on by
	/// "Switch on this floor" raises nothing. Computed on the press and never during render, since it runs the
	/// engine's resolver over the room, reading a state per candidate entity.</remarks>
	private void RaiseSwitchOnNote(AreaConfig area)
	{
		string key = KeyOf(area);

		if (_switchOnRead.Contains(key))
			return;

		if (NoteFor(area) is { } note)
			_switchOnNotes[key] = note;
	}

	public SwitchOnNote? SwitchOnNoteFor(AreaConfig area) => _switchOnNotes.GetValueOrDefault(KeyOf(area));

	/// <summary>Puts one room's note away for the rest of this visit.</summary>
	public void DismissSwitchOnNote(AreaConfig area)
	{
		string key = KeyOf(area);

		_switchOnNotes.Remove(key);
		_switchOnRead.Add(key);
		Notify();
	}

	/// <summary>How a room is keyed while the page is open: its area id, or its position for a row that names
	/// none.</summary>
	/// <remarks>A positional key moves when a row is removed. See <see cref="ForgetPositionalNotes"/>.</remarks>
	private string KeyOf(AreaConfig area) =>
		area.AreaId is { Length: > 0 } areaId ? areaId : $"#{_config.Areas.IndexOf(area)}";

	// ---- adding, adopting and removing a room ----

	public bool AddingRoom { get; private set; }

	public string? AddAreaId { get; private set; }

	public string? SetupNote { get; private set; }

	public void ToggleAddRoom()
	{
		if (AddingRoom)
			CloseAdd();
		else
		{
			SetupNote = null;
			AddingRoom = true;
		}

		Notify();
	}

	private void CloseAdd()
	{
		AddingRoom = false;
		AddAreaId = null;
	}

	/// <summary>Adds a room for one Home Assistant area.</summary>
	/// <remarks>Switched off explicitly, as discovery proposes rooms. The area's name is not copied in: names
	/// resolve from the registry wherever they are shown, so writing one would freeze the room at today's name.
	/// What is written is an area id and a switch, nothing else.</remarks>
	public void AddRoom(string? areaId)
	{
		if (areaId is not { Length: > 0 })
			return;

		CloseAdd();

		_config.Areas.Add(new AreaConfig { AreaId = areaId, Enabled = false });
		_unsaved.Add(areaId);
		SetupNote = $"{AreaName(areaId)} added, switched off. Save and apply, then open it to set it up.";

		Revalidate();
	}

	/// <summary>Gives a room with no Home Assistant area one, which is also what gives it a page.</summary>
	/// <remarks>Marked unsaved like a newly added room: the room page reads from disk, so the link works only
	/// once the file agrees. The area's name is not copied in, as on <see cref="AddRoom"/>.</remarks>
	public void AdoptArea(AreaConfig area, string? areaId)
	{
		ArgumentNullException.ThrowIfNull(area);

		if (areaId is not { Length: > 0 })
			return;

		area.AreaId = areaId;
		_unsaved.Add(areaId);
		Revalidate();
	}

	/// <summary>Takes a room out of the document, and with it every note keyed by where a room sits.</summary>
	/// <remarks>The removal moves every row after it, and <see cref="KeyOf"/> falls back to a row's position, so
	/// the survivor sliding into the removed index would inherit its note and its dismissal.</remarks>
	public void RemoveArea(AreaConfig area)
	{
		_config.Areas.Remove(area);
		ForgetPositionalNotes();
		Revalidate();
	}

	/// <summary>Drops every note and dismissal keyed by a row's position instead of an area id.</summary>
	private void ForgetPositionalNotes()
	{
		foreach (string key in _switchOnNotes.Keys.Where(IsPositional).ToList())
			_switchOnNotes.Remove(key);

		_switchOnRead.RemoveWhere(IsPositional);
	}

	private static bool IsPositional(string key) => key.StartsWith('#');

	/// <summary>The name Home Assistant shows for an area id, for rooms the document does not have yet.</summary>
	public string AreaName(string areaId) =>
		_areas.FirstOrDefault(area => string.Equals(area.Id, areaId, StringComparison.Ordinal))?.Name ?? areaId;

	// ---- setting rooms up again ----

	/// <summary>Null means the re-setup panel is closed; otherwise these are the rooms it opens with
	/// ticked.</summary>
	public IReadOnlyCollection<string>? SetupScope { get; private set; }

	/// <summary>Whether setting rooms up again can be offered: it rebuilds from the registry, so one that is not
	/// answering would produce an empty plan.</summary>
	public bool CanSetUpAgain => HomeAssistantIsAnswering && SetupScope is null;

	public void OpenSetupForEveryRoom()
	{
		SetupNote = null;
		CloseAdd();

		// Opens with nothing ticked, so the destructive answer is not the default one; the panel's Include all
		// is how every room is chosen.
		SetupScope = [];
		Notify();
	}

	public void CloseSetup()
	{
		SetupScope = null;
		Notify();
	}

	/// <summary>Plans a setup run for the panel; nothing changes until the plan is confirmed.</summary>
	public SetupPlan PlanSetup(IReadOnlyCollection<string> scope) => _catalog.PlanSetup(_config, scope);

	/// <summary>Carries out a confirmed setup run against the in-memory document, arming the save bar like any
	/// other edit.</summary>
	public void ApplySetup(SetupPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);

		AreaSetupService.Apply(_config, plan);
		SetupScope = null;

		List<string> done = [];

		if (plan.Rebuilds.Count > 0)
			done.Add($"{Rooms(plan.Rebuilds.Count)} rebuilt");

		if (plan.NewAreas.Count > 0)
		{
			done.Add($"{Rooms(plan.NewAreas.Count)} added, switched off");

			foreach (AreaConfig added in plan.NewAreas)
			{
				if (added.AreaId is { Length: > 0 } areaId)
					_unsaved.Add(areaId);
			}
		}

		SetupNote = done.Count == 0
			? "Nothing changed."
			: $"{string.Join(" · ", done)}. Nothing is written yet — press Save and apply, or Discard changes to undo.";

		Revalidate();

		static string Rooms(int count) => $"{count} {(count == 1 ? "room" : "rooms")}";
	}

	// ---- the schedule ----

	public IReadOnlyList<Sentence> BlendSentence { get; private set; } = [];

	public IReadOnlyList<string> HouseModeOptionValues { get; private set; } = [];

	/// <summary>How a period is held back by movement, as the engine answers it; <c>null</c> when nothing
	/// is.</summary>
	public Func<string, DateOnly, PeriodHold>? PeriodHoldRule => Schedule.PeriodHoldRule(_engine);

	// Read separately from the house mode's: two different helpers, and comparing one document's option strings
	// against the other's list reports renames that never happened.
	public IReadOnlyList<string> LivePeriodSelectOptions { get; private set; } = [];

	public string? ActivePeriodValue { get; private set; }

	public bool PeriodSelectIsAvailable => _catalog.Knows(_config.Global.PeriodSelect?.EntityId);

	/// <summary>Whether a dropdown helper carries the time of day at all.</summary>
	public bool PeriodSelectConnected => _config.Global.PeriodSelect?.EntityId is not null;

	/// <summary>The period-select fold's header line, carrying the direction, which is what decides whether the
	/// start times in the table above mean anything.</summary>
	public string PeriodSelectSummary
	{
		get
		{
			if (_config.Global.PeriodSelect?.EntityId is not { } entity)
				return "not connected — the schedule's start times decide the time of day";

			int mapped = _config.Global.PeriodSelect.Options.Count(option => !option.IsEmpty);
			string rows = mapped == 1 ? "1 option mapped" : $"{mapped} options mapped";

			return Schedule.HomeAssistantDecides(_config.Global)
				? $"{entity} decides the time of day · {rows}"
				: $"{entity} is kept in step with the schedule · {rows}";
		}
	}

	/// <summary>Picks the helper that carries the time of day, and re-reads what that helper offers.</summary>
	/// <remarks>The panel writes every other <c>PeriodSelect</c> field itself; this one is here because it
	/// changes what the panel is drawn from. The mappings are not rebuilt from the new helper, where the
	/// house-mode picker does adopt: a period mapping is one string pointing at another, so adopting would
	/// invent mappings nobody chose.</remarks>
	public void SetPeriodSelectEntity(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			// Leave the object, the mappings may still be wanted; ConfigNormalizer drops it on save if empty.
			if (_config.Global.PeriodSelect is { } existing)
				existing.Entity = null;
		}
		else
		{
			// Lazily created the first time a helper is picked, so a never-adopted document acquires no block.
			(_config.Global.PeriodSelect ??= new PeriodSelectConfig()).Entity = value;
		}

		LivePeriodSelectOptions = _catalog.SelectOptionsOf(_config.Global.PeriodSelect?.EntityId);
		ActivePeriodValue = _catalog.CurrentStateOf(_config.Global.PeriodSelect?.EntityId);
		Revalidate();
	}

	/// <summary>Applies an edit from a house-wide sentence: the schedule's blend and the away debounce.</summary>
	/// <remarks>Blending is one value across two properties. Zero minutes means the lights step at the boundary,
	/// which is what <c>SmoothTransitions</c> off already meant, and the minute count is left alone when it is
	/// switched off so turning it back on restores what it was.</remarks>
	public void OnGlobalEdited(SentenceEdit edit)
	{
		ArgumentNullException.ThrowIfNull(edit);

		switch (edit.Key)
		{
			case nameof(GlobalConfig.BlendMinutes):
				int minutes = edit.Integer;
				_config.Global.SmoothTransitions = minutes > 0;

				if (minutes > 0)
					_config.Global.BlendMinutes = minutes;

				break;

			case nameof(GlobalConfig.AwayDebounceMinutes):
				_config.Global.AwayDebounceMinutes = edit.Minutes;
				break;

			default:
				_logger.LogWarning("A house sentence offered an edit to {Key}, which the House tab does not know how to apply.", edit.Key);

				return;
		}

		Revalidate();
	}

	// ---- house modes ----

	public IReadOnlyList<string> LiveSelectOptions { get; private set; } = [];

	public string? ActiveModeValue { get; private set; }

	public IReadOnlyList<ModeLine> ModeLines { get; private set; } = [];

	public HouseModeOptionsDiff HelperDiff => _helperDiff;

	/// <summary>What the helper and the document disagree about, or <c>null</c> when they do not.</summary>
	public string? HelperDriftText => HouseModeSync.Drift(_helperDiff);

	/// <summary>The helper the house mode lives in, as the document names it.</summary>
	public string? HouseModeEntity => _config.Global.HouseMode?.Entity;

	/// <summary>The helper's id, or an empty string, for a sentence that reads better with the id leading.</summary>
	public string HouseModeEntityId => _config.Global.HouseMode?.EntityId ?? "";

	public bool HasHouseModeEntity => _config.Global.HouseMode?.Entity is { Length: > 0 };

	public bool HouseModeEntityIsAvailable => _catalog.Knows(_config.Global.HouseMode?.EntityId);

	/// <summary>Whether the document hands the mode to Home Assistant.</summary>
	public bool HouseModeAuthorityIsHomeAssistant =>
		_config.Global.HouseMode?.Authority == HouseModeAuthority.HomeAssistant;

	/// <summary>Whether Home Assistant owns the house mode, so the engine's own ways of setting it stand
	/// down.</summary>
	public bool HouseModeIsHomeAssistants => ModeAuthority.HomeAssistantDecides(_config.Global);

	/// <summary>What the document still configures that the authority has stood down.</summary>
	public DormantModeRules DormantRules => ModeAuthority.Dormant(_config.Global, _config.Periods);

	/// <summary>Whether the house is standing on this mode right now, as Home Assistant reports it.</summary>
	public bool IsActiveMode(ModeLine mode)
	{
		ArgumentNullException.ThrowIfNull(mode);

		return ActiveModeValue is { Length: > 0 } active && active.SameName(mode.Name);
	}

	private string HouseModeAuthorityNote => HouseModeIsHomeAssistants
		? "The engine reads this dropdown and never writes it. It still applies what each option means — the scene, "
			+ "the sleep dimming, the sweep — but nothing here moves the house between them."
		: "The engine may set this dropdown itself, from a period, a switch you name, or a quiet house, and applies "
			+ "what the option means either way. Moving it by hand still works.";

	private string HouseModeUnavailableNote => HouseModeIsHomeAssistants
		? "Until it appears, the house stays in everyday lighting and nothing can change the mode. Check the id for a typo."
		: "Until it appears, the engine has nowhere to write the mode, so it stays in everyday lighting. Check the id for a typo.";

	/// <summary>Everything the shared panel says here, rebuilt as the direction in force changes.</summary>
	/// <remarks>The three help bodies are markup and belong to whichever design is drawing the panel, so they
	/// are left unset and filled in by the interface.</remarks>
	public SelectAuthorityCopy HouseModePanelCopy => new()
	{
		Label = "House mode",
		NoneLabel = "(none — the house has no modes)",
		EmptyNote = EmptyNote("No dropdown helpers yet — make one in Home Assistant under Settings → Devices & services → Helpers, or type an id."),
		Placeholder = "input_select.husmodus",
		AuthorityLabel = "Changes to the house mode are set by:",
		AuthorityNote = HouseModeAuthorityNote,
		UnavailableNote = HouseModeUnavailableNote
	};

	/// <summary>What the period-select fold tells a picker with nothing to offer.</summary>
	public string PeriodSelectEmptyNote =>
		EmptyNote("No dropdown helpers yet — make one in Home Assistant under Settings → Devices & services → Helpers, or type an id.");

	/// <summary>The option values a period's "sets mode" dropdown may reference: live ∪ configured,
	/// deduped.</summary>
	private IReadOnlyList<string> ModeOptionValues() =>
	[
		.. LiveSelectOptions
			.Concat(_config.Global.HouseMode?.Options.Select(option => option.Value) ?? [])
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Select(value => value.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)
	];

	public void SetHouseModeEntity(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			// Leave the object, its option tags may still be wanted; ConfigNormalizer drops it on save if empty.
			if (_config.Global.HouseMode is { } existing)
				existing.Entity = null;
		}
		else
		{
			// Lazily created the first time an entity is picked, so a never-adopted document acquires no block.
			(_config.Global.HouseMode ??= new HouseModeConfig()).Entity = value;
		}

		LiveSelectOptions = _catalog.SelectOptionsOf(_config.Global.HouseMode?.Entity);
		ActiveModeValue = _catalog.CurrentStateOf(_config.Global.HouseMode?.Entity);
		HouseModeOptionValues = ModeOptionValues();
		Revalidate();
	}

	/// <summary>Sets which side owns the house mode; every option keeps what it means in both directions.</summary>
	// Creating the block here is safe: ConfigNormalizer drops one carrying nothing but a non-default Authority.
	public void SetHouseModeAuthority(bool homeAssistant)
	{
		(_config.Global.HouseMode ??= new HouseModeConfig()).Authority =
			homeAssistant ? HouseModeAuthority.HomeAssistant : HouseModeAuthority.AdaptiveLighting;

		Revalidate();
	}

	public void OnHouseModeOptionsChanged()
	{
		HouseModeOptionValues = ModeOptionValues();
		Revalidate();
	}

	/// <summary>Applies an edit from a mode sentence; the key carries which option it belongs to.</summary>
	/// <remarks>Decoded by <see cref="HouseSentences.TryReadModeKey"/> and never parsed here, so one file owns
	/// both halves.</remarks>
	public void OnModeEdited(SentenceEdit edit)
	{
		ArgumentNullException.ThrowIfNull(edit);

		if (!HouseSentences.TryReadModeKey(edit.Key, out int index, out string property)
			|| _config.Global.HouseMode is not { } houseMode
			|| index < 0
			|| index >= houseMode.Options.Count)
		{
			_logger.LogWarning("A mode sentence offered an edit to {Key}, which names no option on this document.", edit.Key);

			return;
		}

		HouseModeOptionConfig option = houseMode.Options[index];

		switch (property)
		{
			case nameof(HouseModeOptionConfig.ActivateAfterNoMotionMinutes):
				option.ActivateAfterNoMotionMinutes = edit.Minutes;
				break;

			case nameof(HouseModeOptionConfig.ResetPresenceGraceMinutes):
				option.ResetPresenceGraceMinutes = edit.Minutes;
				break;

			default:
				_logger.LogWarning("A mode sentence offered an edit to {Property}, which the House tab does not know how to apply.", property);

				return;
		}

		Revalidate();
	}

	// ---- the house: what every room starts with ----

	public bool ShowAllRooms { get; private set; }

	public void ToggleAllRooms()
	{
		ShowAllRooms = !ShowAllRooms;
		Notify();
	}

	/// <summary>The sentences for what every room starts with.</summary>
	public IReadOnlyList<Sentence> DefaultsSentences => AreaSentences.ForDefaults(_config.Defaults);

	/// <summary>The line naming rooms the registry no longer knows.</summary>
	public string StrayLine => HouseView.StrayLine(_config.Areas, _catalog.AreaRegistry);

	/// <summary>Whether a section of the detail view is open; none are, until one is opened.</summary>
	private bool IsOpen(RoomSettingGroup group) => _openedGroups.Contains(group.Title);

	public void Toggle(RoomSettingGroup group)
	{
		ArgumentNullException.ThrowIfNull(group);

		if (!_openedGroups.Remove(group.Title))
			_openedGroups.Add(group.Title);

		Notify();
	}

	/// <summary>The all-settings folds as the panel draws them: the house's own values, with nothing above them
	/// to inherit from and so no road back.</summary>
	public AllSettingsPanel.Input HouseSettingsInput => new(
	[
		.. RoomSettings.Groups.Select(group => new AllSettingsPanel.Group(
			group,
			IsOpen(group),
			OwnCount: 0,
			[
				.. group.Settings
					.Where(setting => setting.AppliesTo(_config.Defaults))
					.Select(HouseSettingItem)
			]))
	]);

	// Read by the control that draws it and no other: Shown throws on a setting whose value is not a number.
	private AllSettingsPanel.Item HouseSettingItem(RoomSetting setting)
	{
		AllSettingsPanel.Item item = new(setting) { ValueIsOwn = true };

		return setting.Control switch
		{
			RoomControl.Steps => item with { StepValue = SleepSteps.Of(_config.Defaults).ToString() },
			RoomControl.Flag => item with { Flag = RoomSettings.Flag(null, _config.Defaults, setting.Key) },
			RoomControl.Choice => item with { ChoiceValue = RoomSettings.ChoiceName(null, _config.Defaults, setting.Key) },
			RoomControl.Entity => item with
			{
				Text = RoomSettings.Entity(null, _config.Defaults, setting.Key),
				Picker = new AllSettingsPanel.PickerField
				{
					Options = SunEntities,
					NoneLabel = "(none — the engine needs one)",
					EmptyNote = EmptyNote("No sun entity found — type an id (normally sun.sun)."),
					Placeholder = "sun.sun"
				}
			},
			_ => item with
			{
				Text = RoomSettings.Describe(null, _config.Defaults, setting.Key),
				Number = RoomSettings.Shown(null, _config.Defaults, setting.Key)
			}
		};
	}

	/// <summary>Applies one edit from the all-settings panel, by the kind of control that made it.</summary>
	public void SetHouseSetting(AllSettingsPanel.Change change)
	{
		ArgumentNullException.ThrowIfNull(change);

		switch (change.Setting.Control)
		{
			case RoomControl.Steps:
				SetHouseStep(change.Text ?? "");
				break;
			case RoomControl.Flag:
				SetHouseFlag(change.Setting.Key, change.Flag);
				break;
			case RoomControl.Choice:
				SetHouseChoice(change.Setting.Key, change.Text ?? "");
				break;
			case RoomControl.Entity:
				SetHouseEntity(change.Setting.Key, change.Text);
				break;
			default:
				SetHouseNumber(change.Setting.Key, change.Number);
				break;
		}
	}

	/// <summary>Applies one edit from the all-rooms sentences; a key it cannot apply means the sentence and the
	/// page have drifted apart, so it is logged instead of silently ignored.</summary>
	public void OnDefaultEdited(SentenceEdit edit)
	{
		ArgumentNullException.ThrowIfNull(edit);

		if (!RoomSettings.Apply(_config.Defaults, edit))
		{
			_logger.LogWarning("A sentence offered an edit to {Key}, which the House tab does not know how to apply.", edit.Key);

			return;
		}

		Revalidate();
	}

	private void SetHouseNumber(string key, double value)
	{
		RoomSettings.SetShown(_config.Defaults, key, value);
		Revalidate();
	}

	private void SetHouseFlag(string key, bool value)
	{
		RoomSettings.SetFlag(_config.Defaults, key, value);
		Revalidate();
	}

	private void SetHouseStep(string value)
	{
		if (!SleepSteps.TryParse(value, out SleepStep step))
			return;

		SleepSteps.Set(_config.Defaults, step);
		Revalidate();
	}

	private void SetHouseChoice(string key, string value)
	{
		RoomSettings.SetChoice(_config.Defaults, key, value);
		Revalidate();
	}

	private void SetHouseEntity(string key, string? value)
	{
		RoomSettings.SetEntity(_config.Defaults, key, value);
		Revalidate();
	}

	// ---- the house: finding lights and sensors ----

	/// <summary>Bulbs more than one room commands, found by the engine as it builds.</summary>
	public IReadOnlyList<SuspectLight> SharedLights => _engine.SharedLights;

	/// <summary>The headline over the shared-light warning.</summary>
	public string SharedLightsHeadline => SharedLights.Count == 1
		? "One light is commanded by more than one room."
		: $"{SharedLights.Count} lights are commanded by more than one room.";

	public string? IncludeLabel => _config.Global.IncludeLabel;

	public string? ExcludeLabel => _config.Global.ExcludeLabel;

	public string? MotionLabel => _config.Global.MotionLabel;

	public string? OutdoorLuxSensor => _config.Global.OutdoorLuxSensor;

	public void SetIncludeLabel(string? value) => SetLabel(() => _config.Global.IncludeLabel = value);

	// Exclude and motion are non-nullable in the model, so "none" is written as an empty string. The resolver
	// reads empty as no label: no entity can carry a nameless label, so nothing matches.
	public void SetExcludeLabel(string? value) => SetLabel(() => _config.Global.ExcludeLabel = value ?? string.Empty);

	public void SetMotionLabel(string? value) => SetLabel(() => _config.Global.MotionLabel = value ?? string.Empty);

	/// <summary>Writes a label field and rebuilds what depends on it.</summary>
	/// <remarks>Discovery reads these labels, so an area's counts are only true for the labels in effect when
	/// they were counted. The catalog's cache keys on the labels and notices by itself; the area list is this
	/// page's own snapshot and has to be retaken.</remarks>
	private void SetLabel(Action mutate)
	{
		mutate();
		_areas = _catalog.Areas(_config.Global);
		HomeAssistantIsAnswering = _catalog.IsHomeAssistantResponding(_config.Global);
		Revalidate();
	}

	public void SetOutdoorLux(string? value)
	{
		_config.Global.OutdoorLuxSensor = string.IsNullOrWhiteSpace(value) ? null : value;
		Revalidate();
	}

	public bool MotionDeviceClassIsOn(string deviceClass) =>
		_config.Global.MotionDeviceClasses.Contains(deviceClass, StringComparer.OrdinalIgnoreCase);

	public void ToggleMotionDeviceClass(string deviceClass, bool on)
	{
		if (on)
		{
			if (!MotionDeviceClassIsOn(deviceClass))
				_config.Global.MotionDeviceClasses.Add(deviceClass);
		}
		else
		{
			_config.Global.MotionDeviceClasses.RemoveAll(existing =>
				string.Equals(existing, deviceClass, StringComparison.OrdinalIgnoreCase));
		}

		// All three depend on the class set: the motion picker's contents, the area labels discovery counts, and
		// the presence override picker, which is built from the motion sensors.
		_motionSensors = _catalog.EntitiesWithDeviceClass("binary_sensor", [.. _config.Global.EffectiveMotionDeviceClasses]);
		PresenceEntities = [.. _motionSensors.Concat(_catalog.EntitiesInDomains("person"))];
		_areas = _catalog.Areas(_config.Global);
		HomeAssistantIsAnswering = _catalog.IsHomeAssistantResponding(_config.Global);
		Revalidate();
	}

	/// <summary>The built-in motion classes, for the line that says what ticking nothing means.</summary>
	public static string DefaultMotionDeviceClassesText => string.Join(", ", GlobalConfig.DefaultMotionDeviceClasses);

	/// <summary>The motion classes actually in effect.</summary>
	public string EffectiveMotionDeviceClassesText => string.Join(", ", _config.Global.EffectiveMotionDeviceClasses);

	/// <summary>Which kind of sensor a room takes its light reading from.</summary>
	public string IlluminanceDeviceClass
	{
		get => _config.Global.IlluminanceDeviceClass;
		set
		{
			_config.Global.IlluminanceDeviceClass = value;
			LuxSensors = _catalog.EntitiesWithDeviceClass("sensor", [_config.Global.IlluminanceDeviceClass]);
			_areas = _catalog.Areas(_config.Global);
			HomeAssistantIsAnswering = _catalog.IsHomeAssistantResponding(_config.Global);
			Revalidate();
		}
	}

	/// <summary>Whether any sensor in the house reports the class the document asks for.</summary>
	public bool IlluminanceDeviceClassIsOffered =>
		SensorDeviceClasses.Contains(_config.Global.IlluminanceDeviceClass, StringComparer.OrdinalIgnoreCase);

	// ---- the house: people, the master switch, the name and the fine tuning ----

	public IReadOnlyList<Sentence> AwaySentence { get; private set; } = [];

	public IReadOnlyList<string> Persons => _config.Global.Persons;

	public void SetPersons(IReadOnlyList<string> persons)
	{
		_config.Global.Persons = [.. persons];
		Revalidate();
	}

	public string? KillSwitchEntity => _config.Global.KillSwitchEntity;

	/// <summary>Whether the app's own switch is in use, which is what hides the polarity dropdown.</summary>
	public bool UsesBuiltInKillSwitch => string.IsNullOrWhiteSpace(_config.Global.KillSwitchEntity);

	public string? EffectiveKillSwitchEntity => _config.Global.EffectiveKillSwitchEntity;

	/// <summary>Which way round the master switch reads, as the segment the select stands on.</summary>
	public string KillSwitchPolarityValue => _config.Global.KillSwitchActiveWhenOff ? "true" : "false";

	public void SetKillSwitchEntity(string? value)
	{
		_config.Global.KillSwitchEntity = value;
		Revalidate();
	}

	public void SetKillSwitchPolarity(string? value)
	{
		_config.Global.KillSwitchActiveWhenOff = string.Equals(value, "true", StringComparison.Ordinal);
		Revalidate();
	}

	/// <summary>A label for logs and notifications, so two houses can be told apart.</summary>
	public string? HouseName
	{
		get => _config.ConfigName;
		set
		{
			_config.ConfigName = value;
			Revalidate();
		}
	}

	/// <summary>Whether a NetDaemon user id is stored. The value itself is never read back out.</summary>
	public bool HasUserId => !string.IsNullOrWhiteSpace(_config.Global.NetDaemonUserId);

	/// <summary>What was typed into the write-only user-id box, applied on the next save.</summary>
	/// <remarks>Not part of the document, so it arms the save bar on its own and has to say so.</remarks>
	public string? UserIdInput
	{
		get => _userIdInput;
		set
		{
			_userIdInput = value;
			Notify();
		}
	}

	private string? _userIdInput;

	public void ClearUserId()
	{
		_config.Global.NetDaemonUserId = null;
		Revalidate();
	}

	public int CircadianTickSeconds
	{
		get => _config.Global.CircadianTickSeconds;
		set
		{
			_config.Global.CircadianTickSeconds = value;
			Revalidate();
		}
	}

	public int SelfEchoWindowSeconds
	{
		get => _config.Global.SelfEchoWindowSeconds;
		set
		{
			_config.Global.SelfEchoWindowSeconds = value;
			Revalidate();
		}
	}

	public bool TreatAutomationsAsManual
	{
		get => _config.Global.TreatAutomationsAsManual;
		set
		{
			_config.Global.TreatAutomationsAsManual = value;
			Revalidate();
		}
	}

	// ---- this installation ----

	public bool EngineIsRunning => _engine.IsRunning;

	/// <summary>Why the engine is not running, or the stand-in for never having started.</summary>
	public string EngineFault => _engine.Fault ?? "Not started yet.";

	/// <summary>How many rooms the engine is managing, in words.</summary>
	public string RoomsManagedLine =>
		$"{_engine.RunningAreaCount} {(_engine.RunningAreaCount == 1 ? "room" : "rooms")} managed.";

	/// <summary>How many areas Home Assistant has told this page about, in words.</summary>
	public string AreasKnownLine => $"{_areas.Count} {(_areas.Count == 1 ? "area" : "areas")} known.";

	public string ConfigFilePath => _location.Path;

	/// <summary>The standing warning about where the document lives, or <c>null</c> when there is none.</summary>
	public string? LocationWarning => _location.Warning is { Length: > 0 } warning ? warning : null;

	/// <summary>What keeping the document where it is costs or buys.</summary>
	public string LocationDescription => _location.Source switch
	{
		ConfigLocationSource.External => "outside the deploy folder — survives a deploy",
		ConfigLocationSource.SeededFromTree => "created from the shipped example — survives a deploy",
		_ => "inside the deploy folder — a deploy will overwrite it"
	};

	public bool HasBackup => _engine.Store.HasBackup;

	public string BackupPath => _engine.Store.BackupPath;
}
