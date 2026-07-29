using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     That the theme picker offers what it should, and that a stored choice survives a reload.
/// </summary>
/// <remarks>
///     There is no Razor render harness in this repo and one must not be introduced, so the picker's markup is
///     not what is asserted here. What is asserted is everything the markup delegates: the list, the round trip
///     through the browser's storage, and the fallback — the three places where a mistake is either invisible
///     until somebody reloads, or visible to everybody at once.
/// </remarks>
[TestClass]
public sealed class AppThemeTests
{
	/// <summary>
	///     Following the device is the default, and is what the UI did before a picker existed.
	/// </summary>
	/// <remarks>
	///     Nobody's UI may change appearance because this shipped. That means two things at once: the option has
	///     to exist, and it has to be the one an unset browser lands on — which is the same assertion as the
	///     fallback below, made from the other direction.
	/// </remarks>
	[TestMethod]
	public void Following_The_Device_Is_The_Default_And_Comes_First()
	{
		Assert.AreSame(AppThemes.System, AppThemes.All[0]);
		Assert.AreSame(AppThemes.System, AppThemes.Resolve(null));
		Assert.IsNull(AppThemes.System.DataTheme, "following the device means removing the attribute, not setting one");
	}

	/// <summary>The three palettes are on offer beside it, each with a data-theme value of its own.</summary>
	[TestMethod]
	public void Every_Palette_Is_Offered_And_Carries_A_Data_Theme()
	{
		CollectionAssert.AreEqual(
			new[] { "system", "light", "dark", "0z0" },
			AppThemes.All.Select(theme => theme.Id).ToArray());

		foreach (AppTheme theme in AppThemes.All.Where(theme => theme != AppThemes.System))
		{
			Assert.AreEqual(theme.Id, theme.DataTheme, $"{theme.Id} should paint under its own id");
		}
	}

	/// <summary>Two themes sharing an id would make one of them unreachable, and silently.</summary>
	[TestMethod]
	public void Ids_And_Names_Are_Unique()
	{
		Assert.AreEqual(AppThemes.All.Count, AppThemes.All.Select(theme => theme.Id).Distinct().Count());
		Assert.AreEqual(AppThemes.All.Count, AppThemes.All.Select(theme => theme.Name).Distinct().Count());
	}

	/// <summary>
	///     What was chosen is what comes back: every id round-trips through storage to its own theme.
	/// </summary>
	[TestMethod]
	public void Every_Stored_Choice_Resolves_Back_To_Itself()
	{
		foreach (AppTheme theme in AppThemes.All)
		{
			Assert.AreSame(theme, AppThemes.Resolve(theme.Id), $"{theme.Id} did not survive the round trip");
		}
	}

	/// <summary>
	///     A stored id naming a theme this build no longer ships falls back to the device.
	/// </summary>
	/// <remarks>
	///     The failure this prevents is not a missing theme but a missing palette: <c>data-theme="solarized"</c>
	///     matches no block in app.css, so every token would fall through to the bare <c>:root</c> — a dark page
	///     on a light desk, with no way for the reader to tell why. Empty and whitespace are covered too, because
	///     a browser holding a half-written key is the same case.
	/// </remarks>
	[TestMethod]
	public void An_Unknown_Stored_Theme_Falls_Back_To_The_Device()
	{
		foreach (string stored in new[] { "solarized", "", " ", "System", "0Z0", "light " })
		{
			Assert.AreSame(AppThemes.System, AppThemes.Resolve(stored), $"'{stored}' should not name a theme");
		}
	}

	/// <summary>
	///     The head script's allow-list is every palette and nothing else.
	/// </summary>
	/// <remarks>
	///     <c>theme.js</c> runs before any circuit exists and cannot ask the server what it ships, so it is handed
	///     this list on its own script tag. If a theme were added here and left out of that list, the head script
	///     would refuse the very id it had just stored and the choice would appear not to persist.
	/// </remarks>
	[TestMethod]
	public void The_Head_Scripts_Allow_List_Is_Every_Palette()
	{
		string[] ids = AppThemes.DataThemeIds.Split(' ', StringSplitOptions.RemoveEmptyEntries);

		CollectionAssert.AreEqual(new[] { "light", "dark", "0z0" }, ids);
		Assert.IsFalse(ids.Contains(AppThemes.System.Id), "following the device is an absence, not a value");
	}
}
