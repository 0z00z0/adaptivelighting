using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The snapshot cache's lifetime, which is the part of it that has actually broken a house.
/// </summary>
/// <remarks>
///     <para>
///         The cache holds one dependency-injection scope for the life of the process, and NetDaemon's scoped
///         <c>IHaContext</c> implements <see cref="IAsyncDisposable"/> alone. Disposing such a scope
///         <i>synchronously</i> throws — and because a hosted service's <c>StopAsync</c> runs while the host is
///         still starting its other services, that throw surfaced as "Failed to start host" and killed the process
///         on restart rather than merely leaving the dashboard empty.
///     </para>
///     <para>
///         So the shape is asserted here rather than trusted: the scope goes away through the async path, the
///         teardown survives being run more than once (the host stops the service and the container then disposes
///         the singleton), and it survives a start that never got as far as subscribing.
///     </para>
/// </remarks>
[TestClass]
public sealed class AreaSnapshotCacheTests
{
	/// <summary>
	///     An <see cref="IHaContext"/> that can only be disposed asynchronously — NetDaemon's scoped context in
	///     the one respect that matters here. Only <see cref="IHaContext.Events"/> is exercised; the rest exists
	///     because the interface does.
	/// </summary>
	private sealed class AsyncOnlyHaContext : IHaContext, IAsyncDisposable
	{
		private readonly Subject<Event> _events = new();

		/// <summary>Whether the scope actually got round to disposing this.</summary>
		public bool Disposed { get; private set; }

		IObservable<Event> IHaContext.Events => _events;

		public ValueTask DisposeAsync()
		{
			Disposed = true;
			_events.Dispose();
			return ValueTask.CompletedTask;
		}

		public IObservable<StateChange> StateAllChanges() => Observable.Empty<StateChange>();

		public EntityState? GetState(string entityId) => null;

		public IReadOnlyList<Entity> GetAllEntities() => [];

		public void CallService(string domain, string service, ServiceTarget? target = null, object? data = null)
		{
		}

		public Task<JsonElement?> CallServiceWithResponseAsync(
			string domain, string service, ServiceTarget? target = null, object? data = null) =>
			Task.FromResult<JsonElement?>(null);

		public Area? GetAreaFromEntityId(string entityId) => null;

		public Entity Entity(string entityId) => new(this, entityId);

		public EntityRegistration? GetEntityRegistration(string entityId) => null;

		public void SendEvent(string eventType, object? data)
		{
		}
	}

	private static ServiceProvider Provider(AsyncOnlyHaContext ha)
	{
		ServiceCollection services = new();
		services.AddScoped<IHaContext>(_ => ha);

		return services.BuildServiceProvider();
	}

	private static AreaSnapshotCache Cache(ServiceProvider provider) =>
		new(
			provider.GetRequiredService<IServiceScopeFactory>(),
			NullLogger<AreaSnapshotCache>.Instance,
			new ActivityLog());

	/// <summary>
	///     The bug being guarded, written out. A scope that has handed out an async-only disposable refuses to be
	///     disposed synchronously, and that refusal is what travelled out of <c>StopAsync</c> as "Failed to start
	///     host" — so the cache must never own a synchronous route to disposing its scope.
	/// </summary>
	[TestMethod]
	public async Task A_Scope_Holding_An_Async_Only_Context_Refuses_To_Be_Disposed_Synchronously()
	{
		await using ServiceProvider provider = Provider(new AsyncOnlyHaContext());

		IServiceScope scope = provider.CreateScope();
		scope.ServiceProvider.GetRequiredService<IHaContext>();

		Assert.ThrowsException<InvalidOperationException>(scope.Dispose);
	}

	/// <summary>Stopping the service gives the scope back, through the one path that works.</summary>
	[TestMethod]
	public async Task Stopping_Disposes_The_Scope_Through_The_Async_Path()
	{
		AsyncOnlyHaContext ha = new();
		using ServiceProvider provider = Provider(ha);

		AreaSnapshotCache cache = Cache(provider);

		await cache.StartAsync(CancellationToken.None);
		await cache.StopAsync(CancellationToken.None);

		Assert.IsTrue(ha.Disposed, "the cache's own scope goes away with it, rather than being held for the process");
	}

	/// <summary>
	///     Teardown runs twice in a normal shutdown — the host stops the hosted service, then the container
	///     disposes the singleton — so it has to be idempotent rather than merely correct once.
	/// </summary>
	[TestMethod]
	public async Task Tearing_Down_Twice_Is_Not_An_Error()
	{
		AsyncOnlyHaContext ha = new();
		using ServiceProvider provider = Provider(ha);

		AreaSnapshotCache cache = Cache(provider);

		await cache.StartAsync(CancellationToken.None);
		await cache.StopAsync(CancellationToken.None);
		await cache.DisposeAsync();
		await cache.DisposeAsync();

		Assert.IsTrue(ha.Disposed);
	}

	/// <summary>
	///     <b>The page that met an error screen instead of a shutdown.</b> The host stops hosted services in reverse
	///     registration order and <c>GenericWebHostService</c> is registered by <c>WebApplication.CreateBuilder</c>,
	///     before <c>AddLightingWeb</c> — so this cache stops first and Kestrel keeps serving pages afterwards. All
	///     three live pages subscribe to <see cref="AreaSnapshotCache.Changes"/> in <c>OnInitialized</c>, and
	///     <c>Subject&lt;T&gt;.Subscribe</c> on a disposed subject throws <see cref="ObjectDisposedException"/> —
	///     which <c>SubscribeSafe</c> does not catch, because it guards the handler and not the subscription. The
	///     chain here is the pages' own, sampling included, because the sample operator subscribes to the source.
	/// </summary>
	[TestMethod]
	public async Task A_Page_Opened_While_The_Host_Stops_Can_Still_Subscribe()
	{
		AsyncOnlyHaContext ha = new();
		using ServiceProvider provider = Provider(ha);

		AreaSnapshotCache cache = Cache(provider);

		await cache.StartAsync(CancellationToken.None);
		await cache.StopAsync(CancellationToken.None);

		using IDisposable subscription = cache.Changes
			.Sample(TimeSpan.FromMilliseconds(400))
			.SubscribeSafe((AreaSnapshot _) => { }, NullLogger.Instance);

		Assert.IsNotNull(subscription, "a circuit that starts during shutdown reads a quiet stream, not an exception");
	}

	/// <summary>
	///     A cache that never started, and one whose start could not reach Home Assistant, both tear down cleanly.
	///     A web UI that cannot subscribe is a degraded UI, not a dead host — and the scope it opened before the
	///     failure still has to be given back.
	/// </summary>
	[TestMethod]
	public async Task A_Start_That_Failed_Or_Never_Happened_Still_Tears_Down()
	{
		using ServiceProvider empty = new ServiceCollection().BuildServiceProvider();

		AreaSnapshotCache never = Cache(empty);
		await never.DisposeAsync();

		AreaSnapshotCache failed = Cache(empty);

		// No IHaContext is registered, so subscribing throws inside StartAsync. The host must still come up.
		await failed.StartAsync(CancellationToken.None);

		Assert.IsFalse(failed.HasData, "nothing was subscribed, so nothing arrives");

		await failed.StopAsync(CancellationToken.None);
	}
}
