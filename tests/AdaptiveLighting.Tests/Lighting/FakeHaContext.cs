using System.Reactive.Subjects;
using System.Text.Json;

using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>One recorded <see cref="IHaContext.CallService"/>, kept so a test can assert on the wire format.</summary>
public sealed record ServiceCall(string Domain, string Service, ServiceTarget? Target, object? Data);

/// <summary>
///     A hand-written <see cref="IHaContext"/>: a state dictionary, a subject of changes, and a list of calls.
/// </summary>
/// <remarks>
///     Hand-written because there is no NetDaemon testing package to take this from. The states are built by
///     round-tripping JSON rather than by constructing <see cref="EntityState"/> directly — its attribute bag is
///     a <see cref="JsonElement"/> and the ctor will not take a dictionary, so JSON is the only honest way in,
///     and it has the happy side effect of exercising the same deserialisation path the real client uses.
/// </remarks>
public sealed class FakeHaContext : IHaContext
{
	private readonly Subject<StateChange> _changes = new();
	private readonly Subject<Event> _events = new();
	private readonly Dictionary<string, EntityState> _states = new(StringComparer.Ordinal);

	/// <summary>Every service call made through this context, in order.</summary>
	public List<ServiceCall> Calls { get; } = [];

	/// <summary>Every event sent through this context, in order.</summary>
	public List<(string Type, object? Data)> SentEvents { get; } = [];

	IObservable<Event> IHaContext.Events => _events;

	/// <summary>Sets a state without announcing it. For arranging a fixture.</summary>
	public void SetState(string entityId, string state, Dictionary<string, object>? attributes = null, Context? context = null) =>
		_states[entityId] = Build(entityId, state, attributes, context);

	/// <summary>
	///     Sets a state that Home Assistant last heard about at <paramref name="lastUpdated"/>.
	/// </summary>
	/// <remarks>
	///     Only the staleness rule reads that timestamp, so every other fixture leaves it absent — which is also
	///     what a state with no <c>last_updated</c> in its payload looks like, and is deliberately <i>not</i> read
	///     as death.
	/// </remarks>
	public void SetStateReportedAt(string entityId, string state, DateTimeOffset lastUpdated, Dictionary<string, object>? attributes = null) =>
		_states[entityId] = Build(entityId, state, attributes, context: null, lastUpdated);

	/// <summary>Sets a state and pushes the change, exactly as Home Assistant would.</summary>
	public void Trigger(string entityId, string newState, Dictionary<string, object>? attributes = null, Context? context = null)
	{
		_states.TryGetValue(entityId, out var old);
		var updated = Build(entityId, newState, attributes, context);
		_states[entityId] = updated;
		_changes.OnNext(new StateChange(new Entity(this, entityId), old, updated));
	}

	private static EntityState Build(
		string entityId,
		string state,
		Dictionary<string, object>? attributes,
		Context? context,
		DateTimeOffset? lastUpdated = null)
	{
		var json = JsonSerializer.SerializeToElement(new
		{
			entity_id = entityId,
			state,
			attributes = attributes ?? [],
			last_updated = lastUpdated,
			last_changed = lastUpdated,
			context = context is null ? null : new { id = context.Id, parent_id = context.ParentId, user_id = context.UserId }
		});

		return json.Deserialize<EntityState>()!;
	}

	/// <inheritdoc/>
	public IObservable<StateChange> StateAllChanges() => _changes;

	/// <inheritdoc/>
	public EntityState? GetState(string entityId) => _states.GetValueOrDefault(entityId);

	/// <inheritdoc/>
	public IReadOnlyList<Entity> GetAllEntities() => [.. _states.Keys.Select(id => new Entity(this, id))];

	/// <inheritdoc/>
	public void CallService(string domain, string service, ServiceTarget? target = null, object? data = null) =>
		Calls.Add(new ServiceCall(domain, service, target, data));

	/// <inheritdoc/>
	public Task<JsonElement?> CallServiceWithResponseAsync(string domain, string service, ServiceTarget? target = null, object? data = null) =>
		Task.FromResult<JsonElement?>(null);

	/// <inheritdoc/>
	public Area? GetAreaFromEntityId(string entityId) => null;

	/// <inheritdoc/>
	public Entity Entity(string entityId) => new(this, entityId);

	/// <inheritdoc/>
	public EntityRegistration? GetEntityRegistration(string entityId) => null;

	/// <inheritdoc/>
	public void SendEvent(string eventType, object? data) => SentEvents.Add((eventType, data));
}
