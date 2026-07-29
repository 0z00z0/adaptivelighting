using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>
///     What a room is called, decided once for every surface that has to say it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> Discovery deliberately writes only an <see cref="AreaConfig.AreaId"/>, so that a
///         proposal stays true when somebody renames the room in Home Assistant. Nothing then asked Home Assistant
///         what the area was <i>called</i>, and <see cref="AreaConfig.DisplayName"/> fell straight through to the
///         slug: the room page's heading read <c>kjeller_bad</c>, the board's lane read <c>sykkelbod</c> and the
///         activity log's room column read <c>kjokken</c>, for rooms Home Assistant knows as Kjeller - Bad,
///         Sykkelbod and Kjøkken.
///     </para>
///     <para>
///         The fix is a lookup, never a write. Resolving the name at the point of use is what keeps a rename in
///         Home Assistant arriving here on the next read; persisting it would freeze the room's name at whatever
///         it was called on the day it was set up — the same trap the person-seeding change documented, where a
///         convenience copy outlives the thing it copied.
///     </para>
///     <para>
///         One helper rather than one per page. The engine puts the resolved name into
///         <see cref="AdaptiveLighting.Abstractions.AreaSnapshot.AreaName"/>, so the board, the activity log and
///         the room page's live state inherit it; the surfaces that build a label from the document alone — the
///         house tab's room rows, the first-run chips, the re-setup panel — call in here, so all of them agree
///         about one room.
///     </para>
/// </remarks>
public static class AreaNaming
{
	/// <summary>
	///     What names this room, or <c>null</c> when nothing does.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The order is the whole rule: a <see cref="AreaConfig.Name"/> in the document is the owner overruling
	///         Home Assistant and always wins; the registry's own name comes next; the area id is the last thing
	///         that is still a fact. <c>null</c> means the caller's own placeholder applies — the house tab says
	///         "New room" where the engine says "(unnamed area)", and neither should have to know about the other.
	///     </para>
	///     <para>
	///         <b>Whitespace is not a name.</b> Each step used to test only <c>Length &gt; 0</c>, so a hand-edited
	///         <c>Name: "  "</c> won outright and every surface labelled the room with two spaces. Worse, it reached
	///         <c>SwitchOnWarning.For</c>, whose <c>ThrowIfNullOrWhiteSpace</c> then faulted the settings page on
	///         load — before it could render the row somebody would fix the name on. Falling through instead lands
	///         on the id, then on the caller's placeholder, which is what an unnamed room was always meant to get.
	///     </para>
	/// </remarks>
	/// <param name="area">The room, from the document.</param>
	/// <param name="registryName">
	///     Home Assistant's name for an area id, or <c>null</c> to skip the registry entirely — which is what a
	///     caller with no connection has, and what leaves the answer at the area id rather than blanking it.
	/// </param>
	/// <exception cref="ArgumentNullException"><paramref name="area"/> is <c>null</c>.</exception>
	public static string? Resolve(AreaConfig area, Func<string, string?>? registryName)
	{
		ArgumentNullException.ThrowIfNull(area);

		if (Named(area.Name) is { } stated)
			return stated;

		if (Named(area.AreaId) is not { } areaId)
			return null;

		return Named(registryName?.Invoke(areaId)) ?? areaId;
	}

	/// <inheritdoc cref="Resolve(AreaConfig, Func{string, string?})"/>
	/// <param name="area">The room, from the document.</param>
	/// <param name="registry">The area registry, or <c>null</c> when there is none to ask.</param>
	public static string? Resolve(AreaConfig area, IAreaRegistry? registry) => Resolve(area, NameSource(registry));

	/// <summary>
	///     The room's name for a surface with no placeholder of its own, ending at
	///     <see cref="AreaConfig.DisplayName"/>.
	/// </summary>
	/// <param name="area">The room, from the document.</param>
	/// <param name="registry">The area registry, or <c>null</c> when there is none to ask.</param>
	/// <exception cref="ArgumentNullException"><paramref name="area"/> is <c>null</c>.</exception>
	public static string DisplayName(AreaConfig area, IAreaRegistry? registry) =>
		Resolve(area, registry) ?? area.DisplayName;

	/// <inheritdoc cref="DisplayName(AreaConfig, IAreaRegistry)"/>
	/// <param name="area">The room, from the document.</param>
	/// <param name="registryName">Home Assistant's name for an area id, or <c>null</c> to skip the registry.</param>
	public static string DisplayName(AreaConfig area, Func<string, string?>? registryName) =>
		Resolve(area, registryName) ?? area.DisplayName;

	/// <summary>
	///     Home Assistant's name for one area id, or <c>null</c> when it has none.
	/// </summary>
	/// <remarks>
	///     A registry that cannot answer is <c>null</c>, never an exception. NetDaemon's registry throws until its
	///     first connection completes, and Kestrel serves pages in that window: a room whose name blanked — or a
	///     page that failed outright — while Home Assistant was still connecting would be a worse bug than the one
	///     this class fixes.
	/// </remarks>
	/// <param name="registry">The area registry, or <c>null</c>.</param>
	/// <param name="areaId">The area id to name.</param>
	public static string? OfArea(IAreaRegistry? registry, string? areaId)
	{
		if (registry is null || Named(areaId) is not { } asked)
			return null;

		try
		{
			return Named(registry.NameOf(asked));
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}

	/// <summary>A candidate that actually names something, or <c>null</c> so the next step in the order gets its turn.</summary>
	private static string? Named(string? candidate) => string.IsNullOrWhiteSpace(candidate) ? null : candidate;

	private static Func<string, string?>? NameSource(IAreaRegistry? registry) =>
		registry is null ? null : areaId => OfArea(registry, areaId);
}
