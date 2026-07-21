using System.Collections.Concurrent;
using System.Reactive.Subjects;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Ha;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     The last <see cref="ZoneSnapshot"/> seen for each zone, kept for the life of the process and pushed to
///     whoever is watching. This is the dashboard's only source of engine state.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why the event bus rather than the engine.</b> The obvious wiring — register this class as an
///         <see cref="IStatePublisher"/> and have the engine call it directly — would mean editing
///         <c>AdaptiveLightingApp</c> to compose a second publisher. The UI is not allowed to reshape the
///         engine to suit itself, so instead this listens to the <c>laget_lighting_zone</c> HA event that
///         <see cref="HaStatePublisher"/> already emits on every transition. That event <i>is</i> the engine's
///         published observability seam; consuming it costs the engine nothing and touches none of its code.
///     </para>
///     <para>
///         The price is honest and worth stating: snapshots make a round trip through Home Assistant, so the
///         dashboard shows nothing at all without a live HA connection, and nothing until the first zone
///         transition after start-up. A direct in-process publisher would be tighter, and is the natural
///         thing to do if the engine's bootstrap is ever opened for other reasons.
///     </para>
///     <para>
///         This is a singleton with its own DI scope rather than a per-circuit subscription: the cache must
///         accumulate from process start, not from the moment somebody opened a browser tab.
///     </para>
/// </remarks>
public sealed class ZoneSnapshotCache : IHostedService, IDisposable
{
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly ILogger<ZoneSnapshotCache> _logger;
	private readonly ConcurrentDictionary<string, ZoneSnapshot> _snapshots = new(StringComparer.Ordinal);
	private readonly Subject<ZoneSnapshot> _changes = new();

	private IServiceScope? _scope;
	private IDisposable? _subscription;

	/// <summary>Creates the cache. Nothing is subscribed until <see cref="StartAsync"/>.</summary>
	/// <param name="scopeFactory">Used to hold one long-lived scope for the cache's own <see cref="IHaContext"/>.</param>
	/// <param name="logger">Where subscription failures go.</param>
	public ZoneSnapshotCache(IServiceScopeFactory scopeFactory, ILogger<ZoneSnapshotCache> logger)
	{
		_scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>Every zone the cache has heard from, newest state per zone, ordered by name.</summary>
	public IReadOnlyList<ZoneSnapshot> Snapshots =>
		[.. _snapshots.Values.OrderBy(snapshot => snapshot.ZoneName, StringComparer.CurrentCulture)];

	/// <summary>Pushes each snapshot as it arrives, so a component can re-render without polling.</summary>
	public IObservable<ZoneSnapshot> Changes => _changes;

	/// <summary>Whether any snapshot has arrived yet. Drives the dashboard's empty state.</summary>
	public bool HasData => !_snapshots.IsEmpty;

	/// <summary>Subscribes to the engine's zone events.</summary>
	/// <param name="cancellationToken">Unused; subscribing does not block.</param>
	/// <returns>A completed task.</returns>
	public Task StartAsync(CancellationToken cancellationToken)
	{
		// A web UI that cannot subscribe is a degraded UI, not a dead host: the NetDaemon side of this
		// process must keep running whatever happens here.
		try
		{
			_scope = _scopeFactory.CreateScope();
			IHaContext ha = _scope.ServiceProvider.GetRequiredService<IHaContext>();

			_subscription = ha.Events
				.Filter<ZoneSnapshotEvent>(HaStatePublisher.EventType)
				.SubscribeSafe(Record, _logger);

			_logger.LogInformation(
				"Watching Home Assistant for {EventType} events; the dashboard fills in as zones report.",
				HaStatePublisher.EventType);
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Could not subscribe to zone events. The dashboard will stay empty.");
		}

		return Task.CompletedTask;
	}

	/// <summary>Stops watching.</summary>
	/// <param name="cancellationToken">Unused.</param>
	/// <returns>A completed task.</returns>
	public Task StopAsync(CancellationToken cancellationToken)
	{
		Dispose();
		return Task.CompletedTask;
	}

	/// <summary>Drops the subscription and its scope.</summary>
	public void Dispose()
	{
		_subscription?.Dispose();
		_subscription = null;
		_scope?.Dispose();
		_scope = null;
		_changes.Dispose();
	}

	private void Record(Event<ZoneSnapshotEvent> @event)
	{
		ZoneSnapshot? snapshot = @event.Data?.ToSnapshot();
		if (snapshot is null)
			return;

		_snapshots[snapshot.ZoneName] = snapshot;
		_changes.OnNext(snapshot);
	}
}
