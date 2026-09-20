namespace AdaptiveLighting.Configuration;

/// <summary>The rooms: their names, the entities they refer to, their scenes, and the levels they state.</summary>
internal static class AreaRules
{
	internal static void Validate(
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
			ValidateReferences(area, knownEntityIds, knownAreaIds, result);
			ValidateLevelRows(config.Periods, $"[{area.DisplayName}]", area.Levels, result);
			ValidateLightLevels(config.Periods, area, result);
		}
	}

	/// <summary>What single lights in a room run instead of the room (<see cref="AreaConfig.LightLevels"/>).</summary>
	/// <remarks>
	///     Whether the room actually commands the light named here needs group membership, which this class is not
	///     given; <see cref="Engine.AreaEntityResolver"/> warns about that and the room page shows the orphan. An
	///     unknown entity id reaches the ordinary area error through <see cref="EnumerateAreaEntities"/>.
	/// </remarks>
	private static void ValidateLightLevels(List<TimePeriodConfig> periods, AreaConfig area, ValidationResult result)
	{
		HashSet<string> lights = new(StringComparer.OrdinalIgnoreCase);

		foreach (LightLevelOverride light in area.LightLevels ?? [])
		{
			string entityId = light.EntityId?.Trim() ?? "";

			if (entityId.Length == 0)
			{
				result.AddWarning(
					$"[{area.DisplayName}] has a light-levels entry naming no light, so it reaches nothing. Name the "
					+ "light it was written for, or remove the entry.");
				continue;
			}

			// First wins, matching how the engine builds one calculator per light.
			if (!lights.Add(entityId))
			{
				result.AddWarning(
					$"[{area.DisplayName}] states levels for '{entityId}' more than once; the first entry wins and the "
					+ "rest are ignored. Merge them into one.");
				continue;
			}

			ValidateLevelRows(periods, $"[{area.DisplayName}] {entityId}", light.Levels, result);
		}
	}

	/// <summary>The rows one owner states, whether that owner is a room or a single light inside one.</summary>
	/// <remarks>
	///     A dangling period id warns and the row survives: it is nearly always a period deleted by hand, and the levels
	///     are worth more than the tidiness. A value outside the physical range is an error, as the schedule's own is.
	/// </remarks>
	// scope carries its own brackets and reads as the sentence's subject, so a light's warning names the light
	// and a room's names the room.
	private static void ValidateLevelRows(
		List<TimePeriodConfig> periods,
		string scope,
		IReadOnlyList<RoomLevelOverride>? levels,
		ValidationResult result)
	{
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

		foreach (RoomLevelOverride level in levels ?? [])
		{
			ValidateLevelRange(periods, scope, level, result);

			if (level.PeriodId is not { Length: > 0 } key || string.IsNullOrWhiteSpace(key))
			{
				result.AddWarning(
					$"{scope} has a levels row naming no period, so it replaces nothing. Name the period "
					+ "it was written for, or remove the row.");
				continue;
			}

			// Skipped before the duplicate count, matching CircadianCalculator.LevelsOf: an empty row is not the row that
			// won. Only a hand-edited file reaches here with empty rows, since Save normalises them away first.
			if (level.IsEmpty)
				continue;

			// First wins, matching the calculator.
			if (!seen.Add(key.Trim()))
			{
				result.AddWarning(
					$"{scope} has more than one levels row for period '{ValidationRanges.PeriodLabel(periods, key)}'; the "
					+ "first one wins and the rest are ignored. Merge them into one row.");
				continue;
			}

			if (ValidationRanges.PeriodWithKey(periods, key) is null)
			{
				result.AddWarning(
					$"{scope} has levels for period '{key.Trim()}', which matches no configured period — "
					+ "almost always a period that has been deleted. The row is kept so the levels are not lost, but it "
					+ "does nothing until it names a period that exists.");
			}
		}
	}

	/// <summary>The physical ranges, checked whether or not the row's period resolves.</summary>
	private static void ValidateLevelRange(
		List<TimePeriodConfig> periods,
		string scope,
		RoomLevelOverride level,
		ValidationResult result)
	{
		string label = ValidationRanges.PeriodLabel(periods, level.PeriodId);

		if (level.Brightness is { } brightness && brightness is < ValidationRanges.MinBrightness or > ValidationRanges.MaxBrightness)
			result.AddError(
				$"{scope} levels for period '{label}' have Brightness {brightness}, outside {ValidationRanges.MinBrightness}–{ValidationRanges.MaxBrightness}.");

		if (level.ColorTempKelvin is { } kelvin && kelvin is < ValidationRanges.MinColorTempKelvin or > ValidationRanges.MaxColorTempKelvin)
			result.AddError(
				$"{scope} levels for period '{label}' have ColorTempKelvin {kelvin}, outside {ValidationRanges.MinColorTempKelvin}–{ValidationRanges.MaxColorTempKelvin}.");
	}

	private static void ValidateReferences(
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

		ValidateScenes(area, knownEntityIds, result);

		if (knownEntityIds is null)
			return;

		foreach (string entityId in EnumerateAreaEntities(area))
			if (!knownEntityIds.Contains(entityId))
				result.AddAreaError(area.DisplayName, $"Names '{entityId}', which Home Assistant does not know. Check it for typos, or remove it.");
	}

	/// <summary>The two per-room scenes. A bad one warns: the room still lights, by its own levels.</summary>
	private static void ValidateScenes(
		AreaConfig area,
		IReadOnlyCollection<string>? knownEntityIds,
		ValidationResult result)
	{
		foreach ((string field, string? scene) in new[]
		{
			(nameof(AreaConfig.SceneOnMotion), area.SceneOnMotion),
			(nameof(AreaConfig.SceneWhenEmpty), area.SceneWhenEmpty)
		})
		{
			if (scene is not { Length: > 0 })
				continue;

			if (!scene.HasDomain("scene"))
				result.AddWarning($"[{area.DisplayName}] {field} names '{scene}', which is not a scene entity, so it will never run.");
			else if (knownEntityIds is not null && !knownEntityIds.Contains(scene))
				result.AddWarning($"[{area.DisplayName}] {field} names '{scene}', which Home Assistant does not know. Check it for typos, or remove it.");
		}
	}

	private static IEnumerable<string> EnumerateAreaEntities(AreaConfig area)
	{
		foreach (string light in area.Lights ?? [])
			yield return light;

		foreach (string sensor in area.MotionSensors ?? [])
			yield return sensor;

		foreach (string leadIn in area.LeadInSensors ?? [])
			yield return leadIn;

		foreach (string blocker in area.IgnoreWhenOn ?? [])
			yield return blocker;

		foreach (string holder in area.KeepLitWhenOn ?? [])
			yield return holder;

		if (area.LuxSensor is { Length: > 0 } lux)
			yield return lux;

		foreach (LightLevelOverride light in area.LightLevels ?? [])
			if (light.EntityId is { Length: > 0 } stated && stated.Trim() is { Length: > 0 } trimmed)
				yield return trimmed;
	}

	/// <summary>One settings block, whether it is the house defaults or one room's effective settings.</summary>
	internal static void ValidateSettings(string scope, AreaSettings settings, ValidationResult result)
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

		if (settings.SunElevationThreshold is < ValidationRanges.MinSunElevationDegrees or > ValidationRanges.MaxSunElevationDegrees)
			result.AddError($"[{scope}] SunElevationThreshold must be between {ValidationRanges.MinSunElevationDegrees} and {ValidationRanges.MaxSunElevationDegrees} degrees (is {settings.SunElevationThreshold}).");

		if (settings.DayTransitionSeconds < 0)
			result.AddError($"[{scope}] DayTransitionSeconds must not be negative (is {settings.DayTransitionSeconds}).");

		if (settings.NightTransitionSeconds < 0)
			result.AddError($"[{scope}] NightTransitionSeconds must not be negative (is {settings.NightTransitionSeconds}).");
	}

	/// <summary>The daylight brightness curve: two lux anchors, two brightness ends and a shaping exponent.</summary>
	/// <remarks>
	///     Checked whether or not a period runs the curve, since a bad number comes alive the moment one does.
	///     Every default is valid, so a document predating the feature passes untouched.
	/// </remarks>
	private static void ValidateLuxBrightness(string scope, AreaSettings settings, ValidationResult result)
	{
		// Checked before the ordering: a non-positive anchor is undefined, not merely odd.
		if (settings.LuxBrightnessStartLux <= 0)
			result.AddError($"[{scope}] LuxBrightnessStartLux must be positive (is {settings.LuxBrightnessStartLux}) — the curve interpolates on log10(lux), which has no value at or below zero.");

		// Covers inverted and equal in one: equal anchors leave no range to interpolate across.
		if (settings.LuxBrightnessFullLux <= settings.LuxBrightnessStartLux)
			result.AddError($"[{scope}] LuxBrightnessFullLux ({settings.LuxBrightnessFullLux}) must be above LuxBrightnessStartLux ({settings.LuxBrightnessStartLux}).");

		if (settings.LuxBrightnessMinPct is < ValidationRanges.MinBrightnessPct or > ValidationRanges.MaxBrightnessPct)
			result.AddError($"[{scope}] LuxBrightnessMinPct is {settings.LuxBrightnessMinPct}, outside {ValidationRanges.MinBrightnessPct}–{ValidationRanges.MaxBrightnessPct}.");

		if (settings.LuxBrightnessMaxPct is < ValidationRanges.MinBrightnessPct or > ValidationRanges.MaxBrightnessPct)
			result.AddError($"[{scope}] LuxBrightnessMaxPct is {settings.LuxBrightnessMaxPct}, outside {ValidationRanges.MinBrightnessPct}–{ValidationRanges.MaxBrightnessPct}.");

		// Zero is the dangerous value, not merely the useless one: pow(0, 0) is 1, so it reads as full daylight
		// level at any reading, pitch dark included.
		if (settings.LuxBrightnessGamma <= 0)
			result.AddError($"[{scope}] LuxBrightnessGamma must be positive (is {settings.LuxBrightnessGamma}); 1 is a straight line, above 1 holds the level back until it is properly bright.");
	}
}
