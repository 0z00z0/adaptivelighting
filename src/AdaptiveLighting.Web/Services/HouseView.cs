using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>The decisions the House tab makes about how the house is written down, in one testable place.</summary>
/// <remarks>Nothing here reads or writes the document; the tab mutates its own copy and hands it to the save pipeline.</remarks>
public static class HouseView
{
	/// <summary>How many straying rooms are named before the line falls back to counting them.</summary>
	private const int NamedRooms = 3;

	// null makes Blazor drop the attribute, leaving no link and no tab stop.
	public static string? RoomHref(string? areaId) =>
		areaId is { Length: > 0 } id ? $"room/{Uri.EscapeDataString(id)}" : null;

	public static string RoomSummary(AreaConfig area)
	{
		ArgumentNullException.ThrowIfNull(area);

		// Through AreaSetupService.OverrideCount, never a list of its own: a local list misses settings, and a room
		// tuned only through those reports "all automatic".
		int changed = AreaSetupService.OverrideCount(area);
		string pinned = PinnedSummary(area);

		return (changed, pinned.Length) switch
		{
			(0, 0) => "all automatic",
			(0, _) => $"{pinned} picked by hand",
			(_, 0) => $"{changed} of {AreaView.OverridableSettingCount} changed",
			_ => $"{pinned} picked by hand · {changed} of {AreaView.OverridableSettingCount} changed"
		};
	}

	/// <summary>What a room lists instead of discovering, empty when it discovers everything.</summary>
	public static string PinnedSummary(AreaConfig area)
	{
		ArgumentNullException.ThrowIfNull(area);

		List<string> parts = new(4);

		if (area.Lights is { Count: > 0 } lights)
			parts.Add(Count(lights.Count, "light"));

		if (area.MotionSensors is { Count: > 0 } motion)
			parts.Add(Count(motion.Count, "motion sensor"));

		if (area.LuxSensor is { Length: > 0 })
			parts.Add("light-level sensor");

		if (area.IgnoreWhenOn is { Count: > 0 } blockers)
			parts.Add(Count(blockers.Count, "blocker"));

		return string.Join(", ", parts);
	}

	/// <summary>The line under the house's own sentences: how many rooms take them as written, and which do not.</summary>
	public static string StrayLine(IEnumerable<AreaConfig> areas, IAreaRegistry? registry)
	{
		ArgumentNullException.ThrowIfNull(areas);

		List<AreaConfig> rooms = [.. areas];

		if (rooms.Count == 0)
			return "no rooms yet — these apply to every room you add";

		List<string> straying =
		[
			.. rooms.Where(room => RoomSettings.OwnCount(room) > 0).Select(room => DisplayName(room, registry))
		];

		if (straying.Count == 0)
			return rooms.Count == 1 ? "the one room follows these exactly" : $"all {rooms.Count} rooms follow these exactly";

		int following = rooms.Count - straying.Count;

		return following == 0
			? $"every room carries values of its own: {NameList(straying)}"
			: $"{Count(following, "room")} follow{(following == 1 ? "s" : "")} these exactly; {NameList(straying)} carr{(straying.Count == 1 ? "ies" : "y")} their own values";
	}

	public static string? SwitchedOffLine(IEnumerable<AreaConfig> areas, AreaSettings defaults)
	{
		ArgumentNullException.ThrowIfNull(areas);
		ArgumentNullException.ThrowIfNull(defaults);

		int off = areas.Count(area => !AreaView.IsEnabled(area, defaults));

		return off switch
		{
			<= 0 => null,
			1 => "1 room is switched off — it never changes by itself, and its switch is in the list above.",
			_ => $"{off} rooms are switched off — they never change by themselves, and their switches are in the list above."
		};
	}

	/// <summary>The Home Assistant areas no room has claimed, so two rooms cannot fight over the same lights.</summary>
	public static IReadOnlyList<AreaOption> Unconfigured(IEnumerable<AreaOption> areas, IEnumerable<AreaConfig> configured)
	{
		ArgumentNullException.ThrowIfNull(areas);
		ArgumentNullException.ThrowIfNull(configured);

		HashSet<string> taken =
		[
			.. configured.Select(room => room.AreaId).OfType<string>().Where(areaId => areaId.Length > 0)
		];

		return [.. areas.Where(area => !taken.Contains(area.Id))];
	}

	// AreaNaming's order, so the rows, the board's lanes and the room page's heading name one room one way.
	public static string DisplayName(AreaConfig area, IAreaRegistry? registry)
	{
		ArgumentNullException.ThrowIfNull(area);

		return AreaNaming.Resolve(area, registry) ?? "New room";
	}

	/// <summary>A handful of names written as English writes a list, falling back to a count once it would be a wall.</summary>
	public static string NameList(IReadOnlyList<string> names)
	{
		ArgumentNullException.ThrowIfNull(names);

		if (names.Count == 0)
			return string.Empty;

		if (names.Count == 1)
			return names[0];

		if (names.Count <= NamedRooms)
			return $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

		int rest = names.Count - 2;

		return $"{names[0]}, {names[1]} and {rest} others";
	}

	private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
