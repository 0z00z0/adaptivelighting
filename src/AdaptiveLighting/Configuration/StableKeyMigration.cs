namespace AdaptiveLighting.Configuration;

/// <summary>
///     Gives every period and house-mode option an <c>Id</c>, and repoints the document's own cross-references
///     from the name they used to name onto that id.
/// </summary>
/// <remarks>
///     Idempotent and self-terminating: a document whose periods and options all carry an id, and whose references
///     all resolve to one, is left untouched and reports no change. That report is what stops the load path
///     rewriting the file on every start, and the store keeps a single backup slot, so a second rewrite would push
///     the only pre-migration copy out of it.
///     A reference that resolves to nothing is left exactly as it was. The validator's existing severities then
///     still apply to it: a dangling levels row warns, a dangling select mapping errors.
/// </remarks>
public static class StableKeyMigration
{
	private const StringComparison ByKey = StringComparison.OrdinalIgnoreCase;

	/// <summary>Mutates <paramref name="config"/> in place. <c>true</c> when anything was written.</summary>
	public static bool Apply(AdaptiveLightingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		List<TimePeriodConfig> periods = config.Periods ?? [];
		HouseModeConfig? houseMode = config.Global?.HouseMode;
		List<HouseModeOptionConfig> options = houseMode?.Options ?? [];

		bool changed = AssignPeriodIds(periods);
		changed |= AssignOptionIds(options);

		// After both assignments: a reference can only be repointed onto an id that exists by now.
		changed |= RepointPeriodReferences(config, periods, options);
		changed |= RepointModeReferences(periods, options);

		return changed;
	}

	private static bool AssignPeriodIds(List<TimePeriodConfig> periods)
	{
		HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);

		foreach (TimePeriodConfig period in periods)
			if (period.Id is { Length: > 0 } id)
				taken.Add(id.Trim());

		bool changed = false;

		foreach (TimePeriodConfig period in periods)
			if (period.Id is not { Length: > 0 })
			{
				period.Id = StableId.Create(period.Name, taken);
				changed = true;
			}

		return changed;
	}

	private static bool AssignOptionIds(List<HouseModeOptionConfig> options)
	{
		HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);

		foreach (HouseModeOptionConfig option in options)
			if (option.Id is { Length: > 0 } id)
				taken.Add(id.Trim());

		bool changed = false;

		foreach (HouseModeOptionConfig option in options)
			if (option.Id is not { Length: > 0 })
			{
				option.Id = StableId.Create(option.Value, taken);
				changed = true;
			}

		return changed;
	}

	private static bool RepointPeriodReferences(
		AdaptiveLightingConfig config,
		List<TimePeriodConfig> periods,
		List<HouseModeOptionConfig> options)
	{
		bool changed = false;

		foreach (AreaConfig area in config.Areas ?? [])
			foreach (RoomLevelOverride level in area.Levels ?? [])
				if (PeriodIdFor(level.PeriodId, periods) is { } id)
				{
					level.PeriodId = id;
					changed = true;
				}

		foreach (PeriodSelectOptionConfig mapping in config.Global?.PeriodSelect?.Options ?? [])
			if (PeriodIdFor(mapping.PeriodId, periods) is { } id)
			{
				mapping.PeriodId = id;
				changed = true;
			}

		foreach (HouseModeOptionConfig option in options)
		{
			if (PeriodIdFor(option.ClampPeriodId, periods) is { } clamp)
			{
				option.ClampPeriodId = clamp;
				changed = true;
			}

			if (PeriodIdFor(option.ResetOnPeriodStartId, periods) is { } reset)
			{
				option.ResetOnPeriodStartId = reset;
				changed = true;
			}
		}

		return changed;
	}

	private static bool RepointModeReferences(List<TimePeriodConfig> periods, List<HouseModeOptionConfig> options)
	{
		bool changed = false;

		foreach (TimePeriodConfig period in periods)
			if (OptionIdFor(period.SetsModeId, options) is { } id)
			{
				period.SetsModeId = id;
				changed = true;
			}

		return changed;
	}

	/// <summary>The id <paramref name="reference"/> should become, or <c>null</c> to leave it exactly as it is.</summary>
	/// <remarks>
	///     Ids are tried before names, so a migrated document repoints nothing. Names are matched the way every
	///     other reference in this document is: the referring side is trimmed, <c>Name</c> is not.
	/// </remarks>
	private static string? PeriodIdFor(string? reference, List<TimePeriodConfig> periods)
	{
		if (reference is not { Length: > 0 })
			return null;

		string needle = reference.Trim();

		if (periods.Any(period => string.Equals(period.Id, needle, ByKey)))
			return null;

		return periods.FirstOrDefault(period => string.Equals(period.Name, needle, ByKey))?.Id is { Length: > 0 } id
			&& !string.Equals(id, needle, ByKey)
				? id
				: null;
	}

	/// <summary>
	///     The option id <paramref name="reference"/> should become, or <c>null</c> to leave it alone.
	/// </summary>
	/// <remarks>
	///     A mode switch naming a live select option nobody has configured resolves to no row, so it stays the
	///     raw option string and the engine goes on writing it. The validator has always allowed that.
	/// </remarks>
	private static string? OptionIdFor(string? reference, List<HouseModeOptionConfig> options)
	{
		if (reference is not { Length: > 0 })
			return null;

		string needle = reference.Trim();

		if (options.Any(option => string.Equals(option.Id, needle, ByKey)))
			return null;

		return options.FirstOrDefault(option => string.Equals(option.Value?.Trim(), needle, ByKey))?.Id is { Length: > 0 } id
			&& !string.Equals(id, needle, ByKey)
				? id
				: null;
	}
}
