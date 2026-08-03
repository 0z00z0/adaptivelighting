namespace AdaptiveLighting.Configuration;

/// <summary>
///     Checks an <see cref="AdaptiveLightingConfig"/> before the engine is built. Pure: the known entity and
///     area ids are passed in, never read from HA, so the whole validator is unit-testable without fakes.
/// </summary>
/// <remarks>
///     Document-level problems stop the engine. Referential problems are one area's business: an entity renamed in
///     HA costs that area, not the house.
/// </remarks>
public static class ConfigValidator
{
	private const double MinBrightnessPct = 0;
	private const double MaxBrightnessPct = 100;
	private const int MinColorTempKelvin = 1000;
	private const int MaxColorTempKelvin = 10000;
	private const double MinSunElevationDegrees = -90;
	private const double MaxSunElevationDegrees = 90;

	/// <summary>Validates <paramref name="config"/>. A null collection means "skip the checks that need it".</summary>
	/// <remarks>
	///     <c>labelsInUse</c> lists labels by id and by name, matching either way as the resolver does. The two
	///     selects' live options come in separately: they are different helpers, and crossing them would report
	///     renames that never happened.
	/// </remarks>
	public static ValidationResult Validate(
		AdaptiveLightingConfig config,
		IReadOnlyCollection<string>? knownEntityIds = null,
		IReadOnlyCollection<string>? knownAreaIds = null,
		IReadOnlyCollection<string>? liveSelectOptions = null,
		IReadOnlyCollection<string>? labelsInUse = null,
		IReadOnlyCollection<string>? livePeriodSelectOptions = null)
	{
		ArgumentNullException.ThrowIfNull(config);

		ValidationResult result = new();

		ValidateGlobal(config.Global, knownEntityIds, labelsInUse, result);
		ValidatePeriods(config.Periods, result);
		ValidateHouseMode(config, knownEntityIds, liveSelectOptions, result);
		ValidatePeriodSelect(config, knownEntityIds, livePeriodSelectOptions, result);
		ValidateSettings("Defaults", config.Defaults, result);
		ValidateAreas(config, knownEntityIds, knownAreaIds, result);
		ValidateOutdoorLuxOptIn(config, result);
		ValidateLuxBrightnessSource(config, result);

		return result;
	}

	/// <summary>The <c>input_select</c> tied to the period table, in whichever direction its authority names.</summary>
	/// <remarks>
	///     An unresolvable mapping is an error here where the same shape is a warning on a room's levels: under
	///     <see cref="PeriodAuthority.HomeAssistant"/> it costs every room at once. A stored value the live select no
	///     longer offers stays a warning, because erroring would make the document unsaveable from the page that
	///     fixes it.
	/// </remarks>
	private static void ValidatePeriodSelect(
		AdaptiveLightingConfig config,
		IReadOnlyCollection<string>? knownEntityIds,
		IReadOnlyCollection<string>? livePeriodSelectOptions,
		ValidationResult result)
	{
		if (config.Global.PeriodSelect is not { } select)
			return;

		// Mappings but no entity. Every other branch below is skipped in that case, so without this the household is
		// told nothing at all, and the normaliser cannot drop a block that still holds rows.
		if (string.IsNullOrWhiteSpace(select.Entity) && select.Options.Count > 0)
		{
			result.AddWarning(
				$"[PeriodSelect] maps {select.Options.Count} option(s) to periods but names no Entity, so nothing "
				+ "reads or writes them and the schedule stays in charge. Name the input_select, or remove the block.");
		}

		if (select.Entity is { Length: > 0 } entity)
		{
			// One helper cannot be both the house mode and the time of day. Both are input_selects, so every other
			// rule passes; under AdaptiveLighting authority the period mirror would then overwrite Away or Sleep
			// within one tick of it being set. The per-object authority check cannot see two objects on one helper.
			if (config.Global.HouseMode?.Entity is { Length: > 0 } houseModeEntity
				&& string.Equals(entity.Trim(), houseModeEntity.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				result.AddError(
					$"[PeriodSelect] Entity '{entity}' is also the house-mode select. One helper cannot carry both "
					+ "the house mode and the time of day: the two would overwrite each other every tick. Give the "
					+ "periods their own input_select.");
			}

			// Wrong domain can never work, so it errors. An unknown id fails open in both directions and only warns.
			if (!entity.HasDomain("input_select"))
				result.AddError($"PeriodSelect.Entity '{entity}' is not an input_select. The time of day is a Home Assistant dropdown helper.");
			else if (knownEntityIds is not null && !knownEntityIds.Contains(entity))
				result.AddWarning($"PeriodSelect.Entity '{entity}' is not known to Home Assistant; until it appears, every room follows the schedule.");
		}

		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

		foreach (PeriodSelectOptionConfig option in select.Options)
		{
			if (string.IsNullOrWhiteSpace(option.Value))
			{
				result.AddError("A PeriodSelect option has a blank Value, so no select option can ever match it.");
				continue;
			}

			// Only the first row for an option string is ever read.
			if (!seen.Add(option.Value.Trim()))
				result.AddError($"Duplicate PeriodSelect option value '{option.Value.Trim()}'.");

			if (string.IsNullOrWhiteSpace(option.Period))
				result.AddError($"PeriodSelect option '{option.Value.Trim()}' names no Period, so selecting it would mean nothing.");
			else if (!config.Periods.Any(period => string.Equals(period.Name, option.Period.Trim(), StringComparison.OrdinalIgnoreCase)))
				result.AddError($"PeriodSelect option '{option.Value.Trim()}' maps to period '{option.Period.Trim()}', which matches no configured period.");
		}

		// Authority is Home Assistant's with nothing to decide with. The engine falls back to its own schedule for
		// every unmapped value, so this warns instead of erroring.
		if (select.Authority is PeriodAuthority.HomeAssistant && select.Options.Count == 0)
			result.AddWarning(
				"PeriodSelect.Authority is HomeAssistant but no option is mapped to a period, so the select can never "
				+ "change the time of day and every room keeps following the schedule. Map its options, or set "
				+ "Authority back to AdaptiveLighting.");

		if (livePeriodSelectOptions is null)
			return;

		foreach (PeriodSelectOptionConfig option in select.Options.Where(o => !string.IsNullOrWhiteSpace(o.Value)))
			if (!livePeriodSelectOptions.Any(live => string.Equals(live.Trim(), option.Value.Trim(), StringComparison.OrdinalIgnoreCase)))
				result.AddWarning(
					$"PeriodSelect option '{option.Value.Trim()}' is no longer one of the select's live options — it has "
					+ "probably been renamed in Home Assistant, and until it matches again the mapping does nothing.");
	}

	/// <summary>The house names an outdoor lux sensor and no room asked to read it, or the reverse.</summary>
	/// <remarks>
	///     A warning, not a migration: the validator is pure and cannot run discovery, so it cannot know which rooms
	///     will find a sensor of their own.
	/// </remarks>
	private static void ValidateOutdoorLuxOptIn(AdaptiveLightingConfig config, ValidationResult result)
	{
		bool houseHasOne = config.Global.OutdoorLuxSensor is { Length: > 0 };
		List<AreaConfig> following = [.. config.Areas.Where(area => area.FollowOutdoorLux == true)];

		if (houseHasOne && following.Count == 0)
			result.AddWarning(
				"Global.OutdoorLuxSensor is set but no room follows it. It used to be applied automatically to every room "
				+ "that found no light sensor of its own; that fallback is gone, so those rooms now have no lux reading and "
				+ "count as dark — they will light on movement where they previously waited for the outdoor reading to drop. "
				+ "Set FollowOutdoorLux on the rooms that should keep gating on it.");

		if (!houseHasOne)
			foreach (AreaConfig area in following)
				result.AddWarning(
					$"[{area.DisplayName}] FollowOutdoorLux is on but Global.OutdoorLuxSensor names no sensor, so the room "
					+ "has no lux reading and counts as dark. Name the house's outdoor sensor, or give the room a LuxSensor.");
	}

	/// <summary>
	///     The daylight brightness adjustment is on somewhere, but the document guarantees no lux reading at all.
	/// </summary>
	/// <remarks>
	///     Naming <see cref="GlobalConfig.OutdoorLuxSensor"/> is not enough on its own: a room reads it only when it
	///     sets <see cref="AreaConfig.FollowOutdoorLux"/>. One document-level warning, because the validator cannot
	///     run discovery and a room may still find a sensor of its own at runtime.
	/// </remarks>
	private static void ValidateLuxBrightnessSource(AdaptiveLightingConfig config, ValidationResult result)
	{
		bool someRoomHasAReading =
			config.Areas.Any(area => area.LuxSensor is { Length: > 0 })
			|| (config.Global.OutdoorLuxSensor is { Length: > 0 } && config.Areas.Any(area => area.FollowOutdoorLux == true));

		if (someRoomHasAReading)
			return;

		if (!config.Areas.Any(area => area.Effective(config.Defaults).LuxBrightnessEnabled))
			return;

		result.AddWarning(
			"LuxBrightnessEnabled is on for at least one room, but no room is guaranteed a lux reading: give those rooms "
			+ "a LuxSensor, or set FollowOutdoorLux on them and name Global.OutdoorLuxSensor. Rooms that discover an "
			+ "illuminance sensor of their own still follow the daylight; the rest keep the schedule's brightness.");
	}

	private static void ValidateGlobal(
		GlobalConfig global,
		IReadOnlyCollection<string>? knownEntityIds,
		IReadOnlyCollection<string>? labelsInUse,
		ValidationResult result)
	{
		ValidateIncludeLabel(global, labelsInUse, result);

		if (global.AwayDebounceMinutes < 0)
			result.AddError($"Global.AwayDebounceMinutes must not be negative (is {global.AwayDebounceMinutes}).");

		if (global.CircadianTickSeconds <= 0)
			result.AddError($"Global.CircadianTickSeconds must be positive (is {global.CircadianTickSeconds}).");

		if (global.SelfEchoWindowSeconds < 0)
			result.AddError($"Global.SelfEchoWindowSeconds must not be negative (is {global.SelfEchoWindowSeconds}).");

		if (global.BlendMinutes < 0)
			result.AddError($"Global.BlendMinutes must not be negative (is {global.BlendMinutes}).");

		// MotionDeviceClasses is not checked for emptiness: empty means GlobalConfig.DefaultMotionDeviceClasses.

		if (knownEntityIds is null)
			return;

		foreach ((string? label, string? entityId) in EnumerateGlobalEntities(global))
			if (!knownEntityIds.Contains(entityId))
				result.AddError($"Global.{label} refers to '{entityId}', which Home Assistant does not know.");

		// Never an error: the engine fails open on an unreadable kill switch, so a missing one cannot darken the
		// house, and the built-in switch may simply not be visible to the standalone web host yet.
		if (global.EffectiveKillSwitchEntity is { Length: > 0 } killSwitch && !knownEntityIds.Contains(killSwitch))
		{
			if (global.KillSwitchEntity is { Length: > 0 })
				result.AddWarning($"Global.KillSwitchEntity refers to '{killSwitch}', which Home Assistant does not know — the engine runs ungated (it fails open on a missing switch). Clear it to fall back to the built-in switch.");
			else
				result.AddWarning($"The built-in master switch '{killSwitch}' is not known to Home Assistant yet; the state manager creates it at app start.");
		}

		// Fails open: an unknown or non-sensor id leaves the following rooms with no reading, so they count as dark.
		if (global.OutdoorLuxSensor is { Length: > 0 } outdoorLux)
		{
			if (outdoorLux.Domain() is not "sensor")
				result.AddWarning($"Global.OutdoorLuxSensor '{outdoorLux}' is not a sensor entity; the rooms that follow it have no lux reading and count as dark.");
			else if (!knownEntityIds.Contains(outdoorLux))
				result.AddWarning($"Global.OutdoorLuxSensor '{outdoorLux}' is not known to Home Assistant; the rooms that follow it count as dark until it appears.");
		}
	}

	/// <summary>The include label, when nothing in Home Assistant carries it.</summary>
	/// <remarks>
	///     The filter fails closed room by room, so the per-room messages already appear; this says once that one
	///     typo at the top of the file is behind all of them.
	/// </remarks>
	private static void ValidateIncludeLabel(
		GlobalConfig global,
		IReadOnlyCollection<string>? labelsInUse,
		ValidationResult result)
	{
		if (labelsInUse is null || global.IncludeLabel is not { Length: > 0 } include)
			return;

		if (!labelsInUse.Contains(include, StringComparer.OrdinalIgnoreCase))
			result.AddWarning(
				$"Global.IncludeLabel is '{include}', which nothing in Home Assistant carries — with it set and unmatched, "
				+ "no room finds a light to manage. Clear it to manage every light discovery finds, or label the lights in Home Assistant.");
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
	}

	/// <summary>
	///     The house-mode rules. Structural problems are document-level errors; classification quirks are warnings.
	/// </summary>
	private static void ValidateHouseMode(
		AdaptiveLightingConfig config,
		IReadOnlyCollection<string>? knownEntityIds,
		IReadOnlyCollection<string>? liveSelectOptions,
		ValidationResult result)
	{
		HouseModeConfig? houseMode = config.Global.HouseMode;
		List<TimePeriodConfig> periods = config.Periods;

		foreach (TimePeriodConfig? period in periods.Where(p => p.SetsMode is { Length: > 0 }))
		{
			string setsMode = period.SetsMode!;
			HouseModeOptionConfig? option = houseMode?.OptionFor(setsMode);

			// A live option the owner has not tagged yet is still legitimate. Erroring on it would deadlock the save,
			// because tagging that option is itself a save.
			bool isLiveOption = liveSelectOptions?.Any(live => string.Equals(live.Trim(), setsMode.Trim(), StringComparison.OrdinalIgnoreCase)) ?? false;

			if (option is null && !isLiveOption)
				result.AddError($"Period '{period.Name}' has SetsMode '{setsMode}', which matches no house-mode option — neither a configured one nor a live option of the select.");
			else if (option?.Kind == ModeKind.Normal)
				result.AddWarning($"Period '{period.Name}' SetsMode '{setsMode}', which is a Normal option — the period would schedule a reset to the baseline.");
		}

		ValidateSleepPath(config, houseMode, result);

		if (houseMode?.Entity is not { Length: > 0 })
			return;

		// The "unknown to HA" half of the entity check is in ValidateGlobal.
		if (!houseMode.Entity.HasDomain("input_select"))
			result.AddError($"HouseMode.Entity '{houseMode.Entity}' is not an input_select. The house mode is a Home Assistant dropdown helper.");

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

	/// <summary>One Normal, no more. With none the first option is treated as Normal; with several the first wins.</summary>
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

		// Applied on entry for any kind, so a scene on Normal or Sleep is a legal one-shot, not a mistake.
		if (option.Scene is { Length: > 0 } scene)
		{
			if (!scene.HasDomain("scene"))
				result.AddError($"HouseMode option '{option.Value}' has Scene '{scene}', which is not a scene entity.");
			else if (knownEntityIds is not null && !knownEntityIds.Contains(scene))
				result.AddError($"HouseMode option '{option.Value}' Scene '{scene}' is not known to Home Assistant.");
		}

		// ClampPeriod is load-bearing only on Sleep, so a dangling name errors only there.
		if (option.ClampPeriod is { Length: > 0 } clamp)
		{
			if (option.Kind != ModeKind.Sleep)
				result.AddWarning($"HouseMode option '{option.Value}' has a ClampPeriod but its kind is {option.Kind}; it is inert.");
			else if (!config.Periods.Any(p => string.Equals(p.Name, clamp, StringComparison.OrdinalIgnoreCase)))
				result.AddError($"HouseMode option '{option.Value}' ClampPeriod '{clamp}' matches no configured period.");
		}

		// Normal is the reset target, so a trigger on it is inert.
		if (isNormal && option.HasResetTrigger)
			result.AddWarning($"HouseMode option '{option.Value}' is Normal but carries reset triggers; they are inert on the reset target.");

		if (isAwayOrGuest && !option.HasResetTrigger)
			result.AddWarning($"HouseMode option '{option.Value}' is {option.Kind} but has no reset trigger; it will stay active until a manual change.");

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

		// Zero would fire the instant motion stops.
		if (option.ActivateAfterNoMotionMinutes is { } idleMinutes)
		{
			if (idleMinutes <= 0)
				result.AddError($"HouseMode option '{option.Value}' ActivateAfterNoMotionMinutes must be positive (is {idleMinutes}).");
			else if (isNormal)
				result.AddWarning($"HouseMode option '{option.Value}' is Normal but sets ActivateAfterNoMotionMinutes; a Normal option is the reset target, so it is inert.");
		}

		// Read as on/off, so only input_boolean, switch and binary_sensor can force the mode.
		foreach (string? sensor in option.ActivateWhileOn.Where(s => !string.IsNullOrWhiteSpace(s)))
		{
			if (sensor.Domain() is not ("input_boolean" or "switch" or "binary_sensor"))
				result.AddWarning($"HouseMode option '{option.Value}' ActivateWhileOn includes '{sensor}', which is not an input_boolean, switch or binary_sensor; it cannot turn the mode on.");

			if (knownEntityIds is not null && !knownEntityIds.Contains(sensor))
				result.AddError($"HouseMode option '{option.Value}' ActivateWhileOn refers to '{sensor}', which Home Assistant does not know.");
		}

		// Presence is detected on binary_sensor turn-on, or person/device_tracker moving to home. Nothing else.
		foreach (string? sensor in option.ResetPresenceSensors.Where(s => !string.IsNullOrWhiteSpace(s)))
		{
			if (sensor.Domain() is not ("binary_sensor" or "person" or "device_tracker"))
				result.AddWarning($"HouseMode option '{option.Value}' ResetPresenceSensors includes '{sensor}', which is not a binary_sensor, person or device_tracker; its presence will not reset the mode.");

			if (knownEntityIds is not null && !knownEntityIds.Contains(sensor))
				result.AddError($"HouseMode option '{option.Value}' ResetPresenceSensors refers to '{sensor}', which Home Assistant does not know.");
		}
	}

	/// <summary>When any option is Sleep and any area respects sleep mode, the clamp chain must resolve.</summary>
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
		// An empty area list is what a fresh install starts from, so it warns instead of stopping the app.
		if (config.Areas.Count == 0)
		{
			result.AddWarning("No rooms yet — adaptive lighting is running but managing nothing. Add a room under Configuration → Areas.");
			return;
		}

		IEnumerable<string> duplicateAreas = config.Areas
			.GroupBy(z => z.DisplayName, StringComparer.OrdinalIgnoreCase)
			.Where(g => g.Count() > 1)
			.Select(g => g.Key);

		foreach (string? name in duplicateAreas)
			result.AddError($"Duplicate area name '{name}' — two rooms cannot share one name. Rename one of them.");

		foreach (AreaConfig area in config.Areas)
		{
			ValidateSettings(area.DisplayName, area.Effective(config.Defaults), result);
			ValidateAreaReferences(area, knownEntityIds, knownAreaIds, result);
			ValidateRoomLevels(config.Periods, area, result);
		}
	}

	/// <summary>What one room runs instead of the schedule (<see cref="AreaConfig.Levels"/>).</summary>
	/// <remarks>
	///     A dangling period name warns and the row survives: renaming a period is itself a save, so erroring would
	///     deadlock the file. A value outside the physical range is an error, checked as the schedule's own is.
	/// </remarks>
	private static void ValidateRoomLevels(List<TimePeriodConfig> periods, AreaConfig area, ValidationResult result)
	{
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

		foreach (RoomLevelOverride level in area.Levels ?? [])
		{
			ValidateRoomLevelRange(area, level, result);

			if (level.Period is not { Length: > 0 } name || string.IsNullOrWhiteSpace(name))
			{
				result.AddWarning(
					$"[{area.DisplayName}] has a levels row naming no period, so it replaces nothing. Name the period "
					+ "it was written for, or remove the row.");
				continue;
			}

			// Skipped before the duplicate count, matching CircadianCalculator.LevelsOf: an empty row is not the row
			// that won. Counting it made the warning name the wrong row on a hand-edited file, which is the only
			// file that reaches here with empty rows, since Save normalises them away first.
			if (level.IsEmpty)
				continue;

			// First wins, matching the calculator.
			if (!seen.Add(name))
			{
				result.AddWarning(
					$"[{area.DisplayName}] has more than one levels row for period '{name}'; the first one wins and the "
					+ "rest are ignored. Merge them into one row.");
				continue;
			}

			if (!periods.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
			{
				result.AddWarning(
					$"[{area.DisplayName}] has levels for period '{name}', which matches no configured period — almost "
					+ "always a period that has been renamed. The row is kept so the levels are not lost, but it does "
					+ "nothing until it names a period that exists.");
			}
		}
	}

	/// <summary>The physical ranges, checked whether or not the row's period resolves.</summary>
	private static void ValidateRoomLevelRange(AreaConfig area, RoomLevelOverride level, ValidationResult result)
	{
		if (level.BrightnessPct is { } brightness && brightness is < MinBrightnessPct or > MaxBrightnessPct)
			result.AddError(
				$"[{area.DisplayName}] levels for period '{level.Period}' have BrightnessPct {brightness}, outside {MinBrightnessPct}–{MaxBrightnessPct}.");

		if (level.ColorTempKelvin is { } kelvin && kelvin is < MinColorTempKelvin or > MaxColorTempKelvin)
			result.AddError(
				$"[{area.DisplayName}] levels for period '{level.Period}' have ColorTempKelvin {kelvin}, outside {MinColorTempKelvin}–{MaxColorTempKelvin}.");
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
			result.AddAreaError(area.DisplayName, "This room names no Home Assistant area and lists no lights, so nothing can be found for it. Pick an area, or name its lights by hand.");
			return;
		}

		if (knownAreaIds is not null && area.AreaId is { Length: > 0 } areaId && !knownAreaIds.Contains(areaId))
			result.AddAreaError(area.DisplayName,
				$"AreaId '{areaId}' is not a registry area id. AreaId is the slug, not the display name. Known area ids: {string.Join(", ", knownAreaIds.Order(StringComparer.Ordinal))}.");

		if (knownEntityIds is null)
			return;

		foreach (string entityId in EnumerateAreaEntities(area))
			if (!knownEntityIds.Contains(entityId))
				result.AddAreaError(area.DisplayName, $"Names '{entityId}', which Home Assistant does not know. Check it for typos, or remove it.");
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

		ValidateLuxBrightness(scope, settings, result);

		if (settings.SunElevationThreshold is < MinSunElevationDegrees or > MaxSunElevationDegrees)
			result.AddError($"[{scope}] SunElevationThreshold must be between {MinSunElevationDegrees} and {MaxSunElevationDegrees} degrees (is {settings.SunElevationThreshold}).");

		if (string.IsNullOrWhiteSpace(settings.SunEntity))
			result.AddError($"[{scope}] SunEntity is empty.");

		if (settings.DayTransitionSeconds < 0)
			result.AddError($"[{scope}] DayTransitionSeconds must not be negative (is {settings.DayTransitionSeconds}).");

		if (settings.NightTransitionSeconds < 0)
			result.AddError($"[{scope}] NightTransitionSeconds must not be negative (is {settings.NightTransitionSeconds}).");
	}

	/// <summary>The daylight brightness curve: two anchors, a ceiling and a shaping exponent.</summary>
	/// <remarks>
	///     Checked whether or not the feature is switched on, since a bad number comes alive the moment the switch
	///     is flipped. Every default is valid, so a document predating the feature passes untouched.
	/// </remarks>
	private static void ValidateLuxBrightness(string scope, AreaSettings settings, ValidationResult result)
	{
		// Checked before the ordering: a non-positive anchor is undefined, not merely odd.
		if (settings.LuxBrightnessStartLux <= 0)
			result.AddError($"[{scope}] LuxBrightnessStartLux must be positive (is {settings.LuxBrightnessStartLux}) — the curve interpolates on log10(lux), which has no value at or below zero.");

		// Covers inverted and equal in one: equal anchors leave no range to interpolate across.
		if (settings.LuxBrightnessFullLux <= settings.LuxBrightnessStartLux)
			result.AddError($"[{scope}] LuxBrightnessFullLux ({settings.LuxBrightnessFullLux}) must be above LuxBrightnessStartLux ({settings.LuxBrightnessStartLux}).");

		if (settings.LuxBrightnessMaxPct is < MinBrightnessPct or > MaxBrightnessPct)
			result.AddError($"[{scope}] LuxBrightnessMaxPct is {settings.LuxBrightnessMaxPct}, outside {MinBrightnessPct}–{MaxBrightnessPct}.");

		// Zero is the dangerous value, not merely the useless one: pow(0, 0) is 1, so it reads as full daylight
		// level at any reading, pitch dark included.
		if (settings.LuxBrightnessGamma <= 0)
			result.AddError($"[{scope}] LuxBrightnessGamma must be positive (is {settings.LuxBrightnessGamma}); 1 is a straight line, above 1 holds the level back until it is properly bright.");

		if (settings.LuxBrightnessEnabled && settings.LuxBrightnessMaxPct <= MinBrightnessPct)
			result.AddWarning($"[{scope}] LuxBrightnessEnabled is on but LuxBrightnessMaxPct is {settings.LuxBrightnessMaxPct}, so daylight can never raise the brightness above the schedule.");
	}
}
