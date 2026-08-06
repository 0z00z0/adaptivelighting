using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>Where one value on a levels row came from.</summary>
public enum LevelSource
{
	/// <summary>The house's schedule decides it, and a later edit to the schedule reaches this room.</summary>
	Schedule,

	/// <summary>This room states it for itself, and the schedule no longer reaches it.</summary>
	Room
}

/// <summary>
///     One period as the room will run it: the level in force, and whether the room or the schedule decided it.
/// </summary>
/// <remarks>
///     Brightness and colour carry their provenance separately because the schema does. A room can state a
///     brightness and go on inheriting the warmth.
/// </remarks>
/// <param name="PeriodId">The key the override is stored under, and what an edit writes back against.</param>
/// <param name="Name">The period's display name, for the screen only.</param>
public sealed record RoomLevelRow(
	string PeriodId,
	string Name,
	double BrightnessPct,
	LevelSource Brightness,
	int ColorTempKelvin,
	LevelSource Colour)
{
	/// <summary>Whether this room states anything at all for this period, which is what draws the row's mark.</summary>
	public bool IsOwn => Brightness == LevelSource.Room || Colour == LevelSource.Room;
}

/// <summary>A row this room states for a period the schedule no longer has, nearly always one deleted by hand.</summary>
/// <param name="PeriodId">The key the room wrote, which matches no period in the schedule.</param>
public sealed record RoomLevelOrphan(string PeriodId, double? BrightnessPct, int? ColorTempKelvin)
{
	// A row with values but no period id survives normalisation, so the blank case reaches the screen.
	public string Name => PeriodId is { Length: > 0 } named ? named : "(a row with no period)";

	/// <summary>What the orphan holds, written out, so the row says what removing it would throw away.</summary>
	public string Says
	{
		get
		{
			List<string> parts = [];

			if (BrightnessPct is { } brightness)
				parts.Add(TokenFormat.Percent(brightness));

			if (ColorTempKelvin is { } kelvin)
				parts.Add($"{kelvin.ToString(CultureInfo.InvariantCulture)} K");

			return parts.Count == 0 ? "nothing" : string.Join(" · ", parts);
		}
	}
}

/// <summary>
///     What a room runs instead of the schedule, projected for the surface that shows and edits it.
/// </summary>
/// <remarks>
///     The writing half is the only place <see cref="AreaConfig.Levels"/> is mutated, so the rule that an empty
///     row is dropped, never stored, lives in one place.
/// </remarks>
public static class RoomLevels
{
	// Case-insensitive, matching the engine. Ordinal would show an orphan for a period the engine still applies.
	private const StringComparison ByKey = StringComparison.OrdinalIgnoreCase;

	/// <summary>One row per period in the schedule, in the schedule's own order, showing what this room will run.</summary>
	/// <param name="room">The room, or <c>null</c> to read the schedule alone.</param>
	public static IReadOnlyList<RoomLevelRow> Rows(IReadOnlyList<TimePeriodConfig> periods, AreaConfig? room)
	{
		ArgumentNullException.ThrowIfNull(periods);

		List<RoomLevelRow> rows = [];

		foreach (TimePeriodConfig period in periods)
		{
			RoomLevelOverride? own = Stated(room, period.Key);

			rows.Add(new RoomLevelRow(
				period.Key,
				period.Name,
				own?.BrightnessPct ?? period.BrightnessPct,
				own?.BrightnessPct is not null ? LevelSource.Room : LevelSource.Schedule,
				own?.ColorTempKelvin ?? period.ColorTempKelvin,
				own?.ColorTempKelvin is not null ? LevelSource.Room : LevelSource.Schedule));
		}

		return rows;
	}

	/// <summary>The rows this room states for periods the schedule no longer has. Empty rows are not reported.</summary>
	public static IReadOnlyList<RoomLevelOrphan> Orphans(IReadOnlyList<TimePeriodConfig> periods, AreaConfig? room)
	{
		ArgumentNullException.ThrowIfNull(periods);

		if (room is null)
			return [];

		return
		[
			.. room.Levels
				.Where(level => !level.IsEmpty)
				.Where(level => !periods.Any(period => string.Equals(period.Key, level.PeriodId, ByKey)))
				.Select(level => new RoomLevelOrphan(level.PeriodId, level.BrightnessPct, level.ColorTempKelvin))
		];
	}

	/// <summary>How many of the periods on screen this room states for itself.</summary>
	/// <remarks>Counted off <see cref="Rows"/>, so it cannot disagree with the marks in the table. Orphans are a
	///     separate count.</remarks>
	public static int OwnCount(IReadOnlyList<TimePeriodConfig> periods, AreaConfig? room) =>
		Rows(periods, room).Count(row => row.IsOwn);

	/// <summary>Sets this room's brightness for a period, or sends it back to the schedule with <c>null</c>.</summary>
	public static void SetBrightness(AreaConfig room, string periodId, double? brightnessPct)
	{
		ArgumentNullException.ThrowIfNull(room);

		Edit(room, periodId, level => level.BrightnessPct = brightnessPct);
	}

	/// <summary>Sets this room's colour temperature for a period, or sends it back to the schedule with <c>null</c>.</summary>
	public static void SetColorTemp(AreaConfig room, string periodId, int? kelvin)
	{
		ArgumentNullException.ThrowIfNull(room);

		Edit(room, periodId, level => level.ColorTempKelvin = kelvin);
	}

	/// <summary>Drops everything this room says about a period, which is the road back from an orphan.</summary>
	/// <returns>Whether anything was removed.</returns>
	public static bool Remove(AreaConfig room, string periodId)
	{
		ArgumentNullException.ThrowIfNull(room);

		return room.Levels.RemoveAll(level => string.Equals(level.PeriodId, periodId, ByKey)) > 0;
	}

	/// <summary>The schedule's periods this room states nothing for, which is where an orphan row may be sent.</summary>
	public static IReadOnlyList<TimePeriodConfig> FreePeriods(IReadOnlyList<TimePeriodConfig> periods, AreaConfig? room)
	{
		ArgumentNullException.ThrowIfNull(periods);

		return [.. periods.Where(period => Stated(room, period.Key) is null)];
	}

	/// <summary>Points an orphaned row at a real period, carrying its brightness and warmth across.</summary>
	/// <returns>Whether the row moved.</returns>
	/// <remarks>
	///     Refuses a target the room already states, so a move cannot silently overwrite a level that is on
	///     screen. <see cref="FreePeriods"/> is what the picker offers, and this repeats the check because the
	///     schedule can change under an open page.
	/// </remarks>
	public static bool Repoint(AreaConfig room, string fromPeriodId, string toPeriodId)
	{
		ArgumentNullException.ThrowIfNull(room);

		if (string.IsNullOrWhiteSpace(toPeriodId) || string.Equals(fromPeriodId, toPeriodId, ByKey))
			return false;

		RoomLevelOverride? orphan = Find(room, fromPeriodId);
		if (orphan is null || orphan.IsEmpty || Stated(room, toPeriodId) is not null)
			return false;

		orphan.PeriodId = toPeriodId;
		Prune(room);
		return true;
	}

	// Read path. Skips empty rows because CircadianCalculator.LevelsOf skips them: on a hand-edited file with a
	// cleared row above a real one, taking the first row regardless shows a level the room does not run.
	private static RoomLevelOverride? Stated(AreaConfig? room, string periodId) =>
		room?.Levels.FirstOrDefault(level => !level.IsEmpty && string.Equals(level.PeriodId, periodId, ByKey));

	// Write path. Any row, empty included, so an edit reuses a cleared row instead of adding a second one beside it.
	private static RoomLevelOverride? Find(AreaConfig? room, string periodId) =>
		room?.Levels.FirstOrDefault(level => string.Equals(level.PeriodId, periodId, ByKey));

	/// <summary>Applies one change to the room's row for a period, creating it and dropping it as needed.</summary>
	private static void Edit(AreaConfig room, string periodId, Action<RoomLevelOverride> change)
	{
		// Stated first, so the edit lands on the row the page is showing. Going straight to Find wrote into a
		// cleared row above the real one, which then became first and took over from it.
		RoomLevelOverride? level = Stated(room, periodId) ?? Find(room, periodId);

		if (level is null)
		{
			level = new RoomLevelOverride { PeriodId = periodId };
			room.Levels.Add(level);
		}

		change(level);
		Prune(room);
	}

	// After each edit, not at save time: a row left behind by a clear counts as an override to everything that
	// reads AreaConfig.Levels.
	private static void Prune(AreaConfig room) => room.Levels.RemoveAll(level => level.IsEmpty);
}
