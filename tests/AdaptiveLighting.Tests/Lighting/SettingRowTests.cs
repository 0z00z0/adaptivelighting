using System.Text.RegularExpressions;

using AdaptiveLighting.Web.Components;
using AdaptiveLighting.Web.Services;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>What a settings row paints: the name and the control, with the explanation behind the (i).</summary>
[TestClass]
public sealed class SettingRowTests
{
	private const string Help = "How long after the last movement the lights hold.";

	/// <summary>A shut row is a name and a control: the prose is not merely styled away, it is not drawn.</summary>
	[TestMethod]
	public async Task A_Shut_Row_Offers_The_Explanation_Without_Painting_It()
	{
		string html = await RenderAsync("Lights stay on for", Help);

		StringAssert.Contains(html, "info-icon");
		Assert.IsFalse(
			html.Contains("srow-help", StringComparison.Ordinal),
			"the row must paint no prose of its own; the (i) is where the help lives");
		Assert.IsFalse(
			html.Contains(Help, StringComparison.Ordinal),
			"a shut row that still carries the help has only hidden it, which is the length problem again");
	}

	/// <summary>The button names what it explains, or a screen reader hears twenty identical "More about".</summary>
	[TestMethod]
	public async Task The_Info_Button_Names_The_Setting_It_Explains()
	{
		string html = await RenderAsync("Warning dim level", Help);

		StringAssert.Contains(html, "More about Warning dim level");
	}

	[TestMethod]
	public async Task A_Row_With_Nothing_To_Explain_Draws_No_Info_Button()
	{
		string html = await RenderAsync("Warning dim level", help: null);

		Assert.IsFalse(html.Contains("info-icon", StringComparison.Ordinal), html);
	}

	/// <summary>Provenance is state and an action, not prose: hiding it behind the (i) would take away the only road back.</summary>
	[TestMethod]
	public async Task The_Road_Back_Stays_In_The_Row_Where_A_Thumb_Can_Reach_It()
	{
		string html = await RenderAsync("Lights stay on for", Help, isOwn: true, houseText: "10 min");

		StringAssert.Contains(html, "srow-revert");
		StringAssert.Contains(html, "Use house setting (10 min)");
	}

	/// <summary>Every help line reaches a reader as text, so an HTML escape in one is shown rather than decoded.</summary>
	/// <remarks>
	///     Measured: a row whose help carried the six characters of a quote escape rendered them literally on the
	///     room page. Razor hands an attribute's value through undecoded and the row then escapes the ampersand.
	/// </remarks>
	[TestMethod]
	public void No_Worded_Attribute_On_A_Page_Carries_An_Html_Escape()
	{
		string root = RepositoryRoot();
		Regex worded = new(
			"(?:Help|Title|Description|Label|EmptyLabel|AddLabel|NoneLabel|EmptyNote)=\"([^\"]*)\"",
			RegexOptions.None,
			TimeSpan.FromSeconds(5));
		Regex escape = new("&(?:[A-Za-z]{2,10}|#[0-9]{2,5});", RegexOptions.None, TimeSpan.FromSeconds(5));

		List<string> offenders = [];

		foreach (string file in Directory.EnumerateFiles(
			Path.Combine(root, "src", "AdaptiveLighting.Web"), "*.razor", SearchOption.AllDirectories))
		{
			foreach (Match match in worded.Matches(File.ReadAllText(file)))
			{
				if (escape.Match(match.Groups[1].Value) is { Success: true } found)
					offenders.Add($"{Path.GetFileName(file)}: {found.Value} in {match.Value}");
			}
		}

		Assert.AreEqual(0, offenders.Count, string.Join(Environment.NewLine, offenders));
	}

	/// <summary>The same rule for the help that is data rather than markup.</summary>
	[TestMethod]
	public void No_Setting_Declared_In_Code_Carries_An_Html_Escape()
	{
		Regex escape = new("&(?:[A-Za-z]{2,10}|#[0-9]{2,5});", RegexOptions.None, TimeSpan.FromSeconds(5));

		foreach (RoomSettingGroup group in RoomSettings.Groups)
		{
			Assert.IsFalse(escape.IsMatch(group.Title), group.Title);
			Assert.IsFalse(escape.IsMatch(group.Note), group.Note);

			foreach (RoomSetting setting in group.Settings)
			{
				Assert.IsFalse(escape.IsMatch(setting.Label), setting.Label);
				Assert.IsFalse(escape.IsMatch(setting.Help), $"{setting.Label}: {setting.Help}");
			}
		}
	}

	// Walks up from the test binary. Failing loudly beats a scan that quietly finds no files and passes.
	private static string RepositoryRoot()
	{
		DirectoryInfo? at = new(AppContext.BaseDirectory);

		while (at is not null && !File.Exists(Path.Combine(at.FullName, "AdaptiveLighting.slnx")))
			at = at.Parent;

		Assert.IsNotNull(at, $"no AdaptiveLighting.slnx above {AppContext.BaseDirectory}");

		return at.FullName;
	}

	private static async Task<string> RenderAsync(
		string label,
		string? help,
		bool isOwn = false,
		string? houseText = null)
	{
		ServiceCollection services = new();

		await using ServiceProvider provider = services.BuildServiceProvider();
		await using HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);

		Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
		{
			["Label"] = label,
			["Help"] = help,
			["IsOwn"] = isOwn,
			["HouseText"] = houseText,
			["ChildContent"] = (RenderFragment)(builder => builder.AddMarkupContent(0, "<span class=\"probe\"></span>"))
		};

		return await renderer.Dispatcher.InvokeAsync(async () =>
		{
			HtmlRootComponent root = await renderer.RenderComponentAsync<SettingRow>(
				ParameterView.FromDictionary(parameters)).ConfigureAwait(false);

			return root.ToHtmlString();
		}).ConfigureAwait(false);
	}
}
