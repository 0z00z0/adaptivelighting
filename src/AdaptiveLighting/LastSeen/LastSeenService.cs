using System.Reactive.Concurrency;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AdaptiveLighting.LastSeen;

/// <summary>Gives <see cref="LastSeenTracker"/> a lifetime and a Home Assistant connection, and answers before it has one.</summary>
/// <remarks>
///     The tracker needs a scoped IHaContext while its callers are singletons, so this singleton holds one long-lived
///     scope. No IDisposable: NetDaemon's scoped IHaContext is IAsyncDisposable only, and a synchronous dispose throws.
/// </remarks>
public sealed class LastSeenService : IEntityLastSeen, IHostedService, IAsyncDisposable
{
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly LastSeenStore _store;
	private readonly LastSeenOptions _options;
	private readonly IScheduler _scheduler;
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger<LastSeenService> _logger;

	private AsyncServiceScope? _scope;
	private LastSeenTracker? _tracker;

	/// <summary>Nothing is loaded or sampled until <see cref="StartAsync"/>.</summary>
	public LastSeenService(
		IServiceScopeFactory scopeFactory,
		LastSeenStore store,
		LastSeenOptions options,
		IScheduler scheduler,
		ILoggerFactory loggerFactory)
	{
		_scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
		_loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
		_logger = loggerFactory.CreateLogger<LastSeenService>();
	}

	/// <summary>The running tracker, or <c>null</c> before start-up; exposed for diagnostics only.</summary>
	public LastSeenTracker? Tracker => _tracker;

	/// <inheritdoc/>
	public DateTimeOffset? LastSeenUtc(string entityId) => _tracker?.LastSeenUtc(entityId);

	/// <inheritdoc/>
	public TimeSpan? SilenceOf(string entityId) => _tracker?.SilenceOf(entityId);

	/// <inheritdoc/>
	public bool HasBeenSilentFor(string entityId, TimeSpan threshold) => _tracker?.HasBeenSilentFor(entityId, threshold) ?? false;

	/// <inheritdoc/>
	public DateTimeOffset? HomeAssistantStartedUtc => _tracker?.HomeAssistantStartedUtc;

	/// <inheritdoc/>
	public int TrackedCount => _tracker?.TrackedCount ?? 0;

	/// <summary>Opens the scope and starts the tracker; never throws, and a failure leaves every answer unknown.</summary>
	public Task StartAsync(CancellationToken cancellationToken)
	{
		try
		{
			AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
			_scope = scope;

			LastSeenTracker tracker = new(
				scope.ServiceProvider.GetRequiredService<IHaContext>(),
				_scheduler,
				_store,
				_options,
				_loggerFactory.CreateLogger<LastSeenTracker>());

			tracker.Start();
			_tracker = tracker;

			_logger.LogInformation(
				"Tracking when Home Assistant entities were last heard from; a census every {Census}s, written to {Directory} every {Flush} minutes.",
				_options.CensusInterval.TotalSeconds, _store.DirectoryPath, _options.FlushInterval.TotalMinutes);
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			_logger.LogError(exception, "Could not start last-seen tracking. Every entity will read as unknown, which is the safe answer.");
		}

		return Task.CompletedTask;
	}

	/// <summary>Stops the tracker, writing whatever has changed on the way out.</summary>
	public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync().ConfigureAwait(false);

	/// <summary>Disposes the tracker, which flushes, and then its scope; safe to call more than once.</summary>
	public async ValueTask DisposeAsync()
	{
		LastSeenTracker? tracker = _tracker;
		_tracker = null;
		tracker?.Dispose();

		if (_scope is { } scope)
		{
			_scope = null;
			await scope.DisposeAsync().ConfigureAwait(false);
		}
	}
}
