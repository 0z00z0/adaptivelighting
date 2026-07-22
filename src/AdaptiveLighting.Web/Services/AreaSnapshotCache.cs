using System.Collections.Concurrent;
using System.Reactive.Subjects;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Ha;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     The last <see cref="AreaSnapshot"/> seen for each area, kept for the life of the process and pushed to
///     whoever is watching. This is the dashboard's only source of engine state.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why the event bus rather than the engine.</b> The obvious wiring — register this class as an
///         <see cref="IStatePublisher"/> and have the engine call it directly — would mean editing
///         <c>AdaptiveLightingApp</c> to compose a second publisher. The UI is not allowed to reshape the
///         engine to suit itself, so instead this listens to the <c>adaptive_lighting_area</c> HA event that
///         <see cref="HaStatePublisher"/> already emits on every transition. That event <i>is</i> the engine's
///         published observability seam; consuming it costs the engine nothing and touches none of its code.
///     </para>
///     <para>
///         The price is honest and worth stating: snapshots make a round trip through Home Assistant, so the
///         dashboard shows nothing at all without a live HA connection, and nothing until the first area
///         transition after start-up. A direct in-process publisher would be tighter, and is the natural
///         thing to do if the engine's bootstrap is ever opened for other reasons.
///     </para>
///     <para>
///         This is a singleton with its own DI scope rather than a per-circuit subscription: the cache must
///         accumulate from process start, not from the moment somebody opened a browser tab.
///     </para>
/// </remarks>
public sealed class AreaSnapshotCache : IHostedService, IAsyncDisposable
{
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly ILogger<AreaSnapshotCache> _logger;
	private readonly ConcurrentDictionary<string, AreaSnapshot> _snapshots = new(StringComparer.Ordinal);
	private readonly Subject<AreaSnapshot> _changes = new();

	/// <summary>
	///     The cache's own scope, held for the life of the process.
	/// </summary>
	/// <remarks>
	///     An <i>async</i> scope, and disposed only through <see cref="DisposeAsync"/>, because NetDaemon's
	///     scoped <c>IHaContext</c> implements <see cref="IAsyncDisposable"/> alone. Disposing this scope
	///     synchronously throws — and because <c>StopAsync</c> runs while the host is starting up its other
	///     services, that throw surfaced as "Failed to start host" and killed the process on restart. The class
	///     therefore does not implement <see cref="IDisposable"/> at all: there must be no sync path to get here.
	/// </remarks>
	private AsyncServiceScope? _scope;
	private IDisposable? _subscription;

	/// <summary>Creates the cache. Nothing is subscribed until <see cref="StartAsync"/>.</summary>
	/// <param name="scopeFactory">Used to hold one long-lived scope for the cache's own <see cref="IHaContext"/>.</param>
	/// <param name="logger">Where subscription failures go.</param>
	public AreaSnapshotCache(IServiceScopeFactory scopeFactory, ILogger<AreaSnapshotCache> logger)
	{
		_scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>Every area the cache has heard from, newest state per area, ordered by name.</summary>
	public IReadOnlyList<AreaSnapshot> Snapshots =>
		[.. _snapshots.Values.OrderBy(snapshot => snapshot.AreaName, StringComparer.CurrentCulture)];

	/// <summary>Pushes each snapshot as it arrives, so a component can re-render without polling.</summary>
	public IObservable<AreaSnapshot> Changes => _changes;

	/// <summary>Whether any snapshot has arrived yet. Drives the dashboard's empty state.</summary>
	public bool HasData => !_snapshots.IsEmpty;

	/// <summary>Subscribes to the engine's area events.</summary>
	/// <param name="cancellationToken">Unused; subscribing does not block.</param>
	/// <returns>A completed task.</returns>
	public Task StartAsync(CancellationToken cancellationToken)
	{
		// A web UI that cannot subscribe is a degraded UI, not a dead host: the NetDaemon side of this
		// process must keep running whatever happens here.
		try
		{
			AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
			_scope = scope;
			IHaContext ha = scope.ServiceProvider.GetRequiredService<IHaContext>();

			_subscription = ha.Events
				.Filter<AreaSnapshotEvent>(HaStatePublisher.EventType)
				.SubscribeSafe(Record, _logger);

			_logger.LogInformation(
				"Watching Home Assistant for {EventType} events; the dashboard fills in as areas report.",
				HaStatePublisher.EventType);
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Could not subscribe to area events. The dashboard will stay empty.");
		}

		return Task.CompletedTask;
	}

	/// <summary>Stops watching.</summary>
	/// <param name="cancellationToken">Unused.</param>
	/// <returns>A task that completes once the subscription and its scope are gone.</returns>
	public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync().ConfigureAwait(false);

	/// <summary>Drops the subscription and its scope. Safe to call more than once.</summary>
	/// <returns>A task that completes once the scope has been disposed.</returns>
	public async ValueTask DisposeAsync()
	{
		_subscription?.Dispose();
		_subscription = null;

		if (_scope is { } scope)
		{
			_scope = null;
			await scope.DisposeAsync().ConfigureAwait(false);
		}

		_changes.Dispose();
	}

	private void Record(Event<AreaSnapshotEvent> @event)
	{
		AreaSnapshot? snapshot = @event.Data?.ToSnapshot();
		if (snapshot is null)
			return;

		_snapshots[KeyOf(snapshot)] = snapshot;
		_changes.OnNext(snapshot);
	}

	/// <summary>
	///     What a snapshot is filed under: the registry area id, falling back to the display name.
	/// </summary>
	/// <remarks>
	///     The id is the stable half of the pair. A room renamed while the page is open used to arrive as a second
	///     entry under its new name, leaving the old one to sit in the list forever; keyed by id it simply replaces
	///     itself. The fallback covers the two cases with no id to key on — an area configured with explicit entity
	///     lists and no <c>AreaId</c>, and an event from a build published before <c>area_id</c> existed — which
	///     between them are exactly the old behaviour, unchanged.
	/// </remarks>
	private static string KeyOf(AreaSnapshot snapshot) =>
		snapshot.AreaId is { Length: > 0 } areaId ? areaId : snapshot.AreaName;
}
