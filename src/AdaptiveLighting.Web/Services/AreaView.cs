using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     The dashboard's footer line about switched-off rooms, split where its link goes.
/// </summary>
/// <remarks>
///     Split rather than handed over as one sentence because the second half is a link into the Areas section: a
///     person who switched the kitchen off by accident needs a thread to pull, and a footer that only stated the
///     count would leave them looking for one. Two strings keep the wording — including its plurals — testable in
///     one place instead of half in a helper and half in markup.
/// </remarks>
/// <param name="Lead">The sentence up to the link, ending in the dash the link follows.</param>
/// <param name="LinkText">The link's own words, which have to read as an instruction on their own.</param>
public sealed record HiddenRoomsNote(string Lead, string LinkText);

/// <summary>
///     The decisions the two area screens make about how a room is shown, in one testable place.
/// </summary>
/// <remarks>
///     <para>
///         The dashboard's cards and the settings list are two views of the same rooms, and the design asks them
///         to speak one colour language: a person who learned "amber means somebody touched it" on the dashboard
///         must read the same fact in Settings. Two copies of that mapping would drift, and the drift would show
///         up as one house painted two ways on two pages.
///     </para>
///     <para>
///         Everything here is pure, which is the point: this repo has no Razor render-test harness, so anything
///         worth asserting has to live outside the markup. Bulk enable, the edge colour and the override count
///         are exactly the parts a wrong answer would be expensive in.
///     </para>
/// </remarks>
public static class AreaView
{
	/// <summary>
	///     How many settings a room can override — the denominator in "n of 21 changed".
	/// </summary>
	/// <remarks>
	///     Twenty-one, not twenty-two: <see cref="AreaConfig.Enabled"/> left the override list when the header
	///     toggle took it over. The same twenty-one are counted by <c>AreaSetupService</c>'s rebuild plan, so the
	///     editor's summary and the re-setup warning can never disagree about how much a room has changed. It grew
	///     from sixteen when the five daylight-brightness settings arrived.
	/// </remarks>
	public const int OverridableSettingCount = 21;

	/// <summary>What a floorless group is called, once, so both screens head it the same way.</summary>
	public const string FloorlessTitle = "Other rooms";

	/// <summary>
	///     The colour family a live state belongs to: the engine acting, a person having acted, or neither.
	/// </summary>
	/// <param name="state">The area's last published state.</param>
	/// <returns><c>machine</c>, <c>human</c> or <c>idle</c> — the suffix of a <c>family-*</c> class.</returns>
	public static string Family(AreaState state) => state switch
	{
		AreaState.AutoActive or AreaState.AutoVacant or AreaState.PreOff => "machine",
		AreaState.OverriddenOn or AreaState.SuppressedOff => "human",
		_ => "idle"
	};

	/// <summary>
	///     The left-edge class for a room in the settings list.
	/// </summary>
	/// <remarks>
	///     A switched-off room is flat grey whatever the engine last said about it: the toggle is the room's power
	///     state, and colouring a disabled room as "the engine is acting" would contradict the switch beside it.
	///     A room with no snapshot yet is idle rather than absent — the settings page is reachable before the first
	///     report arrives, and an unpainted edge would read as a fault.
	/// </remarks>
	/// <param name="enabled">The room's effective enablement, as the header toggle shows it.</param>
	/// <param name="state">Its last published state, or <c>null</c> when nothing has been heard yet.</param>
	/// <returns>One <c>family-*</c> class name.</returns>
	public static string EdgeClass(bool enabled, AreaState? state) =>
		!enabled ? "family-off"
		: state is { } live ? $"family-{Family(live)}"
		: "family-idle";

	/// <summary>Whether the engine may command this room, following the document's inheritance.</summary>
	/// <param name="area">The room.</param>
	/// <param name="defaults">The document's all-rooms settings, for a room that states nothing.</param>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static bool IsEnabled(AreaConfig area, AreaSettings defaults)
	{
		ArgumentNullException.ThrowIfNull(area);
		ArgumentNullException.ThrowIfNull(defaults);

		return area.Enabled ?? defaults.Enabled;
	}

	/// <summary>
	///     Whether every room in a group is already switched on, which is what turns the floor's bulk action from
	///     <i>Switch on this floor</i> into <i>Switch off this floor</i>.
	/// </summary>
	/// <remarks>An empty group counts as not-all-on: offering "switch off" over nothing would be a dead control.</remarks>
	/// <param name="areas">The rooms in the group.</param>
	/// <param name="defaults">The document's all-rooms settings.</param>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static bool AllEnabled(IEnumerable<AreaConfig> areas, AreaSettings defaults)
	{
		ArgumentNullException.ThrowIfNull(areas);
		ArgumentNullException.ThrowIfNull(defaults);

		List<AreaConfig> rooms = [.. areas];

		return rooms.Count > 0 && rooms.TrueForAll(area => IsEnabled(area, defaults));
	}

	/// <summary>
	///     Switches a whole floor on or off.
	/// </summary>
	/// <remarks>
	///     Writes an explicit <c>true</c>/<c>false</c> on every room, never <c>null</c>: inheritance stays for old
	///     documents, but a decision a person made with a button is a decision the file should state. Mutating in
	///     place is deliberate — this is an edit to the in-memory document, which the caller then hands to the
	///     ordinary save bar. Nothing here writes anything.
	/// </remarks>
	/// <param name="areas">The rooms to switch.</param>
	/// <param name="on">What to switch them to.</param>
	/// <returns>How many rooms actually changed value, so a caller can skip a no-op.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="areas"/> is <c>null</c>.</exception>
	public static int SwitchAll(IEnumerable<AreaConfig> areas, bool on)
	{
		ArgumentNullException.ThrowIfNull(areas);

		int changed = 0;

		foreach (AreaConfig area in areas)
		{
			if (area.Enabled != on)
				changed++;

			area.Enabled = on;
		}

		return changed;
	}

	/// <summary>
	///     Whether a floor group earns a header, encoding §4.1's degradation rule for every renderer that asks.
	/// </summary>
	/// <remarks>
	///     A house with no floors at all collapses to one unnamed group, and that group gets no header — the list
	///     is exactly the flat list it was before floors existed, and a house that never set one never learns the
	///     feature is there. "Other rooms" is therefore never the only heading on the page.
	/// </remarks>
	/// <param name="groupCount">How many groups the list has.</param>
	/// <param name="floor">The group's floor, or <c>null</c> for the trailing floorless group.</param>
	public static bool ShowsHeader(int groupCount, AreaFloor? floor) => groupCount > 1 || floor is not null;

	/// <summary>What a floor group is headed: its name, or the floorless group's fixed title.</summary>
	public static string FloorTitle(AreaFloor? floor) => floor?.Name ?? FloorlessTitle;

	// ===================== the dashboard =====================

	/// <summary>
	///     The live reports the dashboard gives a card to: the ones from rooms the owner has switched on.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The engine keeps observing and publishing switched-off rooms, deliberately — only the rendering
	///         changes here. So the filter is a question about the document, not about the report: a room's
	///         <c>Disabled</c> snapshots keep arriving and keep being cached, and this decides they get no card.
	///     </para>
	///     <para>
	///         Matched by area id first and display name second, the same join the snapshot cache and the settings
	///         list use, so all three agree about which report belongs to which room. <b>A report that matches no
	///         room at all is shown, never hidden</b> — the dashboard's job is to say what the engine is doing, and
	///         a card silently dropped because the document was edited underneath it would be indistinguishable
	///         from a fault.
	///     </para>
	/// </remarks>
	/// <param name="snapshots">Everything the cache has heard, in the order it should render.</param>
	/// <param name="rooms">The document's rooms, as <c>ModeService.GetRooms</c> projects them.</param>
	/// <returns>The reports that earn a card, in the order they were given.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
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

	/// <summary>
	///     How many rooms the owner has switched off — the number the footer under the grid carries.
	/// </summary>
	/// <remarks>
	///     Counted from the document rather than from the cards that did not render, because those are different
	///     numbers exactly when the difference matters: a room switched off before the engine ever reported it has
	///     no snapshot to be missing, and a footer counting absences would say "0 rooms are switched off" to
	///     somebody staring at a house that is doing nothing.
	/// </remarks>
	/// <param name="rooms">The document's rooms.</param>
	/// <exception cref="ArgumentNullException"><paramref name="rooms"/> is <c>null</c>.</exception>
	public static int SwitchedOffCount(IEnumerable<RoomView> rooms)
	{
		ArgumentNullException.ThrowIfNull(rooms);

		return rooms.Count(room => !room.IsEnabled);
	}

	/// <summary>
	///     The footer line for <paramref name="switchedOff"/> hidden rooms, or <c>null</c> when nothing is hidden.
	/// </summary>
	/// <remarks>
	///     <c>null</c> for nothing hidden is the whole "no footer when nothing is hidden" rule, decided once here
	///     rather than by a condition in the markup that could disagree with the wording beside it.
	/// </remarks>
	/// <param name="switchedOff">How many rooms are switched off.</param>
	public static HiddenRoomsNote? HiddenNote(int switchedOff) => switchedOff switch
	{
		<= 0 => null,
		1 => new HiddenRoomsNote("1 room is switched off —", "turn it on in Settings"),
		_ => new HiddenRoomsNote($"{switchedOff} rooms are switched off —", "turn them on in Settings")
	};

	/// <summary>
	///     Whether the dashboard should show the first-run state instead of a grid: rooms exist, none is switched
	///     on, and Home Assistant is answering.
	/// </summary>
	/// <remarks>
	///     <para>
	///         This is the state a fresh install lands in on purpose — auto-setup writes every discovered room
	///         switched off, so the house is fully configured and deliberately doing nothing. Undesigned, that page
	///         reads as broken software; it is not an error, and the page must not dress it as one.
	///     </para>
	///     <para>
	///         <paramref name="engineIsAttached"/> is what keeps it honest. A disconnected host also shows no lit
	///         rooms, and offering "choose which rooms to switch on" to somebody whose connection is down would
	///         hide the real problem behind onboarding. With no rooms at all there is nothing to choose from
	///         either, which is the empty-document case the settings page already speaks to.
	///     </para>
	/// </remarks>
	/// <param name="rooms">The document's rooms.</param>
	/// <param name="engineIsAttached">Whether the engine has a Home Assistant connection.</param>
	/// <exception cref="ArgumentNullException"><paramref name="rooms"/> is <c>null</c>.</exception>
	public static bool IsAwaitingRoomChoice(IEnumerable<RoomView> rooms, bool engineIsAttached)
	{
		ArgumentNullException.ThrowIfNull(rooms);

		List<RoomView> configured = [.. rooms];

		return engineIsAttached && configured.Count > 0 && configured.TrueForAll(room => !room.IsEnabled);
	}

	private static bool IsShown(AreaSnapshot snapshot, Dictionary<string, bool> byId, Dictionary<string, bool> byName)
	{
		if (snapshot.AreaId is { Length: > 0 } areaId && byId.TryGetValue(areaId, out bool enabledById))
			return enabledById;

		return !byName.TryGetValue(snapshot.AreaName, out bool enabledByName) || enabledByName;
	}
}
