namespace AdaptiveLighting.Configuration;

/// <summary>
///     Checks an <see cref="AdaptiveLightingConfig"/> before the engine is built. Pure: the known entity and
///     area ids are passed in rather than read from HA, so the whole validator is unit-testable without fakes.
/// </summary>
/// <remarks>
///     The split is deliberate. Document-level problems mean nobody can have thought about this config, so the
///     app throws and shows up dead in HA. Referential problems are one area's business — an entity renamed in
///     HA must cost that area, not the house.
/// </remarks>
public static class ConfigValidator
{
	private const double MinBrightnessPct = 0;
	private const double MaxBrightnessPct = 100;
	private const int MinColorTempKelvin = 1000;
	private const int MaxColorTempKelvin = 10000;
	private const double MinSunElevationDegrees = -90;
	private const double MaxSunElevationDegrees = 90;

	/// <summary>
	///     Validates <paramref name="config"/>.
	/// </summary>
	/// <param name="config">The bound configuration document.</param>
	/// <param name="knownEntityIds">
	///     Every entity id HA knows. When <c>null</c>, referential checks against entity ids are skipped —
	///     which is what unit tests want, and what a caller that has no <c>IHaContext</c> gets.
	/// </param>
	/// <param name="knownAreaIds">Every area id the registry knows. When <c>null</c>, area checks are skipped.</param>
	/// <param name="liveSelectOptions">
	///     The live <c>options</c> of the configured house-mode select. When <c>null</c>, the live-option warnings
	///     are skipped — same pattern as <paramref name="knownEntityIds"/>.
	/// </param>
	public static ValidationResult Validate(
		AdaptiveLightingConfig config,
		IReadOnlyCollection<string>? knownEntityIds = null,
		IReadOnlyCollection<string>? knownAreaIds = null,
		IReadOnlyCollection<string>? liveSelectOptions = null)
	{
		ArgumentNullException.ThrowIfNull(config);

		ValidationResult result = new();

		ValidateGlobal(config.Global, knownEntityIds, result);
		ValidatePeriods(config.Periods, result);
		ValidateHouseMode(config, knownEntityIds, liveSelectOptions, result);
		ValidateSettings("Defaults", config.Defaults, result);
		ValidateAreas(config, knownEntityIds, knownAreaIds, result);

		return result;
	}

	private static void ValidateGlobal(GlobalConfig global, IReadOnlyCollection<string>? knownEntityIds, ValidationResult result)
	{
		if (global.AwayDebounceMinutes < 0)
			result.AddError($"Global.AwayDebounceMinutes must not be negative (is {global.AwayDebounceMinutes}).");

		if (global.CircadianTickSeconds <= 0)
			result.AddError($"Global.CircadianTickSeconds must be positive (is {global.CircadianTickSeconds}).");

		if (global.SelfEchoWindowSeconds < 0)
			result.AddError($"Global.SelfEchoWindowSeconds must not be negative (is {global.SelfEchoWindowSeconds}).");

		if (global.BlendMinutes < 0)
			result.AddError($"Global.BlendMinutes must not be negative (is {global.BlendMinutes}).");

		// MotionDeviceClasses is deliberately not checked for emptiness: empty is the default and means
		// GlobalConfig.DefaultMotionDeviceClasses. See the remarks on the property.

		if (global.BrightnessTolerancePct < 0)
			result.AddError($"Global.BrightnessTolerancePct must not be negative (is {global.BrightnessTolerancePct}).");

		if (global.ColorTempToleranceKelvin < 0)
			result.AddError($"Global.ColorTempToleranceKelvin must not be negative (is {global.ColorTempToleranceKelvin}).");

		if (knownEntityIds is null)
			return;

		foreach ((string? label, string? entityId) in EnumerateGlobalEntities(global))
			if (!knownEntityIds.Contains(entityId))
				result.AddError($"Global.{label} refers to '{entityId}', which Home Assistant does not know.");

		// Kill switch: the known-entity check uses the effective id (09 §7). It is only ever a WARNING, never a
		// document-stopping error — the engine fails open on an unavailable kill switch (an unreadable state is read
		// as "not killed", ModeMonitor.KillSwitchActive), so a missing switch can never darken the house. An explicit
		// id HA does not know is a likely mistake worth flagging; the defaulted built-in switch may simply not be
		// visible to the standalone web host yet (the state manager creates it at app start).
		if (global.EffectiveKillSwitchEntity is { Length: > 0 } killSwitch && !knownEntityIds.Contains(killSwitch))
		{
			if (global.KillSwitchEntity is { Length: > 0 })
				result.AddWarning($"Global.KillSwitchEntity refers to '{killSwitch}', which Home Assistant does not know — the engine runs ungated (it fails open on a missing switch). Clear it to fall back to the built-in switch.");
			else
				result.AddWarning($"The built-in master switch '{killSwitch}' is not known to Home Assistant yet; the state manager creates it at app start.");
		}

		// Outdoor lux sensor: the house-wide default lux source. It fails open — an unknown or non-sensor id just
		// leaves areas without their own lux falling back to sun elevation — so both are warnings, not errors.
		if (global.OutdoorLuxSensor is { Length: > 0 } outdoorLux)
		{
			if (outdoorLux.Domain() is not "sensor")
				result.AddWarning($"Global.OutdoorLuxSensor '{outdoorLux}' is not a sensor entity; areas without their own lux sensor will fall back to sun elevation.");
			else if (!knownEntityIds.Contains(outdoorLux))
				result.AddWarning($"Global.OutdoorLuxSensor '{outdoorLux}' is not known to Home Assistant; areas without their own lux sensor fall back to sun elevation until it appears.");
		}
	}

	private static IEnumerable<(string Label, string EntityId)> EnumerateGlobalEntities(GlobalConfig global)
	{
		foreach (string person in global.Persons)
			yield return (nameof(GlobalConfig.Persons), person);

		if (global.HouseMode?.Entity is { Length: > 0 } houseMode)
			yield return ($"{nameof(GlobalConfig.HouseMode)}.{nameof(HouseModeConfig.Entity)}", houseMode);
	}

	private static void ValidatePeriods(List<TimePeriodConfig> periods, ValidationResult result)
	{
		if (periods.Count == 0)
		{
			result.AddError("Periods is empty — the engine has no circadian table and could never pick a target.");
			return;
		}

		// One shared table now (09 §3.5), so a plain duplicate name or fixed start time is an error again.
		IEnumerable<string> duplicateNames = periods
			.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
			.Where(g => g.Count() > 1)
			.Select(g => g.Key);

		foreach (string? name in duplicateNames)
			result.AddError($"Duplicate period name '{name}'.");

		Dictionary<TimeOnly, string> fixedStarts = new();

		foreach (TimePeriodConfig period in periods)
		{
			if (!PeriodStart.TryParse(period.Start, out PeriodStart? start))
				continue;   // start problems are reported in the per-period pass below

			// Sun-anchored boundaries move daily, so an overlap between them cannot be decided here.
			if (start!.FixedTime is { } time)
			{
				if (fixedStarts.TryGetValue(time, out string? other))
					result.AddError($"Periods '{other}' and '{period.Name}' both start at {time:HH\\:mm}.");
				else
					fixedStarts[time] = period.Name;
			}
		}

		foreach (TimePeriodConfig period in periods)
		{
			if (string.IsNullOrWhiteSpace(period.Name))
				result.AddError("A period has no Name.");

			if (!PeriodStart.TryParse(period.Start, out _))
			{
				result.AddError($"Period '{period.Name}' has an unparseable Start '{period.Start}'. Expected \"HH:mm\", \"sunrise\", \"sunset\", or a sun event with an offset such as \"sunset-01:00\".");
				continue;
			}

			ValidatePeriodTargets(period, result);
		}
	}

	private static void ValidatePeriodTargets(TimePeriodConfig period, ValidationResult result)
	{
		if (period.BrightnessPct is < MinBrightnessPct or > MaxBrightnessPct)
			result.AddError($"Period '{period.Name}' has BrightnessPct {period.BrightnessPct}, outside {MinBrightnessPct}–{MaxBrightnessPct}.");

		if (period.ColorTempKelvin is < MinColorTempKelvin or > MaxColorTempKelvin)
			result.AddError($"Period '{period.Name}' has ColorTempKelvin {period.ColorTempKelvin}, outside {MinColorTempKelvin}–{MaxColorTempKelvin}.");

		if (period.MinBrightnessPct is { } min && min is < MinBrightnessPct or > MaxBrightnessPct)
			result.AddError($"Period '{period.Name}' has MinBrightnessPct {min}, outside {MinBrightnessPct}–{MaxBrightnessPct}.");

		if (period.MaxBrightnessPct is { } max && max is < MinBrightnessPct or > MaxBrightnessPct)
			result.AddError($"Period '{period.Name}' has MaxBrightnessPct {max}, outside {MinBrightnessPct}–{MaxBrightnessPct}.");

		if (period.MinBrightnessPct is { } floor && period.MaxBrightnessPct is { } ceiling && floor > ceiling)
			result.AddError($"Period '{period.Name}' has MinBrightnessPct {floor} above MaxBrightnessPct {ceiling}.");
	}

	/// <summary>
	///     The house-mode rules (09 §6). Structural problems are document-level errors; classification quirks
	///     (no/many Normals, inert scenes/resets) are non-blocking warnings; live-option warnings only fire when
	///     <paramref name="liveSelectOptions"/> is given.
	/// </summary>
	private static void ValidateHouseMode(
		AdaptiveLightingConfig config,
		IReadOnlyCollection<string>? knownEntityIds,
		IReadOnlyCollection<string>? liveSelectOptions,
		ValidationResult result)
	{
		HouseModeConfig? houseMode = config.Global.HouseMode;
		List<TimePeriodConfig> periods = config.Periods;

		// SetsMode must match a configured option value (needs the select and its options); warn when it names a
		// Normal option — legal (a scheduled reset) but probably a mistake.
		foreach (TimePeriodConfig? period in periods.Where(p => p.SetsMode is { Length: > 0 }))
		{
			string setsMode = period.SetsMode!;
			HouseModeOptionConfig? option = houseMode?.OptionFor(setsMode);

			// Valid if it names a configured option OR a live option of the select: the engine sets the select to
			// that value at runtime, so a live option it can genuinely select is legitimate even before the owner
			// has tagged it a Kind. Only when it matches neither is it a document-level error — otherwise a period
			// pointing at an untagged live option (e.g. the "Normal" the select already offers) would deadlock the
			// save, since tagging that option is itself a save.
			bool isLiveOption = liveSelectOptions?.Any(live => string.Equals(live.Trim(), setsMode.Trim(), StringComparison.OrdinalIgnoreCase)) ?? false;

			if (option is null && !isLiveOption)
				result.AddError($"Period '{period.Name}' has SetsMode '{setsMode}', which matches no house-mode option — neither a configured one nor a live option of the select.");
			else if (option?.Kind == ModeKind.Normal)
				result.AddWarning($"Period '{period.Name}' SetsMode '{setsMode}', which is a Normal option — the period would schedule a reset to the baseline.");
		}

		// The sleep clamp must resolve when sleep is load-bearing.
		ValidateSleepPath(config, houseMode, result);

		if (houseMode?.Entity is not { Length: > 0 })
			return;

		// The entity must be an input_select. The "unknown to HA" half is checked in ValidateGlobal.
		if (!houseMode.Entity.HasDomain("input_select"))
			result.AddError($"HouseMode.Entity '{houseMode.Entity}' is not an input_select. The house mode is a Home Assistant dropdown helper.");

		// Duplicate or blank option values.
		foreach (HouseModeOptionConfig option in houseMode.Options)
			if (string.IsNullOrWhiteSpace(option.Value))
				result.AddError("A HouseMode option has a blank Value.");

		IEnumerable<string> duplicateOptions = houseMode.Options
			.Where(o => !string.IsNullOrWhiteSpace(o.Value))
			.GroupBy(o => o.Value.Trim(), StringComparer.OrdinalIgnoreCase)
			.Where(g => g.Count() > 1)
			.Select(g => g.Key);

		foreach (string? value in duplicateOptions)
			result.AddError($"Duplicate HouseMode option value '{value}'.");

		ValidateNormalCount(houseMode, result);

		foreach (HouseModeOptionConfig? option in houseMode.Options.Where(o => !string.IsNullOrWhiteSpace(o.Value)))
			ValidateOption(config, option, knownEntityIds, result);

		if (liveSelectOptions is not null)
			WarnOnLiveOptionMismatch(houseMode, liveSelectOptions, result);
	}

	/// <summary>Exactly-one-Normal: none → warning (first is treated as Normal); more than one → warning (first wins).</summary>
	private static void ValidateNormalCount(HouseModeConfig houseMode, ValidationResult result)
	{
		List<HouseModeOptionConfig> configured = houseMode.Options.Where(o => !string.IsNullOrWhiteSpace(o.Value)).ToList();
		if (configured.Count == 0)
			return;

		int normals = configured.Count(o => o.Kind == ModeKind.Normal);
		if (normals == 0)
			result.AddWarning($"No option is marked Normal — '{configured[0].Value}' is being treated as Normal (the reset target). Pick one explicitly.");
		else if (normals > 1)
			result.AddWarning("More than one option is marked Normal; the first wins as the reset target.");
	}

	/// <summary>Per-option rules: scene domain/known, reset triggers, and reset/scene fields set on a Normal option.</summary>
	private static void ValidateOption(
		AdaptiveLightingConfig config,
		HouseModeOptionConfig option,
		IReadOnlyCollection<string>? knownEntityIds,
		ValidationResult result)
	{
		bool isNormal = option.Kind == ModeKind.Normal;
		bool isAwayOrGuest = option.Kind is ModeKind.Away or ModeKind.Guest;

		// Scene: applied on entry for any kind. On Away/Guest it stands (they pause the engine); on Normal/Sleep it is
		// a one-shot the ordinary commands may soon override — legal, so no warning. Must be a scene entity, known when ids are provided.
		if (option.Scene is { Length: > 0 } scene)
		{
			if (!scene.HasDomain("scene"))
				result.AddError($"HouseMode option '{option.Value}' has Scene '{scene}', which is not a scene entity.");
			else if (knownEntityIds is not null && !knownEntityIds.Contains(scene))
				result.AddError($"HouseMode option '{option.Value}' Scene '{scene}' is not known to Home Assistant.");
		}

		// ClampPeriod is load-bearing only on Sleep; a dangling period name is an error only then. On any other
		// kind it is inert, so a stale name is a warning, not something that makes the whole document unsaveable.
		if (option.ClampPeriod is { Length: > 0 } clamp)
		{
			if (option.Kind != ModeKind.Sleep)
				result.AddWarning($"HouseMode option '{option.Value}' has a ClampPeriod but its kind is {option.Kind}; it is inert.");
			else if (!config.Periods.Any(p => string.Equals(p.Name, clamp, StringComparison.OrdinalIgnoreCase)))
				result.AddError($"HouseMode option '{option.Value}' ClampPeriod '{clamp}' matches no configured period.");
		}

		// Reset triggers are only meaningful on a non-Normal option (Normal is the reset target). A scene is not
		// listed here: it now applies on entry to any kind, Normal included.
		if (isNormal && option.HasResetTrigger)
			result.AddWarning($"HouseMode option '{option.Value}' is Normal but carries reset triggers; they are inert on the reset target.");

		// An Away/Guest option with no reset trigger stays active until someone changes it by hand — legal, but
		// usually a forgotten trigger, so warn rather than let it silently stick.
		if (isAwayOrGuest && !option.HasResetTrigger)
			result.AddWarning($"HouseMode option '{option.Value}' is {option.Kind} but has no reset trigger; it will stay active until a manual change.");

		// ResetOnPeriodStart is load-bearing only on a non-Normal option; on a Normal one it is inert, so a stale
		// period name is a warning rather than a document error.
		if (option.ResetOnPeriodStart is { Length: > 0 } resetPeriod
			&& !config.Periods.Any(p => string.Equals(p.Name, resetPeriod, StringComparison.OrdinalIgnoreCase)))
		{
			if (isNormal)
				result.AddWarning($"HouseMode option '{option.Value}' ResetOnPeriodStart '{resetPeriod}' matches no period, but the option is Normal so it is inert.");
			else
				result.AddError($"HouseMode option '{option.Value}' ResetOnPeriodStart '{resetPeriod}' matches no configured period.");
		}

		if (option.ResetPresenceGraceMinutes < 0)
			result.AddError($"HouseMode option '{option.Value}' ResetPresenceGraceMinutes must not be negative (is {option.ResetPresenceGraceMinutes}).");

		// ActivateAfterNoMotionMinutes is an activation trigger — meaningful only on a non-Normal option, and a
		// positive duration (zero would fire the instant motion stops). Inert on Normal, so warn rather than error.
		if (option.ActivateAfterNoMotionMinutes is { } idleMinutes)
		{
			if (idleMinutes <= 0)
				result.AddError($"HouseMode option '{option.Value}' ActivateAfterNoMotionMinutes must be positive (is {idleMinutes}).");
			else if (isNormal)
				result.AddWarning($"HouseMode option '{option.Value}' is Normal but sets ActivateAfterNoMotionMinutes; a Normal option is the reset target, so it is inert.");
		}

		// ResetAtTime accepts any date/time-bearing entity: input_datetime, the time/datetime helper domains, and a
		// sensor (whose device_class the pure validator cannot see — a timestamp/date sensor is the intended case;
		// a value that will not parse is caught at runtime by ResolveResetMoment). Anything else is a document error.
		if (option.ResetAtTime is { Length: > 0 } resetAt)
		{
			if (resetAt.Domain() is not ("input_datetime" or "time" or "datetime" or "sensor"))
				result.AddError($"HouseMode option '{option.Value}' ResetAtTime '{resetAt}' is not a date or time entity (input_datetime, time, datetime, or a timestamp/date sensor).");
			else if (knownEntityIds is not null && !knownEntityIds.Contains(resetAt))
				result.AddError($"HouseMode option '{option.Value}' ResetAtTime '{resetAt}' is not known to Home Assistant.");
		}

		// ActivateWhileOn: the engine reads these as on/off, so only input_boolean, switch and binary_sensor can
		// force the mode — any other domain is inert and warned; an id HA does not know is an error. Mirrors the
		// presence-sensor rules below.
		foreach (string? sensor in option.ActivateWhileOn.Where(s => !string.IsNullOrWhiteSpace(s)))
		{
			if (sensor.Domain() is not ("input_boolean" or "switch" or "binary_sensor"))
				result.AddWarning($"HouseMode option '{option.Value}' ActivateWhileOn includes '{sensor}', which is not an input_boolean, switch or binary_sensor; it cannot turn the mode on.");

			if (knownEntityIds is not null && !knownEntityIds.Contains(sensor))
				result.AddError($"HouseMode option '{option.Value}' ActivateWhileOn refers to '{sensor}', which Home Assistant does not know.");
		}

		// Presence sensors: the engine only detects presence on binary_sensor (turn-on) or person/device_tracker
		// (state → home), so any other domain is inert and warned; an id HA does not know is an error.
		foreach (string? sensor in option.ResetPresenceSensors.Where(s => !string.IsNullOrWhiteSpace(s)))
		{
			if (sensor.Domain() is not ("binary_sensor" or "person" or "device_tracker"))
				result.AddWarning($"HouseMode option '{option.Value}' ResetPresenceSensors includes '{sensor}', which is not a binary_sensor, person or device_tracker; its presence will not reset the mode.");

			if (knownEntityIds is not null && !knownEntityIds.Contains(sensor))
				result.AddError($"HouseMode option '{option.Value}' ResetPresenceSensors refers to '{sensor}', which Home Assistant does not know.");
		}
	}

	/// <summary>When any option is Sleep and any area respects sleep mode, the §4.1 clamp chain must resolve to an existing period.</summary>
	private static void ValidateSleepPath(AdaptiveLightingConfig config, HouseModeConfig? houseMode, ValidationResult result)
	{
		List<HouseModeOptionConfig> sleepOptions = houseMode?.Options.Where(o => o.Kind == ModeKind.Sleep).ToList() ?? [];
		if (sleepOptions.Count == 0)
			return;

		if (!config.Areas.Any(z => z.Effective(config.Defaults).RespectSleepMode))
			return;   // sleep is not load-bearing

		foreach (HouseModeOptionConfig? option in sleepOptions)
		{
			string? clamp = HouseModeConfig.SleepClampPeriodFor(option, config.Periods);
			bool resolves = clamp is { Length: > 0 }
				&& config.Periods.Any(p => string.Equals(p.Name, clamp, StringComparison.OrdinalIgnoreCase));

			if (!resolves)
				result.AddError(
					$"Sleep option '{option.Value}' is load-bearing (an area respects sleep) but no clamp period resolves: " +
					"set its ClampPeriod, have a period SetsMode this option, or add a period named 'night'.");
		}
	}

	private static void WarnOnLiveOptionMismatch(
		HouseModeConfig houseMode,
		IReadOnlyCollection<string> liveSelectOptions,
		ValidationResult result)
	{
		foreach (HouseModeOptionConfig option in houseMode.Options)
			if (option.Value is { Length: > 0 }
				&& !liveSelectOptions.Any(live => string.Equals(live.Trim(), option.Value.Trim(), StringComparison.OrdinalIgnoreCase)))
				result.AddWarning($"HouseMode option '{option.Value}' is no longer one of the select's live options — its kind and reset triggers are inert.");

		foreach (string live in liveSelectOptions)
			if (live is { Length: > 0 } && houseMode.OptionFor(live) is null)
				result.AddWarning($"The select offers option '{live}', which nothing has classified — it behaves as Normal.");
	}

	private static void ValidateAreas(
		AdaptiveLightingConfig config,
		IReadOnlyCollection<string>? knownEntityIds,
		IReadOnlyCollection<string>? knownAreaIds,
		ValidationResult result)
	{
		// A warning, not an error. An empty area list is a legitimate state, not a broken document: it is what a
		// brand-new installation starts from before discovery has run, and what a household is left with after
		// deliberately removing every room. The engine runs perfectly well managing nothing — it simply commands
		// nothing — whereas refusing the document stops the whole app and greets a new owner with "the
		// configuration has document-level errors", which is both alarming and untrue.
		if (config.Areas.Count == 0)
		{
			result.AddWarning("No areas yet — the engine is running but managing nothing. Add a room on the Configuration page.");
			return;
		}

		IEnumerable<string> duplicateAreas = config.Areas
			.GroupBy(z => z.DisplayName, StringComparer.OrdinalIgnoreCase)
			.Where(g => g.Count() > 1)
			.Select(g => g.Key);

		foreach (string? name in duplicateAreas)
			result.AddError($"Duplicate area name '{name}'.");

		foreach (AreaConfig area in config.Areas)
		{
			ValidateSettings(area.DisplayName, area.Effective(config.Defaults), result);
			ValidateAreaReferences(area, knownEntityIds, knownAreaIds, result);
		}
	}

	private static void ValidateAreaReferences(
		AreaConfig area,
		IReadOnlyCollection<string>? knownEntityIds,
		IReadOnlyCollection<string>? knownAreaIds,
		ValidationResult result)
	{
		bool hasExplicitLights = area.Lights is { Count: > 0 };

		if (string.IsNullOrWhiteSpace(area.AreaId) && !hasExplicitLights)
		{
			result.AddAreaError(area.DisplayName, "Neither AreaId nor an explicit Lights list — nothing to resolve.");
			return;
		}

		if (knownAreaIds is not null && area.AreaId is { Length: > 0 } areaId && !knownAreaIds.Contains(areaId))
			result.AddAreaError(area.DisplayName,
				$"AreaId '{areaId}' is not a registry area id. AreaId is the slug, not the display name. Known area ids: {string.Join(", ", knownAreaIds.Order(StringComparer.Ordinal))}.");

		if (knownEntityIds is null)
			return;

		foreach (string entityId in EnumerateAreaEntities(area))
			if (!knownEntityIds.Contains(entityId))
				result.AddAreaError(area.DisplayName, $"Refers to '{entityId}', which Home Assistant does not know.");
	}

	private static IEnumerable<string> EnumerateAreaEntities(AreaConfig area)
	{
		foreach (string light in area.Lights ?? [])
			yield return light;

		foreach (string sensor in area.MotionSensors ?? [])
			yield return sensor;

		foreach (string blocker in area.IgnoreWhenOn ?? [])
			yield return blocker;

		if (area.LuxSensor is { Length: > 0 } lux)
			yield return lux;
	}

	private static void ValidateSettings(string scope, AreaSettings settings, ValidationResult result)
	{
		if (settings.VacancyTimeoutSeconds <= 0)
			result.AddError($"[{scope}] VacancyTimeoutSeconds must be positive (is {settings.VacancyTimeoutSeconds}).");

		if (settings.PreOffSeconds < 0)
			result.AddError($"[{scope}] PreOffSeconds must not be negative (is {settings.PreOffSeconds}).");

		if (settings.PreOffSeconds >= settings.VacancyTimeoutSeconds)
			result.AddError($"[{scope}] PreOffSeconds ({settings.PreOffSeconds}) must be shorter than VacancyTimeoutSeconds ({settings.VacancyTimeoutSeconds}).");

		if (settings.PreOffBrightnessFactor is < 0 or > 1)
			result.AddError($"[{scope}] PreOffBrightnessFactor must be between 0 and 1 (is {settings.PreOffBrightnessFactor}).");

		if (settings.OverrideDurationMinutes < 0)
			result.AddError($"[{scope}] OverrideDurationMinutes must not be negative (is {settings.OverrideDurationMinutes}).");

		if (settings.VacancyResetMinutes < 0)
			result.AddError($"[{scope}] VacancyResetMinutes must not be negative (is {settings.VacancyResetMinutes}).");

		if (settings.LuxThreshold < 0)
			result.AddError($"[{scope}] LuxThreshold must not be negative (is {settings.LuxThreshold}).");

		if (settings.LuxHysteresis < 0)
			result.AddError($"[{scope}] LuxHysteresis must not be negative (is {settings.LuxHysteresis}).");

		if (settings.SunElevationThreshold is < MinSunElevationDegrees or > MaxSunElevationDegrees)
			result.AddError($"[{scope}] SunElevationThreshold must be between {MinSunElevationDegrees} and {MaxSunElevationDegrees} degrees (is {settings.SunElevationThreshold}).");

		if (string.IsNullOrWhiteSpace(settings.SunEntity))
			result.AddError($"[{scope}] SunEntity is empty.");

		if (settings.DayTransitionSeconds < 0)
			result.AddError($"[{scope}] DayTransitionSeconds must not be negative (is {settings.DayTransitionSeconds}).");

		if (settings.NightTransitionSeconds < 0)
			result.AddError($"[{scope}] NightTransitionSeconds must not be negative (is {settings.NightTransitionSeconds}).");
	}
}
