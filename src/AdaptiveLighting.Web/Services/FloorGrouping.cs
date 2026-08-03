using AdaptiveLighting.Abstractions;

namespace AdaptiveLighting.Web.Services;

/// <summary>One floor's rooms, ordered for display. Shared by the dashboard and the Areas section.</summary>
/// <remarks>
///     Items keep the order they were handed in. The caller has already decided what order rooms appear in —
///     document order in the editor, registry order on the dashboard — and re-sorting them here would silently
///     overrule that for the one house that has floors.
/// </remarks>
/// <param name="Floor">The floor these items sit on, or <c>null</c> for the trailing floorless group.</param>
/// <param name="Items">The items on it, in the order they were given.</param>
public sealed record FloorGroup<T>(AreaFloor? Floor, IReadOnlyList<T> Items);

/// <summary>
///     Groups things that belong to an area by the floor that area sits on.
/// </summary>
/// <remarks>
///     <para>
///         One helper, two screens. The dashboard's grid and the Areas section's room list must agree about which
///         rooms are on which floor and in what order — two implementations of "order by level, floorless last"
///         would drift, and the drift would show up as the same house grouped two different ways on two pages.
///     </para>
///     <para>
///         <b>The degradation rule lives here and nowhere else.</b> A house with no floors at all collapses to a
///         single group whose <see cref="FloorGroup{T}.Floor"/> is <c>null</c>, so a renderer that shows a header
///         only when <c>Count &gt; 1 || Floor is not null</c> gets today's flat list for a floorless house, floor
///         headers for a floored one, and never a lone "Other rooms" heading over the whole page.
///     </para>
/// </remarks>
public static class FloorGrouping
{
	/// <summary>
	///     Groups <paramref name="items"/> by floor: ordered by <see cref="AreaFloor.Level"/> (a floor with no
	///     level sorts last among floors) and then by name, with the floorless group trailing them all.
	/// </summary>
	/// <typeparam name="T">Whatever the screen is showing — an area config, a card model, a snapshot.</typeparam>
	/// <param name="items">The things to group. Empty in, empty out: there is nothing to head.</param>
	/// <param name="areaIdOf">
	///     The area id of one item, or <c>null</c> when it has none. An item with no area id is floorless by
	///     construction, which is exactly how a hand-listed room with no <c>AreaId</c> should be treated.
	/// </param>
	/// <param name="registry">Where the floor of an area comes from.</param>
	/// <returns>The groups, in display order. Never <c>null</c>.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static IReadOnlyList<FloorGroup<T>> Group<T>(
		IEnumerable<T> items,
		Func<T, string?> areaIdOf,
		IAreaRegistry registry)
	{
		ArgumentNullException.ThrowIfNull(items);
		ArgumentNullException.ThrowIfNull(areaIdOf);
		ArgumentNullException.ThrowIfNull(registry);

		List<(AreaFloor? Floor, T Item)> placed = [.. items.Select(item => (FloorOf(item, areaIdOf, registry), item))];

		if (placed.Count == 0)
			return [];

		// Keyed by floor id, not by the record: two lookups of the same floor are two AreaFloor instances, and
		// while the record compares by value today, the grouping must not depend on that staying true.
		List<FloorGroup<T>> floored = [.. placed
			.Where(entry => entry.Floor is not null)
			.GroupBy(entry => entry.Floor!.Id, StringComparer.Ordinal)
			.Select(group => new FloorGroup<T>(group.First().Floor, [.. group.Select(entry => entry.Item)]))
			.OrderBy(group => group.Floor!.Level ?? int.MaxValue)
			.ThenBy(group => group.Floor!.Name, StringComparer.CurrentCulture)];

		List<T> floorless = [.. placed.Where(entry => entry.Floor is null).Select(entry => entry.Item)];

		// A house with no floors is one unnamed group, which is the whole degradation story: the renderers ask
		// only "is this the sole group, and is it unnamed?" and get today's flat list without knowing why.
		if (floorless.Count == 0)
			return floored;

		return [.. floored, new FloorGroup<T>(null, floorless)];
	}

	/// <summary>
	///     <see cref="Group"/>, degrading to one unnamed group when the registry cannot answer at all.
	/// </summary>
	/// <remarks>
	///     <b>A flat table beats no table, and that is a policy rather than a catch.</b> A registry that has not
	///     connected throws instead of answering, and each of the three screens that group rooms had written the same
	///     <c>try</c>/<c>catch</c> around <see cref="Group"/> over its own <typeparamref name="T"/> — three copies of
	///     one decision, so a fourth surface would get it subtly wrong or forget it and take the page down over a
	///     dropped connection. The degradation rule already lives in this class; only its failure case was outside it.
	/// </remarks>
	/// <typeparam name="T">Whatever the screen is showing.</typeparam>
	/// <param name="items">The things to group.</param>
	/// <param name="areaIdOf">The area id of one item, or <c>null</c> when it has none.</param>
	/// <param name="registry">Where the floor of an area comes from. May be unreachable.</param>
	/// <returns>The groups, or one unnamed group holding everything when the floors cannot be read.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static IReadOnlyList<FloorGroup<T>> GroupOrFlat<T>(
		IEnumerable<T> items,
		Func<T, string?> areaIdOf,
		IAreaRegistry registry)
	{
		ArgumentNullException.ThrowIfNull(items);

		try
		{
			return Group(items, areaIdOf, registry);
		}
		catch (InvalidOperationException)
		{
			List<T> all = [.. items];
			return all.Count == 0 ? [] : [new FloorGroup<T>(null, all)];
		}
	}

	private static AreaFloor? FloorOf<T>(T item, Func<T, string?> areaIdOf, IAreaRegistry registry) =>
		areaIdOf(item) is { Length: > 0 } areaId ? registry.FloorOf(areaId) : null;
}
