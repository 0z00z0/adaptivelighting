using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

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
	///     How many settings a room can override — the denominator in "n of 16 changed".
	/// </summary>
	/// <remarks>
	///     Sixteen, not seventeen: <see cref="AreaConfig.Enabled"/> left the override list when the header toggle
	///     took it over. The same sixteen are counted by <c>AreaSetupService</c>'s rebuild plan, so the editor's
	///     summary and the re-setup warning can never disagree about how much a room has changed.
	/// </remarks>
	public const int OverridableSettingCount = 16;

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
}
