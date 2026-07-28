using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Hosting;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     One house mode the UI can show, and possibly flip.
/// </summary>
/// <param name="Label">Human name for the toggle.</param>
/// <param name="Description">What flipping it actually does to the engine.</param>
/// <param name="EntityId">The configured entity. Always from the config document, never from a request.</param>
/// <param name="IsOn">Whether the entity currently reads <c>on</c>.</param>
/// <param name="IsAvailable">Whether Home Assistant knows the entity at all.</param>
/// <param name="CanToggle">Whether the entity's domain supports <c>turn_on</c>/<c>turn_off</c>.</param>
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
///     What a mode would do to the lights, resolved for right now — read-only, so the card can say what a mode
///     <i>means</i> before anyone selects it. There is one shared circadian table now, so every field is derived
///     from that single table and the engine's own period/sun maths; nothing here is written back.
/// </summary>
/// <param name="ActivePeriodName">The period active at "now", or <c>null</c> when none can be placed.</param>
/// <param name="PreviewBrightness">The active period's target brightness, or <c>null</c> when off / unresolved.</param>
/// <param name="PreviewKelvin">The active period's target colour temperature, or <c>null</c> when off / unresolved.</param>
/// <param name="IsOffPreview">Whether the swatch should read "dark": an Away mode pauses/sweeps the areas.</param>
/// <param name="EffectSummary">What the mode does to the areas, in words — the away sweep, the sleep clamp, or the period in use.</param>
public sealed record ModePreview(
	string? ActivePeriodName,
	double? PreviewBrightness,
	int? PreviewKelvin,
	bool IsOffPreview,
	string EffectSummary);

/// <summary>One live option of the house-mode select, with the single kind and reset summary the config gave it.</summary>
/// <param name="Value">The option string as the select reports it.</param>
/// <param name="Kind">The option's one behaviour: Normal, Sleep, Away or Guest.</param>
/// <param name="IsCurrent">Whether the select currently stands on this option.</param>
/// <param name="Scene">The <c>scene.*</c> applied on entry, for Away/Guest; <c>null</c> otherwise or when unset.</param>
/// <param name="ClampPeriod">The resolved sleep-clamp period name, for Sleep; <c>null</c> otherwise or when none resolves.</param>
/// <param name="ResetSummary">A one-line summary of the reset triggers, or <c>null</c> for a Normal option.</param>
/// <param name="Preview">What this mode would drive right now — the card's at-a-glance content.</param>
public sealed record HouseModeOptionView(
	string Value,
	ModeKind Kind,
	bool IsCurrent,
	string? Scene,
	string? ClampPeriod,
	string? ResetSummary,
	ModePreview Preview);

/// <summary>
///     The derived house state the modes produce, for the live hero: the one kind the current option carries, which
///     is exactly what the engine's <see cref="Engine.ModeMonitor"/> reads as <c>ActiveKind</c> — so the hero and
///     the engine can never disagree.
/// </summary>
/// <param name="ActiveKind">The current option's kind, or <see cref="ModeKind.Normal"/> when none / unavailable.</param>
/// <param name="IsAvailable">Whether Home Assistant answered — when false the state is "unknown, not off".</param>
public sealed record HouseDerivedState(ModeKind ActiveKind, bool IsAvailable);

/// <summary>The house-mode select as the Modes page shows it: its options, current value, and Normal-fallback note.</summary>
/// <param name="Entity">The configured <c>input_select</c>. Always from the config document, never from a request.</param>
/// <param name="CurrentValue">The select's current option, or <c>null</c> when unknown / unavailable.</param>
/// <param name="Options">The live options (∪ configured), each with its kind and whether it is current.</param>
/// <param name="NoNormalOption">Whether no option is marked Normal, so there is no reset target and every reset is a no-op.</param>
/// <param name="NormalFallbackValue">The reset target's value (<see cref="HouseModeConfig.NormalOption"/>), or <c>null</c> when none is Normal.</param>
/// <param name="IsAvailable">Whether Home Assistant knows the select at all.</param>
public sealed record HouseModeView(
	string Entity,
	string? CurrentValue,
	IReadOnlyList<HouseModeOptionView> Options,
	bool NoNormalOption,
	string? NormalFallbackValue,
	bool IsAvailable);

/// <summary>
///     The engine's master switch as a page (the dashboard) shows it: the toggle to flip, plus whether adaptive
///     lighting is currently commanding, phrased for a reader who does not know what a kill switch is.
/// </summary>
/// <remarks>
///     This is a read-only projection of the single <see cref="ModeToggle"/> <see cref="GetToggles"/> already
///     resolves from the configuration document — it adds no write path. The one write remains
///     <see cref="Toggle(ModeToggle)"/>, called with <see cref="Toggle"/> below, which re-resolves the entity
///     against the config before acting.
/// </remarks>
/// <param name="Toggle">The underlying toggle, resolved from config; the only thing <see cref="ModeService.Toggle"/> will act on.</param>
/// <param name="AdaptiveLightingOn">Whether the engine is currently allowed to command lights (the switch read in its configured polarity).</param>
/// <param name="IsAvailable">Whether Home Assistant knows the switch entity at all.</param>
/// <param name="IsReady">Whether Home Assistant answered — when false the state is "unknown", not "off".</param>
public sealed record MasterSwitchView(
	ModeToggle Toggle,
	bool AdaptiveLightingOn,
	bool IsAvailable,
	bool IsReady);

/// <summary>
///     One watched person, for the dashboard's who's-home panel: read-only.
/// </summary>
/// <remarks>
///     Mirrors what the engine's <see cref="Engine.PresenceMonitor"/> actually watches, so the panel and the
///     Home/Away decision can never show different people. There is no write path attached to this — it is a
///     projection of state the engine already reads.
/// </remarks>
/// <param name="EntityId">The person or device-tracker entity, from the config's person list or the <c>person</c> domain.</param>
/// <param name="Name">Home Assistant's <c>friendly_name</c>, or the id when it has none.</param>
/// <param name="IsHome">Whether the entity currently reads <c>home</c> — the same comparison the monitor makes.</param>
/// <param name="IsAvailable">Whether Home Assistant knew the entity at all; <c>false</c> renders as "unknown", not "away".</param>
public sealed record PersonView(string EntityId, string Name, bool IsHome, bool IsAvailable);

/// <summary>
///     One room as the dashboard reads it: how to recognise its live reports, whether the owner switched it on,
///     and how many lights it would drive.
/// </summary>
/// <remarks>
///     A projection rather than the <see cref="AreaConfig"/> itself. The document behind it is the cached copy
///     every read on the page shares, and handing a page a mutable room would invite an edit outside the save
///     pipeline that is deliberately the only write path. The dashboard only ever looks.
/// </remarks>
/// <param name="AreaId">
///     The registry area id, or <c>null</c> for a room configured with explicit entities. This is how a live
///     report is matched back to the room that produced it — ids survive a rename mid-session, names do not.
/// </param>
/// <param name="Name">The room's display name, which is also the fallback match for a report with no id.</param>
/// <param name="IsEnabled">Its effective enablement, following the document's inheritance.</param>
/// <param name="LightCount">
///     How many lights the room would drive — the length of its own pinned list when it has one, discovery's
///     answer otherwise. 0 when Home Assistant has not answered yet, which is why the first-run chips show a
///     count only when there is one to show.
/// </param>
public sealed record RoomView(string? AreaId, string Name, bool IsEnabled, int LightCount);

/// <summary>
///     Reads and flips the house mode entities named in the configuration document.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the whole of the UI's write surface, and it is deliberately tiny.</b> The engine's
///         modes are Home Assistant entities, so "editing" them is a service call on an entity HA already
///         owns — there is no config file to persist and no engine state to mutate. The state change flows
///         back through the engine's normal <c>ModeMonitor</c> path; this class does not tell the engine
///         anything.
///     </para>
///     <para>
///         The entity ids come exclusively from <see cref="AdaptiveLightingConfig"/>. A caller cannot name
///         an entity: <see cref="ToggleAsync"/> takes a <see cref="ModeToggle"/> that this class built, and
///         re-resolves it against the config before calling. That is what keeps this from being a general
///         service-call proxy, which is the thing it must never become.
///     </para>
/// </remarks>
public sealed class ModeService
{
	/// <summary>Domains this service is willing to call <c>turn_on</c>/<c>turn_off</c> on.</summary>
	/// <remarks>
	///     <c>binary_sensor</c> is a legitimate mode entity for the engine to <i>read</i>, but nothing can
	///     write to one — it is shown, and shown as untoggleable, rather than offered and then failing.
	/// </remarks>
	private static readonly string[] ToggleableDomains = ["input_boolean", "switch", "light", "fan"];

	/// <summary>The domain the who's-home panel falls back to, matching <see cref="Engine.PresenceMonitor"/>.</summary>
	private const string PersonDomain = "person";

	/// <summary>The state a person entity reads when home, matching <see cref="Engine.PresenceMonitor"/>.</summary>
	private const string HomeState = "home";

	private readonly IHaContext _ha;
	private readonly AdaptiveLightingConfig _seedConfig;
	private readonly HaCatalog _catalog;
	private readonly LightingEngineHost _engine;
	private readonly ILogger<ModeService> _logger;

	// The live document, cached against the store file's last-write time so the per-second dashboard ticker
	// costs a stat, not a YAML parse. See the Config property for why this reads the store, not IAppConfig.
	private AdaptiveLightingConfig? _cachedConfig;
	private DateTimeOffset? _cachedStamp;

	/// <summary>
	///     Whether Home Assistant's state cache answered the last time <see cref="GetToggles"/> asked.
	/// </summary>
	/// <remarks>
	///     Kestrel starts serving the moment the process is up, but NetDaemon connects to Home Assistant
	///     asynchronously and its state cache throws until that finishes. So this page is reachable, by
	///     design, in a window where no entity state exists — and again any time Home Assistant is down.
	///     That is a normal condition to render, not an error to throw on.
	/// </remarks>
	public bool IsHomeAssistantReady { get; private set; } = true;

	/// <summary>Creates the service.</summary>
	/// <param name="ha">The HA context the toggles act through.</param>
	/// <param name="config">The bound configuration document; the only source of entity ids.</param>
	/// <param name="catalog">Reads the select's live options — both are scoped, so lifetimes match.</param>
	/// <param name="engine">The engine host, for the built-in master switch that a blank <c>KillSwitchEntity</c> defaults to (09 §7).</param>
	/// <param name="logger">Where failed service calls go.</param>
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
	///     The live configuration document — the same file the engine and the config editor read — re-read
	///     whenever it changes on disk. Falls back to the NetDaemon-bound seed only before anything has ever been
	///     saved (the store file does not exist yet).
	/// </summary>
	/// <remarks>
	///     <para>
	///         The dashboard must show what the operator configured, not what NetDaemon bound at process start.
	///         <c>IAppConfig&lt;AdaptiveLightingConfig&gt;</c> is frozen at startup and — since the engine moved its
	///         source of truth to the external store — no longer reflects a UI save, so a house mode added after the
	///         host started would be invisible here. That is exactly the "the dashboard shows nothing" bug: reading
	///         the store instead keeps this service, the engine and the editor on one document.
	///     </para>
	///     <para>
	///         Cached against the file's last-write time so the per-second ticker costs a stat, not a parse. A parse
	///         failure keeps the last good document (or the seed) rather than blanking the page mid-save.
	///     </para>
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
	///     Copies the engine's built-in enable switch (09 §7) onto the shared config before every read.
	/// </summary>
	/// <remarks>
	///     Read live, not once at construction: this service is scoped while <see cref="LightingEngineHost"/> is a
	///     singleton, and a scope can be built before the per-host bootstrap calls <c>Attach</c>. Copying once would
	///     freeze <c>DefaultKillSwitchEntity</c> at <c>null</c> for that scope, so the master switch would never
	///     appear until a page reload. Assigning on each read lets it show the moment Home Assistant connects.
	/// </remarks>
	private void SyncDefaultKillSwitch() => Config.Global.DefaultKillSwitchEntity = _engine.DefaultKillSwitchEntity;

	/// <summary>
	///     The modes this host has configured, with their current state. Unconfigured modes are absent
	///     rather than shown as disabled — the engine treats them as permanently inactive, and so does this.
	/// </summary>
	/// <returns>The configured toggles, in a fixed order.</returns>
	public IReadOnlyList<ModeToggle> GetToggles()
	{
		SyncDefaultKillSwitch();

		var toggles = new List<ModeToggle>(1);
		GlobalConfig global = Config.Global;

		// Readiness is re-probed on every call, but this method must never clobber a false that GetHouseMode
		// set: when a house-mode select is configured, GetHouseMode owns the reset-to-true and this method only
		// downgrades (its TryGetState probes below set false on failure). With no select, GetHouseMode returns
		// before touching readiness, so this method is the sole owner and resets — otherwise a stale false could
		// never clear once the connection recovers.
		if (global.HouseMode?.Entity is not { Length: > 0 })
			IsHomeAssistantReady = true;

		// The master switch, and only the master switch. A blank KillSwitchEntity defaults to the app's own enable
		// switch (09 §7), so this now always renders — the polarity is forced to enabled-flag while defaulted.
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
	///     The engine's master switch, projected for the dashboard: the toggle to flip and whether adaptive
	///     lighting is currently commanding. <c>null</c> before the built-in default resolves (pre-Attach), the
	///     same window in which <see cref="GetToggles"/> returns nothing.
	/// </summary>
	/// <remarks>
	///     A pure read built on <see cref="GetToggles"/>, so it shares its config-resolution and readiness
	///     bookkeeping and introduces no new write surface. <see cref="MasterSwitchView.AdaptiveLightingOn"/>
	///     folds the switch's polarity in: an enabled-flag reads on to command, a kill switch reads inverted.
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
	///     The house-mode select's live options and current value, or <c>null</c> when none is configured.
	/// </summary>
	/// <remarks>The options are the live ones (∪ configured), so a disconnected HA cannot blank a known option.</remarks>
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

		// The preview is resolved for a single instant and one set of sun times, read once, so every card on the
		// page describes the same "now". The sun times are the engine's own truth (sun.sun), not a re-derivation.
		(TimeOnly? sunrise, TimeOnly? sunset) = _catalog.SunTimesToday();
		var sun = new SunTimes(sunrise, sunset);
		DateTimeOffset now = DateTimeOffset.Now;

		// The resolved period target is identical for every non-Away option, so resolve it once here rather than
		// building a CircadianCalculator per card. Away ignores it (its swatch is dark) and passes null through.
		LightTarget? sharedTarget = new CircadianCalculator(Config.Periods, Config.Global, () => sun).GetTarget(now);

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

		// No option is marked Normal, so there is no reset target at all (the engine no longer falls back to a tagged
		// option — it no-ops). Sourced from NormalOption so the banner, the cards and the engine agree: when it is
		// null the note says resets will not fire, and NormalFallbackValue carries that same (absent) target.
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
	///     A one-line summary of an option's reset triggers, naming the Normal target — the same information the
	///     ModeMonitor acts on, phrased for the card (09 §5.4). <c>null</c> is not returned here; a triggerless
	///     option gets the "no reset trigger" line so a forgotten Borte reads as fixable rather than blank.
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

		if (Blank(option?.ResetAtTime) is { } time)
			parts.Add($"at {time}");

		if (parts.Count == 0)
			return "stays until you switch back by hand";

		// No Normal option → the engine has nothing to reset to and the trigger will not fire. Say so, so the card
		// agrees with the engine's no-op rather than promising a reset that never happens.
		return normal is { Length: > 0 }
			? $"switches back to {normal} {string.Join("; ", parts)}"
			: $"would switch back {string.Join("; ", parts)}, but no option is marked Normal — fix this under Configuration → House modes";
	}

	private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

	/// <summary>
	///     The derived house state the live hero shows: the one kind the current option carries — the same
	///     <c>ActiveKind</c> the engine's <see cref="Engine.ModeMonitor"/> reads.
	/// </summary>
	/// <remarks>
	///     This only ever reads, and only the select the config named. It never resets
	///     <see cref="IsHomeAssistantReady"/> to true — it only downgrades on a failed read — so calling it after
	///     <see cref="GetHouseMode"/> / <see cref="GetToggles"/> cannot mask a real disconnection those methods
	///     discovered.
	/// </remarks>
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
	///     The people the engine watches for Home/Away, with their current presence — read-only, for the
	///     dashboard's who's-home panel.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Resolution mirrors <see cref="Engine.PresenceMonitor"/> exactly: the configured
	///         <see cref="GlobalConfig.Persons"/> when non-empty, otherwise every entity in the <c>person</c>
	///         domain. That is what makes the panel honest — it shows the same people whose presence actually
	///         drives Home and Away, configured <c>device_tracker.*</c> entries included.
	///     </para>
	///     <para>
	///         Adds no write path. Every state read goes through <see cref="TryGetState"/>, so a disconnected Home
	///         Assistant renders each person as "unknown" rather than throwing; like <see cref="GetHouseState"/> it
	///         only ever downgrades readiness, never resets it.
	///     </para>
	/// </remarks>
	/// <returns>One entry per watched person, in configuration / discovery order.</returns>
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
				// The entity registry is not up yet — the same window every read here tolerates. An empty list
				// renders the panel's "no people to watch" state, not a crash.
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
	///     The rooms the document names, in document order — read-only, for the dashboard.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Three questions on the dashboard need the document rather than the live reports: which cards to show
	///         (only rooms the owner switched on), how many rooms are hidden (the footer's count), and whether the
	///         house is waiting for its first choice (the first-run state). All three are about what the owner
	///         decided, and a switched-off room a disconnected Home Assistant has never reported still counts.
	///     </para>
	///     <para>
	///         Adds no write path and no second source of truth: the document is the same cached copy the rest of
	///         this service reads, re-read only when the file changes on disk.
	///     </para>
	/// </remarks>
	/// <returns>One entry per configured room, in the order the document lists them.</returns>
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
	///     How many lights a room drives: its own list when it pins one, discovery's count otherwise.
	/// </summary>
	/// <remarks>
	///     A pinned list replaces discovery for that room entirely (<c>AreaEntityResolver.TryResolve</c>), so
	///     counting discovery's lights for a hand-picked room would name a number the engine will never use.
	/// </remarks>
	private int LightCountOf(AreaConfig area, GlobalConfig global) =>
		area.Lights is { Count: > 0 } pinned ? pinned.Count : _catalog.LightCountIn(area.AreaId, global);

	/// <summary>
	///     What a mode would drive right now: the period active at <paramref name="now"/> on the one shared table,
	///     that period's target, and what the mode does to the areas — all read-only.
	/// </summary>
	/// <remarks>
	///     Pure with respect to time and Home Assistant: the instant and the day's sun times are arguments, and
	///     the period maths is the engine's own <see cref="CircadianCalculator"/>, so this cannot drift from what
	///     the engine would resolve for the same inputs. An Away mode pauses/sweeps the areas, so its swatch is
	///     "dark" rather than a period colour; Sleep, Guest and Normal render the resolved period. The effect line's
	///     counts honour each area's overrides of <see cref="AdaptiveLightingConfig.Defaults"/> and count only
	///     enabled areas, matching the engine's <c>IsEngineAllowed</c> gate.
	/// </remarks>
	/// <param name="config">The bound document — the only source of periods, defaults and areas.</param>
	/// <param name="kind">The option's kind, which decides the swatch and the effect line.</param>
	/// <param name="now">The instant to resolve the active period at.</param>
	/// <param name="sun">The day's sun times, for placing sun-anchored boundaries.</param>
	public static ModePreview ComputePreview(
		AdaptiveLightingConfig config,
		ModeKind kind,
		DateTimeOffset now,
		SunTimes sun)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(sun);

		// Away beats everything: the areas pause/sweep, so there is no period colour to resolve and the swatch is dark.
		if (kind == ModeKind.Away)
			return BuildPreview(config, kind, null);

		var calculator = new CircadianCalculator(config.Periods, config.Global, () => sun);
		return BuildPreview(config, kind, calculator.GetTarget(now));
	}

	/// <summary>
	///     Turns a kind and a pre-resolved circadian <paramref name="target"/> into the preview. Split from
	///     <see cref="ComputePreview"/> so the card loop can resolve the shared target once and reuse it across every
	///     non-Away option — one CircadianCalculator per page, not one per card.
	/// </summary>
	/// <param name="config">The document, for the area-effect counts.</param>
	/// <param name="kind">The option's kind, which decides the swatch and the effect line.</param>
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

	/// <summary>The guest behaviour, in words: normal lighting runs (the panel appends the scene when one is set).</summary>
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

	/// <summary>The effective settings of every enabled area: defaults merged with the area's own overrides.</summary>
	private static IEnumerable<AreaSettings> EnabledAreaSettings(AdaptiveLightingConfig config) =>
		config.Areas
			.Select(area => area.Effective(config.Defaults))
			.Where(settings => settings.Enabled);

	/// <summary>
	///     Sets the house mode. Re-resolves the entity from config (never the request), verifies the domain is
	///     <c>input_select</c> and the option is one of the select's live options, then <c>select_option</c>.
	/// </summary>
	/// <param name="option">The option to select. Verified against the live list before any service call.</param>
	/// <returns><c>true</c> when the call was dispatched; <c>false</c> when refused or HA is not connected.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="option"/> is <c>null</c>.</exception>
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

	/// <summary>
	///     Flips <paramref name="toggle"/> by calling <c>turn_on</c>/<c>turn_off</c> on its entity.
	/// </summary>
	/// <param name="toggle">A toggle previously returned by <see cref="GetToggles"/>.</param>
	/// <returns><c>true</c> when the call was dispatched; <c>false</c> when it was refused or HA is not connected.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="toggle"/> is <c>null</c>.</exception>
	public bool Toggle(ModeToggle toggle)
	{
		ArgumentNullException.ThrowIfNull(toggle);

		// Resolve the built-in master switch live, so a toggle of it is recognised even before a page read did.
		SyncDefaultKillSwitch();

		// Re-resolve against the config rather than trusting the argument. The argument came from a browser,
		// and this method must be incapable of calling a service on an entity the config never named.
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
			// Thrown by the HA context when there is no live connection. Expected when the host runs standalone.
			_logger.LogWarning(exception, "Could not toggle {EntityId}: no connection to Home Assistant.", toggle.EntityId);
			return false;
		}
	}

	private bool IsConfiguredModeEntity(string entityId) =>
		string.Equals(entityId, Config.Global.EffectiveKillSwitchEntity, StringComparison.Ordinal);

	/// <summary>
	///     Reads an entity's state, treating an uninitialised state cache as "no state" rather than as a fault.
	/// </summary>
	/// <remarks>
	///     <c>IHaContext.GetState</c> throws <see cref="InvalidOperationException"/> until NetDaemon's initial
	///     connection to Home Assistant completes. Left uncaught that is a 500 on a page whose whole job is to
	///     say what the modes are doing — including saying "I cannot tell you yet".
	/// </remarks>
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
