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
///     <para>
///         The one subscription feeds two readers. This class keeps the newest report per area, which is what the
///         dashboard's cards are; <see cref="ActivityLog"/> keeps the last few hundred reports in order, which is
///         what the activity page's timeline is. Neither costs the engine anything more than the other did.
///     </para>
/// </remarks>
public sealed class AreaSnapshotCache : IHostedService, IAsyncDisposable
{
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly ILogger<AreaSnapshotCache> _logger;
	private readonly ActivityLog _activity;
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
	/// <param name="activity">
	///     The history the activity page reads. Fed from here rather than from a second subscriber of
	///     <see cref="Changes"/> so that both views are built from the one subscription, and so the history starts
	///     at process start for certain — a hosted service that subscribed later would silently miss whatever
	///     arrived in the gap.
	/// </param>
	public AreaSnapshotCache(IServiceScopeFactory scopeFactory, ILogger<AreaSnapshotCache> logger, ActivityLog activity)
	{
		_scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_activity = activity ?? throw new ArgumentNullException(nameof(activity));
	}

	/// <summary>Every area the cache has heard from, newest state per area, ordered by name.</summary>
	public IReadOnlyList<AreaSnapshot> Snapshots =>
		[.. _snapshots.Values.OrderBy(snapshot => snapshot.AreaName, StringComparer.CurrentCulture)];

	/// <summary>Pushes each snapshot as it arrives, so a component can re-render without polling.</summary>
	/// <remarks>
	///     Subscribable at any point in the process's life, including while it is shutting down — see
	///     <see cref="DisposeAsync"/> for why the subject behind this is never disposed.
	/// </remarks>
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
	/// <remarks>
	///     <para>
	///         <b><see cref="Changes"/>'s subject is deliberately not disposed.</b> The host stops hosted services
	///         in reverse registration order, and <c>GenericWebHostService</c> is registered by
	///         <c>WebApplication.CreateBuilder</c> — before <c>AddLightingWeb</c> — so this stops <i>first</i> and
	///         Kestrel is still serving pages afterwards. Every one of the three live pages subscribes to
	///         <see cref="Changes"/> in <c>OnInitialized</c>, and <c>Subject&lt;T&gt;.Subscribe</c> on a disposed
	///         subject throws <see cref="ObjectDisposedException"/> — which <c>SubscribeSafe</c> does not catch,
	///         because it guards the handler and not the subscription. So a reader who opened a page while the
	///         process was going down met Blazor's unhandled-error screen instead of a graceful shutdown.
	///     </para>
	///     <para>
	///         Not disposing it costs nothing and removes the whole class of "observed after disposal" without a
	///         lock: dropping <c>_subscription</c> above is what actually stops snapshots arriving, so the subject
	///         can never fire again anyway; it holds no unmanaged resources and goes with the singleton at process
	///         exit; and <c>Subject&lt;T&gt;.Dispose</c> does not signal completion, so live subscribers were never
	///         being told anything by it. It also settles the other half of the same race — a Home Assistant event
	///         already inside <see cref="Record"/> reaching <c>OnNext</c> after teardown, which threw for exactly
	///         the same reason.
	///     </para>
	/// </remarks>
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
	}

	private void Record(Event<AreaSnapshotEvent> @event)
	{
		AreaSnapshot? snapshot = @event.Data?.ToSnapshot();
		if (snapshot is null)
			return;

		_snapshots[KeyOf(snapshot)] = snapshot;

		// Filed before the change is pushed: a subscriber that re-reads on this notification — the activity page
		// does exactly that — must not be told there is news and then find the history without it.
		_activity.Record(snapshot);

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
