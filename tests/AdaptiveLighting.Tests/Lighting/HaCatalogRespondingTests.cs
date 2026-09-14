using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The one answer both pages give to whether Home Assistant is responding.</summary>
[TestClass]
public sealed class HaCatalogRespondingTests
{
	[TestMethod]
	public void A_House_That_Returns_Only_A_Switch_Is_Responding()
	{
		FakeHaContext ha = new();
		ha.SetState("switch.test_switch", "off", new() { ["friendly_name"] = "Test switch" });

		HaCatalog catalog = new(ha, new FakeHaRegistry(), NullLoggerFactory.Instance);

		Assert.IsTrue(catalog.IsHomeAssistantResponding(new GlobalConfig()),
			"a switch came back with no areas, so the room page must not say it is waiting for Home Assistant");
	}

	[TestMethod]
	public void A_House_That_Returns_Nothing_Is_Not_Responding()
	{
		HaCatalog catalog = new(new FakeHaContext(), new FakeHaRegistry(), NullLoggerFactory.Instance);

		Assert.IsFalse(catalog.IsHomeAssistantResponding(new GlobalConfig()),
			"an empty answer is indistinguishable from Home Assistant not being there yet");
	}
}
