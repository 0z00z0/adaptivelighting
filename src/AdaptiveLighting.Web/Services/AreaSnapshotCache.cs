using System.Collections.Concurrent;
using System.Reactive.Subjects;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Ha;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AdaptiveLighting.Web.Services;

/// <summary>The last <see cref="AreaSnapshot"/> seen per area, and the dashboard's only source of engine state.</summary>
/// <remarks>
///     Fed from the <c>adaptive_lighting_area</c> HA event, not from the engine in process, so snapshots make a
///     round trip through Home Assistant: nothing is shown without a live connection, and nothing until the first
///     area transition after start-up. A singleton with its own scope, since the cache accumulates from process
///     start, not from when a tab was opened.
/// </remarks>
public sealed class AreaSnapshotCache : IHostedService, IAsyncDisposable
{
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly ILogger<AreaSnapshotCache> _logger;
	private readonly ActivityLog _activity;
	private readonly ConcurrentDictionary<string, AreaSnapshot> _snapshots = new(StringComparer.Ordinal);
	private readonly Subject<AreaSnapshot> _changes = new();

	// Async scope, disposed only through DisposeAsync: NetDaemon's scoped IHaContext is IAsyncDisposable alone and
	// throws on a sync dispose. This class implements no IDisposable so there is no sync path to here.
	private AsyncServiceScope? _scope;
	private IDisposable? _subscription;

	/// <summary>Creates the cache. Nothing is subscribed until <see cref="StartAsync"/>.</summary>
	/// <param name="activity">
	///     The history the activity page reads. Fed from here, not by a second subscriber, so both views come off
	///     the one subscription and the history starts at process start.
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
	/// <remarks>Subscribable at any point in the process's life, shutdown included. See <see cref="DisposeAsync"/>.</remarks>
	public IObservable<AreaSnapshot> Changes => _changes;

	public bool HasData => !_snapshots.IsEmpty;

	public Task StartAsync(CancellationToken cancellationToken)
	{
		// A web UI that cannot subscribe is a degraded UI, not a dead host: the NetDaemon side keeps running.
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

	public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync().ConfigureAwait(false);

	/// <summary>Drops the subscription and its scope. Safe to call more than once.</summary>
	/// <remarks>
	///     The <see cref="Changes"/> subject is never disposed. This hosted service stops before Kestrel does, so a
	///     page can still reach <c>OnInitialized</c> afterwards, and <c>Subject&lt;T&gt;.Subscribe</c> on a disposed
	///     subject throws past <c>SubscribeSafe</c>, which guards the handler and not the subscription. Dropping
	///     <c>_subscription</c> is what stops snapshots arriving.
	/// </remarks>
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

		// Filed before the push: the activity page re-reads the history on this notification, so it must not be
		// told there is news and then find the history without it.
		_activity.Record(snapshot);

		_changes.OnNext(snapshot);
	}

	// Keyed on the registry area id, which is the stable half: a room renamed while the page is open replaces
	// itself instead of arriving as a second entry. The name covers an area with no AreaId at all.
	private static string KeyOf(AreaSnapshot snapshot) =>
		snapshot.AreaId is { Length: > 0 } areaId ? areaId : snapshot.AreaName;
}
