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
	/// <param name="labelsInUse">
	///     Every registry label that at least one entity carries, by id and by name — the same either-way matching
	///     the resolver does. When <c>null</c>, the include-label warning is skipped, same pattern as
	///     <paramref name="knownEntityIds"/>.
	/// </param>
	/// <param name="livePeriodSelectOptions">
	///     The live <c>options</c> of the configured period select. A second collection rather than a reuse of
	///     <paramref name="liveSelectOptions"/>: they are two different helpers, and checking one document's option
	///     strings against the other helper's list would report renames that had not happened and miss the ones that
	///     had. When <c>null</c>, the rename warning is skipped.
	/// </param>
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

	/// <summary>
	///     The <c>input_select</c> tied to the period table, in whichever direction its authority names.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>A mapping that cannot resolve is an error here, where the same shape is a warning for a room's
	///         levels.</b> The severities are not inconsistent: a levels row naming a renamed period costs one room
	///         one preference and is nearly always recoverable by hand, whereas a period mapping that resolves to
	///         nothing leaves the whole house unable to place the time of day the household just selected — under
	///         <see cref="PeriodAuthority.HomeAssistant"/> that is every room, at once, for as long as the select
	///         sits on that option.
	///     </para>
	///     <para>
	///         The one thing that is only a warning is a stored <c>Value</c> the live select no longer offers. That
	///         is a rename in Home Assistant rather than a mistake in this document, the row is inert rather than
	///         dangerous, and erroring would make the document unsaveable from the very page that exists to fix it.
	///     </para>
	/// </remarks>
	private static void ValidatePeriodSelect(
		AdaptiveLightingConfig config,
		IReadOnlyCollection<string>? knownEntityIds,
		IReadOnlyCollection<string>? livePeriodSelectOptions,
		ValidationResult result)
	{
		if (config.Global.PeriodSelect is not { } select)
			return;

		if (select.Entity is { Length: > 0 } entity)
		{
			// The domain is an error because it can never work: nothing but an input_select has options to read or
			// write. An id Home Assistant does not know is only a warning, on the same argument the outdoor lux
			// sensor gets — it fails open in both directions (a missing select yields no override under HomeAssistant
			// authority, and a mirror write that lands nowhere under AdaptiveLighting), so the house degrades to the
			// schedule rather than going dark, and refusing the whole document over a typo would be the harsher fault.
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

			// Duplicates are reported rather than silently resolved: two rows for one option string means the file
			// says two things about the same selection, and only the first would ever be read.
			if (!seen.Add(option.Value.Trim()))
				result.AddError($"Duplicate PeriodSelect option value '{option.Value.Trim()}'.");

			if (string.IsNullOrWhiteSpace(option.Period))
				result.AddError($"PeriodSelect option '{option.Value.Trim()}' names no Period, so selecting it would mean nothing.");
			else if (!config.Periods.Any(period => string.Equals(period.Name, option.Period.Trim(), StringComparison.OrdinalIgnoreCase)))
				result.AddError($"PeriodSelect option '{option.Value.Trim()}' maps to period '{option.Period.Trim()}', which matches no configured period.");
		}

		// Authority is Home Assistant's and there is nothing for it to decide with. Not an error: the engine falls
		// back to its own schedule for every unmapped value, so the house keeps working — it simply never follows
		// the select, which is the opposite of what the document asked for and worth saying out loud.
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

	/// <summary>
	///     The house names an outdoor lux sensor, and no room asked to read it.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>This is a meaning change, said out loud, and it is the only place a document can be told about
	///         it.</b> The outdoor sensor was once handed to every room that resolved no lux sensor of its own,
	///         silently. It is now an opt-in per room (<see cref="AreaConfig.FollowOutdoorLux"/>), because one
	///         shaded outdoor sensor reading several hundred lux through the day held off every sensorless room in
	///         a house that was genuinely dark. A document written under the old rule looks identical under the
	///         new one and means something different: rooms that used to gate on the outdoor reading now have no
	///         reading, so the lux half of their gate stops refusing and they light on movement.
	///     </para>
	///     <para>
	///         A warning rather than a migration, deliberately. The validator is pure and cannot run discovery, so
	///         it cannot know which rooms will find a sensor of their own and are therefore unaffected; and
	///         rewriting somebody's file to preserve a behaviour they may well have been suffering under is the
	///         kind of help nobody asked for. The new behaviour is the intended one — better to light too early
	///         than never — so this says what changed and how to put it back, room by room, and leaves the choice
	///         where it belongs.
	///     </para>
	///     <para>
	///         The mirror case is an area-level warning: a room that asked to follow an outdoor sensor the house
	///         does not name has asked for nothing, and would sit there counting as dark while believing itself
	///         gated.
	///     </para>
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
	///     The daylight brightness adjustment is switched on somewhere, but the document names no lux sensor at all.
	/// </summary>
	/// <remarks>
	///     <para>
	///         A warning, and only one, at document level. It degrades rather than breaks: a room with no reading
	///         gets the schedule's brightness, which is what the whole house did before the feature existed. And the
	///         validator is pure — it cannot run discovery — so it cannot know that a room will find an illuminance
	///         sensor of its own at runtime. Erroring on something it cannot see would refuse a perfectly good
	///         document.
	///     </para>
	///     <para>
	///         What it <i>can</i> see is the case that motivates the feature: a hallway has no lux sensor, so the
	///         reading has to come from <see cref="GlobalConfig.OutdoorLuxSensor"/> — and if no room pins one and
	///         no room follows the house's, the switch is on and nothing anywhere is guaranteed to feed it. Said
	///         once, at the top, in the same spirit as the include-label warning.
	///     </para>
	///     <para>
	///         <b>Naming the outdoor sensor is no longer enough to satisfy this.</b> It used to be: the sensor was
	///         handed to every room that had none. Now a room reads it only if it says so
	///         (<see cref="AreaConfig.FollowOutdoorLux"/>), and the daylight curve reads whatever the darkness gate
	///         reads — one sensor per room, one answer — so a house that names an outdoor sensor no room follows
	///         feeds the curve nothing at all.
	///     </para>
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

		// MotionDeviceClasses is deliberately not checked for emptiness: empty is the default and means
		// GlobalConfig.DefaultMotionDeviceClasses. See the remarks on the property.

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

		// Outdoor lux sensor: the reading offered to the rooms that ask for it. It fails open — an unknown or
		// non-sensor id just leaves those rooms with no reading, which now means they count as dark rather than
		// that they stop lighting — so both are warnings, not errors.
		if (global.OutdoorLuxSensor is { Length: > 0 } outdoorLux)
		{
			if (outdoorLux.Domain() is not "sensor")
				result.AddWarning($"Global.OutdoorLuxSensor '{outdoorLux}' is not a sensor entity; the rooms that follow it have no lux reading and count as dark.");
			else if (!knownEntityIds.Contains(outdoorLux))
				result.AddWarning($"Global.OutdoorLuxSensor '{outdoorLux}' is not known to Home Assistant; the rooms that follow it count as dark until it appears.");
		}
	}

	/// <summary>
	///     The include label, when nothing in Home Assistant carries it.
	/// </summary>
	/// <remarks>
	///     A warning and never an error, deliberately. The filter fails closed room by room — every room reports
	///     that its lights carry no such label and is skipped — so the house degrades exactly as it does for any
	///     other unresolvable room, and the document stays saveable. What the per-room messages cannot say is that
	///     one typo at the top of the file is behind all of them, which is what this says once.
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

	/// <summary>
	///     What one room runs instead of the schedule (<see cref="AreaConfig.Levels"/>).
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>A dangling period name is a warning and the row survives.</b> It is nearly always a rename, and
	///         deleting somebody's levels on a rename is the worse failure by a distance — the row is inert until
	///         the name matches something again, which is a state a human can look at and fix. Refusing the document
	///         over it would be worse still: renaming a period is itself a save, so the file would deadlock.
	///     </para>
	///     <para>
	///         <b>A value outside the physical range is an error.</b> That is not a rename, it is a number nobody
	///         could have meant, and it is checked exactly as the schedule's own levels are — same range, same
	///         severity. The editor writes these, so refusing the save is where it is cheapest to notice.
	///     </para>
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

			// An empty row is not a claim, so it cannot be the row that "won". CircadianCalculator.LevelsOf skips
			// these before it takes the first match, and counting them here made the warning say the opposite of
			// what the engine does: a cleared row followed by a real one drew "the first one wins and the rest are
			// ignored" while the room actually ran on the second. Only reachable on a hand-edited file — Save
			// normalises empty rows away before validating — which is exactly the file whose reader has no other
			// way to find out.
			if (level.IsEmpty)
				continue;

			// First wins, matching the calculator and the house-mode Normal rows. Reported rather than silently
			// resolved: two rows for one period means the file says two things, and the reader has to be told which.
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

	/// <summary>The physical ranges, checked whether or not the row's period resolves — a nonsense number is nonsense either way.</summary>
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

	/// <summary>
	///     The daylight brightness curve: two anchors, a ceiling and a shaping exponent.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Checked whether or not the feature is switched on, matching how <c>LuxThreshold</c> is checked for an
	///         area gating on the sun alone: a number outside its range is a mistake in the document, and it is no
	///         less a mistake for currently being inert — it would come alive the moment somebody flipped the
	///         switch. Every default is valid, so a document that predates the feature passes untouched.
	///     </para>
	///     <para>
	///         The engine survives all of these on its own (<c>LuxBrightnessCurve</c> makes a nonsensical curve
	///         inert rather than dangerous), but that is a safety net, not a reason to accept the document. Silently
	///         ignoring the curve an owner wrote is worse than telling them it cannot be read.
	///     </para>
	/// </remarks>
	private static void ValidateLuxBrightness(string scope, AreaSettings settings, ValidationResult result)
	{
		// A logarithm needs a positive anchor. This is the one that would be genuinely undefined rather than merely
		// odd, so it is checked before the ordering.
		if (settings.LuxBrightnessStartLux <= 0)
			result.AddError($"[{scope}] LuxBrightnessStartLux must be positive (is {settings.LuxBrightnessStartLux}) — the curve interpolates on log10(lux), which has no value at or below zero.");

		// Covers inverted and equal in one: equal anchors leave no range to interpolate across.
		if (settings.LuxBrightnessFullLux <= settings.LuxBrightnessStartLux)
			result.AddError($"[{scope}] LuxBrightnessFullLux ({settings.LuxBrightnessFullLux}) must be above LuxBrightnessStartLux ({settings.LuxBrightnessStartLux}).");

		if (settings.LuxBrightnessMaxPct is < MinBrightnessPct or > MaxBrightnessPct)
			result.AddError($"[{scope}] LuxBrightnessMaxPct is {settings.LuxBrightnessMaxPct}, outside {MinBrightnessPct}–{MaxBrightnessPct}.");

		// Zero is the dangerous one rather than merely the useless one: pow(0, 0) is 1, so a zero exponent reads as
		// "full daylight level at any reading, including pitch dark" to anything that trusts the arithmetic.
		if (settings.LuxBrightnessGamma <= 0)
			result.AddError($"[{scope}] LuxBrightnessGamma must be positive (is {settings.LuxBrightnessGamma}); 1 is a straight line, above 1 holds the level back until it is properly bright.");

		// On but unable to add anything: legal, inert, and almost certainly a switch flipped without a ceiling set.
		if (settings.LuxBrightnessEnabled && settings.LuxBrightnessMaxPct <= MinBrightnessPct)
			result.AddWarning($"[{scope}] LuxBrightnessEnabled is on but LuxBrightnessMaxPct is {settings.LuxBrightnessMaxPct}, so daylight can never raise the brightness above the schedule.");
	}
}
