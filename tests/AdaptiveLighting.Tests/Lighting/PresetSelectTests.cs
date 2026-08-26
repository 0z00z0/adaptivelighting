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

/// <summary>The preset dropdown rendered statically, so what the markup says is what a reader sees.</summary>
[TestClass]
public sealed class PresetSelectTests
{
	[TestMethod]
	public async Task A_Listed_Value_Is_Offered_By_Its_Own_Label_And_Adds_No_Custom_Entry()
	{
		string html = await RenderAsync(60);

		StringAssert.Contains(html, "60 %");
		Assert.IsFalse(html.Contains("custom", StringComparison.Ordinal), "60 is on the list");
	}

	/// <summary>Half a percent is a number no surface can show twice: the control rounds it as the summary does.</summary>
	[TestMethod]
	public async Task A_Half_Percent_Is_Shown_As_The_Whole_One_A_Save_Would_Keep()
	{
		string html = await RenderAsync(62.5);

		// The dash is encoded on its way into the markup, so the two ends of the label are asserted apart.
		StringAssert.Contains(html, ">63 %");
		StringAssert.Contains(html, "custom");
		Assert.IsFalse(html.Contains("62.5", StringComparison.Ordinal), html);
		Assert.IsFalse(html.Contains("62,5", StringComparison.Ordinal), html);
	}

	/// <summary>A fraction that rounds onto a preset is that preset, not a second entry beside it.</summary>
	[TestMethod]
	public async Task A_Fraction_Rounding_Onto_A_Preset_Grows_No_Duplicate_Row()
	{
		string html = await RenderAsync(59.6);

		Assert.IsFalse(html.Contains("custom", StringComparison.Ordinal), "59.6 rounds onto the 60 % stop");
		Assert.AreEqual(Presets.BrightnessPct.Count, Count(html, "<option"));
	}

	[TestMethod]
	public async Task The_Value_The_Select_Is_Set_To_Is_One_Of_The_Options_It_Offers()
	{
		// A select whose bound value matches no option renders blank, which is how a decimal comma shows up.
		foreach (double value in new[] { 0, 62.5, 59.6, 100 })
		{
			string html = await RenderAsync(value);
			string key = ConfigNormalizer.Whole(value).ToString("0", CultureInfo.InvariantCulture);

			StringAssert.Contains(html, $"value=\"{key}\"", $"{value} is bound to a value no option carries");
		}
	}

	[TestMethod]
	public async Task Picking_A_Row_Never_Writes_Back_A_Fraction()
	{
		CultureInfo original = CultureInfo.CurrentCulture;

		try
		{
			CultureInfo.CurrentCulture = new CultureInfo("nb-NO");

			string html = await RenderAsync(62.5);

			Assert.IsFalse(html.Contains(',', StringComparison.Ordinal), html);
		}
		finally
		{
			CultureInfo.CurrentCulture = original;
		}
	}

	/// <summary>The stored number, the control and the collapsed summary are one number after a save, not three.</summary>
	[TestMethod]
	public async Task After_A_Save_The_Stored_Number_And_Both_Shown_Numbers_Agree()
	{
		AdaptiveLightingConfig config = AdaptiveLightingConfig.CreateDefault();
		config.Periods[0].BrightnessPct = 62.5;

		ConfigNormalizer.Normalize(config);

		TimePeriodConfig period = config.Periods[0];
		string stored = period.BrightnessPct.ToString("0.##", CultureInfo.InvariantCulture);
		string html = await RenderAsync(period.BrightnessPct);

		Assert.AreEqual("63", stored);
		StringAssert.Contains(TokenFormat.PeriodLevel(period), $"{stored}%");
		StringAssert.Contains(html, $"{stored} %");
	}

	private static int Count(string html, string needle)
	{
		int count = 0;
		for (int at = html.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = html.IndexOf(needle, at + 1, StringComparison.Ordinal))
			count++;

		return count;
	}

	private static async Task<string> RenderAsync(double value)
	{
		ServiceCollection services = new();

		await using ServiceProvider provider = services.BuildServiceProvider();
		await using HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);

		Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
		{
			["Value"] = value,
			["Options"] = Presets.BrightnessPct,
			["Unit"] = "%"
		};

		return await renderer.Dispatcher.InvokeAsync(async () =>
		{
			HtmlRootComponent root = await renderer.RenderComponentAsync<PresetSelect>(
				ParameterView.FromDictionary(parameters)).ConfigureAwait(false);

			return root.ToHtmlString();
		}).ConfigureAwait(false);
	}
}
