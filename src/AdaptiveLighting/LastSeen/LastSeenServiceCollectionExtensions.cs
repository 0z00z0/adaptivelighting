using System.Reactive.Concurrency;

using AdaptiveLighting.Hosting;

using Microsoft.Extensions.DependencyInjection;

namespace AdaptiveLighting.LastSeen;

/// <summary>
///     Registers the last-seen cache. One call, so a host adopting it changes one line.
/// </summary>
public static class LastSeenServiceCollectionExtensions
{
	/// <summary>
	///     Adds <see cref="IEntityLastSeen"/> and the hosted service that keeps it current.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>The cache files' location is derived, never configured.</b> They are written beside the
	///         configuration document, whose path <see cref="LightingConfigStore"/> has already resolved
	///         server-side — so the cache follows the document wherever a host puts it, and two houses running
	///         different document names each get their own files rather than one silently working and the other
	///         silently not. That directory is also the only one on a Home Assistant box that survives a redeploy;
	///         the deploy folder is wiped and re-copied every time, which for a cache whose entire purpose is to
	///         outlive restarts would be fatal.
	///     </para>
	///     <para>
	///         <b>This must be called after the configuration store is registered</b>, which
	///         <c>AddLightingWeb</c> does. Nothing here reads <c>IConfiguration</c>, so no credential is in reach.
	///     </para>
	/// </remarks>
	/// <param name="services">The host's services.</param>
	/// <param name="options">The tracker's tuning, or <c>null</c> for the documented defaults.</param>
	/// <returns><paramref name="services"/>, for chaining.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="services"/> is <c>null</c>.</exception>
	public static IServiceCollection AddEntityLastSeen(this IServiceCollection services, LastSeenOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(services);

		LastSeenOptions resolved = options ?? new LastSeenOptions();

		services.AddSingleton(provider => new LastSeenStore(
			provider.GetRequiredService<LightingConfigStore>().FilePath,
			provider.GetRequiredService<ILogger<LastSeenStore>>()));

		// Singleton, and registered as itself so both the interface and the hosted service resolve to one instance:
		// the record must accumulate from process start, not from the moment somebody asked a question.
		services.AddSingleton(provider => new LastSeenService(
			provider.GetRequiredService<IServiceScopeFactory>(),
			provider.GetRequiredService<LastSeenStore>(),
			resolved,
			// The engine's own scheduler when the host registers one, so tests and hosts share a clock; otherwise
			// the Rx default, which is what a timer wants and costs nothing when nobody has an opinion.
			provider.GetService<IScheduler>() ?? DefaultScheduler.Instance,
			provider.GetRequiredService<ILoggerFactory>()));

		services.AddSingleton<IEntityLastSeen>(provider => provider.GetRequiredService<LastSeenService>());
		services.AddHostedService(provider => provider.GetRequiredService<LastSeenService>());

		return services;
	}
}
