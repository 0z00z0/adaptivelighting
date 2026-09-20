using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Components;
using AdaptiveLighting.Web.Presentation;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>What the all-settings panel draws, and that it draws it from what it was handed.</summary>
/// <remarks>
///     No document, no store and no engine appear here. That is the point of the test: a second design mounts
///     this component against a prepared input and nothing else.
/// </remarks>
[TestClass]
public sealed class AllSettingsPanelTests
{
	[TestMethod]
	public async Task A_Shut_Fold_Draws_Its_Title_And_None_Of_Its_Settings()
	{
		string html = await RenderAsync(Fold(open: false, Stepper()));

		StringAssert.Contains(html, "Movement &amp; timing");
		Assert.IsFalse(html.Contains("Lights stay on for", StringComparison.Ordinal), html);
	}

	[TestMethod]
	public async Task An_Open_Fold_Draws_Every_Control_Kind_From_The_Input()
	{
		string html = await RenderAsync(Fold(
			open: true,
			Stepper(),
			Steps(),
			Flag(on: true),
			Choice()));

		StringAssert.Contains(html, "class=\"steps\"", "a steps row draws its ladder");
		StringAssert.Contains(html, "aria-checked=\"true\"", "a flag row reads its state from the input");
		StringAssert.Contains(html, "class=\"seg\"", "a choice row draws its segments");
		StringAssert.Contains(html, "10 min", "a stepper row draws the wording it was given");
	}

	/// <summary>The count badge is the room's, and a panel told nothing about a room shows none.</summary>
	[TestMethod]
	public async Task A_Fold_With_No_Settings_Of_Its_Own_Draws_No_Count()
	{
		string html = await RenderAsync(Fold(open: true, Stepper()));

		Assert.IsFalse(html.Contains("sgroup-count", StringComparison.Ordinal), html);
	}

	[TestMethod]
	public async Task A_Setting_Stated_Here_Offers_The_Road_Back_To_The_House()
	{
		AllSettingsPanel.Item own = Stepper() with { IsOwn = true, HouseText = "15 min" };

		string html = await RenderAsync(Fold(open: true, own));

		StringAssert.Contains(html, "Use house setting (15 min)");
	}

	private static AllSettingsPanel.Item Stepper() =>
		new(new RoomSetting(
			nameof(AreaSettings.VacancyTimeoutSeconds),
			"Lights stay on for",
			"How long after the last movement the lights hold.",
			RoomControl.Minutes))
		{
			Text = "10 min",
			Number = 10
		};

	private static AllSettingsPanel.Item Steps() =>
		new(new RoomSetting(
			SleepSteps.Key,
			"While the house sleeps",
			"What this room does while the house sleeps.",
			RoomControl.Steps))
		{
			StepValue = SleepStep.Normal.ToString()
		};

	private static AllSettingsPanel.Item Flag(bool on) =>
		new(new RoomSetting(
			nameof(AreaSettings.SkipAwaySweep),
			"Stays on when the house goes away",
			"Whether leaving switches this room off.",
			RoomControl.Flag))
		{
			Flag = on
		};

	private static AllSettingsPanel.Item Choice() =>
		new(new RoomSetting(
			nameof(AreaSettings.ColorControl),
			"How warmth reaches these lights",
			"Which way the room sets its warmth.",
			RoomControl.Choice))
		{
			ChoiceValue = ColorControl.Auto.ToString()
		};

	private static AllSettingsPanel.Input Fold(bool open, params AllSettingsPanel.Item[] items) =>
		new([new AllSettingsPanel.Group(
			new RoomSettingGroup("Movement & timing", "How long the lights stay on", items.Select(item => item.Source).ToList()),
			open,
			OwnCount: 0,
			items)]);

	private static async Task<string> RenderAsync(AllSettingsPanel.Input input)
	{
		ServiceCollection services = new();

		await using ServiceProvider provider = services.BuildServiceProvider();
		await using HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);

		Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
		{
			["Settings"] = input
		};

		return await renderer.Dispatcher.InvokeAsync(async () =>
		{
			HtmlRootComponent root = await renderer
				.RenderComponentAsync<AllSettingsPanel>(ParameterView.FromDictionary(parameters))
				.ConfigureAwait(false);

			return root.ToHtmlString();
		}).ConfigureAwait(false);
	}
}
