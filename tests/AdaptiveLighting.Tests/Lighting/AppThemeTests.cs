using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     That the theme picker offers what it should, and that a stored choice survives a reload.
/// </summary>
/// <remarks>
///     There is no Razor render harness here, so the markup is not asserted. What is asserted is what the markup
///     delegates: the list, the storage round trip, and the fallback.
/// </remarks>
[TestClass]
public sealed class AppThemeTests
{
	[TestMethod]
	public void Following_The_Device_Is_The_Default_And_Comes_First()
	{
		Assert.AreSame(AppThemes.System, AppThemes.All[0]);
		Assert.AreSame(AppThemes.System, AppThemes.Resolve(null));
		Assert.IsNull(AppThemes.System.DataTheme, "following the device means removing the attribute, not setting one");
	}

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

	// Two themes sharing an id make one of them unreachable, silently.
	[TestMethod]
	public void Ids_And_Names_Are_Unique()
	{
		Assert.AreEqual(AppThemes.All.Count, AppThemes.All.Select(theme => theme.Id).Distinct().Count());
		Assert.AreEqual(AppThemes.All.Count, AppThemes.All.Select(theme => theme.Name).Distinct().Count());
	}

	[TestMethod]
	public void Every_Stored_Choice_Resolves_Back_To_Itself()
	{
		foreach (AppTheme theme in AppThemes.All)
		{
			Assert.AreSame(theme, AppThemes.Resolve(theme.Id), $"{theme.Id} did not survive the round trip");
		}
	}

	// An id no build ships matches no block in app.css, so every token falls through to the bare :root.
	// Empty and whitespace are the same case: a browser holding a half-written key.
	[TestMethod]
	public void An_Unknown_Stored_Theme_Falls_Back_To_The_Device()
	{
		foreach (string stored in new[] { "solarized", "", " ", "System", "0Z0", "light " })
		{
			Assert.AreSame(AppThemes.System, AppThemes.Resolve(stored), $"'{stored}' should not name a theme");
		}
	}

	// theme.js runs before any circuit exists, so it is handed the allow-list on its own script tag.
	[TestMethod]
	public void The_Head_Scripts_Allow_List_Is_Every_Palette()
	{
		string[] ids = AppThemes.DataThemeIds.Split(' ', StringSplitOptions.RemoveEmptyEntries);

		CollectionAssert.AreEqual(new[] { "light", "dark", "0z0" }, ids);
		Assert.IsFalse(ids.Contains(AppThemes.System.Id), "following the device is an absence, not a value");
	}
}
