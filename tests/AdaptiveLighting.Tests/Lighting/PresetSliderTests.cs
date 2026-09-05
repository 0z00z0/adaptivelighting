using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Components;
using AdaptiveLighting.Web.Services;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

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

		// The value group still exists (a drag back out of the pocket needs it live), but it stays hidden.
		StringAssert.Contains(html, "psl-value-group\" hidden");
	}

	/// <summary>The number the room states, on its own stop, with none of the borrowing marks.</summary>
	[TestMethod]
	public async Task A_Stated_Value_Sits_On_Its_Own_Stop_With_No_Borrowing_Marks()
	{
		string html = await RenderAsync(30, atDefault: false, inheritable: true);

		StringAssert.Contains(html, "psl-value");
		StringAssert.Contains(html, ">30 %");

		// The wrapper carries no psl-default class; the hidden borrow group's own psl-default-text span is a
		// different thing and stays out of view.
		Assert.IsFalse(html.Contains("psl psl-default", StringComparison.Ordinal), html);
		StringAssert.Contains(html, "psl-borrow-group\" hidden");

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

			// Scoped to the style attribute alone: data-psl-readouts is JSON, and a comma there is syntax, not a
			// locale slip.
			string style = StyleOf(html);
			StringAssert.Contains(style, "--psl-pocket:");
			Assert.IsFalse(style.Contains(',', StringComparison.Ordinal), style);
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

	/// <summary>The house default has to read as outside the 0-100 rail, not as its dimmest stop, on an
	/// inheritable slider: a fixed pocket zone and the seam that separates it from the real range.</summary>
	[TestMethod]
	public async Task An_Inheritable_Rail_Draws_A_Pocket_Separate_From_The_Real_Range()
	{
		string html = await RenderAsync(30, atDefault: false, inheritable: true);

		StringAssert.Contains(html, "psl-inheritable");
		StringAssert.Contains(html, "--psl-pocket:");
	}

	/// <summary>Nothing to borrow means nothing to separate from: no pocket is drawn at all.</summary>
	[TestMethod]
	public async Task A_Rail_With_Nothing_To_Borrow_Draws_No_Pocket()
	{
		string html = await RenderAsync(30, atDefault: false, inheritable: false);

		Assert.IsFalse(html.Contains("psl-inheritable", StringComparison.Ordinal), html);
		Assert.IsFalse(html.Contains("--psl-pocket:", StringComparison.Ordinal), html);
	}

	/// <summary>The pocket position (0) is marked as borrowed in the live-readout data, with the words the
	/// borrowed-state markup shows, so JS can restore that exact text while dragging back into the pocket.</summary>
	[TestMethod]
	public async Task Live_Readout_Data_Marks_The_Pocket_Position_As_Borrowed()
	{
		string html = await RenderAsync(63, atDefault: true, inheritable: true);

		JsonElement[] readouts = ReadoutsOf(html);

		Assert.AreEqual(true, readouts[0].GetProperty("d").GetBoolean());
		Assert.AreEqual("63 %", readouts[0].GetProperty("t").GetString());
	}

	/// <summary>Every real stop's live-readout entry carries the same words the static markup would show if that
	/// position were the current one — the whole point of shipping the data client-side.</summary>
	[TestMethod]
	public async Task Live_Readout_Data_Covers_Every_Named_Stop_In_Order()
	{
		string html = await RenderAsync(30, atDefault: false, inheritable: true);

		JsonElement[] readouts = ReadoutsOf(html);

		// One pocket entry plus the sixteen named brightness stops.
		Assert.AreEqual(17, readouts.Length);
		Assert.AreEqual("0 %", readouts[1].GetProperty("t").GetString());
		Assert.AreEqual("100 %", readouts[16].GetProperty("t").GetString());
		Assert.IsTrue(readouts.Skip(1).All(readout => !readout.GetProperty("d").GetBoolean()));
	}

	/// <summary>An off-ladder value's own inserted stop is the one live-readout entry marked custom, and only
	/// that one — dragging away from it always lands on a named stop.</summary>
	[TestMethod]
	public async Task Live_Readout_Data_Marks_Only_The_Off_Ladder_Position_As_Custom()
	{
		string html = await RenderAsync(62, atDefault: false, inheritable: true);

		JsonElement[] readouts = ReadoutsOf(html);

		Assert.AreEqual(1, readouts.Count(readout => readout.GetProperty("c").GetBoolean()));
		Assert.AreEqual("62 %", readouts[13].GetProperty("t").GetString());
		Assert.IsTrue(readouts[13].GetProperty("c").GetBoolean());
	}

	/// <summary>Brightness alone carries the fine-adjust satellite; it renders hidden until a hold reveals it.</summary>
	[TestMethod]
	public async Task FineAdjustable_Renders_A_Satellite_Handle_Hidden_By_Default()
	{
		string html = await RenderAsync(30, atDefault: false, inheritable: true, fineAdjustable: true);

		StringAssert.Contains(html, "psl-satellite");
		StringAssert.Contains(html, "hidden");
	}

	/// <summary>Colour temperature (and any other non-brightness rail) gets no satellite at all.</summary>
	[TestMethod]
	public async Task Without_FineAdjustable_No_Satellite_Handle_Is_Rendered()
	{
		string html = await RenderAsync(30, atDefault: false, inheritable: true, fineAdjustable: false);

		Assert.IsFalse(html.Contains("psl-satellite", StringComparison.Ordinal), html);
	}

	/// <summary>
	///     A press-and-hold on a rail sitting in the pocket must not arm the fine handle. Arming hands pointer
	///     capture to the satellite, and from that moment the coarse rail cannot move at all — so a drag out of
	///     the pocket, or back into it, is swallowed by a handle whose nudges clamp at 0 % and can never mean
	///     "borrow".
	/// </summary>
	[TestMethod]
	public async Task A_Rail_Docked_In_The_Pocket_Offers_No_Fine_Handle()
	{
		string borrowing = await RenderAsync(30, atDefault: true, inheritable: true, fineAdjustable: true);
		string stated = await RenderAsync(30, atDefault: false, inheritable: true, fineAdjustable: true);

		Assert.IsFalse(borrowing.Contains("data-psl-fine", StringComparison.Ordinal), borrowing);
		StringAssert.Contains(stated, "data-psl-fine");
	}

	/// <summary>
	///     The number follows the rail in the markup and shares its line, so a phone reads left to right across one
	///     line instead of down four. Where a settings column is too narrow for both, the stylesheet reorders it
	///     back above the rail; the markup order is what a screen reader gets either way.
	/// </summary>
	[TestMethod]
	public async Task The_Readout_Follows_The_Rail_And_Has_No_Line_Of_Its_Own()
	{
		string html = await RenderAsync(30, atDefault: false, inheritable: true);

		int rail = html.IndexOf("psl-range", StringComparison.Ordinal);
		int read = html.IndexOf("psl-read", StringComparison.Ordinal);

		Assert.IsTrue(rail >= 0 && read > rail, $"the readout is at {read} and the rail at {rail}");
		Assert.IsFalse(html.Contains("psl-head", StringComparison.Ordinal), "the head line is gone: everything is one row");
	}

	private static JsonElement[] ReadoutsOf(string html)
	{
		const string marker = "data-psl-readouts=\"";
		int start = html.IndexOf(marker, StringComparison.Ordinal);
		Assert.IsTrue(start >= 0, "data-psl-readouts attribute not found: " + html);

		start += marker.Length;
		int end = html.IndexOf('"', start);
		string encoded = html[start..end];
		string json = System.Net.WebUtility.HtmlDecode(encoded);

		return JsonDocument.Parse(json).RootElement.EnumerateArray().ToArray();
	}

	private static string StyleOf(string html)
	{
		const string marker = "style=\"";
		int start = html.IndexOf(marker, StringComparison.Ordinal);
		Assert.IsTrue(start >= 0, "style attribute not found: " + html);

		start += marker.Length;
		int end = html.IndexOf('"', start);

		return html[start..end];
	}

	private static async Task<string> RenderAsync(
		double value, bool atDefault, bool inheritable, bool fineAdjustable = false)
	{
		ServiceCollection services = new();
		services.AddSingleton<IJSRuntime>(new FakeJsRuntime());
		services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

		await using ServiceProvider provider = services.BuildServiceProvider();
		await using HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);

		Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
		{
			["Value"] = value,
			["AtDefault"] = atDefault,
			["Inheritable"] = inheritable,
			["DefaultLabel"] = "the schedule's",
			["Options"] = Presets.BrightnessPct,
			["Unit"] = "%",
			["FineAdjustable"] = fineAdjustable
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

	/// <summary>
	///     A room borrowing the house value reads its number once. Both readout groups are always in the markup so
	///     the drag can swap them without a round trip, and only the <c>hidden</c> attribute separates them — which
	///     a class rule setting <c>display</c> silently beats, printing "the schedule's 100 % 100 %".
	/// </summary>
	[TestMethod]
	public void The_Stylesheet_Lets_The_Hidden_Attribute_Win()
	{
		string css = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "AdaptiveLighting.Web", "wwwroot", "app.css"));

		Assert.IsTrue(
			Regex.IsMatch(css, @"\[hidden\][^{]*\{[^}]*display\s*:\s*none\s*!important", RegexOptions.None, TimeSpan.FromSeconds(5)),
			"app.css has to neutralise a class rule that sets display on an element the components hide by attribute");
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

	private static string RepositoryRoot()
	{
		DirectoryInfo? at = new(AppContext.BaseDirectory);

		while (at is not null && !File.Exists(Path.Combine(at.FullName, "AdaptiveLighting.slnx")))
			at = at.Parent;

		Assert.IsNotNull(at, $"no AdaptiveLighting.slnx above {AppContext.BaseDirectory}");

		return at.FullName;
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
