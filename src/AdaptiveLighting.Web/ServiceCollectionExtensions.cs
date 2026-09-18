using AdaptiveLighting.Hosting;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.DependencyInjection;

namespace AdaptiveLighting.Web;

/// <summary>Registers the lighting web UI's services.</summary>
public static class ServiceCollectionExtensions
{
	/// <summary>Adds the configuration store, the engine host, the area snapshot cache and the per-circuit services.</summary>
	/// <remarks>The engine's lifetime owner is registered here, not only UI helpers, and the order below matters.</remarks>
	public static IServiceCollection AddLightingWeb(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);

		// The configuration path, the store, the engine host and the last-seen cache, in the order they depend on
		// each other. Everything below is the UI's own and must come after it.
		services.AddLightingEngine();

		// Before the cache that fills it, and before ActivityLog, which reads it back once at construction.
		services.AddActivityJournal();
		services.AddSingleton<ActivityLog>();

		// Singleton so snapshots accumulate from process start, plus hosted so it subscribes once.
		services.AddSingleton<AreaSnapshotCache>();
		services.AddHostedService(provider => provider.GetRequiredService<AreaSnapshotCache>());

		// The record's other feed: the engine's own rebuilds, which never reach Home Assistant as an area event.
		services.AddHostedService<EngineNoticeRecorder>();

		// Scoped, so an edit to a shared document stays inside one circuit.
		services.AddScoped<DocumentCache>();

		// Scoped: these depend on IHaContext, which NetDaemon scopes per Blazor circuit.
		services.AddScoped<ModeService>();
		services.AddScoped<HaCatalog>();
		services.AddScoped<HomeLocation>();

		return services;
	}
}
