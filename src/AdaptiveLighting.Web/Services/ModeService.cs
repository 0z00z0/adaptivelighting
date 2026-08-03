using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Hosting;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Web.Services;

/// <param name="EntityId">The configured entity. Always from the config document, never from a request.</param>
/// <param name="Meaning">What the current state means for the engine, in words.</param>
public sealed record ModeToggle(
	string Label,
	string Description,
	string EntityId,
	bool IsOn,
	bool IsAvailable,
	bool CanToggle,
	string Meaning);

/// <summary>
///     What a mode would do to the lights, resolved for right now, so a card can say what a mode means before
///     anyone selects it. Read-only, and derived from the one shared circadian table.
/// </summary>
/// <param name="IsOffPreview">Whether the swatch should read "dark": an Away mode pauses and sweeps the areas.</param>
public sealed record ModePreview(
	string? ActivePeriodName,
	double? PreviewBrightness,
	int? PreviewKelvin,
	bool IsOffPreview,
	string EffectSummary);

/// <summary>One live option of the house-mode select, with the single kind and reset summary the config gave it.</summary>
/// <param name="Scene">The <c>scene.*</c> applied on entry, for Away/Guest; <c>null</c> otherwise or when unset.</param>
/// <param name="ClampPeriod">The resolved sleep-clamp period name, for Sleep; <c>null</c> otherwise.</param>
/// <param name="ResetSummary">A one-line summary of the reset triggers, or <c>null</c> for a Normal option.</param>
public sealed record HouseModeOptionView(
	string Value,
	ModeKind Kind,
	bool IsCurrent,
	string? Scene,
	string? ClampPeriod,
	string? ResetSummary,
	ModePreview Preview);

/// <summary>
///     The derived house state the modes produce, for the live hero: the same <c>ActiveKind</c> the engine's
///     <see cref="Engine.ModeMonitor"/> reads.
/// </summary>
/// <param name="IsAvailable">Whether Home Assistant answered. When false the state is "unknown", not "off".</param>
public sealed record HouseDerivedState(ModeKind ActiveKind, bool IsAvailable);

/// <summary>The house-mode select as the Modes page shows it: its options, current value, and Normal-fallback note.</summary>
/// <param name="Entity">The configured <c>input_select</c>. Always from the config document, never from a request.</param>
/// <param name="Options">The live options unioned with the configured ones, each with its kind.</param>
/// <param name="NoNormalOption">Whether no option is marked Normal, so there is no reset target and resets no-op.</param>
public sealed record HouseModeView(
	string Entity,
	string? CurrentValue,
	IReadOnlyList<HouseModeOptionView> Options,
	bool NoNormalOption,
	string? NormalFallbackValue,
	bool IsAvailable);

/// <summary>
///     The engine's master switch as the dashboard shows it: the toggle to flip, plus whether adaptive lighting is
///     currently commanding.
/// </summary>
/// <remarks>A read-only projection; it adds no write path. The one write is <see cref="ModeService.Toggle"/>.</remarks>
/// <param name="AdaptiveLightingOn">Whether the engine may command, with the switch read in its configured polarity.</param>
/// <param name="IsReady">Whether Home Assistant answered. When false the state is "unknown", not "off".</param>
public sealed record MasterSwitchView(
	ModeToggle Toggle,
	bool AdaptiveLightingOn,
	bool IsAvailable,
	bool IsReady);

/// <summary>One watched person, for the dashboard's who's-home panel. Read-only.</summary>
/// <param name="IsAvailable">Whether Home Assistant knew the entity; <c>false</c> renders as "unknown", not "away".</param>
public sealed record PersonView(string EntityId, string Name, bool IsHome, bool IsAvailable);

/// <summary>
///     One room as the dashboard reads it: how to recognise its live reports, whether the owner switched it on,
///     and how many lights it would drive.
/// </summary>
/// <remarks>
///     A projection, not the <see cref="AreaConfig"/> itself. The document behind it is a cached copy shared by
///     every read on the page, and handing a page a mutable room invites an edit outside the save pipeline, which
///     is the only write path.
/// </remarks>
/// <param name="AreaId">
///     The registry area id, or <c>null</c> for a room configured with explicit entities. This is how a live
///     report is matched back to the room that produced it: ids survive a rename mid-session, names do not.
/// </param>
/// <param name="Name">The room's display name, and the fallback match for a report with no id.</param>
/// <param name="LightCount">
///     How many lights the room would drive: its pinned list when it has one, discovery's answer otherwise.
///     0 when Home Assistant has not answered yet.
/// </param>
public sealed record RoomView(string? AreaId, string Name, bool IsEnabled, int LightCount);

/// <summary>
///     Reads and flips the house mode entities named in the configuration document.
/// </summary>
/// <remarks>
///     The whole of the UI's write surface. Entity ids come only from <see cref="AdaptiveLightingConfig"/>: a
///     caller cannot name one, because <see cref="Toggle"/> re-resolves against the config before calling. That is
///     what keeps this from becoming a general service-call proxy. The state change flows back through the
///     engine's normal <c>ModeMonitor</c> path; this class tells the engine nothing.
/// </remarks>
public sealed class ModeService
{
	/// <summary>
	///     Domains this service is willing to call <c>turn_on</c>/<c>turn_off</c> on. <c>binary_sensor</c> is a
	///     legitimate mode entity to read but nothing can write to one, so it renders as untoggleable.
	/// </summary>
	private static readonly string[] ToggleableDomains = ["input_boolean", "switch", "light", "fan"];

	// Both match Engine.PresenceMonitor. If they drift, the panel shows different people from the ones whose
	// presence drives Home and Away.
	private const string PersonDomain = "person";
	private const string HomeState = "home";

	private readonly IHaContext _ha;
	private readonly AdaptiveLightingConfig _seedConfig;
	private readonly HaCatalog _catalog;
	private readonly LightingEngineHost _engine;
	private readonly ILogger<ModeService> _logger;

	// Cached against the store file's last-write time, so the per-second dashboard ticker costs a stat, not a
	// YAML parse.
	private AdaptiveLightingConfig? _cachedConfig;
	private DateTimeOffset? _cachedStamp;

	/// <summary>
	///     Whether Home Assistant's state cache answered the last time <see cref="GetToggles"/> asked.
	/// </summary>
	/// <remarks>
	///     Kestrel serves the moment the process is up, but NetDaemon connects asynchronously and its state cache
	///     throws until that finishes. Every page here is reachable in that window, and again whenever Home
	///     Assistant is down, so this is a condition to render, not an error to throw on.
	/// </remarks>
	public bool IsHomeAssistantReady { get; private set; } = true;

	/// <param name="config">The bound configuration document; the only source of entity ids.</param>
	public ModeService(IHaContext ha, IAppConfig<AdaptiveLightingConfig> config, HaCatalog catalog, LightingEngineHost engine, ILogger<ModeService> logger)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(engine);

		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_seedConfig = config.Value;
		_catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
		_engine = engine;
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>
	///     The live configuration document, the same file the engine and the config editor read, re-read
	///     whenever it changes on disk. Falls back to the NetDaemon-bound seed only before anything has ever been
	///     saved (the store file does not exist yet).
	/// </summary>
	/// <remarks>
	///     Reads the store, never <c>IAppConfig</c>. That is frozen at process start and no longer reflects a UI
	///     save, so anything configured since the host started would be invisible here. A parse failure keeps the
	///     last good document instead of blanking the page mid-save.
	/// </remarks>
	private AdaptiveLightingConfig Config
	{
		get
		{
			if (!_engine.Store.Exists)
				return _seedConfig;

			DateTimeOffset? stamp = _engine.Store.LastWrittenUtc;
			if (_cachedConfig is null || stamp != _cachedStamp)
			{
				try
				{
					_cachedConfig = _engine.Store.Load();
					_cachedStamp = stamp;
				}
				catch (LightingConfigException exception)
				{
					_logger.LogDebug(exception, "Could not read the lighting configuration for the dashboard; keeping the last good copy.");
					return _cachedConfig ?? _seedConfig;
				}
			}

			return _cachedConfig ?? _seedConfig;
		}
	}

	/// <summary>
	///     Copies the engine's built-in enable switch onto the shared config before every read.
	/// </summary>
	/// <remarks>
	///     On every read, not once at construction. This service is scoped while <see cref="LightingEngineHost"/>
	///     is a singleton, so a scope can be built before the bootstrap calls <c>Attach</c>; copying once freezes
	///     <c>DefaultKillSwitchEntity</c> at <c>null</c> and the master switch never appears until a page reload.
	/// </remarks>
	private void SyncDefaultKillSwitch() => Config.Global.DefaultKillSwitchEntity = _engine.DefaultKillSwitchEntity;

	/// <summary>
	///     The modes this host has configured, with their current state. An unconfigured mode is absent rather
	///     than disabled, matching the engine, which treats it as permanently inactive.
	/// </summary>
	public IReadOnlyList<ModeToggle> GetToggles()
	{
		SyncDefaultKillSwitch();

		var toggles = new List<ModeToggle>(1);
		GlobalConfig global = Config.Global;

		// One method owns the reset of IsHomeAssistantReady to true. With a select configured that is
		// GetHouseMode and this one only downgrades; with no select GetHouseMode returns before touching it, so
		// this one resets. Reset from both and a real disconnection gets masked; from neither and a stale false
		// never clears.
		if (global.HouseMode?.Entity is not { Length: > 0 })
			IsHomeAssistantReady = true;

		// A blank KillSwitchEntity defaults to the app's own enable switch, and the polarity is forced to
		// enabled-flag while defaulted.
		if (global.EffectiveKillSwitchEntity is { Length: > 0 } killSwitch)
		{
			var defaulted = global.KillSwitchIsDefaulted;
			var enabledFlag = defaulted || global.KillSwitchActiveWhenOff;

			var isOn = IsOn(killSwitch);
			var engineEnabled = enabledFlag ? isOn : !isOn;

			toggles.Add(Build(
				"Adaptive lighting",
				defaulted
					? "The master switch. Off pauses the whole app, this page included."
					: enabledFlag
						? "The master switch. Off pauses all automatic lighting."
						: "The master switch, read inverted: on means paused.",
				killSwitch,
				engineEnabled ? "Running — lights adjust automatically." : "Paused — no lights will change."));
		}

		return toggles;
	}

	/// <summary>
	///     The engine's master switch, projected for the dashboard. <c>null</c> before the built-in default
	///     resolves, the same window in which <see cref="GetToggles"/> returns nothing.
	/// </summary>
	/// <remarks>
	///     <see cref="MasterSwitchView.AdaptiveLightingOn"/> folds the polarity in: an enabled-flag reads on to
	///     command, a kill switch reads inverted.
	/// </remarks>
	public MasterSwitchView? GetMasterSwitch()
	{
		ModeToggle? toggle = GetToggles().FirstOrDefault();
		if (toggle is null)
			return null;

		GlobalConfig global = Config.Global;
		var enabledFlag = global.KillSwitchIsDefaulted || global.KillSwitchActiveWhenOff;
		var commanding = enabledFlag ? toggle.IsOn : !toggle.IsOn;

		return new MasterSwitchView(toggle, commanding, toggle.IsAvailable, IsHomeAssistantReady);
	}

	/// <summary>
	///     The house-mode select's live options and current value, or <c>null</c> when none is configured. The
	///     options are the live ones unioned with the configured, so a disconnected HA cannot blank a known one.
	/// </summary>
	public HouseModeView? GetHouseMode()
	{
		SyncDefaultKillSwitch();

		HouseModeConfig? houseMode = Config.Global.HouseMode;
		if (houseMode?.Entity is not { Length: > 0 } entity)
			return null;

		IsHomeAssistantReady = true;

		EntityState? state = TryGetState(entity);
		var current = state.AsUsableState();

		var values = _catalog.SelectOptionsOf(entity)
			.Concat(houseMode.Options.Select(option => option.Value))
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Select(value => value.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		// One instant and one set of sun times, read once, so every card on the page describes the same "now".
		(TimeOnly? sunrise, TimeOnly? sunset) = _catalog.SunTimesToday();
		var sun = new SunTimes(sunrise, sunset);
		DateTimeOffset now = DateTimeOffset.Now;

		// Identical for every non-Away option, so one calculator serves the whole page, not one per card.
		LightTarget? sharedTarget = Calculator(Config, sun, PeriodSelectValue()).GetTarget(now);

		var options = values
			.Select(value =>
			{
				HouseModeOptionConfig? option = houseMode.OptionFor(value);
				ModeKind kind = option?.Kind ?? ModeKind.Normal;

				var scene = kind is ModeKind.Away or ModeKind.Guest ? Blank(option?.Scene) : null;
				var clamp = kind == ModeKind.Sleep
					? HouseModeConfig.SleepClampPeriodFor(option ?? new HouseModeOptionConfig { Value = value }, Config.Periods)
					: null;
				var resetSummary = kind == ModeKind.Normal ? null : ResetSummary(option, houseMode);

				return new HouseModeOptionView(
					value,
					kind,
					current is not null && string.Equals(value, current, StringComparison.OrdinalIgnoreCase),
					scene,
					clamp,
					resetSummary,
					BuildPreview(Config, kind, kind == ModeKind.Away ? null : sharedTarget));
			})
			.ToList();

		// No Normal option means no reset target at all; the engine no-ops instead of falling back to a tagged
		// one. Sourced from NormalOption so the banner, the cards and the engine agree.
		var noNormal = values.Count > 0 && houseMode.NormalOption is null;

		return new HouseModeView(
			entity,
			current,
			options,
			noNormal,
			houseMode.NormalOption?.Value,
			IsAvailable: state is not null);
	}

	/// <summary>
	///     A one-line summary of an option's reset triggers, naming the Normal target. Never <c>null</c>: a
	///     triggerless option gets its own line, so a forgotten away mode reads as fixable, not blank.
	/// </summary>
	private static string ResetSummary(HouseModeOptionConfig? option, HouseModeConfig houseMode)
	{
		var normal = houseMode.NormalOption?.Value;
		var parts = new List<string>(3);

		if (Blank(option?.ResetOnPeriodStart) is { } period)
			parts.Add($"when '{period}' starts");

		if (option?.ResetOnPresence == true)
		{
			var where = option.ResetPresenceSensors.Count > 0
				? $"{option.ResetPresenceSensors.Count} chosen {(option.ResetPresenceSensors.Count == 1 ? "sensor" : "sensors")}"
				: "any room's motion";
			parts.Add($"on presence ({where}, after {option.ResetPresenceGraceMinutes} min)");
		}

		if (parts.Count == 0)
			return "stays until you switch the house back yourself";

		// With no Normal option the engine has nothing to reset to and the trigger will not fire.
		return normal is { Length: > 0 }
			? $"switches back to {normal} {string.Join("; ", parts)}"
			: $"would switch back {string.Join("; ", parts)}, but no option is marked Normal — fix this under Configuration → House modes";
	}

	private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

	/// <summary>
	///     The derived house state the live hero shows. Only downgrades <see cref="IsHomeAssistantReady"/>, never
	///     resets it, so calling it after <see cref="GetHouseMode"/> cannot mask a disconnection that found.
	/// </summary>
	public HouseDerivedState GetHouseState()
	{
		SyncDefaultKillSwitch();

		HouseModeConfig? houseMode = Config.Global.HouseMode;

		HouseModeOptionConfig? currentOption = null;
		if (houseMode?.Entity is { Length: > 0 } entity)
			currentOption = houseMode.OptionFor(TryGetState(entity).AsUsableState());

		return new HouseDerivedState(currentOption?.Kind ?? ModeKind.Normal, IsHomeAssistantReady);
	}

	/// <summary>
	///     The people the engine watches for Home/Away. Resolution mirrors <see cref="Engine.PresenceMonitor"/>:
	///     the configured persons when non-empty, otherwise the whole <c>person</c> domain. Only downgrades
	///     readiness, never resets it.
	/// </summary>
	public IReadOnlyList<PersonView> GetPeople()
	{
		IReadOnlyList<string> entityIds;

		if (Config.Global.Persons.Count > 0)
		{
			entityIds = Config.Global.Persons;
		}
		else
		{
			try
			{
				entityIds = _ha.EntityIdsInDomain(PersonDomain);
			}
			catch (InvalidOperationException)
			{
				// The registry is not up yet. An empty list renders the panel's "no people to watch" state.
				IsHomeAssistantReady = false;
				return [];
			}
		}

		List<PersonView> people = new(entityIds.Count);

		foreach (string entityId in entityIds)
		{
			EntityState? state = TryGetState(entityId);
			people.Add(new PersonView(
				entityId,
				_catalog.FriendlyNameOf(entityId) ?? entityId,
				state.StateIs(HomeState),
				state is not null));
		}

		return people;
	}

	/// <summary>
	///     The rooms the document names, in document order. The dashboard reads this, not the live reports,
	///     because a room a disconnected Home Assistant has never reported still counts.
	/// </summary>
	public IReadOnlyList<RoomView> GetRooms()
	{
		AdaptiveLightingConfig config = Config;

		return
		[
			.. config.Areas.Select(area => new RoomView(
				area.AreaId,
				AreaNaming.DisplayName(area, _catalog.AreaRegistry),
				AreaView.IsEnabled(area, config.Defaults),
				LightCountOf(area, config.Global)))
		];
	}

	/// <summary>
	///     How many lights a room drives. A pinned list replaces discovery for that room entirely, so counting
	///     discovery's lights for a hand-picked room names a number the engine will never use.
	/// </summary>
	private int LightCountOf(AreaConfig area, GlobalConfig global) =>
		area.Lights is { Count: > 0 } pinned ? pinned.Count : _catalog.LightCountIn(area.AreaId, global);

	/// <summary>
	///     What a mode would drive right now: the active period, its target, and what it does to the areas.
	/// </summary>
	/// <remarks>
	///     Pure with respect to time and Home Assistant: the instant and the sun times are arguments, and the
	///     period maths is the engine's own <see cref="CircadianCalculator"/>. The counts honour each area's
	///     overrides and only enabled areas, matching the engine's <c>IsEngineAllowed</c> gate.
	/// </remarks>
	/// <param name="sun">The day's sun times, for placing sun-anchored boundaries.</param>
	/// <param name="periodSelectValue">
	///     What the period select reads, or <c>null</c> when this house's own schedule decides. Under
	///     <see cref="PeriodAuthority.HomeAssistant"/> the engine takes the period from here and never from the
	///     clock, so a preview resolved from the clock would show a card the lights disagree with.
	/// </param>
	public static ModePreview ComputePreview(
		AdaptiveLightingConfig config,
		ModeKind kind,
		DateTimeOffset now,
		SunTimes sun,
		string? periodSelectValue = null)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(sun);

		// Away beats everything: the areas pause and sweep, so there is no period colour to resolve.
		if (kind == ModeKind.Away)
			return BuildPreview(config, kind, null);

		return BuildPreview(config, kind, Calculator(config, sun, periodSelectValue).GetTarget(now));
	}

	/// <summary>
	///     A calculator that resolves the period the way the engine's does: from the select when Home Assistant
	///     owns the time of day, from the clock otherwise.
	/// </summary>
	/// <remarks>
	///     One place, because a calculator built without the select places periods by clock time while every room
	///     runs the dropdown. The name is resolved once and closed over, so every card describes one instant.
	/// </remarks>
	private static CircadianCalculator Calculator(AdaptiveLightingConfig config, SunTimes sun, string? periodSelectValue)
	{
		string? forced = Schedule.NamedBySelect(config.Periods, config.Global, periodSelectValue)?.Name;

		return new CircadianCalculator(
			config.Periods, config.Global, () => sun, null, forced is null ? null : () => forced);
	}

	/// <summary>
	///     What the period select reads right now, or <c>null</c> when this house's own schedule decides.
	/// </summary>
	/// <remarks>
	///     <c>null</c> under <see cref="PeriodAuthority.AdaptiveLighting"/>, never the select's value: there the
	///     engine writes that select as a mirror of what its schedule resolved, so reading it back as an input
	///     lets a stale mirror decide what the page shows.
	/// </remarks>
	private string? PeriodSelectValue() =>
		Schedule.HomeAssistantDecides(Config.Global)
			? _catalog.CurrentStateOf(Config.Global.PeriodSelect!.EntityId)
			: null;

	/// <summary>
	///     Split from <see cref="ComputePreview"/> so the card loop resolves one target and reuses it across every
	///     non-Away option.
	/// </summary>
	/// <param name="target">The resolved period target, or <c>null</c> for Away and when no period places.</param>
	private static ModePreview BuildPreview(AdaptiveLightingConfig config, ModeKind kind, LightTarget? target)
	{
		if (kind == ModeKind.Away)
			return new ModePreview(null, null, null, IsOffPreview: true, AwayEffect(config));

		var effect = kind switch
		{
			ModeKind.Sleep => SleepEffect(config),
			ModeKind.Guest => GuestEffect(config),
			_ => NormalEffect(target?.PeriodName)
		};

		return new ModePreview(
			target?.PeriodName,
			target?.BrightnessPct,
			target?.ColorTempKelvin,
			IsOffPreview: false,
			effect);
	}

	private static string GuestEffect(AdaptiveLightingConfig config)
	{
		var count = EnabledAreaSettings(config).Count();
		return count == 0
			? "guests over — normal lighting, once rooms are configured"
			: "guests over — normal lighting";
	}

	/// <summary>The leaving sweep, counted over enabled areas honouring each area's <c>SkipAwaySweep</c> override.</summary>
	private static string AwayEffect(AdaptiveLightingConfig config)
	{
		var settings = EnabledAreaSettings(config).ToList();
		if (settings.Count == 0)
			return "turns everything off — no rooms configured yet";

		var kept = settings.Count(area => area.SkipAwaySweep);
		var swept = settings.Count - kept;

		return kept == 0
			? $"turns all {settings.Count} rooms off"
			: $"turns {swept} of {settings.Count} rooms off, keeps {kept} on";
	}

	/// <summary>The sleep clamp, counted over enabled areas honouring <c>RespectSleepMode</c> / <c>SleepBlocksAutoOn</c>.</summary>
	private static string SleepEffect(AdaptiveLightingConfig config)
	{
		var settings = EnabledAreaSettings(config).ToList();
		if (settings.Count == 0)
			return "night levels — no rooms configured yet";

		var clamped = settings.Count(area => area.RespectSleepMode);
		var blocked = settings.Count(area => area.SleepBlocksAutoOn);

		var clause = clamped == 1 ? "night levels in 1 room" : $"night levels in {clamped} rooms";
		return blocked == 0 ? clause : $"{clause}, {blocked} never turn on by themselves";
	}

	private static string NormalEffect(string? activePeriodName) =>
		activePeriodName is { Length: > 0 } name
			? $"everyday lighting — the \"{name}\" period right now"
			: "everyday lighting";

	private static IEnumerable<AreaSettings> EnabledAreaSettings(AdaptiveLightingConfig config) =>
		config.Areas
			.Select(area => area.Effective(config.Defaults))
			.Where(settings => settings.Enabled);

	/// <summary>
	///     Sets the house mode. Re-resolves the entity from config, never the request, verifies the domain and
	///     that the option is one of the select's live options, then calls <c>select_option</c>.
	/// </summary>
	/// <returns><c>true</c> when the call was dispatched; <c>false</c> when refused or HA is not connected.</returns>
	public bool SelectHouseMode(string option)
	{
		ArgumentNullException.ThrowIfNull(option);

		if (Config.Global.HouseMode?.Entity is not { Length: > 0 } entity)
		{
			_logger.LogWarning("Refused to set the house mode: no HouseMode.Entity is configured.");
			return false;
		}

		if (!string.Equals(entity.Domain(), "input_select", StringComparison.Ordinal))
		{
			_logger.LogWarning("Refused to set the house mode: {Entity} is not an input_select.", entity);
			return false;
		}

		if (!_catalog.SelectOptionsOf(entity).Any(live => string.Equals(live.Trim(), option.Trim(), StringComparison.OrdinalIgnoreCase)))
		{
			_logger.LogWarning("Refused to set the house mode to '{Option}': it is not one of {Entity}'s live options.", option, entity);
			return false;
		}

		try
		{
			_ha.CallService("input_select", "select_option", ServiceTarget.FromEntity(entity), new { option = option.Trim() });
			_logger.LogInformation("Set the house mode {Entity} to {Option} from the lighting web UI.", entity, option);
			return true;
		}
		catch (InvalidOperationException exception)
		{
			_logger.LogWarning(exception, "Could not set the house mode {Entity}: no connection to Home Assistant.", entity);
			return false;
		}
	}

	/// <param name="toggle">A toggle previously returned by <see cref="GetToggles"/>.</param>
	public bool Toggle(ModeToggle toggle)
	{
		ArgumentNullException.ThrowIfNull(toggle);

		// Resolve the built-in master switch live, so a toggle of it is recognised even before a page read did.
		SyncDefaultKillSwitch();

		// The argument came from a browser. This method must be incapable of calling a service on an entity the
		// config never named, so the id is re-resolved, never trusted.
		if (!IsConfiguredModeEntity(toggle.EntityId))
		{
			_logger.LogWarning(
				"Refused to toggle {EntityId}: it is not one of this host's configured mode entities.",
				toggle.EntityId);
			return false;
		}

		var domain = toggle.EntityId.Domain() ?? "";
		if (!ToggleableDomains.Contains(domain, StringComparer.Ordinal))
		{
			_logger.LogWarning("Refused to toggle {EntityId}: domain {Domain} has nothing to turn on.", toggle.EntityId, domain);
			return false;
		}

		try
		{
			_ha.CallService(
				domain,
				toggle.IsOn ? "turn_off" : "turn_on",
				ServiceTarget.FromEntity(toggle.EntityId));

			_logger.LogInformation(
				"Turned {EntityId} {Direction} from the lighting web UI.", toggle.EntityId, toggle.IsOn ? "off" : "on");
			return true;
		}
		catch (InvalidOperationException exception)
		{
			_logger.LogWarning(exception, "Could not toggle {EntityId}: no connection to Home Assistant.", toggle.EntityId);
			return false;
		}
	}

	private bool IsConfiguredModeEntity(string entityId) =>
		string.Equals(entityId, Config.Global.EffectiveKillSwitchEntity, StringComparison.Ordinal);

	/// <summary>
	///     Reads an entity's state, treating an uninitialised state cache as "no state", not as a fault.
	///     <c>IHaContext.GetState</c> throws until NetDaemon's initial connection completes.
	/// </summary>
	private EntityState? TryGetState(string entityId)
	{
		try
		{
			return _ha.GetState(entityId);
		}
		catch (InvalidOperationException)
		{
			IsHomeAssistantReady = false;
			return null;
		}
	}

	private bool IsOn(string? entityId) =>
		!string.IsNullOrWhiteSpace(entityId) && TryGetState(entityId).StateIs("on");

	private ModeToggle Build(string label, string description, string entityId, string meaning) =>
		new(
			label,
			description,
			entityId,
			IsOn(entityId),
			TryGetState(entityId) is not null,
			ToggleableDomains.Contains(entityId.Domain() ?? "", StringComparer.Ordinal),
			meaning);
}
