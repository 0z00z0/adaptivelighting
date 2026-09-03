using System.Globalization;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Components;
using AdaptiveLighting.Web.Services;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The stepped rail rendered statically, so what the markup says is what a reader sees.</summary>
[TestClass]
public sealed class PresetSliderTests
{
	/// <summary>The leftmost stop must never read as the dimmest setting, which is the whole point of the control.</summary>
	[TestMethod]
	public async Task Borrowing_A_Value_Puts_The_Thumb_At_Zero_And_Says_So_In_Words()
	{
		string html = await RenderAsync(30, atDefault: true, inheritable: true);

		StringAssert.Contains(html, "psl psl-default");
		StringAssert.Contains(html, "the schedule&#x27;s");
		StringAssert.Contains(html, "value=\"0\"");
		Assert.IsFalse(html.Contains("psl-value", StringComparison.Ordinal), "a borrowed level is words, never a set number");
	}

	/// <summary>The number the room states, on its own stop, with none of the borrowing marks.</summary>
	[TestMethod]
	public async Task A_Stated_Value_Sits_On_Its_Own_Stop_With_No_Borrowing_Marks()
	{
		string html = await RenderAsync(30, atDefault: false, inheritable: true);

		StringAssert.Contains(html, "psl-value");
		StringAssert.Contains(html, ">30 %");
		Assert.IsFalse(html.Contains("psl-default", StringComparison.Ordinal), html);

		// 30 % is the ninth of sixteen stops, and the borrowing stop shifts every one of them along by one.
		StringAssert.Contains(html, "value=\"9\"");
		StringAssert.Contains(html, "max=\"16\"");
	}

	/// <summary>Where nothing sits above this surface there is no borrowing stop, so the ladder starts at zero.</summary>
	[TestMethod]
	public async Task Without_Anything_To_Borrow_From_The_First_Stop_Is_Position_Zero()
	{
		string html = await RenderAsync(0, atDefault: false, inheritable: false);

		StringAssert.Contains(html, "value=\"0\"");
		StringAssert.Contains(html, "max=\"15\"");
		Assert.IsFalse(html.Contains("psl-home", StringComparison.Ordinal), "there is no default cap to draw");
		Assert.IsFalse(html.Contains("psl-default", StringComparison.Ordinal), html);
	}

	/// <summary>A hand-edited number keeps a stop of its own rather than being pulled onto a neighbour.</summary>
	[TestMethod]
	public async Task An_Off_Ladder_Value_Gets_A_Stop_Of_Its_Own_And_Is_Marked_Custom()
	{
		string html = await RenderAsync(62, atDefault: false, inheritable: true);

		StringAssert.Contains(html, ">62 %");
		StringAssert.Contains(html, "psl-custom");

		// The ladder grew by the one stop 62 needed: seventeen stops plus the borrowing one.
		StringAssert.Contains(html, "max=\"17\"");
		StringAssert.Contains(html, "value=\"13\"");
	}

	/// <summary>A half percent is a number no surface can show twice: the rail rounds it as the summary does.</summary>
	[TestMethod]
	public async Task A_Half_Percent_Is_Shown_As_The_Whole_One_A_Save_Would_Keep()
	{
		string html = await RenderAsync(62.5, atDefault: false, inheritable: true);

		StringAssert.Contains(html, ">63 %");
		Assert.IsFalse(html.Contains("62.5", StringComparison.Ordinal), html);
		Assert.IsFalse(html.Contains("62,5", StringComparison.Ordinal), html);
	}

	/// <summary>The fill is a CSS length: a decimal comma is not one, and the rail would render empty.</summary>
	[TestMethod]
	public async Task The_Fill_Carries_No_Decimal_Comma_Under_A_Norwegian_Locale()
	{
		CultureInfo original = CultureInfo.CurrentCulture;

		try
		{
			CultureInfo.CurrentCulture = new CultureInfo("nb-NO");

			string html = await RenderAsync(62, atDefault: false, inheritable: true);

			StringAssert.Contains(html, "--psl-fill:");
			Assert.IsFalse(html.Contains(',', StringComparison.Ordinal), html);
		}
		finally
		{
			CultureInfo.CurrentCulture = original;
		}
	}

	/// <summary>Warmth is picked by its name, so a stop with no name is a position a thumb crosses for nothing.</summary>
	[TestMethod]
	public void Every_Warmth_Stop_Carries_Wording_And_The_Two_Silent_Ones_Are_Gone()
	{
		foreach (PresetChoice stop in Presets.ColorTempKelvin)
			StringAssert.Contains(stop.Label, " — ", $"{stop.Value} K has no wording");

		Assert.IsFalse(Presets.ColorTempKelvin.Any(stop => stop.Value is 2500 or 5000));
	}

	private static async Task<string> RenderAsync(double value, bool atDefault, bool inheritable)
	{
		ServiceCollection services = new();

		await using ServiceProvider provider = services.BuildServiceProvider();
		await using HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);

		Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
		{
			["Value"] = value,
			["AtDefault"] = atDefault,
			["Inheritable"] = inheritable,
			["DefaultLabel"] = "the schedule's",
			["Options"] = Presets.BrightnessPct,
			["Unit"] = "%"
		};

		return await renderer.Dispatcher.InvokeAsync(async () =>
		{
			HtmlRootComponent root = await renderer.RenderComponentAsync<PresetSlider>(
				ParameterView.FromDictionary(parameters)).ConfigureAwait(false);

			return root.ToHtmlString();
		}).ConfigureAwait(false);
	}
}

/// <summary>The room's levels table: headings once at the top, and nothing written by opening it.</summary>
[TestClass]
public sealed class LevelsEditorTests
{
	/// <summary>Opening a room must not rewrite a number somebody typed into the document by hand.</summary>
	[TestMethod]
	public async Task Rendering_A_Room_Leaves_An_Off_Ladder_Level_Exactly_As_It_Was()
	{
		AdaptiveLightingConfig config = AdaptiveLightingConfig.CreateDefault();
		AreaConfig room = new() { AreaId = "stue", Name = "Stue" };
		room.Levels.Add(new RoomLevelOverride
		{
			PeriodId = config.Periods[0].Key,
			BrightnessPct = 62.5,
			ColorTempKelvin = 2750
		});

		string html = await RenderAsync(config, room);

		Assert.AreEqual(1, room.Levels.Count);
		Assert.AreEqual(62.5, room.Levels[0].BrightnessPct);
		Assert.AreEqual(2750, room.Levels[0].ColorTempKelvin);

		// Shown rounded, and marked as a number the ladder does not carry.
		StringAssert.Contains(html, ">63 %");
		StringAssert.Contains(html, "2750 K");
		StringAssert.Contains(html, "psl-custom");
	}

	/// <summary>Four headings for the whole table, however many periods the schedule has.</summary>
	[TestMethod]
	public async Task The_Headings_Are_Written_Once_For_The_Table_And_Not_Once_Per_Period()
	{
		AdaptiveLightingConfig config = AdaptiveLightingConfig.CreateDefault();

		string html = await RenderAsync(config, new AreaConfig { AreaId = "stue", Name = "Stue" });

		Assert.AreEqual(4, config.Periods.Count, "the fixture is what makes one heading row worth counting");
		Assert.AreEqual(1, Count(html, "lvl-head "));
		Assert.AreEqual(4, Count(html, "lvl-head-cell"));
	}

	/// <summary>One Test button per period row, and the schedule here has four.</summary>
	[TestMethod]
	public async Task Every_Period_Row_Carries_Its_Own_Test_Button()
	{
		AdaptiveLightingConfig config = AdaptiveLightingConfig.CreateDefault();

		string html = await RenderAsync(config, new AreaConfig { AreaId = "stue", Name = "Stue" });

		Assert.AreEqual(4, Count(html, "class=\"lvl-test\""));
		Assert.IsFalse(html.Contains("disabled", StringComparison.Ordinal), "nothing is refusing, so nothing is dimmed");
	}

	/// <summary>A dimmed row of buttons explains nothing on its own, and a phone has no hover to find a reason with.</summary>
	[TestMethod]
	public async Task A_Room_That_Cannot_Be_Commanded_Says_So_In_Words_And_Closes_Every_Button()
	{
		AdaptiveLightingConfig config = AdaptiveLightingConfig.CreateDefault();

		string html = await RenderAsync(config, new AreaConfig { AreaId = "stue", Name = "Stue" },
			extra: new() { ["TestRefusal"] = "The master switch is on, so nothing may command a light." });

		StringAssert.Contains(html, "lvl-test-off");
		StringAssert.Contains(html, "The master switch is on");
		Assert.AreEqual(4, Count(html, "disabled"), "one per row, and no way to press past the reason");
	}

	/// <summary>Real lights are changing in a real room, so the page has to say so and say it ends on its own.</summary>
	[TestMethod]
	public async Task A_Running_Test_Names_Itself_And_Counts_Its_Own_Seconds_Down()
	{
		AdaptiveLightingConfig config = AdaptiveLightingConfig.CreateDefault();

		string html = await RenderAsync(config, new AreaConfig { AreaId = "stue", Name = "Stue" },
			extra: new()
			{
				["TestingPeriodId"] = config.Periods[1].Key,
				["TestSecondsLeft"] = 7
			});

		StringAssert.Contains(html, "lvl-test-live");
		StringAssert.Contains(html, "go back to normal on their own");
		Assert.AreEqual(1, Count(html, "lvl-test-on"), "one row is running, and only that row says so");
		StringAssert.Contains(html, ">7 s<");

		// The other three stay live: pressing one moves the test rather than queuing a second.
		Assert.AreEqual(1, Count(html, "disabled"));
	}

	/// <summary>The revert button is gone: the leftmost stop is the way back, and two ways would be one too many.</summary>
	[TestMethod]
	public async Task A_Room_That_States_Nothing_Offers_No_Revert_Button()
	{
		AdaptiveLightingConfig config = AdaptiveLightingConfig.CreateDefault();

		string html = await RenderAsync(config, new AreaConfig { AreaId = "stue", Name = "Stue" });

		Assert.IsFalse(html.Contains("Follow the schedule", StringComparison.Ordinal), html);
		Assert.IsFalse(html.Contains("lvl-revert", StringComparison.Ordinal), html);

		// Eight rails, all of them borrowing: four periods, brightness and warmth each. The needle carries the
		// wrapper's first class, or psl-default-text on the readout doubles every count.
		Assert.AreEqual(8, Count(html, "psl psl-default"));
	}

	private static int Count(string html, string needle)
	{
		int count = 0;
		for (int at = html.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = html.IndexOf(needle, at + 1, StringComparison.Ordinal))
			count++;

		return count;
	}

	private static async Task<string> RenderAsync(
		AdaptiveLightingConfig config,
		AreaConfig room,
		Dictionary<string, object?>? extra = null)
	{
		ServiceCollection services = new();

		await using ServiceProvider provider = services.BuildServiceProvider();
		await using HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);

		Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
		{
			["Periods"] = config.Periods,
			["Room"] = room,
			["Defaults"] = config.Defaults
		};

		foreach (KeyValuePair<string, object?> pair in extra ?? [])
			parameters[pair.Key] = pair.Value;

		return await renderer.Dispatcher.InvokeAsync(async () =>
		{
			HtmlRootComponent root = await renderer.RenderComponentAsync<LevelsEditor>(
				ParameterView.FromDictionary(parameters)).ConfigureAwait(false);

			return root.ToHtmlString();
		}).ConfigureAwait(false);
	}
}
