using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     The decisions the House tab makes about how the house is written down, in one testable place.
/// </summary>
/// <remarks>
///     Nothing here reads or writes the document. The tab mutates its own copy and hands it to the save pipeline;
///     these functions only decide what to say about it.
/// </remarks>
public static class HouseView
{
	/// <summary>How many straying rooms are named before the line falls back to counting them.</summary>
	private const int NamedRooms = 3;

	/// <summary>Where a room's own page is, or <c>null</c> when it has no area id and therefore no route.</summary>
	/// <remarks><c>null</c> makes Blazor drop the attribute, leaving no link and no tab stop.</remarks>
	public static string? RoomHref(string? areaId) =>
		areaId is { Length: > 0 } id ? $"room/{Uri.EscapeDataString(id)}" : null;

	/// <summary>One line on how far a room strays from the house, for a list row nobody has opened.</summary>
	public static string RoomSummary(AreaConfig area)
	{
		ArgumentNullException.ThrowIfNull(area);

		// Through AreaSetupService.OverrideCount, never a list of its own: a hand-written twin missed the five
		// daylight-brightness settings, and a room tuned only through them reported "all automatic".
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

	/// <summary>
	///     What a room lists instead of discovering: "2 lights, 1 motion sensor, lux sensor". Empty when it
	///     discovers everything.
	/// </summary>
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
	/// <param name="registry">The area registry, for the names it lists, or <c>null</c> when there is none to ask.</param>
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

	/// <summary>The line under the room list about the rooms that are switched off, or <c>null</c> when none is.</summary>
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

	/// <summary>
	///     The Home Assistant areas that have no room in the document yet, which is what Add a room offers. An area
	///     a room already claims is left out, so two rooms cannot fight over the same lights.
	/// </summary>
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

	/// <summary>What a room is called on this page.</summary>
	/// <remarks>
	///     The order is <see cref="AreaNaming"/>'s, so the rows, the board's lanes and the room page's heading name
	///     one room one way. Only the final "New room" is this page's own.
	/// </remarks>
	/// <param name="registry">The area registry, or <c>null</c> when Home Assistant has not answered.</param>
	public static string DisplayName(AreaConfig area, IAreaRegistry? registry)
	{
		ArgumentNullException.ThrowIfNull(area);

		return AreaNaming.Resolve(area, registry) ?? "New room";
	}

	/// <summary>A handful of names written as English writes a list, falling back to a count once it would be a wall.</summary>
	/// <param name="names">The names, in the order they should be read.</param>
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
