using AdaptiveLighting.Configuration;
using AdaptiveLighting.Hosting;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.Logging.Abstractions;

using NetDaemon.AppModel;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The reset sentence each house-mode card carries; under Home Assistant's authority the triggers stand down.</summary>
[TestClass]
public sealed class ModeServiceResetSummaryTests
{
	private sealed class FakeAppConfig(AdaptiveLightingConfig value) : IAppConfig<AdaptiveLightingConfig>
	{
		public AdaptiveLightingConfig Value { get; } = value;
	}

	private const string Select = "input_select.husmodus";

	private static AdaptiveLightingConfig ConfigWith(HouseModeAuthority authority) => new()
	{
		Periods = [new() { Id = "morgen-a1", Name = "Morgen", Start = "07:00", BrightnessPct = 90, ColorTempKelvin = 4500 }],
		Defaults = new AreaSettings(),
		Global = new GlobalConfig
		{
			HouseMode = new HouseModeConfig
			{
				Entity = Select,
				Authority = authority,
				Options =
				[
					new() { Value = "Normal", Kind = ModeKind.Normal },
					new()
					{
						Value = "Borte",
						Kind = ModeKind.Away,
						ResetOnPeriodStartId = "morgen-a1",
						ResetOnPresence = true,
						ResetPresenceGraceMinutes = 5
					}
				]
			}
		}
	};

	private static HouseModeOptionView Away(HouseModeAuthority authority) => Away(ConfigWith(authority));

	private static HouseModeOptionView Away(AdaptiveLightingConfig config)
	{
		FakeHaContext ha = new();
		ha.SetState(Select, "Normal", new Dictionary<string, object> { ["options"] = new[] { "Normal", "Borte" } });

		LightingEngineHost host = new(
			new LightingConfigStore(
				Path.Combine(Path.GetTempPath(), $"resetsummary-{Guid.NewGuid():N}.yaml"),
				NullLogger<LightingConfigStore>.Instance),
			NullLoggerFactory.Instance);
		ModeService service = new(
			ha,
			new FakeAppConfig(config),
			new HaCatalog(ha, new FakeHaRegistry(), NullLoggerFactory.Instance),
			host,
			NullLogger<ModeService>.Instance);

		HouseModeView view = service.GetHouseMode()!;
		return view.Options.Single(option => option.Value == "Borte");
	}

	[TestMethod]
	public void The_Engine_Deciding_Still_Describes_Both_Reset_Triggers()
	{
		string? summary = Away(HouseModeAuthority.AdaptiveLighting).ResetSummary;

		StringAssert.Contains(summary, "switches back to Normal", StringComparison.Ordinal);
		StringAssert.Contains(summary, "'Morgen' starts", StringComparison.Ordinal);
		StringAssert.Contains(summary, "on presence", StringComparison.Ordinal);
	}

	[TestMethod]
	public void Home_Assistant_Deciding_Says_The_Reset_Rules_Are_Paused()
	{
		string? summary = Away(HouseModeAuthority.HomeAssistant).ResetSummary;

		StringAssert.Contains(summary, "stays until you switch the house back yourself", StringComparison.Ordinal);
		StringAssert.Contains(summary, "paused while Home Assistant decides the mode", StringComparison.Ordinal);
		Assert.IsFalse(summary!.Contains("switches back", StringComparison.Ordinal),
			"the engine stands these triggers down, so the card must not promise a reset that cannot fire");
	}

	[TestMethod]
	public void An_Authority_Naming_No_Entity_Leaves_The_Triggers_Described_As_Live()
	{
		AdaptiveLightingConfig config = ConfigWith(HouseModeAuthority.HomeAssistant);
		config.Global.HouseMode!.Entity = "   ";

		// HomeAssistantDecides tests the trimmed EntityId, so a whitespace entity leaves the engine deciding.
		StringAssert.Contains(Away(config).ResetSummary, "switches back to Normal", StringComparison.Ordinal);
	}
}
