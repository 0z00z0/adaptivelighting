using System.Reactive.Concurrency;
using System.Reactive.Subjects;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
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

	/// <summary>Written, but the file system or the engine rebuild failed afterwards.</summary>
	Failed
}

/// <summary>The outcome of a save.</summary>
/// <remarks>
///     On <see cref="SaveStatus.Saved"/> the validation may still carry area errors: those cost an area, not the
///     save. <c>Message</c> is a sentence for the operator, never a restatement of the validation.
/// </remarks>
public sealed record SaveResult(SaveStatus Status, ValidationResult Validation, string Message)
{
	/// <summary>Whether the document reached disk.</summary>
	public bool Written => Status is SaveStatus.Saved;
}

/// <summary>
///     Owns the running <see cref="LightingOrchestrator"/> for this host, and is the only thing allowed to
///     replace it.
/// </summary>
/// <remarks>
///     A bad document never throws out of here. An app put into <c>ApplicationState.Error</c> is disposed along
///     with its DI scope and its <c>IHaContext</c>, which would leave this host holding a dead connection and no
///     way to rebuild once the browser saved a corrected file. Instead the host stays attached and reports itself
///     faulted, with the notification, the log and the web UI all still saying so.
/// </remarks>
public sealed class LightingEngineHost : IDisposable
{
	private const string InvalidConfigTitle = "Adaptive lighting: the settings file has errors";

	private readonly LightingConfigStore _store;
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger<LightingEngineHost> _logger;

	/// <summary>Handed to every area's illuminance gate, so a dead sensor is judged on evidence that survives a restart.</summary>
	private readonly IEntityLastSeen? _lastSeen;

	/// <summary>
	///     Handed to the mode brain, so a period boundary that went by while the engine was stopped is not lost.
	///     Built here, not injected: its path comes from <see cref="LightingConfigStore.FilePath"/>.
	/// </summary>
	private readonly ILastPeriodStore? _lastPeriod;

	// Every transition of the orchestrator goes through this: two browser tabs saving must not interleave a
	// Dispose with a Start.
	private readonly Lock _gate = new();

	// Replayed, because the engine is started by the NetDaemon bootstrap and the web host's recorder may not have
	// subscribed by then. One is enough: nothing but the engine's own start can precede a browser being open.
	private readonly ReplaySubject<EngineNotice> _notices = new(bufferSize: 1);

	private IHaContext? _ha;
	private IHaRegistry? _registry;
	private IScheduler? _scheduler;
	private string? _defaultKillSwitchEntity;
	private LightingOrchestrator? _orchestrator;

	/// <summary>Creates the host. Nothing runs until <see cref="Attach"/> and <see cref="Reload"/>.</summary>
	/// <remarks>
	///     Without <c>lastSeen</c> the gates fall back to Home Assistant's own timestamps, which reset on its restart.
	/// </remarks>
	public LightingEngineHost(LightingConfigStore store, ILoggerFactory loggerFactory, IEntityLastSeen? lastSeen = null)
	{
		_lastSeen = lastSeen;
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
		_logger = loggerFactory.CreateLogger<LightingEngineHost>();

		try
		{
			_lastPeriod = new LastPeriodStore(_store.FilePath, _loggerFactory.CreateLogger<LastPeriodStore>());
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			// A path this class cannot write beside is not a reason to have no engine.
			_logger.LogWarning(exception,
				"Could not place the note recording which circadian period the engine is in, beside {Path}. A period's "
				+ "house mode will be applied only at a boundary the engine is running to see.",
				_store.FilePath);
		}
	}

	public LightingConfigStore Store => _store;

	/// <summary>
	///     The house-wide things the engine did to itself: it started, or a save rebuilt every room. One per
	///     rebuild, never one per area.
	/// </summary>
	public IObservable<EngineNotice> Notices => _notices;

	/// <summary>
	///     The app's built-in enable switch, or <c>null</c> before <see cref="Attach"/>. Used as the kill switch
	///     whenever the document leaves <c>KillSwitchEntity</c> unset; never written to YAML.
	/// </summary>
	public string? DefaultKillSwitchEntity => _defaultKillSwitchEntity;

	/// <summary>
	///     Whether the bootstrap has handed over Home Assistant yet. When <c>false</c> the UI can still edit and
	///     save, but nothing can be started or validated against the registry.
	/// </summary>
	public bool IsAttached => _ha is not null;

	public bool IsRunning => _orchestrator is not null;

	/// <summary>How many areas resolved and are being commanded. Zero while faulted.</summary>
	public int RunningAreaCount => _orchestrator?.Areas.Count ?? 0;

	/// <summary>
	///     The bulbs more than one room commands, as the running engine found them. Empty while faulted, and empty
	///     in the ordinary house.
	/// </summary>
	/// <remarks>Forwarded, never recomputed: the finding is made once, at engine start.</remarks>
	public IReadOnlyList<SuspectLight> SharedLights => _orchestrator?.SharedLights ?? [];

	public ValidationResult? LastValidation { get; private set; }

	public string? Fault { get; private set; }

	public DateTimeOffset? LastStartedUtc { get; private set; }

	/// <summary>
	///     Hands this host the Home Assistant connection it rebuilds against. Called once, by the per-host
	///     <c>[NetDaemonApp]</c> bootstrap, which is the only thing the app model gives a live scope to.
	/// </summary>
	/// <remarks>
	///     <c>defaultKillSwitchEntity</c> comes from <see cref="NetDaemonAppSwitch.EntityIdFor"/> and is held in
	///     memory only, never written to YAML.
	/// </remarks>
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

	/// <summary>
	///     Stops the engine and gives back the Home Assistant connection. Called when the bootstrap app is
	///     disposed, because the context it handed over dies with it.
	/// </summary>
	public void Detach()
	{
		lock (_gate)
		{
			StopCore();
			_ha = null;
			_registry = null;
			_scheduler = null;
			Fault = "The lighting app was switched off in Home Assistant, so nothing is connected to it.";
		}
	}

	/// <summary>
	///     Reads the current document from disk and, if it can be run, replaces the running engine with one built
	///     on it.
	/// </summary>
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

			if (read.UsedLegacyKeys)
				RewriteInCurrentSchema(config);

			ScheduleAreaDiscoveryIfNeeded(config);

			return ApplyCore(config, EngineNoticeKind.Started);
		}
	}

	/// <summary>
	///     Writes a document that loaded through the pre-2.0 key names straight back out in the current schema.
	/// </summary>
	/// <remarks>
	///     On first load, before the engine is built, so a house that never opens the web UI does not depend on the
	///     translation table for ever. The write goes through <see cref="LightingConfigStore.Save"/>, so the
	///     pre-migration file survives at <see cref="LightingConfigStore.BackupPath"/>. A failed write only warns:
	///     the in-memory document is the same either way.
	/// </remarks>
	private void RewriteInCurrentSchema(AdaptiveLightingConfig config)
	{
		try
		{
			_store.Save(config);

			_logger.LogInformation(
				"The configuration file used the pre-2.0 key names and has been rewritten in the current schema. "
				+ "The file as it was is at {Backup}.",
				_store.BackupPath);
		}
		catch (LightingConfigException exception)
		{
			_logger.LogWarning(
				exception,
				"Could not rewrite {Path} in the current schema. The engine is running on it either way; the old key names will be translated again on the next start.",
				_store.FilePath);
		}
	}

	/// <summary>How long to let Home Assistant's state cache fill before discovering areas.</summary>
	/// <remarks>
	///     Discovery must not run inline in <see cref="Reload"/>. The reload follows <see cref="Attach"/> at once,
	///     while NetDaemon's state cache is still filling, and the resolver drops any entity without a state, so an
	///     early scan proposes a partial set of rooms and the once-only flag locks that in.
	/// </remarks>
	private static readonly TimeSpan DiscoverySettle = TimeSpan.FromSeconds(30);

	private IDisposable? _discovery;
	private bool _discoveryScheduled;

	/// <summary>Arms the one-time area discovery: only when the document has no areas and has never been scanned.</summary>
	/// <remarks>
	///     Does nothing before <see cref="Attach"/>, since the registry is the whole input. Once-only, so a household
	///     that removes every area does not find them grown back after a restart.
	/// </remarks>
	private void ScheduleAreaDiscoveryIfNeeded(AdaptiveLightingConfig config)
	{
		if (_discoveryScheduled || config.Global.AreasAutoDiscovered || config.Areas.Count > 0)
			return;

		if (_ha is null || _registry is null || _scheduler is null)
			return;

		_discoveryScheduled = true;
		_logger.LogInformation(
			"No areas configured yet — discovering from the Home Assistant area registry in {Seconds}s, once the state cache has filled.",
			DiscoverySettle.TotalSeconds);

		_discovery = _scheduler.Schedule(DiscoverySettle, RunAreaDiscovery);
	}

	/// <summary>The scheduled callback. Exists to stop anything thrown by discovery reaching the scheduler.</summary>
	/// <remarks>
	///     Runs on a timer thread with no caller to catch anything, and on a thread-pool scheduler an unobserved
	///     exception ends the whole host. Reachable: NetDaemon's registry throws
	///     <see cref="InvalidOperationException"/> until its first connection completes, which the settle delay only
	///     guesses at.
	/// </remarks>
	private void RunAreaDiscovery()
	{
		try
		{
			RunAreaDiscoveryCore();
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			_logger.LogWarning(
				exception,
				"Area discovery failed and was abandoned; the configuration is unchanged and the rooms will be proposed again on the next start.");
		}
	}

	/// <summary>Proposes areas from the area registry, saves them, and rebuilds on the result.</summary>
	/// <remarks>
	///     The rules live in <see cref="AreaSetupService"/>, so a first run and "Set up rooms again" are the same
	///     code. What is here is the once-only part.
	/// </remarks>
	private void RunAreaDiscoveryCore()
	{
		lock (_gate)
		{
			if (_ha is null || _registry is null)
				return;

			// Re-read, never the document captured when this was armed: half a minute is plenty of time for somebody
			// to have added a room from the UI.
			AdaptiveLightingConfig config;
			try
			{
				config = _store.Load();
			}
			catch (LightingConfigException exception)
			{
				_logger.LogWarning(exception, "Could not read the configuration for area discovery.");
				return;
			}

			if (config.Global.AreasAutoDiscovered || config.Areas.Count > 0)
				return;

			HaAreaRegistry areas = new(_registry);
			AreaEntityResolver resolver = new(
				_ha, areas, config.Global, _loggerFactory.CreateLogger<AreaEntityResolver>());

			// Empty scope: this path only runs on a document with no areas, so the plan is entirely NewAreas.
			SetupPlan plan = AreaSetupService.Plan(config, areas, resolver, []);

			if (plan.NewAreas.Count == 0)
			{
				// The flag stays unset: finding nothing usually means the scan was too early.
				_logger.LogInformation(
					"No Home Assistant area has both a light and a motion sensor yet. Add rooms under Configuration → Areas, or restart to look again.");
				return;
			}

			AreaSetupService.Apply(config, plan);
			config.Global.AreasAutoDiscovered = true;

			// First setup only, never a re-run: an emptied list must stay empty next start.
			IReadOnlyList<string> seeded = AreaSetupService.SeedPersons(config, _ha);

			// Never overwrites a select the household has already chosen.
			if (config.Global.HouseMode?.Entity is not { Length: > 0 })
				config.Global.HouseMode = HouseModeAutoDetect.Detect(_ha, _loggerFactory.CreateLogger(typeof(HouseModeAutoDetect)));

			try
			{
				_store.Save(config);
			}
			catch (LightingConfigException exception)
			{
				// Areas was empty on the way in, so clearing restores what was loaded. Persons is cleared only when
				// this run filled it, or a document that already named somebody would come out having forgotten them.
				config.Areas.Clear();

				if (seeded.Count > 0)
					config.Global.Persons.Clear();

				config.Global.AreasAutoDiscovered = false;
				_logger.LogWarning(exception, "Could not save the discovered areas; they will be proposed again on the next start.");
				return;
			}

			_logger.LogInformation(
				"Discovered {Count} rooms from the area registry ({Areas}), all switched off. Choose which to switch on "
				+ "under Configuration → Areas — no lights will change until you do.",
				plan.NewAreas.Count, string.Join(", ", plan.NewAreas.Select(area => area.AreaId)));

			if (seeded.Count > 0)
				_logger.LogInformation(
					"Home and Away will follow {Count} people ({Persons}). Change who under Configuration → House.",
					seeded.Count, string.Join(", ", seeded));

			// Discovery rewrote the document during start-up; nobody saved anything.
			ApplyCore(config, EngineNoticeKind.Started);
		}
	}

	/// <summary>The only write path: normalise, validate, write, re-read, rebuild every area controller.</summary>
	/// <remarks>
	///     The order matters. Document-level errors are refused before anything reaches the disk, so a bad save
	///     cannot leave the host unable to start next time. Area-level errors do not refuse the save.
	/// </remarks>
	public SaveResult Save(AdaptiveLightingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		lock (_gate)
		{
			config = ConfigNormalizer.Normalize(config);

			ValidationResult validation = Validate(config);

			if (!validation.IsValid)
			{
				LastValidation = validation;
				_logger.LogWarning(
					"Refused to save the lighting configuration: {Count} document-level errors.", validation.Errors.Count);

				return new SaveResult(
					SaveStatus.Rejected,
					validation,
					"Not saved. The file on disk and the running engine are untouched.");
			}

			try
			{
				_store.Save(config);
			}
			catch (LightingConfigException exception)
			{
				LastValidation = validation;
				_logger.LogError(exception, "Could not write the lighting configuration.");

				return new SaveResult(SaveStatus.Failed, validation, exception.Message);
			}

			// Re-read, never the in-memory object: a save is reported successful only once the bytes on disk parse
			// back into a document the engine accepts, which is what matters after a restart.
			return ApplyCore(_store.Load(), EngineNoticeKind.SettingsSaved);
		}
	}

	/// <summary>
	///     Validates <paramref name="config"/> against what Home Assistant currently knows, without saving anything.
	///     Referential checks are skipped when HA is not connected.
	/// </summary>
	public ValidationResult Validate(AdaptiveLightingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		// Resolved in memory before validating, because it is what EffectiveKillSwitchEntity, and so the validator,
		// sees when the document leaves KillSwitchEntity unset. Never written back.
		config.Global.DefaultKillSwitchEntity = _defaultKillSwitchEntity;

		// The two selects are different helpers, so their live options are read separately.
		return ConfigValidator.Validate(
			config,
			KnownEntityIds(),
			KnownAreaIds(),
			LiveSelectOptions(config.Global.HouseMode?.Entity),
			LabelsInUse(),
			LiveSelectOptions(config.Global.PeriodSelect?.EntityId));
	}

	public void Dispose()
	{
		lock (_gate)
		{
			// An armed discovery holds the scheduler and would resurrect work after shutdown.
			_discovery?.Dispose();
			_discovery = null;

			StopCore();
			_ha = null;
			_registry = null;
			_scheduler = null;
			_notices.Dispose();
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

		StopCore();

		try
		{
			LightingOrchestrator orchestrator = new(
				_ha,
				_registry,
				_scheduler,
				config,
				new HaLightActuator(_ha, config.Global, _loggerFactory.CreateLogger<HaLightActuator>()),
				new HaStatePublisher(_ha, _loggerFactory.CreateLogger<HaStatePublisher>()),
				new HaNotifier(_ha, _loggerFactory.CreateLogger<HaNotifier>()),
				_loggerFactory,
				_lastSeen,
				_lastPeriod);

			orchestrator.Start();

			_orchestrator = orchestrator;
			Fault = null;
			LastStartedUtc = DateTimeOffset.UtcNow;

			_logger.LogInformation(
				"Adaptive lighting is running: {Areas} of {Configured} areas resolved.",
				orchestrator.Areas.Count, config.Areas.Count);

			_notices.OnNext(new EngineNotice(notice, DateTimeOffset.Now));

			return new SaveResult(SaveStatus.Saved, validation, $"Saved: {orchestrator.Areas.Count} of {config.Areas.Count} rooms are running.");
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			// Broad on purpose. A narrower filter once let a NullReferenceException from mode construction escape to
			// the Blazor circuit, where the save silently rendered nothing.
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

	private void Notify(ValidationResult validation)
	{
		if (_ha is null)
			return;

		try
		{
			new HaNotifier(_ha, _loggerFactory.CreateLogger<HaNotifier>()).Notify(InvalidConfigTitle, validation.ToHtml());
		}
		catch (InvalidOperationException exception)
		{
			// No live connection. Reporting the config problem must not become a second problem.
			_logger.LogWarning(exception, "Could not post the invalid-configuration notification to Home Assistant.");
		}
	}

	/// <summary>
	///     Every entity id Home Assistant knows, or <c>null</c> when it cannot be asked. The validator reads
	///     <c>null</c> as "skip the referential checks", not as "nothing exists".
	/// </summary>
	private IReadOnlyCollection<string>? KnownEntityIds()
	{
		if (_ha is null)
			return null;

		try
		{
			return [.. _ha.GetAllEntities().Select(entity => entity.EntityId)];
		}
		catch (InvalidOperationException)
		{
			// NetDaemon's state cache throws until its first connection to HA completes.
			return null;
		}
	}

	private IReadOnlyCollection<string>? KnownAreaIds()
	{
		if (_registry is null)
			return null;

		try
		{
			return _registry.AreaIds();
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}

	/// <summary>
	///     Every label at least one entity carries, by id and by name, or <c>null</c> when the registry cannot be
	///     read.
	/// </summary>
	/// <remarks>
	///     Both forms, because <see cref="AdaptiveLighting.Extensions.RegistryExtensions.LabelsOf"/> matches either
	///     way. Labels nobody carries are left out: one on no entity filters every light out as thoroughly as a typo.
	/// </remarks>
	private IReadOnlyCollection<string>? LabelsInUse()
	{
		if (_registry is null)
			return null;

		try
		{
			return
			[
				.. _registry.Labels
					.Where(label => label.Entities.Any())
					.SelectMany(label => new[] { label.Id, label.Name })
					.OfType<string>()
					.Where(value => value.Length > 0)
			];
		}
		catch (InvalidOperationException)
		{
			// NetDaemon's registry throws until its first connection to HA completes.
			return null;
		}
	}

	/// <summary>
	///     The live <c>options</c> of a select, or <c>null</c> when there is no select, no connection, or the
	///     attribute cannot be read. The validator reads <c>null</c> as "skip the live-option warnings".
	/// </summary>
	private IReadOnlyCollection<string>? LiveSelectOptions(string? entityId)
	{
		if (_ha is null || entityId is not { Length: > 0 })
			return null;

		try
		{
			IReadOnlyList<string> options = _ha.GetState(entityId).AttrStringList("options");
			return options.Count > 0 ? options : null;
		}
		catch (InvalidOperationException)
		{
			// NetDaemon's state cache throws until its first connection to HA completes.
			return null;
		}
	}
}
