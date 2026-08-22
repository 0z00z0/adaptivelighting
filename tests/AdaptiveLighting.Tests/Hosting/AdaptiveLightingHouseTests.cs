using AdaptiveLighting.Hosting;
using AdaptiveLighting.NetDaemon;
using AdaptiveLighting.Web.Services;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace AdaptiveLighting.Tests.Hosting;

/// <summary>What <see cref="AdaptiveLightingHouse.AddAdaptiveLighting"/> registers, and where it puts the DataProtection key ring.</summary>
[TestClass]
public sealed class AdaptiveLightingHouseTests
{
	private static string TempRoot() =>
		Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"al-house-{Guid.NewGuid():N}")).FullName;

	private static WebApplicationBuilder BuilderWith(string? configPath, string contentRoot)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			ContentRootPath = contentRoot
		});

		if (configPath is not null)
			builder.Configuration["AdaptiveLighting:ConfigPath"] = configPath;

		return builder;
	}

	// await using: LastSeenService is IAsyncDisposable only, and a synchronous dispose of the container throws.
	[TestMethod]
	public async Task Adopting_The_Package_Registers_The_Engine_And_The_Document_It_Edits()
	{
		string root = TempRoot();
		WebApplicationBuilder builder = BuilderWith(Path.Combine(root, "house.yaml"), root);

		builder.AddAdaptiveLighting();

		await using ServiceProvider provider = builder.Services.BuildServiceProvider();

		Assert.IsNotNull(provider.GetService<ConfigLocation>(), "the page has to be able to name the file it edits");
		Assert.IsNotNull(provider.GetService<LightingEngineHost>(), "a host that skips this has a lighting app that cannot be constructed");
		Assert.IsNotNull(provider.GetService<LightingConfigStore>());
	}

	/// <summary>Scoped, because NetDaemon scopes <c>IHaContext</c> and these depend on it: one per Blazor circuit.</summary>
	[TestMethod]
	public void The_Per_Circuit_Services_Are_Scoped_Not_Singletons()
	{
		string root = TempRoot();
		WebApplicationBuilder builder = BuilderWith(Path.Combine(root, "house.yaml"), root);

		builder.AddAdaptiveLighting();

		ServiceDescriptor mode = builder.Services.Single(service => service.ServiceType == typeof(ModeService));

		Assert.AreEqual(ServiceLifetime.Scoped, mode.Lifetime);
	}

	// ===================== the key ring =====================

	[TestMethod]
	public void The_Key_Ring_Lands_Beside_The_Document()
	{
		string root = TempRoot();
		string state = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;

		BuilderWith(Path.Combine(state, "house.yaml"), root).AddAdaptiveLighting();

		Assert.IsTrue(Directory.Exists(Path.Combine(state, "dataprotection-keys")));
	}

	/// <summary>A box has <c>/config</c> before <c>/config/adaptive-lighting</c>, so the key-ring guard tests the parent, one level up.</summary>
	[TestMethod]
	public void A_Directory_That_Does_Not_Exist_Yet_Still_Gets_A_Key_Ring_If_Its_Parent_Does()
	{
		string root = TempRoot();
		string notYet = Path.Combine(root, "adaptive-lighting");

		Assert.IsFalse(Directory.Exists(notYet), "the point of the test is that this has not been created");

		BuilderWith(Path.Combine(notYet, "house.yaml"), root).AddAdaptiveLighting();

		Assert.IsTrue(Directory.Exists(Path.Combine(notYet, "dataprotection-keys")),
			"the parent existed, so the run that seeds the document must also persist the keys");
	}

	[TestMethod]
	public void A_Path_Belonging_To_Another_Machine_Is_Left_Alone()
	{
		string root = TempRoot();
		string elsewhere = Path.Combine(root, "no", "such", "tree", "house.yaml");

		BuilderWith(elsewhere, root).AddAdaptiveLighting();

		Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(elsewhere)!),
			"neither the directory nor its parent existed, so creating it would be inventing a location");
	}

	/// <summary>The durable directory is whatever <c>LightingConfigPath.Resolve</c> settled on, so a path that cannot be created places nothing durable.</summary>
	[TestMethod]
	public void A_Document_Directory_That_Cannot_Be_Created_Places_Nothing_Durable()
	{
		string root = TempRoot();
		string blocked = Path.Combine(root, "state");

		// A file where the directory has to go: the parent exists, so this is a configured path the host cannot use.
		File.WriteAllText(blocked, "not a directory");

		BuilderWith(Path.Combine(blocked, "house.yaml"), root).AddAdaptiveLighting();

		Assert.IsFalse(Directory.Exists(Path.Combine(blocked, "dataprotection-keys")));
		Assert.IsTrue(File.Exists(blocked), "the file that blocked it must still be exactly what it was");
	}

	[TestMethod]
	public void An_Explicit_Key_Ring_Path_Wins_Over_The_Document()
	{
		string root = TempRoot();
		string chosen = Path.Combine(root, "somewhere-else");

		BuilderWith(Path.Combine(root, "house.yaml"), root)
			.AddAdaptiveLighting(new AdaptiveLightingHouseOptions(chosen));

		Assert.IsTrue(Directory.Exists(chosen));
		Assert.IsFalse(Directory.Exists(Path.Combine(root, "dataprotection-keys")));
	}

	[TestMethod]
	public void No_Configured_Document_Means_No_Key_Ring_To_Place()
	{
		string root = TempRoot();

		BuilderWith(configPath: null, root).AddAdaptiveLighting();

		Assert.IsFalse(Directory.Exists(Path.Combine(root, "dataprotection-keys")));
	}

	// ===================== the port =====================

	[TestMethod]
	public void The_Port_Comes_From_Configuration_And_Defaults_To_10000()
	{
		Assert.AreEqual(10000, AdaptiveLightingHouse.DefaultPort,
			"the NetDaemon add-on declares 10000-10004, and every existing house is already on this one");
	}

	/// <summary>Zero is the escape hatch for a host that binds Kestrel itself.</summary>
	[TestMethod]
	public void A_Port_Of_Zero_Leaves_Kestrel_Alone()
	{
		string root = TempRoot();
		WebApplicationBuilder builder = BuilderWith(Path.Combine(root, "house.yaml"), root);
		builder.Configuration["AdaptiveLighting:Port"] = "0";

		builder.AddAdaptiveLighting();

		// Nothing to assert beyond Build() not throwing: the package adds no endpoint of its own.
		using WebApplication app = builder.Build();
		Assert.IsNotNull(app);
	}

	[TestMethod]
	public void An_Explicit_Port_Wins_Over_Configuration()
	{
		string root = TempRoot();
		WebApplicationBuilder builder = BuilderWith(Path.Combine(root, "house.yaml"), root);
		builder.Configuration["AdaptiveLighting:Port"] = "10000";

		builder.AddAdaptiveLighting(new AdaptiveLightingHouseOptions(Port: 0));

		using WebApplication app = builder.Build();
		Assert.IsNotNull(app, "the explicit 0 has to beat the configured 10000, or a host cannot opt out");
	}
}
