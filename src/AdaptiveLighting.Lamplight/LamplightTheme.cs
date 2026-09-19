namespace AdaptiveLighting.Lamplight;

/// <summary>One entry in Lamplight's theme picker.</summary>
/// <param name="Id">
///     The stored id. Held in the browser and carried as <c>data-theme</c>, so renaming one drops every
///     browser that had chosen it: add themes, never rename them.
/// </param>
/// <param name="Name">What the picker calls it.</param>
/// <param name="IsDark">Which optgroup the picker lists it under. Meaningless for <see cref="LamplightThemes.System"/>.</param>
public sealed record LamplightTheme(string Id, string Name, bool IsDark);

/// <summary>
/// Lamplight's six palettes and the device-following default, kept apart from <c>AppThemes</c>: the two sites
/// store their choice under different keys and never share it.
/// </summary>
public static class LamplightThemes
{
	/// <summary>The default. Resolves to <see cref="DarkDefault"/> or <see cref="LightDefault"/> in the browser.</summary>
	public static readonly LamplightTheme System = new("system", "Follow the device", false);

	public static readonly LamplightTheme WarmCharcoal = new("warm-charcoal", "Warm charcoal", true);

	public static readonly LamplightTheme PlumDusk = new("plum-dusk", "Plum dusk", true);

	public static readonly LamplightTheme Blackout = new("blackout", "Blackout", true);

	/// <summary>The ZeroZero Software palette: blue-black surfaces, teal accent, monospace throughout.</summary>
	public static readonly LamplightTheme ZeroZero = new("0z0", "0z0", true);

	public static readonly LamplightTheme Paper = new("paper", "Paper", false);

	public static readonly LamplightTheme Fern = new("fern", "Fern", false);

	/// <summary>Every theme, in picker order: <see cref="System"/> first, then dark, then light.</summary>
	public static IReadOnlyList<LamplightTheme> All { get; } =
		[System, WarmCharcoal, PlumDusk, Blackout, ZeroZero, Paper, Fern];

	/// <summary>What a dark device with nothing stored gets.</summary>
	public static LamplightTheme DarkDefault => WarmCharcoal;

	/// <summary>What a light device with nothing stored gets.</summary>
	public static LamplightTheme LightDefault => Paper;

	/// <summary>The six <c>data-theme</c> values, space-separated, for the head script's allow-list. Excludes
	/// <see cref="System"/>, which paints as an absence of a stored choice rather than a value of its own.</summary>
	public static string DataThemeIds { get; } =
		string.Join(' ', All.Where(theme => theme != System).Select(theme => theme.Id));

	/// <summary>The theme a stored id names, or <see cref="System"/> when it names nothing this build ships.</summary>
	/// <param name="storedId">Whatever came back out of the browser's storage, <c>null</c> included.</param>
	public static LamplightTheme Resolve(string? storedId) =>
		// Ordinal: these are storage keys, not words, and a Turkish locale lower-casing an I would break the match.
		All.FirstOrDefault(theme => string.Equals(theme.Id, storedId, StringComparison.Ordinal)) ?? System;
}
