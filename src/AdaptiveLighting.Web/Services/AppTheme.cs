namespace AdaptiveLighting.Web.Services;

/// <summary>One entry in the theme picker: what it is called, and what it puts on the <c>&lt;html&gt;</c> element.</summary>
/// <param name="Id">
///     The stored id. Held in the browser, so renaming one drops every browser that had chosen it: add themes,
///     never rename them.
/// </param>
/// <param name="Name">What the picker calls it.</param>
/// <param name="DataTheme">
///     The <c>data-theme</c> value, or <c>null</c> to let the device decide. Following the system means removing
///     the attribute, not setting it to a value.
/// </param>
public sealed record AppTheme(string Id, string Name, string? DataTheme);

/// <summary>The themes this UI offers, and how a stored choice turns back into one of them.</summary>
public static class AppThemes
{
	/// <summary>The default.</summary>
	public static readonly AppTheme System = new("system", "Follow the system", null);

	public static readonly AppTheme Light = new("light", "Light", "light");

	public static readonly AppTheme Dark = new("dark", "Dark", "dark");

	/// <summary>The ZeroZero Software palette: blue-black surfaces, teal accent, monospace throughout.</summary>
	public static readonly AppTheme ZeroZero = new("0z0", "0z0 tech", "0z0");

	/// <summary>Every theme on offer, in the order the picker lists them.</summary>
	public static IReadOnlyList<AppTheme> All { get; } = [System, Light, Dark, ZeroZero];

	/// <summary>The <c>data-theme</c> values, space-separated, for the head script's allow-list.</summary>
	/// <remarks>
	///     <c>theme.js</c> runs before any circuit exists and cannot ask the server, but still has to refuse an id
	///     this build no longer ships. It is handed the list on its script tag, so the ids stay defined only here.
	/// </remarks>
	public static string DataThemeIds { get; } =
		string.Join(' ', All.Select(theme => theme.DataTheme).OfType<string>());

	/// <summary>The theme a stored id names, or <see cref="System"/> when it names nothing this build ships.</summary>
	/// <param name="storedId">Whatever came back out of the browser's storage, <c>null</c> included.</param>
	public static AppTheme Resolve(string? storedId) =>
		// Ordinal: these are storage keys, not words, and a Turkish locale lower-casing an I would break the match.
		All.FirstOrDefault(theme => string.Equals(theme.Id, storedId, StringComparison.Ordinal)) ?? System;
}
