using AdaptiveLighting.LastSeen;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AdaptiveLighting.Hosting;

/// <summary>Registers the engine and the state it owns, with no dependency on the web UI.</summary>
public static class EngineServiceCollectionExtensions
{
	/// <summary>Adds the resolved configuration path, the configuration store, the engine host and the last-seen cache.</summary>
	/// <remarks>
	///     The order below is a lifetime rule, not a style: the store is built from the resolved path, and the
	///     last-seen cache derives its file names from the store's path. A host that needs the UI as well calls
	///     <c>AddLightingWeb</c>, which calls this first.
	/// </remarks>
	public static IServiceCollection AddLightingEngine(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);

		// Resolved once and then immutable, which holds the UI's write surface to one file.
		services.AddSingleton(provider => LightingConfigPath.Resolve(
			provider.GetRequiredService<IConfiguration>(),
			provider.GetRequiredService<IHostEnvironment>().ContentRootPath,
			provider.GetRequiredService<ILogger<ConfigLocation>>()));

		services.AddSingleton(provider => new LightingConfigStore(
			provider.GetRequiredService<ConfigLocation>().Path,
			provider.GetRequiredService<ILogger<LightingConfigStore>>()));

		// One engine per process, outliving every Blazor circuit and every load of the document.
		services.AddSingleton<LightingEngineHost>();

		// After the store: the last-seen cache derives its file names from the document's path.
		services.AddEntityLastSeen();

		return services;
	}
}
