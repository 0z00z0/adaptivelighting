using AdaptiveLighting.Web.Components;
using AdaptiveLighting.Web.Services;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NetDaemon.HassModel;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The Starts picker rendered statically through <see cref="HtmlRenderer"/>, so no event fires: the only writer is a changed control.</summary>
[TestClass]
public class PeriodStartEditorTests
{
	/// <summary>A Start the parser refuses and neither offered shape can compose.</summary>
	private const string Unreadable = "whenever the cat wakes up";

	[TestMethod]
	public async Task A_Start_Neither_Shape_Can_Express_Survives_A_Render_Untouched()
	{
		(string html, List<string> written) = await RenderAsync(Unreadable);

		Assert.AreEqual(0, written.Count, "a render must never write a Start back");
		StringAssert.Contains(html, Unreadable, "the stored Start has to stay on screen, not vanish with the text box");
	}

	[TestMethod]
	public async Task An_Unset_Start_Renders_Without_Throwing_And_Writes_Nothing()
	{
		(string html, List<string> written) = await RenderAsync("");

		Assert.AreEqual(0, written.Count);
		StringAssert.Contains(html, "No start set yet");
	}

	[TestMethod]
	public async Task A_Sun_Anchored_Start_Survives_A_Render_Untouched()
	{
		(_, List<string> written) = await RenderAsync("sunset-01:00");

		Assert.AreEqual(0, written.Count);
	}

	[TestMethod]
	public async Task The_Picker_Offers_A_Clock_Time_And_The_Sun_And_Nothing_Else()
	{
		// A clock start renders one select and one time input, so every option in the markup is a mode option.
		(string html, _) = await RenderAsync("22:30");

		StringAssert.Contains(html, "at a clock time");
		StringAssert.Contains(html, "relative to the sun");
		Assert.AreEqual(2, CountOptions(html), "the mode select offers exactly two shapes");
		Assert.IsFalse(html.Contains("as text", StringComparison.OrdinalIgnoreCase), "the text escape is gone");
	}

	private static int CountOptions(string html)
	{
		int count = 0;
		for (int at = html.IndexOf("<option", StringComparison.Ordinal); at >= 0; at = html.IndexOf("<option", at + 1, StringComparison.Ordinal))
			count++;

		return count;
	}

	private static async Task<(string Html, List<string> Written)> RenderAsync(string value)
	{
		List<string> written = [];

		ServiceCollection services = new();
		services.AddSingleton<IHaContext>(new FakeHaContext());
		services.AddSingleton<IHaRegistry>(new FakeHaRegistry());
		services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
		services.AddSingleton<HaCatalog>();

		await using ServiceProvider provider = services.BuildServiceProvider();
		await using HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);

		Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
		{
			["Value"] = value,
			["ValueChanged"] = EventCallback.Factory.Create<string>(new object(), written.Add)
		};

		return (await renderer.Dispatcher.InvokeAsync(async () =>
		{
			HtmlRootComponent root = await renderer.RenderComponentAsync<PeriodStartEditor>(
				ParameterView.FromDictionary(parameters)).ConfigureAwait(false);

			return root.ToHtmlString();
		}).ConfigureAwait(false), written);
	}
}
