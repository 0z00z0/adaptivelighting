using System.Reactive.Concurrency;
using System.Text.Json;

using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>A <see cref="FakeHaContext"/> that can act as another thread at the moment an entity is read.</summary>
internal sealed class InterleavingHaContext(FakeHaContext inner) : IHaContext
{
	private (string EntityId, Action Action)? _beforeRead;

	public FakeHaContext Inner => inner;

	/// <summary>Runs <paramref name="action"/> once, on the next read of <paramref name="entityId"/>, before the state is returned.</summary>
	public void BeforeNextRead(string entityId, Action action) => _beforeRead = (entityId, action);

	IObservable<Event> IHaContext.Events => ((IHaContext)inner).Events;

	public IObservable<StateChange> StateAllChanges() => inner.StateAllChanges();

	public EntityState? GetState(string entityId)
	{
		if (_beforeRead is { } pending && string.Equals(pending.EntityId, entityId, StringComparison.Ordinal))
		{
			_beforeRead = null;
			pending.Action();
		}

		return inner.GetState(entityId);
	}

	public IReadOnlyList<Entity> GetAllEntities() => [.. inner.GetAllEntities().Select(entity => new Entity(this, entity.EntityId))];

	public void CallService(string domain, string service, ServiceTarget? target = null, object? data = null) =>
		inner.CallService(domain, service, target, data);

	public Task<JsonElement?> CallServiceWithResponseAsync(string domain, string service, ServiceTarget? target = null, object? data = null) =>
		inner.CallServiceWithResponseAsync(domain, service, target, data);

	public Area? GetAreaFromEntityId(string entityId) => inner.GetAreaFromEntityId(entityId);

	public EntityRegistration? GetEntityRegistration(string entityId) => inner.GetEntityRegistration(entityId);

	public void SendEvent(string eventType, object? data) => inner.SendEvent(eventType, data);
}

/// <summary>Records every relative schedule, so a test can run a timer callback by hand after it was cancelled.</summary>
internal sealed class CapturingScheduler(IScheduler inner) : IScheduler
{
	private readonly List<(TimeSpan DueTime, Action Run)> _relative = [];

	public DateTimeOffset Now => inner.Now;

	/// <summary>The newest callback scheduled <paramref name="dueTime"/> ahead.</summary>
	public Action LastScheduled(TimeSpan dueTime) => _relative.Last(entry => entry.DueTime == dueTime).Run;

	public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action) =>
		inner.Schedule(state, action);

	public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
	{
		_relative.Add((dueTime, () => action(this, state)));
		return inner.Schedule(state, dueTime, action);
	}

	public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action) =>
		inner.Schedule(state, dueTime, action);
}
