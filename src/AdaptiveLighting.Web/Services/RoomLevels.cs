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
///     One period as the room will actually run it: the level in force, and whether the room or the schedule
///     decided it.
/// </summary>
/// <remarks>
///     The two values carry their provenance separately because the schema does — a room that only wanted to be
///     dimmer states a brightness and goes on inheriting the warmth — and a row that reported one origin for the
///     pair would have to choose which of the two to lie about.
/// </remarks>
/// <param name="Period">The period's name, which is also the key its override is stored under.</param>
/// <param name="BrightnessPct">The brightness this room runs during the period.</param>
/// <param name="Brightness">Who decided it.</param>
/// <param name="ColorTempKelvin">The white this room runs during the period.</param>
/// <param name="Colour">Who decided it.</param>
/// <remarks>
///     This once carried the period's own floor and ceiling as well, and a <c>Limit</c> line saying what they
///     would do to the brightness above — a room set to 100 % under a night capped at 30 % ran at 30, and the row
///     had to say so or name a level the room never reached. The caps were removed in the 2026-07 simplification,
///     so the brightness in this row is now simply what the room runs.
/// </remarks>
public sealed record RoomLevelRow(
	string Period,
	double BrightnessPct,
	LevelSource Brightness,
	int ColorTempKelvin,
	LevelSource Colour)
{
	/// <summary>Whether this room states anything at all for this period — what draws the row's mark.</summary>
	public bool IsOwn => Brightness == LevelSource.Room || Colour == LevelSource.Room;
}

/// <summary>
///     A row this room states for a period the schedule no longer has.
/// </summary>
/// <remarks>
///     Kept rather than dropped, because it is nearly always a rename in progress — see
///     <see cref="RoomLevelOverride.Period"/>. What it needs from a surface is to be named, to say what it holds,
///     and to be removable; what it must not do is offer controls, because there is no period to edit against.
/// </remarks>
/// <param name="Period">The name the room wrote, which matches no period in the schedule.</param>
/// <param name="BrightnessPct">The brightness it pins, or <c>null</c>.</param>
/// <param name="ColorTempKelvin">The white it pins, or <c>null</c>.</param>
public sealed record RoomLevelOrphan(string Period, double? BrightnessPct, int? ColorTempKelvin)
{
	/// <summary>
	///     What to call the row on screen.
	/// </summary>
	/// <remarks>
	///     A row carrying values but no period name at all survives normalisation — only a row saying nothing is
	///     dropped — so it reaches this surface, and rendered as its blank name it would be a remove button beside
	///     an empty space. It is named for what it is instead, which is also what makes it obvious it is junk.
	/// </remarks>
	public string Name => Period is { Length: > 0 } named ? named : "(a row with no period name)";

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
///     <para>
///         The reading half answers one question — <i>what will this room actually use, and who decided</i> — for
///         every period at once, so a row can never show a value from one source and a provenance mark from
///         another. The writing half is the only place <see cref="AreaConfig.Levels"/> is mutated, so the rule
///         that an empty row is dropped rather than stored lives in exactly one place.
///     </para>
///     <para>
///         <b>Matched by name, case-insensitively.</b> The schema says "by name" and leaves the comparison open;
///         this follows <c>ModeMonitor</c>, which is the engine's existing precedent for comparing a period name.
///         Ordinal would make a room whose period was recased into "Kveld" show an orphan the engine would still
///         apply, and a surface that disagrees with the engine about which rows are live is worse than one that
///         is slightly more forgiving than it needs to be.
///     </para>
///     <para>
///         Pure, because this repo has no Razor render harness and does not gain one: the inheritance, the
///         orphan detection and the pruning are asserted here rather than screenshotted.
///     </para>
/// </remarks>
public static class RoomLevels
{
	/// <summary>How period names are matched, here and everywhere this class is read.</summary>
	private const StringComparison ByName = StringComparison.OrdinalIgnoreCase;

	/// <summary>
	///     One row per period in the schedule, in the schedule's own order, showing what this room will run.
	/// </summary>
	/// <remarks>
	///     A period the room says nothing about still gets a row: the table's job is to show the room's levels,
	///     and a room that follows the house has levels — the house's. Drawing only the overridden periods would
	///     hide three quarters of the answer and make "add an override" a hunt.
	/// </remarks>
	/// <param name="periods">The document's period list.</param>
	/// <param name="room">The room, or <c>null</c> to read the schedule alone.</param>
	/// <exception cref="ArgumentNullException"><paramref name="periods"/> is <c>null</c>.</exception>
	public static IReadOnlyList<RoomLevelRow> Rows(IReadOnlyList<TimePeriodConfig> periods, AreaConfig? room)
	{
		ArgumentNullException.ThrowIfNull(periods);

		List<RoomLevelRow> rows = [];

		foreach (TimePeriodConfig period in periods)
		{
			RoomLevelOverride? own = Stated(room, period.Name);

			rows.Add(new RoomLevelRow(
				period.Name,
				own?.BrightnessPct ?? period.BrightnessPct,
				own?.BrightnessPct is not null ? LevelSource.Room : LevelSource.Schedule,
				own?.ColorTempKelvin ?? period.ColorTempKelvin,
				own?.ColorTempKelvin is not null ? LevelSource.Room : LevelSource.Schedule));
		}

		return rows;
	}

	/// <summary>
	///     The rows this room states for periods the schedule no longer has.
	/// </summary>
	/// <remarks>
	///     A row saying nothing is not reported, whatever it is keyed to: it holds no levels, so there is nothing
	///     for a reader to decide about and nothing for a remove button to rescue. <see cref="Prune"/> drops those
	///     on the next write anyway.
	/// </remarks>
	/// <param name="periods">The document's period list.</param>
	/// <param name="room">The room, or <c>null</c>, which has no overrides and therefore no orphans.</param>
	/// <exception cref="ArgumentNullException"><paramref name="periods"/> is <c>null</c>.</exception>
	public static IReadOnlyList<RoomLevelOrphan> Orphans(IReadOnlyList<TimePeriodConfig> periods, AreaConfig? room)
	{
		ArgumentNullException.ThrowIfNull(periods);

		if (room is null)
			return [];

		return
		[
			.. room.Levels
				.Where(level => !level.IsEmpty)
				.Where(level => !periods.Any(period => string.Equals(period.Name, level.Period, ByName)))
				.Select(level => new RoomLevelOrphan(level.Period, level.BrightnessPct, level.ColorTempKelvin))
		];
	}

	/// <summary>
	///     How many of the periods on screen this room states for itself — the count beside the card's title.
	/// </summary>
	/// <remarks>
	///     Counted against the schedule rather than off the stored list, so it can never disagree with the marks
	///     in the table below it. Counting the rows would include a row kept for a period that no longer exists,
	///     and the card would say "1 of 4 periods are this room's own" over four rows with no mark on any of them.
	///     Orphans are counted by <see cref="Orphans"/> and said separately, because they are a different fact.
	/// </remarks>
	/// <param name="periods">The document's period list.</param>
	/// <param name="room">The room, or <c>null</c>.</param>
	/// <exception cref="ArgumentNullException"><paramref name="periods"/> is <c>null</c>.</exception>
	public static int OwnCount(IReadOnlyList<TimePeriodConfig> periods, AreaConfig? room) =>
		Rows(periods, room).Count(row => row.IsOwn);

	/// <summary>
	///     Sets this room's brightness for a period, or sends it back to the schedule with <c>null</c>.
	/// </summary>
	/// <remarks>
	///     Clearing writes <c>null</c> rather than the schedule's current number, for the reason every other
	///     revert on this page does: the room then keeps following the schedule the next time the schedule
	///     changes. A row left saying nothing by the clear is dropped, so the document never carries one.
	/// </remarks>
	/// <param name="room">The room to change, in place.</param>
	/// <param name="period">The period's name.</param>
	/// <param name="brightnessPct">The brightness, or <c>null</c> to follow the schedule.</param>
	/// <exception cref="ArgumentNullException"><paramref name="room"/> is <c>null</c>.</exception>
	public static void SetBrightness(AreaConfig room, string period, double? brightnessPct)
	{
		ArgumentNullException.ThrowIfNull(room);

		Edit(room, period, level => level.BrightnessPct = brightnessPct);
	}

	/// <summary>
	///     Sets this room's colour temperature for a period, or sends it back to the schedule with <c>null</c>.
	/// </summary>
	/// <inheritdoc cref="SetBrightness"/>
	/// <param name="room">The room to change, in place.</param>
	/// <param name="period">The period's name.</param>
	/// <param name="kelvin">The white, or <c>null</c> to follow the schedule.</param>
	/// <exception cref="ArgumentNullException"><paramref name="room"/> is <c>null</c>.</exception>
	public static void SetColorTemp(AreaConfig room, string period, int? kelvin)
	{
		ArgumentNullException.ThrowIfNull(room);

		Edit(room, period, level => level.ColorTempKelvin = kelvin);
	}

	/// <summary>
	///     Drops everything this room says about a period — the road back from an orphan, and from a row whose
	///     two values a reader would otherwise have to clear one at a time.
	/// </summary>
	/// <param name="room">The room to change, in place.</param>
	/// <param name="period">The period's name.</param>
	/// <returns>Whether anything was removed.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="room"/> is <c>null</c>.</exception>
	public static bool Remove(AreaConfig room, string period)
	{
		ArgumentNullException.ThrowIfNull(room);

		return room.Levels.RemoveAll(level => string.Equals(level.Period, period, ByName)) > 0;
	}

	/// <summary>
	///     The row this room's levels actually come from for a period — the first that states something — or
	///     <c>null</c> when the room states nothing and follows the schedule.
	/// </summary>
	/// <remarks>
	///     <b>Empty rows are skipped, because <c>CircadianCalculator.LevelsOf</c> skips them.</b> This is the read
	///     path, and a read path that picks a different row from the engine's tells the owner their room is doing
	///     something it is not. A hand-edited file with a cleared <c>Kveld</c> row above a real one at 8 % runs at
	///     8 %; taking the first row regardless would show the schedule's level, mark it "the schedule's", and
	///     offer no way to revert an override the page had decided did not exist. Only reachable on a hand-edited
	///     file — the app's own save normalises empty rows away — which is exactly the file nothing else explains.
	///     The same asymmetry, in the other direction, was a live defect in <c>ConfigValidator</c>.
	/// </remarks>
	private static RoomLevelOverride? Stated(AreaConfig? room, string period) =>
		room?.Levels.FirstOrDefault(level => !level.IsEmpty && string.Equals(level.Period, period, ByName));

	/// <summary>
	///     This room's row for a period whatever it holds, or <c>null</c> when it has none.
	/// </summary>
	/// <remarks>
	///     The write path's lookup, and deliberately <i>not</i> <see cref="Stated"/>: an edit must reuse a cleared
	///     row that is already there rather than add a second one beside it, which would leave the file saying two
	///     things about one period. Reading is the other question — see <see cref="Stated"/>.
	/// </remarks>
	private static RoomLevelOverride? Find(AreaConfig? room, string period) =>
		room?.Levels.FirstOrDefault(level => string.Equals(level.Period, period, ByName));

	/// <summary>Applies one change to the room's row for a period, creating it and dropping it as needed.</summary>
	private static void Edit(AreaConfig room, string period, Action<RoomLevelOverride> change)
	{
		RoomLevelOverride? level = Find(room, period);

		if (level is null)
		{
			level = new RoomLevelOverride { Period = period };
			room.Levels.Add(level);
		}

		change(level);
		Prune(room);
	}

	/// <summary>
	///     Drops every row that says nothing.
	/// </summary>
	/// <remarks>
	///     Run after each edit rather than at save time, so what the page holds and what would be written are the
	///     same object graph: a row left behind by a clear would be counted as an override by everything that
	///     reads <see cref="AreaConfig.Levels"/>, and the room would report itself as disagreeing with a schedule
	///     it now follows exactly.
	/// </remarks>
	private static void Prune(AreaConfig room) => room.Levels.RemoveAll(level => level.IsEmpty);
}
