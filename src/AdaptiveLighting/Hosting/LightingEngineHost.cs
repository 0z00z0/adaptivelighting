using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Extensions;
using AdaptiveLighting.Ha;
using AdaptiveLighting.LastSeen;

namespace AdaptiveLighting.Hosting;

/// <summary>How a call to <see cref="LightingEngineHost.Save"/> ended.</summary>
public enum SaveStatus
{
	/// <summary>Written and the engine rebuilt on the new document.</summary>
	Saved,

	/// <summary>Refused: the document has errors that make it unrunnable. Nothing was written.</summary>
	Rejected,

	/// <summary>Refused: the file changed while the page sat open, and saving would revert that. Nothing was written.</summary>
	/// <remarks>Raised by the pages. A retry cannot clear it, so a page offers a reload.</remarks>
	Conflicted,

	/// <summary>Written, but the file system or the engine rebuild failed afterwards.</summary>
	Failed
}

/// <summary>What the host's write step does with a document the engine cannot run.</summary>
internal enum InvalidDocument
{
	/// <summary>Refuse the write and leave the file as it was.</summary>
	Refuse,

	/// <summary>Write it, and hand the errors back to be reported.</summary>
	WriteAnyway
}

/// <summary>What one pass through the host's write step did.</summary>
/// <param name="Written">Whether the bytes reached the disk.</param>
/// <param name="Validation">The document as validated after normalisation, whether or not it was written.</param>
internal sealed record ConfigWriteResult(bool Written, ValidationResult Validation);

/// <summary>The outcome of a save. <c>Message</c> is one sentence for the operator.</summary>
/// <remarks>A <see cref="SaveStatus.Saved"/> result may still carry area errors, which cost an area and not the save.</remarks>
public sealed record SaveResult(SaveStatus Status, ValidationResult Validation, string Message)
{
	/// <summary>Whether the document reached disk.</summary>
	public bool Written => Status is SaveStatus.Saved;
}

/// <summary>Owns the running <see cref="LightingOrchestrator"/> for this host, and is the only thing allowed to replace it.</summary>
/// <remarks>
///     A bad document never throws out of here. An app put into <c>ApplicationState.Error</c> is disposed along with
///     its DI scope and its <c>IHaContext</c>, which would leave this host holding a dead connection and no way to
///     rebuild once a corrected file was saved. The host stays attached and reports itself faulted instead.
/// </remarks>
public sealed class LightingEngineHost : IDisposable
{
	private const string InvalidConfigTitle = "Adaptive lighting: the settings file has errors";
	private const string ForcedWriteTitle = "Adaptive lighting: the settings file was written with errors";

	private readonly LightingConfigStore _store;
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger<LightingEngineHost> _logger;

	/// <summary>Handed to every area's illuminance gate, so a dead sensor is judged on evidence that survives a restart.</summary>
	private readonly IEntityLastSeen? _lastSeen;

	// A period boundary that went by while the engine was stopped is not lost. Its path comes from the store.
	private readonly ILastPeriodStore? _lastPeriod;

	// A room that cannot be set up is reported once instead of at every start. Its path comes from the store.
	private readonly IAreaSetupMemory? _setupMemory;

	// Each room's last movement, last change and who changed it, so a restart does not forget them. Its path
	// comes from the store. Null and every room's history is lost across a restart, never a reason to have no engine.
	private readonly IRoomHistoryStore? _roomHistory;

	private readonly StateStoreRegistry _stateStores;

	// Set only where the host built the registry itself; one the container supplies is the container's to dispose.
	private readonly StateStoreRegistry? _ownedStateStores;

	private bool _restoreReported;

	// The house's own copy, shared across every rebuild so a room that has not published since the last save is
	// not dropped from the file a save writes. Seeded once from what the note held at start.
	private ConcurrentDictionary<string, AreaHistory>? _roomHistoryLive;

	// Every transition of the orchestrator goes through this: two browser tabs saving must not interleave a
	// Dispose with a Start.
	private readonly Lock _gate = new();

	// Replayed, because the engine is started by the NetDaemon bootstrap and the web host's recorder may not have
	// subscribed by then. One is enough: nothing but the engine's own start can precede a browser being open.
	private readonly ReplaySubject<EngineNotice> _notices = new(bufferSize: 1);

	// Runs behind the same gate, because its scan writes the document and rebuilds the engine on the result.
	private readonly AreaDiscoveryScheduler _discovery;

	private IHaContext? _ha;
	private IHaRegistry? _registry;
	private IScheduler? _scheduler;
	private string? _defaultKillSwitchEntity;
	private LightingOrchestrator? _orchestrator;

	// The retired-key sentences of the file as last read. Swapped whole, so Validate outside the gate reads one list.
	private IReadOnlyList<string> _retiredKeys = [];

	/// <summary>Creates the host. Nothing runs until <see cref="Attach"/> and <see cref="Reload"/>.</summary>
	/// <remarks>Without <c>lastSeen</c> the gates use Home Assistant's own timestamps, which reset on its restart.</remarks>
	public LightingEngineHost(LightingConfigStore store, ILoggerFactory loggerFactory, IEntityLastSeen? lastSeen = null)
		: this(store, loggerFactory, lastSeen, stateStores: null)
	{
	}

	/// <summary>Creates the host on a registry the container owns; without one the host builds and disposes its own.</summary>
	internal LightingEngineHost(LightingConfigStore store, ILoggerFactory loggerFactory, IEntityLastSeen? lastSeen, StateStoreRegistry? stateStores)
	{
		_lastSeen = lastSeen;
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
		_logger = loggerFactory.CreateLogger<LightingEngineHost>();

		_discovery = new AreaDiscoveryScheduler(_gate, _store, _loggerFactory, WriteDiscoveredAreas, AdoptDiscoveredAreas);

		// Real time, not the scheduler Attach hands over: that one is not known yet, and a house runs for
		// hours between saves, so the periodic flush must tick regardless of what a test controls.
		_stateStores = stateStores
			?? new StateStoreRegistry(_store.FilePath, _loggerFactory.CreateLogger<StateStoreRegistry>(), DefaultScheduler.Instance);
		_ownedStateStores = stateStores is null ? _stateStores : null;

		try
		{
			_lastPeriod = new LastPeriodStore(_store.FilePath, _loggerFactory.CreateLogger<LastPeriodStore>(), _stateStores);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			// A path this class cannot write beside is not a reason to have no engine.
			_logger.LogWarning(exception,
				"Could not place the note recording which circadian period the engine is in, beside {Path}. A period's "
				+ "house mode will be applied only at a boundary the engine is running to see.",
				_store.FilePath);
		}

		try
		{
			_setupMemory = new AreaSetupMemoryStore(_store.FilePath, _loggerFactory.CreateLogger<AreaSetupMemoryStore>(), _stateStores);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			// Without it the card comes back on every start, which is a nuisance and not a reason to have no engine.
			_logger.LogWarning(exception,
				"Could not place the note recording which rooms have already been reported as impossible to set up, "
				+ "beside {Path}. Any such room will be reported again at every start.",
				_store.FilePath);
		}

		try
		{
			_roomHistory = new RoomHistoryStore(_stateStores, _loggerFactory);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			_logger.LogWarning(exception,
				"Could not place the note recording each room's history, beside {Path}. Every room's history "
				+ "starts again as unknown after a restart.",
				_store.FilePath);
		}
	}

	public LightingConfigStore Store => _store;

	/// <summary>The house-wide things the engine did to itself: one row per rebuild, never one per area.</summary>
	public IObservable<EngineNotice> Notices => _notices;

	/// <summary>The app's enable switch, or <c>null</c> before <see cref="Attach"/>: the kill switch when the document names none.</summary>
	public string? DefaultKillSwitchEntity => _defaultKillSwitchEntity;

	/// <summary>Whether Home Assistant is handed over yet. Until then the UI can edit and save, but nothing starts.</summary>
	public bool IsAttached => _ha is not null;

	public bool IsRunning => _orchestrator is not null;

	/// <summary>How many areas resolved and are being commanded. Zero while faulted.</summary>
	public int RunningAreaCount => _orchestrator?.Areas.Count ?? 0;

	/// <summary>The bulbs more than one room commands, found once at engine start. Empty while faulted.</summary>
	public IReadOnlyList<SuspectLight> SharedLights => _orchestrator?.SharedLights ?? [];

	/// <summary>The running engine's latch for periods that wait for movement, or <c>null</c> while it is not running.</summary>
	// Never cache it: a save rebuilds the orchestrator, and a stale latch answers "not begun" for every held period.
	public MotionPeriodLatch? MotionPeriods => _orchestrator?.MotionPeriods;

	/// <summary>The document as last validated, or <c>null</c> before anything has been read or written.</summary>
	public ValidationResult? LastValidation { get; private set; }

	/// <summary>Why nothing is running, or <c>null</c> while it is.</summary>
	public string? Fault { get; private set; }

	public DateTimeOffset? LastStartedUtc { get; private set; }

	/// <summary>Why a level test cannot run in <paramref name="areaId"/> right now, or <c>null</c> when one can.</summary>
	/// <remarks>Asked before a press so the button can carry its own reason; the press asks again, under the lock.</remarks>
	public string? LevelTestRefusal(string? areaId)
	{
		lock (_gate)
			return RunningArea(areaId) is { } area ? area.LevelTestRefusal() : NotRunningRefusal();
	}

	/// <summary>Puts a period's levels on one room's lights for <see cref="AreaController.LevelTestSeconds"/> seconds, then hands the room back.</summary>
	/// <returns><c>null</c> once the test is running, or the sentence saying why it is not.</returns>
	/// <remarks>The engine schedules the return, so closing the page cannot strand a room on test levels.</remarks>
	public string? TestPeriod(string? areaId, string periodKey)
	{
		lock (_gate)
			return RunningArea(areaId) is { } area ? area.TestPeriod(periodKey) : NotRunningRefusal();
	}

	/// <summary>Puts a period's levels on one light for <see cref="AreaController.LevelTestSeconds"/> seconds, then gives it back.</summary>
	/// <returns><c>null</c> once the test is running, or the sentence saying why it is not.</returns>
	public string? TestLight(string? areaId, string lightEntityId, string periodKey)
	{
		lock (_gate)
			return RunningArea(areaId) is { } area ? area.TestLight(lightEntityId, periodKey) : NotRunningRefusal();
	}

	/// <summary>Ends a running level test in <paramref name="areaId"/> now, putting back what the lights showed before it.</summary>
	public void EndLevelTest(string? areaId)
	{
		lock (_gate)
			RunningArea(areaId)?.EndTest();
	}

	/// <summary>Whether <paramref name="areaId"/> is running here, and the level test it is running if any.</summary>
	/// <returns><c>false</c> when the room is not running, so the engine cannot say.</returns>
	public bool TryReadLevelTest(string? areaId, out LevelTestNow? test)
	{
		lock (_gate)
		{
			AreaController? area = RunningArea(areaId);
			test = area?.CurrentLevelTest;
			return area is not null;
		}
	}

	// For tests, which cannot build a room Home Assistant knows by area id.
	internal IReadOnlyList<AreaController> RunningAreas
	{
		get
		{
			lock (_gate)
				return _orchestrator?.Areas ?? [];
		}
	}

	/// <summary>Why <paramref name="areaId"/> cannot be lit by hand right now, or <c>null</c> when it can.</summary>
	/// <remarks>Asked before a press so the button can carry its own reason; the press asks again, under the lock.</remarks>
	public string? LightNowRefusal(string? areaId)
	{
		lock (_gate)
			return RunningArea(areaId) is { } area ? area.LightNowRefusal() : NotRunningRefusal();
	}

	/// <summary>Lights one room the way walking into it would, and starts the same vacancy countdown.</summary>
	/// <returns><c>null</c> once the room is lit, or the sentence saying why it is not.</returns>
	public string? LightNow(string? areaId)
	{
		lock (_gate)
			return RunningArea(areaId) is { } area ? area.LightNow() : NotRunningRefusal();
	}

	/// <summary>Why <paramref name="areaId"/> cannot be switched off by hand right now, or <c>null</c> when it can.</summary>
	public string? LightOffRefusal(string? areaId)
	{
		lock (_gate)
			return RunningArea(areaId) is { } area ? area.LightOffRefusal() : NotRunningRefusal();
	}

	/// <summary>Switches one room off the way a hand at the wall would.</summary>
	/// <returns><c>null</c> once the room is off, or the sentence saying why it is not.</returns>
	public string? LightOff(string? areaId)
	{
		lock (_gate)
			return RunningArea(areaId) is { } area ? area.LightOff() : NotRunningRefusal();
	}

	private AreaController? RunningArea(string? areaId) =>
		areaId is { Length: > 0 } wanted && _orchestrator is { } running
			? running.Areas.FirstOrDefault(area => string.Equals(area.AreaId, wanted, StringComparison.OrdinalIgnoreCase))
			: null;

	// Two different facts, and a household can act on only one of them: the whole engine is down, or it is up and
	// this one room resolved to nothing.
	private string NotRunningRefusal() =>
		_orchestrator is null
			? "Nothing is running, so no light can be commanded."
			: "This room is not running: no lights resolved for it, so there is nothing to command.";

	/// <summary>Hands this host the Home Assistant connection it rebuilds against, once, from the house's <c>[NetDaemonApp]</c>.</summary>
	/// <remarks><c>defaultKillSwitchEntity</c> comes from <see cref="NetDaemonAppSwitch"/> and is never written to YAML.</remarks>
	public void Attach(IHaContext ha, IHaRegistry registry, IScheduler scheduler, string? defaultKillSwitchEntity = null)
	{
		ArgumentNullException.ThrowIfNull(ha);
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(scheduler);

		lock (_gate)
		{
			_ha = ha;
			_registry = registry;
			_scheduler = scheduler;
			_defaultKillSwitchEntity = defaultKillSwitchEntity;
		}
	}

	/// <summary>Stops the engine and gives back the Home Assistant connection, which dies with the bootstrap app.</summary>
	/// <remarks>Discovery is forgotten here, so switching the app off and on again is how a house asks for another scan.</remarks>
	public void Detach()
	{
		lock (_gate)
		{
			StopCore();
			_discovery.Cancel();
			_ha = null;
			_registry = null;
			_scheduler = null;
			Fault = "The lighting app was switched off in Home Assistant, so nothing is connected to it.";
		}
	}

	/// <summary>Reads the current document from disk and, if it can be run, replaces the running engine with one built on it.</summary>
	/// <remarks>
	///     Never throws. The caller is either a NetDaemon app whose death would take the connection with it, or a
	///     Razor component rendering a page.
	/// </remarks>
	public SaveResult Reload()
	{
		lock (_gate)
		{
			DocumentReadResult read;

			try
			{
				read = _store.Read();
			}
			catch (LightingConfigException exception)
			{
				StopCore();
				Fault = exception.Message;
				_logger.LogError(exception, "Could not load the lighting configuration from {Path}.", _store.FilePath);

				ValidationResult unreadable = new();
				unreadable.AddError(exception.Message);
				LastValidation = unreadable;

				return new SaveResult(SaveStatus.Failed, unreadable, "The configuration file could not be read.");
			}

			AdaptiveLightingConfig config = read.Config;
			_retiredKeys = read.RetiredKeys;

			// Both reasons take the same write. Translation runs first, so one rewrite covers a document that needs
			// both, and its result is what decides whether a document in the current schema is written at all.
			bool translated = LabelTranslation.Apply(config.Global, KnownLabels());

			if (read.NeedsMigratingWrite || translated)
				RewriteInCurrentSchema(config, read.NeedsMigratingWrite);

			_discovery.ArmIfNeeded(config, _ha, _registry, _scheduler);

			SaveResult result = ApplyCore(config, EngineNoticeKind.Started);

			// Once, after the first start that ran: the engine's own stores have been read by then.
			if (!_restoreReported && _orchestrator is not null)
			{
				_restoreReported = true;
				_stateStores.ReportRestored();
			}

			return result;
		}
	}

	/// <summary>Writes a document that loaded through a superseded schema, or that still stores label names, back out.</summary>
	/// <remarks>
	///     On first load, before the engine is built, so a house that never opens the web UI does not depend on the
	///     translation tables for ever. The write goes through <see cref="NormaliseValidateAndWrite"/>, so the
	///     pre-migration file survives at <see cref="LightingConfigStore.BackupPath"/>. Once only, because the store
	///     keeps one backup slot. A document the engine cannot run is rewritten all the same, with the errors reported.
	/// </remarks>
	private void RewriteInCurrentSchema(AdaptiveLightingConfig config, bool olderSchema)
	{
		try
		{
			ConfigWriteResult write = NormaliseValidateAndWrite(config, InvalidDocument.WriteAnyway);

			_logger.LogInformation(
				"The configuration file has been rewritten: {Reason}. The file as it was is at {Backup}.",
				olderSchema ? "it was written against an older schema" : "it stored label names, which are now stored as label ids",
				_store.BackupPath);

			ReportForcedWrite(write.Validation,
				"The configuration file was rewritten in the current schema, but the document it holds has errors.");
		}
		catch (LightingConfigException exception)
		{
			_logger.LogWarning(
				exception,
				"Could not rewrite {Path} in the current schema. The engine is running on it either way; the migration will be run again on the next start.",
				_store.FilePath);
		}
	}

	/// <summary>The person's write: refuse a document the engine cannot run, then re-read and rebuild every area.</summary>
	/// <remarks>
	///     Normalisation and validation belong to <see cref="NormaliseValidateAndWrite"/>, so all three writers get
	///     them. What is here is what follows the refusal: a document-level error costs the save and nothing reaches
	///     the disk, while area-level errors do not refuse it.
	/// </remarks>
	public SaveResult Save(AdaptiveLightingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		lock (_gate)
		{
			ConfigWriteResult write;

			try
			{
				write = NormaliseValidateAndWrite(config, InvalidDocument.Refuse);
			}
			catch (LightingConfigException exception)
			{
				// Already normalised and validated before the write threw, so asking again is the way back to what
				// the refusal check saw.
				ValidationResult attempted = Validate(config);
				LastValidation = attempted;
				_logger.LogError(exception, "Could not write the lighting configuration.");

				return new SaveResult(SaveStatus.Failed, attempted, exception.Message);
			}

			if (!write.Written)
			{
				LastValidation = write.Validation;
				_logger.LogWarning(
					"Refused to save the lighting configuration: {Count} document-level errors.",
					write.Validation.Errors.Count);

				return new SaveResult(
					SaveStatus.Rejected,
					write.Validation,
					"Not saved. The file on disk and the running engine are untouched.");
			}

			// Re-read, never the in-memory object: a save is reported successful only once the bytes on disk parse
			// back into a document the engine accepts, which is what matters after a restart.
			AdaptiveLightingConfig written;

			try
			{
				DocumentReadResult reread = _store.Read();
				written = reread.Config;
				_retiredKeys = reread.RetiredKeys;
			}
			catch (LightingConfigException exception)
			{
				StopCore();
				Fault = $"The settings file was written but does not read back, so nothing is running. {exception.Message}";
				_logger.LogError(exception, "The lighting configuration at {Path} does not read back after the write.", _store.FilePath);

				ValidationResult unreadable = new();
				unreadable.AddError(exception.Message);
				LastValidation = unreadable;

				return new SaveResult(SaveStatus.Failed, unreadable, "The file was written but does not read back, so nothing is running.");
			}

			return ApplyCore(written, EngineNoticeKind.SettingsSaved);
		}
	}

	/// <summary>
	///     Validates <paramref name="config"/> against what Home Assistant currently knows, without saving anything.
	///     Referential checks are skipped when HA is not connected.
	/// </summary>
	public ValidationResult Validate(AdaptiveLightingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		HouseFacts facts = HouseFacts.Read(_ha, _registry, config);

		return ConfigValidator.Validate(config, new ValidationContext
		{
			KnownEntityIds = facts.EntityIds,
			KnownAreaIds = facts.AreaIds,
			LiveSelectOptions = facts.HouseModeOptions,
			LabelsInUse = facts.LabelsInUse,
			LivePeriodSelectOptions = facts.PeriodSelectOptions,
			KnownLabelIds = facts.LabelIds,
			DefaultKillSwitchEntity = _defaultKillSwitchEntity,
			RetiredKeys = _retiredKeys
		});
	}

	/// <summary>Every label the house has, or <c>null</c> while the registry cannot be asked.</summary>
	// Null and empty mean different things here: null leaves stored values alone, empty would too, but only null
	// says the question was never put.
	private IReadOnlyList<RegistryLabel>? KnownLabels()
	{
		if (_registry is null)
			return null;

		try
		{
			return _registry.KnownLabels();
		}
		catch (InvalidOperationException)
		{
			// NetDaemon's registry throws until its first connection to HA completes.
			return null;
		}
	}

	/// <summary>The one way a document reaches the disk: normalise it, validate it, then refuse it or write it.</summary>
	/// <remarks>
	///     All three writers come through here, so none of them can skip either step. <paramref name="onInvalid"/>
	///     refuses a person's save of a document the engine cannot run, while this class's own two writes go out and
	///     carry their errors back. Warnings and area errors never affect a write.
	/// </remarks>
	/// <exception cref="LightingConfigException">The file could not be written.</exception>
	private ConfigWriteResult NormaliseValidateAndWrite(AdaptiveLightingConfig config, InvalidDocument onInvalid)
	{
		// In place, so the caller's own object carries the drops: the page and the scan both look at it afterwards.
		ConfigNormalizer.Normalize(config);
		LabelTranslation.Apply(config.Global, KnownLabels());

		ValidationResult validation = Validate(config);

		if (!validation.IsValid && onInvalid is InvalidDocument.Refuse)
			return new ConfigWriteResult(Written: false, validation);

		_store.Write(config);

		return new ConfigWriteResult(Written: true, validation);
	}

	/// <summary>The discovery scan's write. Runs behind the gate, because its caller holds it.</summary>
	/// <returns><c>null</c> when the write failed, which tells the scan to put the document back as it found it.</returns>
	private ConfigWriteResult? WriteDiscoveredAreas(AdaptiveLightingConfig config)
	{
		try
		{
			return NormaliseValidateAndWrite(config, InvalidDocument.WriteAnyway);
		}
		catch (LightingConfigException exception)
		{
			_logger.LogWarning(exception, "Could not save the discovered areas; they will be proposed again on the next start.");

			return null;
		}
	}

	/// <summary>Reports whatever the discovered document carries, then rebuilds on it.</summary>
	// Discovery rewrote the document during start-up; nobody saved anything, so the notice is a start.
	private void AdoptDiscoveredAreas(AdaptiveLightingConfig config, ValidationResult validation)
	{
		ReportForcedWrite(validation,
			"The rooms discovery found were written, but the document that now holds them has errors.");

		ApplyCore(config, EngineNoticeKind.Started);
	}

	public void Dispose()
	{
		lock (_gate)
		{
			_discovery.Dispose();

			// The freshest word on every room's history, ahead of disposing the orchestrator that knows it.
			if (_orchestrator is not null)
				PersistRoomHistory(_orchestrator.CarryOver(), flushNow: true);

			StopCore();
			_ha = null;
			_registry = null;
			_scheduler = null;
			_notices.Dispose();
			_ownedStateStores?.Dispose();
		}
	}

	/// <remarks>
	///     <c>notice</c> is raised only where the engine actually came up. A rebuild that ends faulted is in the log
	///     and the notification; it is not a row in the record saying the house was rebuilt.
	/// </remarks>
	private SaveResult ApplyCore(AdaptiveLightingConfig config, EngineNoticeKind notice)
	{
		_logger.LogInformation(
			"Applying lighting configuration update: {Areas} areas, {Periods} periods, house-mode select {Select}.",
			config.Areas.Count, config.Periods.Count, config.Global.HouseMode?.Entity ?? "(none)");

		ValidationResult validation = Validate(config);
		LastValidation = validation;

		foreach (AreaError areaError in validation.AreaErrors)
			_logger.LogError("Area {Area} will not resolve: {Error}", areaError.AreaName, areaError.Message);

		if (!validation.IsValid)
		{
			StopCore();
			Fault = "The settings file has errors that stop the whole house, so nothing is running. Fix them under Configuration.";

			_logger.LogError(
				"Adaptive lighting configuration is invalid, engine stopped:{NewLine}{Validation}",
				Environment.NewLine, validation);

			// Notify as well as log: the log only reaches whoever is tailing the add-on.
			Notify(validation);

			return new SaveResult(SaveStatus.Failed, validation, "Saved, but nothing can run on these settings.");
		}

		if (_ha is null || _registry is null || _scheduler is null)
		{
			Fault = "The lighting app has not started yet, so nothing is connected to Home Assistant.";
			_logger.LogWarning("Configuration is valid but no Home Assistant connection is attached; not starting.");

			return new SaveResult(SaveStatus.Saved, validation, "Saved. Rooms start being managed as soon as Home Assistant answers.");
		}

		// A save hands running rooms on directly. Anything else has nothing running to take over from, so it
		// reads what the last run wrote instead.
		IReadOnlyDictionary<string, AreaCarryOver>? carried;

		if (notice is EngineNoticeKind.SettingsSaved)
		{
			carried = _orchestrator?.CarryOver(config);

			if (carried is not null)
			{
				// A person's save, so a room no longer in the document at all loses its history here, not on the
				// next room that happens to publish. Disabled-but-still-declared rooms keep their key.
				HashSet<string> stillDocumented = LightingOrchestrator.DocumentRoomKeys(config, _registry);
				PersistRoomHistory(carried, flushNow: true, prune: stillDocumented);
			}
		}
		else
		{
			carried = _roomHistory is null
				? null
				: RoomHistoryLive().ToDictionary(
					pair => pair.Key,
					pair => new AreaCarryOver(pair.Value, Hold: null),
					StringComparer.OrdinalIgnoreCase);
		}

		StopCore();

		try
		{
			IStatePublisher publisher = new HaStatePublisher(_ha, _loggerFactory.CreateLogger<HaStatePublisher>());

			if (_roomHistory is not null)
				publisher = new HistoryTrackingPublisher(publisher, _roomHistory, RoomHistoryLive());

			LightingOrchestrator orchestrator = new(
				_ha,
				_registry,
				_scheduler,
				config,
				new HaLightActuator(_ha, _loggerFactory.CreateLogger<HaLightActuator>()),
				publisher,
				new HaNotifier(_ha, _loggerFactory.CreateLogger<HaNotifier>()),
				_loggerFactory,
				_lastSeen,
				_lastPeriod,
				_setupMemory,
				// A save is not a boundary that went by while the engine was down; the note on disk cannot tell
				// the two apart on its own.
				afterSave: notice is EngineNoticeKind.SettingsSaved,
				defaultKillSwitchEntity: _defaultKillSwitchEntity,
				carried: carried);

			orchestrator.Start();

			_orchestrator = orchestrator;
			Fault = null;
			LastStartedUtc = DateTimeOffset.UtcNow;

			_logger.LogInformation(
				"Adaptive lighting is running: {Areas} of {Configured} areas resolved.",
				orchestrator.Areas.Count, config.ManagedAreaCount);

			_notices.OnNext(new EngineNotice(notice, DateTimeOffset.Now));

			return new SaveResult(SaveStatus.Saved, validation, $"Saved: {orchestrator.Areas.Count} of {config.ManagedAreaCount} rooms are running.");
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			// Broad on purpose: a narrower filter lets an exception from mode construction escape to the Blazor circuit,
			// where the save renders nothing at all.
			StopCore();
			Fault = $"Adaptive lighting could not start: {exception.Message}";
			_logger.LogError(exception, "The lighting engine failed to start.");

			return new SaveResult(SaveStatus.Failed, validation, Fault);
		}
	}

	private void StopCore()
	{
		_orchestrator?.Dispose();
		_orchestrator = null;
	}

	/// <summary>The house's own copy of every room's history, seeded once from what the note held at start.</summary>
	private ConcurrentDictionary<string, AreaHistory> RoomHistoryLive() =>
		_roomHistoryLive ??= new ConcurrentDictionary<string, AreaHistory>(
			_roomHistory?.Load() ?? new Dictionary<string, AreaHistory>(), StringComparer.OrdinalIgnoreCase);

	/// <summary>Folds a rebuild's carry-over into the house's live history and writes it.</summary>
	/// <remarks>
	///     <paramref name="flushNow"/> is true on a save: a minute's wait is not acceptable there. <paramref name="prune"/>
	///     is given only on a person's save: every key not in it is dropped, so a room removed from the document does not
	///     keep growing the file for ever. Left <c>null</c> elsewhere, where there is no fresh document to prune against.
	/// </remarks>
	private void PersistRoomHistory(IReadOnlyDictionary<string, AreaCarryOver> carried, bool flushNow, IReadOnlySet<string>? prune = null)
	{
		if (_roomHistory is null)
			return;

		ConcurrentDictionary<string, AreaHistory> live = RoomHistoryLive();

		foreach ((string key, AreaCarryOver value) in carried)
			live[key] = value.History;

		if (prune is not null)
		{
			foreach (string staleKey in live.Keys.Where(key => !prune.Contains(key)).ToList())
				live.TryRemove(staleKey, out _);
		}

		_roomHistory.TrySave(live);

		if (flushNow)
			_roomHistory.Flush();
	}

	/// <summary>Reports a write this class made over a document the engine cannot run.</summary>
	/// <remarks>The bytes are already on disk; this is the log line and the notification saying what went out with them.</remarks>
	private void ReportForcedWrite(ValidationResult validation, string what)
	{
		if (validation.IsValid)
			return;

		_logger.LogError(
			"{What} It was written all the same:{NewLine}{Validation}",
			what, Environment.NewLine, validation);

		Notify(ForcedWriteTitle, validation);
	}

	private void Notify(ValidationResult validation) => Notify(InvalidConfigTitle, validation);

	private void Notify(string title, ValidationResult validation)
	{
		if (_ha is null)
			return;

		try
		{
			new HaNotifier(_ha, _loggerFactory.CreateLogger<HaNotifier>()).Notify(title, validation.ToHtml());
		}
		catch (InvalidOperationException exception)
		{
			// No live connection. Reporting the config problem must not become a second problem.
			_logger.LogWarning(exception, "Could not post the invalid-configuration notification to Home Assistant.");
		}
	}
}
