using AdaptiveLighting.Hosting;
using AdaptiveLighting.LastSeen;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AdaptiveLighting.Web;

/// <summary>Registers the lighting web UI's services.</summary>
public static class ServiceCollectionExtensions
{
	/// <summary>
	///     Adds the configuration store, the engine host, the area snapshot cache and the per-circuit services.
	/// </summary>
	/// <remarks>
	///     Registers the engine's lifetime owner, not only UI helpers: a host that skips this call has a lighting app
	///     whose constructor cannot be satisfied. The registration order below is significant.
	/// </remarks>
	public static IServiceCollection AddLightingWeb(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);

		// Resolved once and then immutable, which is what holds the UI's write surface to one file. Registered as its
		// own singleton too, so the Configuration page can name the file it is editing.
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

		// Before the cache that fills it.
		services.AddSingleton<ActivityLog>();

		// Singleton so snapshots accumulate from process start, plus hosted so it subscribes once.
		services.AddSingleton<AreaSnapshotCache>();
		services.AddHostedService(provider => provider.GetRequiredService<AreaSnapshotCache>());

		// Scoped: these depend on IHaContext, which NetDaemon scopes. One per Blazor circuit.
		services.AddScoped<ModeService>();
		services.AddScoped<HaCatalog>();
		services.AddScoped<HomeLocation>();

		return services;
	}
}
