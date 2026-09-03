using AdaptiveLighting.Configuration;

using Microsoft.Extensions.Configuration;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The second reader of this model: NetDaemon binds a house's app YAML with the .NET configuration binder.</summary>
/// <remarks>
///     It runs no pre-pass, so nothing repairs what it produces. A nullable list has to survive it in both
///     directions: absent stays absent, and a named list arrives whole.
/// </remarks>
[TestClass]
public sealed class ConfigurationBinderTests
{
	private static AdaptiveLightingConfig Bind(Dictionary<string, string?> settings)
	{
		AdaptiveLightingConfig config = new();
		new ConfigurationBuilder().AddInMemoryCollection(settings).Build().Bind(config);

		return config;
	}

	[TestMethod]
	public void AnAbsentStartsOnMotionAreas_BindsToNull_AndNotToAnEmptyList()
	{
		AdaptiveLightingConfig config = Bind(
			new Dictionary<string, string?>
			{
				["Periods:0:Name"] = "morning",
				["Periods:0:Start"] = "06:30",
				["Periods:0:StartsOnMotion"] = "true"
			});

		Assert.IsNull(config.Periods.Single().StartsOnMotionAreas,
			"the binder must leave a key the file never had alone, as it does for a room's entity lists");
	}

	[TestMethod]
	public void ANamedStartsOnMotionAreas_BindsToTheRoomsItNames()
	{
		AdaptiveLightingConfig config = Bind(
			new Dictionary<string, string?>
			{
				["Periods:0:Name"] = "morning",
				["Periods:0:Start"] = "06:30",
				["Periods:0:StartsOnMotion"] = "true",
				["Periods:0:StartsOnMotionAreas:0"] = "kjokken",
				["Periods:0:StartsOnMotionAreas:1"] = "gang"
			});

		CollectionAssert.AreEqual(
			new[] { "kjokken", "gang" }, config.Periods.Single().StartsOnMotionAreas,
			"a house that names its rooms in app YAML gets them, in order");
	}
}
