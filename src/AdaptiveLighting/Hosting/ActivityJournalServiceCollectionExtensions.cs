using System.Reactive.Concurrency;

using AdaptiveLighting.Engine;

using Microsoft.Extensions.DependencyInjection;

namespace AdaptiveLighting.Hosting;

/// <summary>Registers the activity record's journal, the one piece of history the web layer persists.</summary>
public static class ActivityJournalServiceCollectionExtensions
{
	/// <summary>Adds <see cref="IActivityJournalStore"/>, derived from the resolved configuration path.</summary>
	/// <remarks>Must be called after <see cref="EngineServiceCollectionExtensions.AddLightingEngine"/>, which resolves <see cref="ConfigLocation"/>.</remarks>
	public static IServiceCollection AddActivityJournal(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);

		services.AddSingleton<IActivityJournalStore>(provider => new ActivityJournalStore(
			provider.GetRequiredService<ConfigLocation>().Path,
			provider.GetRequiredService<ILoggerFactory>(),
			// The host's scheduler when it registers one, so tests and hosts share a clock.
			provider.GetService<IScheduler>() ?? DefaultScheduler.Instance));

		return services;
	}
}
