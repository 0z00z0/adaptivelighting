using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>One line of a room's light list: something the engine commands, and what is underneath it.</summary>
/// <param name="Leaves">The lights beneath this entry, all the way down, in entity-id order. One for a lamp.</param>
/// <param name="OwnLights">How many of those lights state levels of their own. What a group's line counts.</param>
/// <param name="OwnPeriods">How many periods this one light states for itself. What a light's line counts.</param>
// Two counts and not one: a group's line is about lights and a light's line is about periods, and a single
// number would read as whichever the last caller meant.
public sealed record LightEntry(string EntityId, IReadOnlyList<string> Leaves, int OwnLights, int OwnPeriods)
{
	/// <summary>Whether this entry reaches lights through membership rather than being one itself.</summary>
	public bool IsGroup => Leaves.Count > 1 || !Leaves.Contains(EntityId, StringComparer.OrdinalIgnoreCase);

	/// <summary>What the line says about itself, beside its name.</summary>
	public string Summary => IsGroup
		? OwnLights == 0
			? $"a group of {Leaves.Count}, all follow the room"
			: $"a group of {Leaves.Count}, {OwnLights} set differently"
		: OwnPeriods == 0
			? "follows the room"
			: $"own levels for {OwnPeriods} {(OwnPeriods == 1 ? "period" : "periods")}";
}

/// <summary>One period as a single light will run it: the level in force, and whether the light or the room decided it.</summary>
public sealed record LightLevelRow(
	string PeriodId,
	string Name,
	double BrightnessPct,
	bool BrightnessIsOwn,
	int ColorTempKelvin,
	bool ColourIsOwn,
	bool FollowsDaylightCurve,
	bool CurveIsOwn)
{
	/// <summary>Whether this light states anything at all for this period, which is what draws the row's mark.</summary>
	// The curve counts only where this light claimed it: a light following the room's curve states nothing, and
	// counting that would mark every row of every light in a curve-following room as its own.
	public bool IsOwn => BrightnessIsOwn || ColourIsOwn || CurveIsOwn;
}

/// <summary>A light this room states levels for and no longer commands.</summary>
public sealed record LightLevelOrphan(string EntityId, int PeriodCount);

/// <summary>What single lights inside a room run instead of the room, projected for the page that shows and edits it.</summary>
/// <remarks>
///     The writing half is the only place <see cref="AreaConfig.LightLevels"/> is mutated, so the rule that an
///     empty row is dropped and never stored lives in one place, as it does for a room's own levels.
/// </remarks>
public static class LightLevels
{
	/// <summary>Whether this room is worth adjusting light by light at all.</summary>
	// A one-lamp room has nothing to distinguish, so it never sees the question.
	public static bool Applies(ResolvedArea? resolved) => LeavesOf(resolved).Count > 1;

	/// <summary>Whether any light in this room states levels of its own, which is what ticks the box on load.</summary>
	// Nothing is stored for the box itself: the rows are the fact, and a stored flag could disagree with them.
	public static bool InUse(AreaConfig? room) =>
		room?.LightLevels?.Any(light => !light.IsEmpty) == true;

	/// <summary>One line per entry the engine commands, in the engine's own order.</summary>
	// The engine's settled list and no second approximation of it, so the page and the engine agree on what a
	// group is and what is inside one.
	public static IReadOnlyList<LightEntry> Entries(
		IReadOnlyList<TimePeriodConfig> periods,
		ResolvedArea? resolved,
		AreaConfig? room)
	{
		ArgumentNullException.ThrowIfNull(periods);

		if (resolved is null)
			return [];

		List<LightEntry> entries = [];

		foreach (string entry in resolved.Lights)
		{
			IReadOnlyList<string> leaves = LeavesOfEntry(resolved, entry);

			entries.Add(new LightEntry(
				entry,
				leaves,
				leaves.Count(leaf => Stated(room, leaf) is { Count: > 0 }),
				OwnPeriodsOf(periods, room, entry)));
		}

		return entries;
	}

	/// <summary>One light on its own, for the flat list inside a group.</summary>
	public static LightEntry Line(IReadOnlyList<TimePeriodConfig> periods, AreaConfig? room, string entityId)
	{
		ArgumentNullException.ThrowIfNull(periods);

		return new LightEntry(entityId, [entityId], 0, OwnPeriodsOf(periods, room, entityId));
	}

	// Counted off Rows, so a line cannot disagree with the marks in the table it opens.
	private static int OwnPeriodsOf(IReadOnlyList<TimePeriodConfig> periods, AreaConfig? room, string entityId) =>
		Rows(periods, room, entityId).Count(row => row.IsOwn);

	/// <summary>One row per period in the schedule, showing what one light will run and where each value came from.</summary>
	public static IReadOnlyList<LightLevelRow> Rows(
		IReadOnlyList<TimePeriodConfig> periods,
		AreaConfig? room,
		string entityId)
	{
		ArgumentNullException.ThrowIfNull(periods);

		IReadOnlyList<RoomLevelRow> theRoom = RoomLevels.Rows(periods, room);
		IReadOnlyList<RoomLevelOverride> own = Stated(room, entityId);

		List<LightLevelRow> rows = [];

		foreach (RoomLevelRow inherited in theRoom)
		{
			RoomLevelOverride? stated = own.FirstOrDefault(level => !level.IsEmpty && level.PeriodId.SameName(inherited.PeriodId));

			// The engine's own rule, asked of the engine's own function, so the page cannot answer differently.
			RoomLevelOverride merged = LightLevelMerge.Merge(
				new RoomLevelOverride
				{
					PeriodId = inherited.PeriodId,
					BrightnessPct = inherited.BrightnessPct,
					ColorTempKelvin = inherited.ColorTempKelvin,
					FollowDaylightCurve = inherited.FollowsDaylightCurve ? true : null
				},
				stated);

			rows.Add(new LightLevelRow(
				inherited.PeriodId,
				inherited.Name,
				merged.BrightnessPct ?? inherited.BrightnessPct,
				stated?.BrightnessPct is not null,
				merged.ColorTempKelvin ?? inherited.ColorTempKelvin,
				stated?.ColorTempKelvin is not null,
				merged.FollowDaylightCurve == true,
				stated?.FollowDaylightCurve == true));
		}

		return rows;
	}

	/// <summary>The lights this room states levels for and no longer commands.</summary>
	public static IReadOnlyList<LightLevelOrphan> Orphans(ResolvedArea? resolved, AreaConfig? room)
	{
		if (room?.LightLevels is not { Count: > 0 } lights)
			return [];

		// A room that cannot be resolved knows nothing about what it commands, so nothing is called an orphan.
		if (resolved is null)
			return [];

		HashSet<string> commanded = new(LeavesOf(resolved), StringComparer.OrdinalIgnoreCase);

		return
		[
			.. lights
				.Where(light => !light.IsEmpty)
				.Where(light => !commanded.Contains(light.EntityId))
				.Select(light => new LightLevelOrphan(light.EntityId, light.Levels.Count(level => !level.IsEmpty)))
		];
	}

	/// <summary>Sets one light's brightness for a period, or sends it back to the room with <c>null</c>.</summary>
	public static void SetBrightness(AreaConfig room, string entityId, string periodId, double? brightnessPct)
	{
		ArgumentNullException.ThrowIfNull(room);

		Edit(room, entityId, periodId, level => level.BrightnessPct = brightnessPct);
	}

	/// <summary>Sets one light's colour temperature for a period, or sends it back to the room with <c>null</c>.</summary>
	public static void SetColorTemp(AreaConfig room, string entityId, string periodId, int? kelvin)
	{
		ArgumentNullException.ThrowIfNull(room);

		Edit(room, entityId, periodId, level => level.ColorTempKelvin = kelvin);
	}

	/// <summary>Puts one light's period on the daylight curve or back on a number of its own.</summary>
	/// <remarks>
	///     Coming off a curve the room itself follows writes <paramref name="currentBrightnessPct"/> as this
	///     light's own, because a row saying nothing about brightness goes on inheriting the room's curve. That is
	///     the only way one lamp opts out of a curve the rest of the room keeps.
	/// </remarks>
	public static void SetFollowsDaylightCurve(
		AreaConfig room,
		string entityId,
		string periodId,
		double currentBrightnessPct,
		bool roomFollowsCurve,
		bool follow)
	{
		ArgumentNullException.ThrowIfNull(room);

		Edit(room, entityId, periodId, level =>
		{
			level.FollowDaylightCurve = follow ? true : null;

			if (!follow && roomFollowsCurve && level.BrightnessPct is null)
				level.BrightnessPct = currentBrightnessPct;
		});
	}

	/// <summary>Drops everything one light says, which is the road back from an orphan.</summary>
	/// <returns>Whether anything was removed.</returns>
	public static bool Remove(AreaConfig room, string entityId)
	{
		ArgumentNullException.ThrowIfNull(room);

		if (room.LightLevels is not { } lights)
			return false;

		bool removed = lights.RemoveAll(light => light.EntityId.SameName(entityId)) > 0;
		Prune(room);

		return removed;
	}

	/// <summary>Forgets every light's own levels in this room, which is what unticking the box does.</summary>
	public static void Forget(AreaConfig room)
	{
		ArgumentNullException.ThrowIfNull(room);

		room.LightLevels = null;
	}

	/// <summary>How many lights in this room state levels of their own, for the wording on the box.</summary>
	public static int StatingCount(AreaConfig? room) =>
		room?.LightLevels?.Count(light => !light.IsEmpty) ?? 0;

	/// <summary>Every light one entry reaches, all the way down; the entry itself when it groups nothing.</summary>
	private static IReadOnlyList<string> LeavesOfEntry(ResolvedArea resolved, string entry) =>
		resolved.LeavesOfEntry.TryGetValue(entry, out IReadOnlySet<string>? leaves) && leaves.Count > 0
			? [.. leaves.Order(StringComparer.Ordinal)]
			: [entry];

	// Flattened once: a group inside a group is not a fold inside a fold, so its lights appear in the flat list.
	private static IReadOnlyList<string> LeavesOf(ResolvedArea? resolved)
	{
		if (resolved is null)
			return [];

		HashSet<string> leaves = new(StringComparer.OrdinalIgnoreCase);

		foreach (string entry in resolved.Lights)
			leaves.UnionWith(LeavesOfEntry(resolved, entry));

		return [.. leaves];
	}

	// Read path. First entry wins on a duplicate light, matching the engine and the validator.
	private static IReadOnlyList<RoomLevelOverride> Stated(AreaConfig? room, string entityId) =>
		room?.LightLevels?.FirstOrDefault(light => !light.IsEmpty && light.EntityId.SameName(entityId))?.Levels ?? [];

	/// <summary>Applies one change to one light's row for a period, creating and dropping as needed.</summary>
	private static void Edit(AreaConfig room, string entityId, string periodId, Action<RoomLevelOverride> change)
	{
		room.LightLevels ??= [];

		LightLevelOverride? light = room.LightLevels.FirstOrDefault(entry => entry.EntityId.SameName(entityId));

		if (light is null)
		{
			light = new LightLevelOverride { EntityId = entityId };
			room.LightLevels.Add(light);
		}

		RoomLevelOverride? level = light.Levels.FirstOrDefault(row => row.PeriodId.SameName(periodId));

		if (level is null)
		{
			level = new RoomLevelOverride { PeriodId = periodId };
			light.Levels.Add(level);
		}

		change(level);
		Prune(room);
	}

	// After each edit, never at save time: a row left behind by a clear counts as a level to everything that
	// reads the key, and a light left holding no row would keep the box ticked over nothing.
	private static void Prune(AreaConfig room)
	{
		if (room.LightLevels is not { } lights)
			return;

		foreach (LightLevelOverride light in lights)
			light.Levels.RemoveAll(level => level.IsEmpty);

		lights.RemoveAll(light => light.Levels.Count == 0);

		room.LightLevels = lights.Count > 0 ? lights : null;
	}
}
