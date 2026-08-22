using System.Reactive.Subjects;
using System.Text.Json;

using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>One recorded <see cref="IHaContext.CallService"/>, kept so a test can assert on the wire format.</summary>
public sealed record ServiceCall(string Domain, string Service, ServiceTarget? Target, object? Data);

/// <summary>A hand-written <see cref="IHaContext"/>: a state dictionary, a subject of changes, and a list of calls.</summary>
/// <remarks>States go in through JSON: <see cref="EntityState"/> holds its attributes as a <see cref="JsonElement"/> and its constructor takes no dictionary.</remarks>
public sealed class FakeHaContext : IHaContext
{
	private readonly Subject<StateChange> _changes = new();
	private readonly Subject<Event> _events = new();
	private readonly Dictionary<string, EntityState> _states = new(StringComparer.Ordinal);

	public List<ServiceCall> Calls { get; } = [];

	public List<(string Type, object? Data)> SentEvents { get; } = [];

	IObservable<Event> IHaContext.Events => _events;

	/// <summary>Sets a state without announcing it. For arranging a fixture.</summary>
	public void SetState(string entityId, string state, Dictionary<string, object>? attributes = null, Context? context = null) =>
		_states[entityId] = Build(entityId, state, attributes, context);

	/// <summary>Sets a state Home Assistant last heard about at <paramref name="lastUpdated"/>, which only the staleness rule reads.</summary>
	public void SetStateReportedAt(string entityId, string state, DateTimeOffset lastUpdated, Dictionary<string, object>? attributes = null) =>
		_states[entityId] = Build(entityId, state, attributes, context: null, lastUpdated);

	/// <summary>Sets a state and pushes the change, as Home Assistant would.</summary>
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

	public IObservable<StateChange> StateAllChanges() => _changes;

	public EntityState? GetState(string entityId) => _states.GetValueOrDefault(entityId);

	public IReadOnlyList<Entity> GetAllEntities() => [.. _states.Keys.Select(id => new Entity(this, id))];

	public void CallService(string domain, string service, ServiceTarget? target = null, object? data = null) =>
		Calls.Add(new ServiceCall(domain, service, target, data));

	public Task<JsonElement?> CallServiceWithResponseAsync(string domain, string service, ServiceTarget? target = null, object? data = null) =>
		Task.FromResult<JsonElement?>(null);

	public Area? GetAreaFromEntityId(string entityId) => null;

	public Entity Entity(string entityId) => new(this, entityId);

	public EntityRegistration? GetEntityRegistration(string entityId) => null;

	public void SendEvent(string eventType, object? data) => SentEvents.Add((eventType, data));

	/// <summary>Delivers an event, as Home Assistant would. Not the other end of <see cref="SendEvent"/>.</summary>
	public void RaiseEvent(string eventType, object? data) =>
		_events.OnNext(new Event { EventType = eventType, DataElement = JsonSerializer.SerializeToElement(data) });
}
