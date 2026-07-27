namespace AdaptiveLighting.Web.Services;

/// <summary>
///     One entry in the theme picker: what it is called, and what it puts on the <c>&lt;html&gt;</c> element.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Id"/> is stored in the browser and read back by <c>theme.js</c> before the first paint, so it
///         is part of a contract with somebody's saved preference: renaming one silently drops every browser that
///         had chosen it back to the default. Add themes; do not rename them.
///     </para>
///     <para>
///         <see cref="DataTheme"/> is <c>null</c> for the one option that has no palette of its own — following the
///         operating system means removing the attribute and letting <c>prefers-color-scheme</c> answer, which is
///         a different act from setting it to a value.
///     </para>
/// </remarks>
/// <param name="Id">The stored id, and the <c>data-theme</c> value where there is one.</param>
/// <param name="Name">What the picker calls it.</param>
/// <param name="DataTheme">The <c>data-theme</c> value, or <c>null</c> to let the device decide.</param>
public sealed record AppTheme(string Id, string Name, string? DataTheme);

/// <summary>
///     The themes this UI offers, and how a stored choice turns back into one of them.
/// </summary>
/// <remarks>
///     <para>
///         Pure and total, because this repo has no Razor render harness: the list, the round trip through storage
///         and the fallback are the three things worth asserting, and none of them can be asserted from markup.
///     </para>
///     <para>
///         <see cref="System"/> is first and is what an unset, empty or unrecognised value resolves to. That last
///         case is the one that matters: a browser holding <c>"solarized"</c> from a theme that was withdrawn must
///         land on the device's own answer rather than on a missing palette, where every token would fall back to
///         the bare <c>:root</c> block and the page would be dark on a light desk.
///     </para>
/// </remarks>
public static class AppThemes
{
	/// <summary>The default, and the behaviour every browser had before the picker existed.</summary>
	public static readonly AppTheme System = new("system", "Follow the system", null);

	/// <summary>The workshop palette in daylight.</summary>
	public static readonly AppTheme Light = new("light", "Light", "light");

	/// <summary>The workshop palette after dark.</summary>
	public static readonly AppTheme Dark = new("dark", "Dark", "dark");

	/// <summary>
	///     The ZeroZero Software palette: blue-black surfaces, teal accent, monospace throughout.
	/// </summary>
	public static readonly AppTheme ZeroZero = new("0z0", "0z0 tech", "0z0");

	/// <summary>
	///     Every theme on offer, in the order the picker lists them: the device's own answer, then the palettes.
	/// </summary>
	public static IReadOnlyList<AppTheme> All { get; } = [System, Light, Dark, ZeroZero];

	/// <summary>
	///     The <c>data-theme</c> values, space-separated, for the head script's allow-list.
	/// </summary>
	/// <remarks>
	///     <c>theme.js</c> runs before any circuit exists and so cannot ask the server anything, but it must still
	///     refuse an id this build no longer ships. Handing it the list on its own script tag keeps the ids defined
	///     once, here, instead of in two files that drift the first time a theme is added.
	/// </remarks>
	public static string DataThemeIds { get; } =
		string.Join(' ', All.Select(theme => theme.DataTheme).OfType<string>());

	/// <summary>
	///     The theme a stored id names, or <see cref="System"/> when it names nothing this build still ships.
	/// </summary>
	/// <remarks>
	///     Ordinal comparison on purpose: the ids are storage keys, not words, and a Turkish locale lower-casing
	///     an <c>I</c> is exactly the sort of defect that only appears on somebody else's phone.
	/// </remarks>
	/// <param name="storedId">Whatever came back out of the browser's storage, including <c>null</c>.</param>
	public static AppTheme Resolve(string? storedId) =>
		All.FirstOrDefault(theme => string.Equals(theme.Id, storedId, StringComparison.Ordinal)) ?? System;
}
