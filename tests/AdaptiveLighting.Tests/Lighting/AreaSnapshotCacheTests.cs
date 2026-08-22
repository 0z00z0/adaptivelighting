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

/// <summary>The snapshot cache's lifetime.</summary>
/// <remarks>NetDaemon's scoped <see cref="IHaContext"/> is <see cref="IAsyncDisposable"/> only, so the cache must own no synchronous disposal route.</remarks>
[TestClass]
public sealed class AreaSnapshotCacheTests
{
	/// <summary>An <see cref="IHaContext"/> that can only be disposed asynchronously, as NetDaemon's scoped context is.</summary>
	private sealed class AsyncOnlyHaContext : IHaContext, IAsyncDisposable
	{
		private readonly Subject<Event> _events = new();

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

	[TestMethod]
	public async Task A_Scope_Holding_An_Async_Only_Context_Refuses_To_Be_Disposed_Synchronously()
	{
		await using ServiceProvider provider = Provider(new AsyncOnlyHaContext());

		IServiceScope scope = provider.CreateScope();
		scope.ServiceProvider.GetRequiredService<IHaContext>();

		Assert.ThrowsException<InvalidOperationException>(scope.Dispose);
	}

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

	// Teardown runs twice in a normal shutdown: the host stops the hosted service, the container then disposes
	// the singleton.
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

	// Hosted services stop in reverse registration order, so this cache stops before Kestrel and pages keep
	// arriving. Subject.Subscribe on a disposed subject throws, and SubscribeSafe guards the handler, not the
	// subscription. The chain here is the pages' own, sampling included: Sample subscribes to the source.
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
