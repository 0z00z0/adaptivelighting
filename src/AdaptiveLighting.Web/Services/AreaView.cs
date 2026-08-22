using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>The dashboard's footer line about switched-off rooms, split where its link goes.</summary>
/// <param name="Lead">The sentence up to the link, ending in the dash the link follows.</param>
/// <param name="LinkText">The link's own words, which have to read as an instruction on their own.</param>
public sealed record HiddenRoomsNote(string Lead, string LinkText);

/// <summary>The decisions the two area screens make about how a room is shown, in one testable place.</summary>
/// <remarks>The dashboard's cards and the settings list share this colour mapping, so one house is painted once.</remarks>
public static class AreaView
{
	// Must equal RoomSettings.Keys.Count; a test holds the two together. Enabled is not in the list, since the
	// header toggle owns it.
	public const int OverridableSettingCount = 22;

	/// <summary>What a floorless group is called, once, so both screens head it the same way.</summary>
	public const string FloorlessTitle = "Other rooms";

	/// <summary>The colour family a live state belongs to: the engine acting, a person having acted, or neither.</summary>
	/// <returns><c>machine</c>, <c>human</c> or <c>idle</c>, the suffix of a <c>family-*</c> class.</returns>
	public static string Family(AreaState state) => state switch
	{
		AreaState.AutoActive or AreaState.AutoVacant or AreaState.PreOff => "machine",
		AreaState.OverriddenOn or AreaState.SuppressedOff => "human",
		_ => "idle"
	};

	/// <summary>
	///     The left-edge class for a room in the settings list. A switched-off room is flat grey whatever the
	///     engine last said, and a room with no snapshot is idle, never unpainted.
	/// </summary>
	public static string EdgeClass(bool enabled, AreaState? state) =>
		!enabled ? "family-off"
		: state is { } live ? $"family-{Family(live)}"
		: "family-idle";

	/// <summary>Whether the engine may command this room, following the document's inheritance.</summary>
	public static bool IsEnabled(AreaConfig area, AreaSettings defaults)
	{
		ArgumentNullException.ThrowIfNull(area);
		ArgumentNullException.ThrowIfNull(defaults);

		return area.Enabled ?? defaults.Enabled;
	}

	/// <summary>Whether every room in a group is switched on, which flips the floor's bulk action.</summary>
	/// <remarks>An empty group counts as not-all-on, so the button never offers to switch off nothing.</remarks>
	public static bool AllEnabled(IEnumerable<AreaConfig> areas, AreaSettings defaults)
	{
		ArgumentNullException.ThrowIfNull(areas);
		ArgumentNullException.ThrowIfNull(defaults);

		List<AreaConfig> rooms = [.. areas];

		return rooms.Count > 0 && rooms.TrueForAll(area => IsEnabled(area, defaults));
	}

	/// <summary>Switches a whole floor on or off, in the in-memory document. Nothing here writes to disk.</summary>
	/// <returns>How many rooms actually changed value, so a caller can skip a no-op.</returns>
	public static int SwitchAll(IEnumerable<AreaConfig> areas, bool on)
	{
		ArgumentNullException.ThrowIfNull(areas);

		int changed = 0;

		// Explicit true/false on every room, never null: a decision made with a button is one the file should state.
		foreach (AreaConfig area in areas)
		{
			if (area.Enabled != on)
				changed++;

			area.Enabled = on;
		}

		return changed;
	}

	/// <summary>Whether a floor group earns a header. A house with no floors collapses to one unheaded group.</summary>
	/// <param name="floor">The group's floor, or <c>null</c> for the trailing floorless group.</param>
	public static bool ShowsHeader(int groupCount, AreaFloor? floor) => groupCount > 1 || floor is not null;

	public static string FloorTitle(AreaFloor? floor) => floor?.Name ?? FloorlessTitle;

	// ===================== the dashboard =====================

	/// <summary>The live reports the dashboard gives a card to: those from rooms switched on in the document.</summary>
	/// <remarks>
	///     The engine keeps observing and publishing switched-off rooms, so this is a question about the document,
	///     not about the report. Matched by area id first and display name second, as the snapshot cache and the
	///     settings list match.
	/// </remarks>
	/// <param name="snapshots">Everything the cache has heard, in the order it should render.</param>
	/// <param name="rooms">The document's rooms, as <c>ModeService.GetRooms</c> projects them.</param>
	public static IReadOnlyList<AreaSnapshot> VisibleCards(IEnumerable<AreaSnapshot> snapshots, IEnumerable<RoomView> rooms)
	{
		ArgumentNullException.ThrowIfNull(snapshots);
		ArgumentNullException.ThrowIfNull(rooms);

		Dictionary<string, bool> byId = new(StringComparer.Ordinal);
		Dictionary<string, bool> byName = new(StringComparer.OrdinalIgnoreCase);

		foreach (RoomView room in rooms)
		{
			if (room.AreaId is { Length: > 0 } areaId)
				byId[areaId] = room.IsEnabled;

			byName[room.Name] = room.IsEnabled;
		}

		return [.. snapshots.Where(snapshot => IsShown(snapshot, byId, byName))];
	}

	/// <summary>How many rooms are switched off, for the footer under the grid.</summary>
	/// <remarks>
	///     Counted off the document, not off the cards that did not render: a room switched off before the engine
	///     ever reported it has no snapshot to be missing.
	/// </remarks>
	public static int SwitchedOffCount(IEnumerable<RoomView> rooms)
	{
		ArgumentNullException.ThrowIfNull(rooms);

		return rooms.Count(room => !room.IsEnabled);
	}

	/// <summary>The footer line for the hidden rooms, or <c>null</c> when nothing is hidden.</summary>
	public static HiddenRoomsNote? HiddenNote(int switchedOff) => switchedOff switch
	{
		<= 0 => null,
		1 => new HiddenRoomsNote("1 room is switched off —", "turn it on in Configuration"),
		_ => new HiddenRoomsNote($"{switchedOff} rooms are switched off —", "turn them on in Configuration")
	};

	/// <summary>
	///     Whether the dashboard shows the first-run state instead of a grid: rooms exist, none is switched on,
	///     and Home Assistant is answering.
	/// </summary>
	/// <param name="engineIsAttached">
	///     Whether the engine has a Home Assistant connection. A disconnected host also shows no lit rooms, and
	///     onboarding offered there would hide that.
	/// </param>
	public static bool IsAwaitingRoomChoice(IEnumerable<RoomView> rooms, bool engineIsAttached)
	{
		ArgumentNullException.ThrowIfNull(rooms);

		List<RoomView> configured = [.. rooms];

		return engineIsAttached && configured.Count > 0 && configured.TrueForAll(room => !room.IsEnabled);
	}

	// A report matching no room is shown, never hidden: a card dropped because the document changed underneath it
	// is indistinguishable from a fault.
	private static bool IsShown(AreaSnapshot snapshot, Dictionary<string, bool> byId, Dictionary<string, bool> byName)
	{
		if (snapshot.AreaId is { Length: > 0 } areaId && byId.TryGetValue(areaId, out bool enabledById))
			return enabledById;

		return !byName.TryGetValue(snapshot.AreaName, out bool enabledByName) || enabledByName;
	}
}
