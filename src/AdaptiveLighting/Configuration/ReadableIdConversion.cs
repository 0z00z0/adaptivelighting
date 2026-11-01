using Adaptive.Settings;

namespace AdaptiveLighting.Configuration;

/// <summary>Converts a document to readable ids once; every reference follows in the same write. Lighting only.</summary>
// A converted document reports no change, or every start would rewrite it and push the original out of the .bak slot.
// A reference matching no old id is left alone, so the validator still reports it.
internal static class ReadableIdConversion
{
	private const string RetiredNightName = "night";

	/// <summary>Mutates <paramref name="config"/> in place. <c>true</c> when anything was written.</summary>
	/// <param name="config">The document as read.</param>
	/// <param name="periodIds">Each renumbered period's old id and its new one, compared ignoring case.</param>
	public static bool Apply(AdaptiveLightingConfig config, out IReadOnlyDictionary<string, string> periodIds)
	{
		ArgumentNullException.ThrowIfNull(config);

		List<TimePeriodConfig> periods = config.Periods;
		IdCounters counters = config.Global.IdCounters;
		List<HouseModeOptionConfig> options = config.Global.HouseMode?.Options ?? [];

		Dictionary<string, string> renumbered = new(StringComparer.OrdinalIgnoreCase);
		periodIds = renumbered;

		List<string> oldIds =
		[
			.. periods
				.Select(period => period.Id?.Trim())
				.OfType<string>()
				.Where(id => id.Length > 0 && ReadableId.NumberOf(ReadableId.PeriodPrefix, id) is null)
		];

		// A shared old id cannot say which period a reference meant, so nothing moves and the validator reports it.
		if (oldIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() < oldIds.Count)
			return false;

		// Only an option written before readable ids carries an Id, and only those documents had the night name link.
		bool oldOptions = options.Any(option => option.Id is not null);
		bool oldShape = oldOptions || periods.Any(period => ReadableId.NumberOf(ReadableId.PeriodPrefix, period.Id?.Trim()) is null);

		// Read before anything moves: the dim period the retired name link gave a sleep option, set explicitly below.
		Dictionary<HouseModeOptionConfig, TimePeriodConfig> nightClamps = oldOptions ? NightClamps(periods, options) : [];

		foreach (TimePeriodConfig period in periods)
		{
			if (ReadableId.NumberOf(ReadableId.PeriodPrefix, period.Id?.Trim()) is not null)
				continue;

			string fresh = ReadableId.NextPeriod(counters, periods);

			if (period.Id?.Trim() is { Length: > 0 } old)
				renumbered.Add(old, fresh);

			period.Id = fresh;
		}

		RepointPeriodReferences(config, options, renumbered);
		RepointModeReferences(periods, options);

		foreach ((HouseModeOptionConfig option, TimePeriodConfig night) in nightClamps)
			option.ClampPeriodId = night.Id;

		bool roomsChanged = false;

		foreach (AreaConfig area in config.Areas)
			if (string.IsNullOrWhiteSpace(area.AreaId) && ReadableId.NumberOf(ReadableId.RoomPrefix, area.Id?.Trim()) is null)
			{
				area.Id = ReadableId.NextRoom(counters, config.Areas);
				roomsChanged = true;
			}

		// A hand-written document may carry readable ids and no counter.
		bool countersRaised = ReadableId.RaiseCounters(counters, periods, config.Areas);

		return oldShape || roomsChanged || countersRaised;
	}

	// The old chain: an explicit clamp, else the period whose SetsModeId names the option, else a period named night.
	private static Dictionary<HouseModeOptionConfig, TimePeriodConfig> NightClamps(
		List<TimePeriodConfig> periods,
		List<HouseModeOptionConfig> options)
	{
		Dictionary<HouseModeOptionConfig, TimePeriodConfig> clamps = [];

		if (periods.FirstOrDefault(period => period.Name.SameName(RetiredNightName)) is not { } night)
			return clamps;

		foreach (HouseModeOptionConfig option in options.Where(option => option.Kind is ModeKind.Sleep))
		{
			if (option.ClampPeriodId is { Length: > 0 })
				continue;

			string oldKey = option.Id is { Length: > 0 } id ? id : option.Value;

			if (!periods.Any(period => period.SetsModeId is { Length: > 0 } && period.SetsModeId.SameName(oldKey)))
				clamps[option] = night;
		}

		return clamps;
	}

	private static void RepointPeriodReferences(
		AdaptiveLightingConfig config,
		List<HouseModeOptionConfig> options,
		Dictionary<string, string> periodIds)
	{
		if (periodIds.Count == 0)
			return;

		foreach (AreaConfig area in config.Areas)
		{
			foreach (RoomLevelOverride level in area.Levels ?? [])
				level.PeriodId = Renamed(level.PeriodId, periodIds) ?? level.PeriodId;

			foreach (LightLevelOverride light in area.LightLevels ?? [])
				foreach (RoomLevelOverride level in light.Levels ?? [])
					level.PeriodId = Renamed(level.PeriodId, periodIds) ?? level.PeriodId;
		}

		foreach (PeriodSelectOptionConfig mapping in config.Global.PeriodSelect?.Options ?? [])
			mapping.PeriodId = Renamed(mapping.PeriodId, periodIds) ?? mapping.PeriodId;

		foreach (HouseModeOptionConfig option in options)
		{
			option.ClampPeriodId = Renamed(option.ClampPeriodId, periodIds) ?? option.ClampPeriodId;
			option.ResetOnPeriodStartId = Renamed(option.ResetOnPeriodStartId, periodIds) ?? option.ResetOnPeriodStartId;
		}
	}

	// A period's SetsModeId named the option's minted id; the option's own text is its id now.
	private static void RepointModeReferences(List<TimePeriodConfig> periods, List<HouseModeOptionConfig> options)
	{
		Dictionary<string, string> optionIds = new(StringComparer.OrdinalIgnoreCase);

		foreach (HouseModeOptionConfig option in options)
		{
			if (option.Id?.Trim() is { Length: > 0 } id)
				optionIds.TryAdd(id, option.Value.Trim());

			option.Id = null;
		}

		foreach (TimePeriodConfig period in periods)
			period.SetsModeId = Renamed(period.SetsModeId, optionIds) ?? period.SetsModeId;
	}

	private static string? Renamed(string? reference, Dictionary<string, string> map) =>
		reference?.Trim() is { Length: > 0 } key && map.TryGetValue(key, out string? renamed) ? renamed : null;
}
