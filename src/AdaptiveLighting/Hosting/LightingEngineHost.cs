using System.Reactive.Concurrency;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Ha;

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

/// <summary>
///     The outcome of a save.
/// </summary>
/// <param name="Status">What happened.</param>
/// <param name="Validation">
///     The validator's verdict on the submitted document. On <see cref="SaveStatus.Rejected"/> this is why.
///     On <see cref="SaveStatus.Saved"/> it may still carry area errors: those cost an area, not the save.
/// </param>
/// <param name="Message">A sentence for the operator. Never a restatement of <paramref name="Validation"/>.</param>
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
///     <para>
///         <b>Why this exists.</b> The engine used to be built inside the per-host <c>[NetDaemonApp]</c> bootstrap,
///         which meant the orchestrator's lifetime was the app's lifetime and nothing outside the app could touch
///         it. A UI that saves configuration has to be able to say "that document is now the truth, rebuild" —
///         so the lifetime moves here, to a singleton both the bootstrap and the web UI can reach, and the
///         bootstrap becomes the thing that hands over Home Assistant and starts the first load.
///         <see cref="LightingOrchestrator"/> was made <see cref="IDisposable"/> with airtight subscription
///         cleanup for exactly this; <see cref="Reload"/> is the seam finally being used.
///     </para>
///     <para>
///         <b>The engine no longer throws on a bad document, and that is a deliberate reversal.</b> The original
///         design had the bootstrap throw, putting the app into <c>ApplicationState.Error</c> so the failure was
///         loud. But an app in <c>Error</c> has been disposed along with its DI scope, and its <c>IHaContext</c>
///         with it — which would leave this host holding a dead Home Assistant connection and no way to rebuild
///         anything. The browser could then save a corrected file and still not start the engine, which is the
///         one thing this whole feature exists to do. So a bad document now leaves the host attached, running,
///         and reporting itself as faulted: the persistent notification in Home Assistant still fires, the errors
///         are still logged, and the web UI shows them — but the process stays in a state a human can fix from
///         the browser.
///     </para>
///     <para>
///         Token safety: nothing here reads <c>IConfiguration</c>. The file path arrives already resolved on
///         <see cref="LightingConfigStore"/>, and the only configuration object this class ever touches is
///         <see cref="AdaptiveLightingConfig"/>, which carries no credentials.
///     </para>
/// </remarks>
public sealed class LightingEngineHost : IDisposable
{
	private const string InvalidConfigTitle = "Adaptive lighting: invalid configuration";

	private readonly LightingConfigStore _store;
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger<LightingEngineHost> _logger;

	// Every transition of the orchestrator goes through this. A save from one browser tab and a save from
	// another must not interleave a Dispose with a Start.
	private readonly Lock _gate = new();

	private IHaContext? _ha;
	private IHaRegistry? _registry;
	private IScheduler? _scheduler;
	private string? _defaultKillSwitchEntity;
	private LightingOrchestrator? _orchestrator;

	/// <summary>Creates the host. Nothing runs until <see cref="Attach"/> and <see cref="Reload"/>.</summary>
	/// <param name="store">The configuration document, and the only file this host reads or writes.</param>
	/// <param name="loggerFactory">Builds the loggers for every part of the engine.</param>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public LightingEngineHost(LightingConfigStore store, ILoggerFactory loggerFactory)
	{
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
		_logger = loggerFactory.CreateLogger<LightingEngineHost>();
	}

	/// <summary>The configuration file this host reads and writes.</summary>
	public LightingConfigStore Store => _store;

	/// <summary>
	///     The app's built-in enable switch (09 §7), from <see cref="Attach"/>, or <c>null</c> before the per-host
	///     bootstrap has handed it over. Readers use it as the kill switch whenever the document leaves
	///     <c>KillSwitchEntity</c> unset; it is never written to YAML.
	/// </summary>
	public string? DefaultKillSwitchEntity => _defaultKillSwitchEntity;

	/// <summary>
	///     Whether the per-host <c>[NetDaemonApp]</c> bootstrap has handed over Home Assistant yet. When
	///     <c>false</c> the UI can still edit and save, but nothing can be started or validated against the
	///     registry.
	/// </summary>
	public bool IsAttached => _ha is not null;

	/// <summary>Whether an orchestrator is currently running.</summary>
	public bool IsRunning => _orchestrator is not null;

	/// <summary>How many areas resolved and are being commanded. Zero while faulted.</summary>
	public int RunningAreaCount => _orchestrator?.Areas.Count ?? 0;

	/// <summary>The validator's verdict from the last load, or <c>null</c> before the first one.</summary>
	public ValidationResult? LastValidation { get; private set; }

	/// <summary>Why the engine is not running, or <c>null</c> when it is.</summary>
	public string? Fault { get; private set; }

	/// <summary>When the engine last started, or <c>null</c> if it never has.</summary>
	public DateTimeOffset? LastStartedUtc { get; private set; }

	/// <summary>
	///     Hands this host the Home Assistant connection it rebuilds against. Called once, by the per-host
	///     <c>[NetDaemonApp]</c> bootstrap, which is the only thing the app model gives a live scope to.
	/// </summary>
	/// <param name="ha">The HA context the engine reads and commands through.</param>
	/// <param name="registry">Source of areas and labels for area discovery.</param>
	/// <param name="scheduler">The engine's only clock.</param>
	/// <param name="defaultKillSwitchEntity">
	///     The app's built-in enable switch (09 §7), from <see cref="NetDaemonAppSwitch.EntityIdFor"/>. Used in
	///     memory as the kill switch whenever the document leaves <c>KillSwitchEntity</c> unset; never written to YAML.
	/// </param>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
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
			Fault = "The lighting app was stopped, so the engine has no Home Assistant connection.";
		}
	}

	/// <summary>
	///     Reads the current document from disk and, if it can be run, replaces the running engine with one
	///     built on it.
	/// </summary>
	/// <remarks>
	///     Never throws. A configuration this host cannot run is a state to report, not an exception to
	///     propagate — the caller is either a NetDaemon app whose death would take the connection with it, or a
	///     Razor component rendering a page.
	/// </remarks>
	/// <returns>What happened, in the same shape a save reports.</returns>
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

			return ApplyCore(config);
		}
	}

	/// <summary>
	///     Writes a document that loaded through the pre-2.0 key names straight back out in the current schema.
	/// </summary>
	/// <remarks>
	///     <para>
	///         On the first load rather than on the next save, and before the engine is built: a house that never
	///         opens the web UI would otherwise keep a file only <c>LegacyKeys</c> can read, indefinitely, and the
	///         translation table's job would quietly become permanent instead of transitional.
	///     </para>
	///     <para>
	///         The write goes through <see cref="LightingConfigStore.Save"/> — which keeps the file it replaced at
	///         <see cref="LightingConfigStore.BackupPath"/> — precisely so this needs no backup mechanism of its
	///         own: the pre-migration document survives at the path the Configuration page already shows.
	///     </para>
	///     <para>
	///         A failed write is a warning, not a fault. The document in memory is the same either way, so the
	///         engine still starts on it; a read-only <c>/config</c> just means the translation happens again on
	///         the next start, which is exactly what it is for.
	///     </para>
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
	///     Discovery used to run inline in <see cref="Reload"/>, and it was wrong: the reload happens immediately
	///     after <see cref="Attach"/>, when NetDaemon has connected but its state cache is still filling. The
	///     resolver drops any entity without a state — a registry row with no state is not a device — so an early
	///     scan sees a partial house, proposes a partial set of rooms, and the once-only flag then locks that in.
	///     Observed on a real installation: four rooms that plainly had lights and motion were missed because their
	///     entities had not arrived yet. Waiting costs nothing — the area list is empty either way — and is the
	///     difference between discovering a house and discovering whatever happened to load first.
	/// </remarks>
	private static readonly TimeSpan DiscoverySettle = TimeSpan.FromSeconds(30);

	private IDisposable? _discovery;
	private bool _discoveryScheduled;

	/// <summary>
	///     Arms the one-time area discovery: only when the document has no areas and has never been scanned.
	/// </summary>
	/// <remarks>
	///     Does nothing before <see cref="Attach"/> — the registry is the whole input — so the reload that follows
	///     Attach is what arms it. The flag makes it once-only: a household that deliberately removes every area
	///     must not find them grown back after a restart.
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

	/// <summary>
	///     The scheduled callback. Exists only to make sure nothing thrown by discovery reaches the scheduler.
	/// </summary>
	/// <remarks>
	///     This runs on a timer thread half a minute after start-up, with no caller to catch anything: an exception
	///     out of here is unobserved, and on a thread-pool scheduler an unobserved exception ends the process — the
	///     whole Home Assistant host, not just the lighting engine. And it is not hypothetical. The settle delay is
	///     a guess at how long Home Assistant needs; a house on a slow link can still be filling its registry when
	///     the timer fires, and NetDaemon's registry throws <see cref="InvalidOperationException"/> until its first
	///     connection completes — which <see cref="KnownAreaIds"/> and <see cref="LabelsInUse"/> in this same class
	///     already catch for exactly that reason. Discovery finding nothing is a thing to log and try again next
	///     start; it is never a reason to take the house down.
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
	///     The rules themselves live in <see cref="AreaSetupService"/>, so that a first run and the owner pressing
	///     "Set up rooms again" are the same code observed twice. What is left here is the once-only part: re-read,
	///     plan, apply, seed the people, set the flag, save.
	/// </remarks>
	private void RunAreaDiscoveryCore()
	{
		lock (_gate)
		{
			if (_ha is null || _registry is null)
				return;

			// Re-read rather than trusting the document captured when this was armed: half a minute is plenty of
			// time for somebody to have added a room from the UI, and discovery must not overwrite that.
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

			// Nothing to rebuild: this path only runs on a document with no areas at all, so the scope is empty and
			// the plan is entirely NewAreas. The re-run from the UI is the same call with rooms ticked.
			SetupPlan plan = AreaSetupService.Plan(config, areas, resolver, []);

			if (plan.NewAreas.Count == 0)
			{
				// The flag deliberately stays unset. Finding nothing is far more likely to mean "asked too early"
				// than "this house has no lit rooms", and looking again on the next start costs nothing.
				_logger.LogInformation(
					"No Home Assistant area has both a light and a motion sensor yet. Add rooms on the Configuration page, or restart to look again.");
				return;
			}

			AreaSetupService.Apply(config, plan);
			config.Global.AreasAutoDiscovered = true;

			// Only at first setup, never on a re-run: a household that deliberately empties the list must find it
			// still empty next start, the same one-way principle as the flag above.
			IReadOnlyList<string> seeded = AreaSetupService.SeedPersons(config, _ha);

			// The house mode is part of the same "look before asking" idea: most houses already have the dropdown,
			// and only its meaning needs stating. Never overwrites one the household has already chosen.
			if (config.Global.HouseMode?.Entity is not { Length: > 0 })
				config.Global.HouseMode = HouseModeAutoDetect.Detect(_ha, _loggerFactory.CreateLogger(typeof(HouseModeAutoDetect)));

			try
			{
				_store.Save(config);
			}
			catch (LightingConfigException exception)
			{
				// Areas was empty on the way in, so clearing restores exactly what was loaded; the people list is
				// only cleared when this run is what filled it, or a document that already named somebody would
				// come out of a failed write having forgotten them.
				config.Areas.Clear();

				if (seeded.Count > 0)
					config.Global.Persons.Clear();

				config.Global.AreasAutoDiscovered = false;
				_logger.LogWarning(exception, "Could not save the discovered areas; they will be proposed again on the next start.");
				return;
			}

			_logger.LogInformation(
				"Discovered {Count} areas from the area registry ({Areas}), all switched off. Choose which to switch on "
				+ "from the Configuration page — no lights will change until you do.",
				plan.NewAreas.Count, string.Join(", ", plan.NewAreas.Select(area => area.AreaId)));

			if (seeded.Count > 0)
				_logger.LogInformation(
					"Home and Away will follow {Count} people ({Persons}). Change who on the Configuration page.",
					seeded.Count, string.Join(", ", seeded));

			ApplyCore(config);
		}
	}

	/// <summary>
	///     Validates <paramref name="config"/>, writes it, and rebuilds the engine on it.
	/// </summary>
	/// <remarks>
	///     The order is the point: a document with document-level errors is refused <i>before</i> anything
	///     reaches the disk, so a bad save cannot leave the host unable to start next time. Area-level errors do
	///     not refuse the save — an area naming an entity Home Assistant has since renamed must cost that area,
	///     not the household's ability to fix the rest of the file.
	/// </remarks>
	/// <param name="config">The edited document.</param>
	/// <returns>What happened, with the validator's own messages attached.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="config"/> is <c>null</c>.</exception>
	public SaveResult Save(AdaptiveLightingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		lock (_gate)
		{
			// Normalise before validating and writing: a document that has adopted the house-mode model sheds its
			// now-redundant deprecated keys, but only once they are provably safe to drop (07 §5.3).
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

			// Re-read rather than applying the in-memory object. It costs one file read and it means a save is
			// only reported as successful once the bytes on disk parse back into a document the engine accepts —
			// which is the property that actually matters after a restart.
			return ApplyCore(_store.Load());
		}
	}

	/// <summary>
	///     Validates <paramref name="config"/> against what Home Assistant currently knows, without saving
	///     anything. This is what the editor calls to show problems before the operator commits to them.
	/// </summary>
	/// <param name="config">The document to check.</param>
	/// <returns>The validator's verdict. Referential checks are skipped when HA is not connected.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="config"/> is <c>null</c>.</exception>
	public ValidationResult Validate(AdaptiveLightingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		// Resolve the built-in master switch in memory (09 §7) before validating: it is what
		// EffectiveKillSwitchEntity — and therefore every reader, including the validator — sees when the document
		// leaves KillSwitchEntity unset. Never written back to the document.
		config.Global.DefaultKillSwitchEntity = _defaultKillSwitchEntity;

		return ConfigValidator.Validate(config, KnownEntityIds(), KnownAreaIds(), LiveSelectOptions(config.Global.HouseMode?.Entity), LabelsInUse());
	}

	/// <summary>Stops the engine and drops the Home Assistant connection.</summary>
	public void Dispose()
	{
		lock (_gate)
		{
			// An armed discovery holds the scheduler; letting it fire against a torn-down host would resurrect
			// work after shutdown.
			_discovery?.Dispose();
			_discovery = null;

			StopCore();
			_ha = null;
			_registry = null;
			_scheduler = null;
		}
	}

	private SaveResult ApplyCore(AdaptiveLightingConfig config)
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
			Fault = "The configuration on disk has document-level errors, so no engine is running.";

			_logger.LogError(
				"Adaptive lighting configuration is invalid, engine stopped:{NewLine}{Validation}",
				Environment.NewLine, validation);

			// Notify as well as log: the log only reaches whoever is tailing the add-on, and this is the
			// failure nobody notices otherwise.
			Notify(validation);

			return new SaveResult(SaveStatus.Failed, validation, "Saved, but the engine cannot run this document.");
		}

		if (_ha is null || _registry is null || _scheduler is null)
		{
			Fault = "The lighting app has not started yet, so the engine has no Home Assistant connection.";
			_logger.LogWarning("Configuration is valid but no Home Assistant connection is attached; not starting.");

			return new SaveResult(SaveStatus.Saved, validation, "Saved. The engine will start when the host connects to Home Assistant.");
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
				_loggerFactory);

			orchestrator.Start();

			_orchestrator = orchestrator;
			Fault = null;
			LastStartedUtc = DateTimeOffset.UtcNow;

			_logger.LogInformation(
				"Adaptive lighting is running: {Areas} of {Configured} areas resolved.",
				orchestrator.Areas.Count, config.Areas.Count);

			return new SaveResult(SaveStatus.Saved, validation, $"Engine rebuilt: {orchestrator.Areas.Count} of {config.Areas.Count} areas are running.");
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			// A rebuild that throws must not take the process, or the web UI that could fix it, down with it —
				// whatever the exception. A narrower filter here once let a NullReferenceException from mode
				// construction escape to the Blazor circuit, where the save silently rendered nothing. Every failure
				// is now caught (bar the two unrecoverable ones), logged with its stack trace, and reported as a
				// failed save the operator can see.
			StopCore();
			Fault = $"The engine failed to start: {exception.Message}";
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
			// No live connection. Reporting the config problem must not become a second, different problem.
			_logger.LogWarning(exception, "Could not post the invalid-configuration notification to Home Assistant.");
		}
	}

	/// <summary>
	///     Every entity id Home Assistant knows, or <c>null</c> when it cannot be asked — which the validator
	///     reads as "skip the referential checks" rather than "nothing exists".
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
	///     Every label at least one entity carries, listed by id <i>and</i> by name, or <c>null</c> when the
	///     registry cannot be read — which the validator reads as "skip the include-label check".
	/// </summary>
	/// <remarks>
	///     Both forms, because <see cref="AdaptiveLighting.Extensions.RegistryExtensions.LabelsOf"/> matches either
	///     way and the validator must not warn about a label the resolver would happily have found. Labels nobody
	///     carries are left out on purpose: a label that exists in Home Assistant but is on no entity filters every
	///     light out just as thoroughly as a typo, and that is precisely the case worth warning about.
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
	///     The live <c>options</c> of the configured house-mode select, or <c>null</c> when there is no select, no
	///     connection, or the attribute cannot be read — which the validator reads as "skip the live-option warnings".
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
