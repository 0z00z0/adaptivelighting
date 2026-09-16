using System.Reactive.Concurrency;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Ha;

namespace AdaptiveLighting.Hosting;

/// <summary>Proposes rooms from the Home Assistant area registry, once, on a house that has none.</summary>
/// <remarks>
///     The rules live in <see cref="AreaSetupService"/>, so a first run and "Set up rooms again" share them. What is
///     here is the timing and the once-only part. The scan runs behind the host's gate, because it writes the
///     document and rebuilds the engine on the result.
/// </remarks>
internal sealed class AreaDiscoveryScheduler : IDisposable
{
	/// <summary>How long to let Home Assistant's state cache fill before discovering areas.</summary>
	/// <remarks>
	///     Discovery must not run inline in <see cref="LightingEngineHost.Reload"/>: the reload follows
	///     <see cref="LightingEngineHost.Attach"/> at once while NetDaemon's state cache is still filling, and the
	///     resolver drops any entity without a state, so an early scan proposes a partial set of rooms and the
	///     once-only flag locks that in.
	/// </remarks>
	private static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);

	private readonly Lock _gate;
	private readonly LightingConfigStore _store;
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger _logger;

	// The host's write, already the only thing allowed to touch the document. Null comes back when the write threw.
	private readonly Func<AdaptiveLightingConfig, ConfigWriteResult?> _write;

	// The host's report-and-rebuild, so the discovered rooms are running before anything else looks at them.
	private readonly Action<AdaptiveLightingConfig, ValidationResult> _adopt;

	private IDisposable? _armed;
	private bool _scheduled;
	private IHaContext? _ha;
	private IHaRegistry? _registry;

	public AreaDiscoveryScheduler(
		Lock gate,
		LightingConfigStore store,
		ILoggerFactory loggerFactory,
		Func<AdaptiveLightingConfig, ConfigWriteResult?> write,
		Action<AdaptiveLightingConfig, ValidationResult> adopt)
	{
		_gate = gate ?? throw new ArgumentNullException(nameof(gate));
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
		_logger = loggerFactory.CreateLogger<AreaDiscoveryScheduler>();
		_write = write ?? throw new ArgumentNullException(nameof(write));
		_adopt = adopt ?? throw new ArgumentNullException(nameof(adopt));
	}

	/// <summary>Arms the scan: only on a document with no areas that has never been scanned, and only while connected.</summary>
	/// <remarks>
	///     Once per attached connection, so a household that removes every area does not find them grown back on a
	///     reload. Switching the app off in Home Assistant is what asks for another scan; see <see cref="Cancel"/>.
	/// </remarks>
	public void ArmIfNeeded(AdaptiveLightingConfig config, IHaContext? ha, IHaRegistry? registry, IScheduler? scheduler)
	{
		ArgumentNullException.ThrowIfNull(config);

		if (_scheduled || config.Global.AreasAutoDiscovered || config.Areas.Count > 0)
			return;

		if (ha is null || registry is null || scheduler is null)
			return;

		_ha = ha;
		_registry = registry;
		_scheduled = true;

		_logger.LogInformation(
			"No areas configured yet — discovering from the Home Assistant area registry in {Seconds}s, once the state cache has filled.",
			Settle.TotalSeconds);

		_armed = scheduler.Schedule(Settle, Run);
	}

	/// <summary>Cancels anything pending and forgets that a scan was armed, so the next connection can arm one.</summary>
	public void Cancel()
	{
		// An armed scan holds the scheduler and would otherwise resurrect work against a connection that is gone.
		_armed?.Dispose();
		_armed = null;
		_scheduled = false;
		_ha = null;
		_registry = null;
	}

	public void Dispose() => Cancel();

	/// <summary>The scheduled callback. Exists to stop anything thrown by the scan reaching the scheduler.</summary>
	/// <remarks>
	///     Runs on a timer thread with no caller to catch anything, and on a thread-pool scheduler an unobserved
	///     exception ends the whole host. Reachable: NetDaemon's registry throws <see cref="InvalidOperationException"/>
	///     until its first connection completes, which the settle delay only guesses at.
	/// </remarks>
	private void Run()
	{
		try
		{
			Scan();
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			_logger.LogWarning(
				exception,
				"Area discovery failed and was abandoned; the configuration is unchanged and the rooms will be proposed again on the next start.");
		}
	}

	private void Scan()
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

			if (_write(config) is not { } write)
			{
				// Areas was empty on the way in, so clearing restores what was loaded. Persons is cleared only when
				// this run filled it, or a document that already named somebody would come out having forgotten them.
				config.Areas.Clear();

				if (seeded.Count > 0)
					config.Global.Persons.Clear();

				config.Global.AreasAutoDiscovered = false;

				return;
			}

			_logger.LogInformation(
				"Discovered {Count} rooms from the area registry ({Areas}), all switched off. Choose which to switch on "
				+ "under Configuration → Areas — no lights will change until you do.",
				plan.NewAreas.Count, string.Join(", ", plan.NewAreas.Select(area => area.AreaId)));

			if (seeded.Count > 0)
				_logger.LogInformation(
					"The dashboard will show who is home from {Count} people ({Persons}); the house-mode dropdown is what "
					+ "decides whether the house is away. Change who under Configuration → House.",
					seeded.Count, string.Join(", ", seeded));

			_adopt(config, write.Validation);
		}
	}
}
