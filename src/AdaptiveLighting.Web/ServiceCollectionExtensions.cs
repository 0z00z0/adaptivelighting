using AdaptiveLighting.Hosting;
using AdaptiveLighting.LastSeen;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AdaptiveLighting.Web;

/// <summary>
///     Registers the lighting web UI's services. One call, so a host's <c>program.cs</c> stays a copy job.
/// </summary>
public static class ServiceCollectionExtensions
{
	/// <summary>
	///     Adds the configuration store, the engine host, the area snapshot cache and the per-circuit services.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>This now registers the engine's lifetime owner, not just UI helpers.</b>
	///         <see cref="LightingEngineHost"/> is a singleton here because both the per-host
	///         <c>[NetDaemonApp]</c> bootstrap and the Configuration page must reach the same one: the bootstrap
	///         hands it Home Assistant, the page tells it a new document is on disk. That makes this call
	///         load-bearing for the engine itself — a host that does not make it has a lighting app whose
	///         constructor cannot be satisfied. Both hosts make it, and the name is now a slight lie: it is no
	///         longer only the web.
	///     </para>
	///     <para>
	///         <b>The file path is resolved here, once, from the host's own configuration.</b> Nothing
	///         downstream can change it, which is what keeps the UI's write surface to exactly one file. The
	///         root <see cref="IConfiguration"/> — which carries the Home Assistant token — is read only by
	///         <see cref="LightingConfigPath"/>, and only for the two keys it names.
	///     </para>
	/// </remarks>
	/// <param name="services">The host's services.</param>
	/// <returns><paramref name="services"/>, for chaining.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="services"/> is <c>null</c>.</exception>
	public static IServiceCollection AddLightingWeb(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);

		// Resolved once, here, and then immutable. Registered as its own singleton as well as being handed to
		// the store, because the Configuration page has to be able to tell the operator which file it is
		// editing and whether a deploy will destroy it.
		services.AddSingleton(provider => LightingConfigPath.Resolve(
			provider.GetRequiredService<IConfiguration>(),
			provider.GetRequiredService<IHostEnvironment>().ContentRootPath,
			provider.GetRequiredService<ILogger<ConfigLocation>>()));

		services.AddSingleton(provider => new LightingConfigStore(
			provider.GetRequiredService<ConfigLocation>().Path,
			provider.GetRequiredService<ILogger<LightingConfigStore>>()));

		// Singleton: there is one engine per process, and it must outlive both any Blazor circuit and any
		// single load of the configuration document.
		services.AddSingleton<LightingEngineHost>();

		// After the store, because the last-seen cache derives its own file names from the document's path. Nothing
		// consumes IEntityLastSeen yet; the cache has to be running and accumulating history before anything can,
		// which is exactly why it is registered on its own rather than alongside a first consumer.
		services.AddEntityLastSeen();

		// Singleton, and registered before the cache that fills it: the activity page's history is the same
		// stream the cards come from, kept in order and bounded, and it must survive every browser circuit.
		services.AddSingleton<ActivityLog>();

		// Singleton: the cache must accumulate area snapshots from process start, not from the moment a
		// browser connected. Also registered as a hosted service so it subscribes exactly once.
		services.AddSingleton<AreaSnapshotCache>();
		services.AddHostedService(provider => provider.GetRequiredService<AreaSnapshotCache>());

		// Scoped: these depend on IHaContext, which NetDaemon scopes. One per Blazor circuit is correct.
		services.AddScoped<ModeService>();
		services.AddScoped<HaCatalog>();
		services.AddScoped<HomeLocation>();

		return services;
	}
}
