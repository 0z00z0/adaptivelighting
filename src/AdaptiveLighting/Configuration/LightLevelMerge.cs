namespace AdaptiveLighting.Configuration;

/// <summary>How one light's own levels sit on top of its room's, period by period.</summary>
/// <remarks>
///     Pure: neither argument is read for identity and neither is mutated. The result is an ordinary
///     <see cref="RoomLevelOverride"/> list, so the calculator built on it is the same class the room runs and
///     needs no notion of a light at all.
/// </remarks>
public static class LightLevelMerge
{
	/// <summary>The rows one light runs: its own where it states a value, its room's otherwise.</summary>
	/// <returns>One row per period either side names, in the room's order and then the light's.</returns>
	public static IReadOnlyList<RoomLevelOverride> MergeOnto(
		IReadOnlyList<RoomLevelOverride>? roomLevels,
		IReadOnlyList<RoomLevelOverride>? lightLevels)
	{
		Dictionary<string, RoomLevelOverride> room = ByPeriod(roomLevels);
		Dictionary<string, RoomLevelOverride> light = ByPeriod(lightLevels);

		List<RoomLevelOverride> merged = [];

		foreach (string periodId in room.Keys.Concat(light.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			room.TryGetValue(periodId, out RoomLevelOverride? theirs);
			light.TryGetValue(periodId, out RoomLevelOverride? own);

			RoomLevelOverride row = Merge(theirs, own);

			if (!row.IsEmpty)
				merged.Add(row);
		}

		return merged;
	}

	/// <summary>One period's merged row: what the light states, falling back to what the room states.</summary>
	// Brightness and the curve flag are one answer and travel together, or a lamp pinned to 30 % under a room
	// that follows the daylight curve would be silently overruled by the curve. Warmth travels alone.
	public static RoomLevelOverride Merge(RoomLevelOverride? room, RoomLevelOverride? light)
	{
		bool? curve = SpeaksToBrightness(light)
			? light!.FollowDaylightCurve == true ? true : null
			: room?.FollowDaylightCurve == true ? true : null;

		return new RoomLevelOverride
		{
			PeriodId = (light?.PeriodId ?? room?.PeriodId ?? "").Trim(),
			BrightnessPct = light?.BrightnessPct ?? room?.BrightnessPct,
			ColorTempKelvin = light?.ColorTempKelvin ?? room?.ColorTempKelvin,
			FollowDaylightCurve = curve
		};
	}

	// Either half of the pair counts. A row carrying only the curve flag is exactly what the page writes when the
	// curve box is ticked, since nothing seeds a brightness beside it, and reading such a row as saying nothing
	// would make ticking the box on a light do nothing at all.
	private static bool SpeaksToBrightness(RoomLevelOverride? level) =>
		level is not null && (level.BrightnessPct is not null || level.FollowDaylightCurve == true);

	// First row wins on a duplicate period, matching the calculator and what the validator reports. An empty row
	// is not the row that won, so it cannot shadow a later row that says something.
	private static Dictionary<string, RoomLevelOverride> ByPeriod(IReadOnlyList<RoomLevelOverride>? levels)
	{
		Dictionary<string, RoomLevelOverride> byPeriod = new(StringComparer.OrdinalIgnoreCase);

		foreach (RoomLevelOverride level in levels ?? [])
			if (!level.IsEmpty && level.PeriodId is { Length: > 0 })
				byPeriod.TryAdd(level.PeriodId.Trim(), level);

		return byPeriod;
	}
}
