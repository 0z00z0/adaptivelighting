using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Components;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace AdaptiveLighting.Tests.Lighting;

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

	/// <summary>Ticking the curve leaves nothing to aim at, so the rail goes rather than being replaced by a sentence.</summary>
	[TestMethod]
	public async Task A_Period_On_The_Daylight_Curve_Collapses_Its_Rail_And_Keeps_Its_Test_Button()
	{
		AdaptiveLightingConfig config = AdaptiveLightingConfig.CreateDefault();
		AreaConfig room = new() { AreaId = "stue", Name = "Stue" };
		room.Levels.Add(new RoomLevelOverride { PeriodId = config.Periods[0].Key, FollowDaylightCurve = true });

		string html = await RenderAsync(config, room);

		// Four periods carry a brightness rail each; the one on the curve gives its up.
		Assert.AreEqual(3, Count(html, "Brightness during"));
		Assert.IsFalse(html.Contains("lvl-inherit", StringComparison.Ordinal), "no replacement sentence either");
		Assert.AreEqual(4, Count(html, "class=\"lvl-test\""), "the Test button is not part of what collapses");
	}

	/// <summary>The name line carries the curve question, and the row says Brightness once rather than twice.</summary>
	[TestMethod]
	public async Task The_Period_Line_Carries_The_Curve_Toggle_And_The_Row_Names_Brightness_Once()
	{
		AdaptiveLightingConfig config = AdaptiveLightingConfig.CreateDefault();

		string html = await RenderAsync(config, new AreaConfig { AreaId = "stue", Name = "Stue" });

		int when = html.IndexOf("lvl-when", StringComparison.Ordinal);
		int toggle = html.IndexOf("lvl-curve-toggle", StringComparison.Ordinal);
		int cell = html.IndexOf("lvl-cell", StringComparison.Ordinal);

		Assert.IsTrue(when >= 0 && toggle > when && toggle < cell, "the toggle belongs between the period name and the first cell");
		Assert.AreEqual(0, Count(html, "lvl-cell-label\">Brightness"), "the heading row and the rail's own label already say it");
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
		services.AddSingleton<IJSRuntime>(new FakeJsRuntime());
		services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

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
