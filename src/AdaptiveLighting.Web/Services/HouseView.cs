using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     The decisions the House tab makes about how the house is written down, in one testable place.
/// </summary>
/// <remarks>
///     <para>
///         The House tab is the page that talks about rooms in the plural — how many follow the defaults, how
///         many are switched off, what one room's row says about itself without being opened. Every one of those
///         is a counted sentence with plurals and a name list in it, and every one of them is wrong in a way
///         nobody notices until a house has seventeen rooms. So they are pure functions with tests rather than
///         string interpolation inside markup, exactly as <see cref="AreaView"/> and <see cref="AreaSentences"/>
///         already are.
///     </para>
///     <para>
///         Nothing here reads or writes the document. The tab mutates its own copy and hands it to the one save
///         pipeline; these functions only decide what to say about it.
///     </para>
/// </remarks>
public static class HouseView
{
	/// <summary>How many straying rooms are named before the line falls back to counting them.</summary>
	/// <remarks>
	///     Two named plus a count is the design's own wording ("Stue, Kontor and 6 others"). Three are named in
	///     full instead of "and 1 other", which would spend a clause to avoid printing a word.
	/// </remarks>
	private const int NamedRooms = 3;

	/// <summary>
	///     Where a room's own page is, or <c>null</c> when it has no address.
	/// </summary>
	/// <remarks>
	///     A room configured with explicit entities and no area id has no route, and <c>null</c> makes Blazor drop
	///     the attribute — leaving something that is neither a link nor a tab stop rather than one pointing at
	///     nowhere. The id is escaped because it reaches the URL.
	/// </remarks>
	/// <param name="areaId">The Home Assistant area id.</param>
	public static string? RoomHref(string? areaId) =>
		areaId is { Length: > 0 } id ? $"room/{Uri.EscapeDataString(id)}" : null;

	/// <summary>
	///     One line on how far a room strays from the house — what a list row answers without being opened.
	/// </summary>
	/// <remarks>
	///     Counted through <see cref="AreaSetupService.OverrideCount"/> rather than by a list of its own. The
	///     shipped editor kept a hand-written twin of that list, the five daylight-brightness settings were added
	///     to one copy and not the other, and a room tuned solely through them reported itself as "all automatic".
	/// </remarks>
	/// <param name="area">The room.</param>
	/// <exception cref="ArgumentNullException"><paramref name="area"/> is <c>null</c>.</exception>
	public static string RoomSummary(AreaConfig area)
	{
		ArgumentNullException.ThrowIfNull(area);

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
	/// <param name="area">The room.</param>
	/// <exception cref="ArgumentNullException"><paramref name="area"/> is <c>null</c>.</exception>
	public static string PinnedSummary(AreaConfig area)
	{
		ArgumentNullException.ThrowIfNull(area);

		List<string> parts = new(4);

		if (area.Lights is { Count: > 0 } lights)
			parts.Add(Count(lights.Count, "light"));

		if (area.MotionSensors is { Count: > 0 } motion)
			parts.Add(Count(motion.Count, "motion sensor"));

		if (area.LuxSensor is { Length: > 0 })
			parts.Add("lux sensor");

		if (area.IgnoreWhenOn is { Count: > 0 } blockers)
			parts.Add(Count(blockers.Count, "blocker"));

		return string.Join(", ", parts);
	}

	/// <summary>
	///     The line under the house's own sentences: how many rooms take them as written, and which do not.
	/// </summary>
	/// <remarks>
	///     The House tab's answer to "does changing this actually change anything?". A house where every room has
	///     been tuned individually is a house where editing a default moves almost nothing, and somebody about to
	///     spend a minute on these sentences deserves to know that before they start rather than after.
	/// </remarks>
	/// <param name="areas">The document's rooms.</param>
	/// <param name="registry">The area registry, for the names it lists, or <c>null</c> when there is none to ask.</param>
	/// <exception cref="ArgumentNullException"><paramref name="areas"/> is <c>null</c>.</exception>
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

	/// <summary>
	///     The line under the room list about the rooms that are switched off, or <c>null</c> when none is.
	/// </summary>
	/// <remarks>
	///     <c>null</c> for "nothing is off" keeps the no-line-when-nothing-is-hidden rule beside the wording it
	///     governs, rather than in a condition in the markup that could drift from it. This is where the board's
	///     footer thread lands: somebody who switched the kitchen off by accident arrives here, and the rooms are
	///     already in the list above with their own switches.
	/// </remarks>
	/// <param name="areas">The document's rooms.</param>
	/// <param name="defaults">The document's all-rooms settings, for a room that states nothing.</param>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
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
	///     The Home Assistant areas that have no room in the document yet — what <i>Add a room</i> offers.
	/// </summary>
	/// <remarks>
	///     Offering an area a room already claims is how a house acquires two rooms that fight over the same
	///     lights, so the picker simply does not list one. An area id is HA's own slug, matched ordinally.
	/// </remarks>
	/// <param name="areas">Every area Home Assistant knows.</param>
	/// <param name="configured">The document's rooms.</param>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
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

	/// <summary>
	///     What a room is called on this page: its own name, else Home Assistant's name for its area, else the
	///     area id, else that it is new.
	/// </summary>
	/// <remarks>
	///     The order is <see cref="AreaNaming"/>'s, not a second copy of it — the room rows, the board's lanes and
	///     the room page's heading have to name one room one way. Only the last step is this page's own: a row that
	///     names nothing at all is a room somebody has just added, and "New room" says that where the engine's
	///     "(unnamed area)" would read as a fault.
	/// </remarks>
	/// <param name="area">The room.</param>
	/// <param name="registry">The area registry, or <c>null</c> when Home Assistant has not answered.</param>
	/// <exception cref="ArgumentNullException"><paramref name="area"/> is <c>null</c>.</exception>
	public static string DisplayName(AreaConfig area, IAreaRegistry? registry)
	{
		ArgumentNullException.ThrowIfNull(area);

		return AreaNaming.Resolve(area, registry) ?? "New room";
	}

	/// <summary>
	///     A handful of names written as English writes a list, falling back to a count once it would be a wall.
	/// </summary>
	/// <param name="names">The names, in the order they should be read.</param>
	/// <exception cref="ArgumentNullException"><paramref name="names"/> is <c>null</c>.</exception>
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
