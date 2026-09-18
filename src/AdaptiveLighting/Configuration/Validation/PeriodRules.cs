namespace AdaptiveLighting.Configuration;

/// <summary>The circadian table: ids, starts, targets, and the rooms whose movement may begin a period.</summary>
internal static class PeriodRules
{
	internal static void Validate(List<TimePeriodConfig> periods, ValidationResult result)
	{
		if (periods.Count == 0)
		{
			result.AddError("Periods is empty — the engine has no circadian table and could never pick a target.");
			return;
		}

		// Names are free to repeat: nothing resolves by one any more. Two rows answering to one key are not, or the
		// first would silently take every reference to the second.
		IEnumerable<string> duplicateKeys = periods
			.Where(p => p.Key is { Length: > 0 })
			.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
			.Where(g => g.Count() > 1)
			.Select(g => g.Key);

		foreach (string key in duplicateKeys)
			result.AddError($"Two periods share the id '{key}'. Give one of them an Id of its own, or delete it and add it again.");

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

			ValidateTargets(period, result);
		}
	}

	/// <summary>The rooms whose movement may start a period (<see cref="TimePeriodConfig.StartsOnMotionAreas"/>).</summary>
	/// <remarks>
	///     A warning: an id no room answers to costs that one room its trigger, and an error would block the save from
	///     the page a room is renamed on. Area ids are matched ordinally, so a display name written here does warn.
	/// </remarks>
	internal static void ValidateStartsOnMotion(AdaptiveLightingConfig config, ValidationResult result)
	{
		HashSet<string> areaIds = new(StringComparer.Ordinal);

		foreach (AreaConfig area in config.Areas)
			if (area.AreaId is { Length: > 0 } areaId)
				areaIds.Add(areaId.Trim());

		// An unparseable Start already drops the whole period, so it earns nothing further here.
		List<TimePeriodConfig> placeable = [.. config.Periods.Where(period => PeriodStart.TryParse(period.Start, out _))];

		// A period that waits for movement is out of the table until it begins, so a table of nothing else leaves
		// the rooms with no period at all from midnight until somebody moves.
		if (placeable.Count > 0 && placeable.All(period => period.StartsOnMotion))
			result.AddWarning(
				"Every period sets StartsOnMotion, so none of them begins on the clock. From midnight until somebody "
				+ "moves there is no period in force and the rooms are commanded nothing. At least one period should "
				+ "start on its own time.");

		foreach (TimePeriodConfig period in placeable.Where(p => p.StartsOnMotion))
		{
			foreach (string areaId in (period.StartsOnMotionAreas ?? []).Where(id => !string.IsNullOrWhiteSpace(id)))
				if (!areaIds.Contains(areaId.Trim()))
					result.AddWarning(
						$"Period '{period.Name}' StartsOnMotionAreas names '{areaId.Trim()}', which matches no room in "
						+ "this document, so movement there can never start the period. It is the room's area id — the "
						+ "slug, not its display name.");
		}
	}

	private static void ValidateTargets(TimePeriodConfig period, ValidationResult result)
	{
		// The raw value, because that is the key the document carries and the number a person has to find in it.
		// A file still written in percent reaches this through the same property, scaled.
		if (period.Brightness is < ValidationRanges.MinBrightness or > ValidationRanges.MaxBrightness)
			result.AddError($"Period '{period.Name}' has Brightness {period.Brightness}, outside {ValidationRanges.MinBrightness}–{ValidationRanges.MaxBrightness}.");

		if (period.ColorTempKelvin is < ValidationRanges.MinColorTempKelvin or > ValidationRanges.MaxColorTempKelvin)
			result.AddError($"Period '{period.Name}' has ColorTempKelvin {period.ColorTempKelvin}, outside {ValidationRanges.MinColorTempKelvin}–{ValidationRanges.MaxColorTempKelvin}.");
	}
}
