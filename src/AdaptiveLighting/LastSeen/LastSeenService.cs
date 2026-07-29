using System.Reactive.Concurrency;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AdaptiveLighting.LastSeen;

/// <summary>
///     Gives <see cref="LastSeenTracker"/> a lifetime and a Home Assistant connection, and answers on its behalf
///     before it has one.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a facade rather than registering the tracker directly.</b> The tracker needs
///         <see cref="IHaContext"/>, which NetDaemon scopes, but everything that will ask it questions — the
///         lighting decisions — is a singleton and must be able to hold the reference from start-up. So this
///         singleton holds one long-lived scope of its own, exactly as <c>AreaSnapshotCache</c> does, builds the
///         tracker inside it, and forwards every question. Before the host has started, and if the scope could not
///         be created at all, every answer is "unknown" — which <see cref="IEntityLastSeen"/> defines as safe.
///     </para>
///     <para>
///         The scope is an <i>async</i> one and is disposed only through <see cref="DisposeAsync"/>, because
///         NetDaemon's scoped <see cref="IHaContext"/> implements <see cref="IAsyncDisposable"/> alone and disposing
///         it synchronously throws. This class therefore deliberately does not implement <see cref="IDisposable"/>:
///         there must be no synchronous path into here.
///     </para>
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

	/// <summary>Creates the service. Nothing is loaded or sampled until <see cref="StartAsync"/>.</summary>
	/// <param name="scopeFactory">Used to hold one long-lived scope for this service's own <see cref="IHaContext"/>.</param>
	/// <param name="store">The cache files beside the configuration document.</param>
	/// <param name="options">The tracker's tuning.</param>
	/// <param name="scheduler">The tracker's only clock.</param>
	/// <param name="loggerFactory">Builds the tracker's logger as well as this one.</param>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
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

	/// <summary>The running tracker, or <c>null</c> before start-up. Exposed for diagnostics, not for wiring.</summary>
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

	/// <summary>Opens the scope and starts the tracker.</summary>
	/// <param name="cancellationToken">Unused; starting does not block.</param>
	/// <returns>A completed task.</returns>
	public Task StartAsync(CancellationToken cancellationToken)
	{
		// A cache that cannot start is a degraded engine, not a dead host: everything downstream reads "unknown"
		// and carries on exactly as it did before this module existed.
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
	/// <param name="cancellationToken">Unused.</param>
	/// <returns>A task that completes once the tracker and its scope are gone.</returns>
	public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync().ConfigureAwait(false);

	/// <summary>Disposes the tracker — which flushes — and then its scope. Safe to call more than once.</summary>
	/// <returns>A task that completes once the scope has been disposed.</returns>
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
