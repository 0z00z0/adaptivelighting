namespace AdaptiveLighting.Configuration;

/// <summary>The house-wide settings: the numbers, the entities they name, the labels, and the outdoor lux sensor.</summary>
internal static class GlobalSettingsRules
{
	internal static void Validate(GlobalConfig global, ValidationContext context, ValidationResult result)
	{
		IReadOnlyCollection<string>? knownEntityIds = context.KnownEntityIds;

		ValidateIncludeLabel(global, context.LabelsInUse, result);

		if (global.AwayDebounceMinutes < 0)
			result.AddError($"Global.AwayDebounceMinutes must not be negative (is {global.AwayDebounceMinutes}).");

		if (global.CircadianTickSeconds <= 0)
			result.AddError($"Global.CircadianTickSeconds must be positive (is {global.CircadianTickSeconds}).");

		if (global.SelfEchoWindowSeconds < 0)
			result.AddError($"Global.SelfEchoWindowSeconds must not be negative (is {global.SelfEchoWindowSeconds}).");

		if (global.BlendMinutes < 0)
			result.AddError($"Global.BlendMinutes must not be negative (is {global.BlendMinutes}).");

		if (string.IsNullOrWhiteSpace(global.SunEntity))
			result.AddError("Global.SunEntity is empty.");

		// MotionDeviceClasses is not checked for emptiness: empty means GlobalConfig.DefaultMotionDeviceClasses.

		if (knownEntityIds is null)
			return;

		foreach ((string? label, string? entityId) in EnumerateGlobalEntities(global))
			if (!knownEntityIds.Contains(entityId))
				result.AddError($"Global.{label} refers to '{entityId}', which Home Assistant does not know.");

		// Never an error: the engine fails open on an unreadable kill switch, so a missing one cannot darken the
		// house, and the built-in switch may simply not be visible to the standalone web host yet.
		if (global.EffectiveKillSwitchEntity(context.DefaultKillSwitchEntity) is { Length: > 0 } killSwitch
			&& !knownEntityIds.Contains(killSwitch))
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

	/// <summary>A label setting still holding a name, which the start-up write could not turn into a label id.</summary>
	/// <remarks>It keeps working, by name, until the label is renamed in Home Assistant. Saying so is the only warning there is about it.</remarks>
	internal static void ValidateLabelsAreIds(
		GlobalConfig global,
		IReadOnlyCollection<string>? knownLabelIds,
		ValidationResult result)
	{
		foreach ((string setting, string value) in LabelTranslation.NotIds(global, knownLabelIds))
			result.AddWarning(
				$"Global.{setting} is '{value}', which Home Assistant does not know as a label id. It still matches by name, "
				+ "but it stops matching if that label is renamed. Pick the label again on the settings page to store its id.");
	}

	/// <summary>The include label, when nothing in Home Assistant carries it.</summary>
	/// <remarks>The filter fails closed room by room, so this says once that one typo at the top of the file is behind every per-room message.</remarks>
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

	/// <summary>The house names an outdoor lux sensor and nothing reads it, or a room follows a sensor the house does not name.</summary>
	/// <remarks>A warning only: the validator is pure and cannot run discovery, so it cannot know which rooms will find a sensor of their own.</remarks>
	internal static void ValidateOutdoorLuxOptIn(AdaptiveLightingConfig config, ValidationResult result)
	{
		bool houseHasOne = config.Global.OutdoorLuxSensor is { Length: > 0 };
		List<AreaConfig> following = [.. config.Areas.Where(area => area.FollowOutdoorLux == true)];

		if (houseHasOne && following.Count == 0 && !config.Areas.Any(ReadsHouseSensorForCurve))
			result.AddWarning(
				"Global.OutdoorLuxSensor is set, but no room follows it for darkness and no room reads it for the "
				+ "daylight curve. Set FollowOutdoorLux on the rooms that should go dark by it.");

		if (!houseHasOne)
			foreach (AreaConfig area in following)
				result.AddWarning(
					$"[{area.DisplayName}] FollowOutdoorLux is on but Global.OutdoorLuxSensor names no sensor, so the room "
					+ "has no lux reading and counts as dark. Name the house's outdoor sensor, or give the room a LuxSensor.");
	}

	// A room with no DaylightSensor of its own reads the house sensor once the room or one of its lights follows the curve.
	private static bool ReadsHouseSensorForCurve(AreaConfig area) =>
		area.DaylightSensor is not { Length: > 0 }
		&& (area.Levels.Any(level => level.FollowDaylightCurve == true)
			|| (area.LightLevels ?? []).Any(light => light?.Levels?.Any(level => level.FollowDaylightCurve == true) == true));

	/// <summary>A room follows the daylight curve for one of its periods, but has nothing to read it from.</summary>
	/// <remarks>
	///     The curve reads <see cref="GlobalConfig.OutdoorLuxSensor"/> unless a room names its own
	///     <see cref="AreaConfig.DaylightSensor"/>, so the house sensor is what covers the rooms that say nothing.
	///     With no reading at all the curve sits at its dark end, which is a level nobody chose. Room-driven, not
	///     period-driven: the opt-in is <see cref="RoomLevelOverride.FollowDaylightCurve"/> on that room's own
	///     <see cref="AreaConfig.Levels"/> row, so a room that never claims the curve is never named here.
	/// </remarks>
	internal static void ValidateLuxBrightnessSource(AdaptiveLightingConfig config, ValidationResult result)
	{
		if (config.Global.OutdoorLuxSensor is { Length: > 0 })
			return;

		IReadOnlyList<string> unsupplied =
		[
			.. config.Areas
				.Where(area => area.DaylightSensor is not { Length: > 0 })
				.Where(area => area.Levels.Any(level => level.FollowDaylightCurve == true))
				.Select(area => area.DisplayName)
		];

		if (unsupplied.Count == 0)
			return;

		result.AddWarning(
			$"At least one room follows the daylight curve for one of its periods, but Global.OutdoorLuxSensor "
			+ $"names no sensor, so {unsupplied.Count} room(s) have nothing to read ({string.Join(", ", unsupplied)}). "
			+ "Name the house's outdoor light-level sensor, or give each of those rooms a DaylightSensor of its own. "
			+ "Until then the curve holds those rooms at LuxBrightnessMinPct.");
	}

	/// <summary>Settings the document still carries that no longer do anything.</summary>
	/// <remarks>
	///     Forwarded, never derived: the key is unmatched by the time the document is bound, so only
	///     <see cref="LightingConfigDocument.Deserialize"/> is in a position to see one. A warning, because the
	///     document runs perfectly well with it and refusing the save would block the page that removes it.
	/// </remarks>
	internal static void ValidateRetiredKeys(IReadOnlyList<string> retiredKeys, ValidationResult result)
	{
		foreach (string retired in retiredKeys)
			result.AddWarning(retired);
	}
}
