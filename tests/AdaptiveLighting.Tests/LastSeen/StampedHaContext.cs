using System.Globalization;
using System.Reactive.Subjects;
using System.Text.Json;

using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.LastSeen;

/// <summary>
///     An <see cref="IHaContext"/> whose entities carry Home Assistant's own <c>last_updated</c>, which is the one
///     thing the last-seen cache reads and the shared <c>FakeHaContext</c> does not model.
/// </summary>
/// <remarks>
///     States go in through the wire format, offset and all, so the timestamps arrive by the same deserialisation
///     the real client uses. This module turns on those instants being right.
/// </remarks>
internal sealed class StampedHaContext : IHaContext
{
	private readonly Subject<StateChange> _changes = new();
	private readonly Subject<Event> _events = new();
	private readonly Dictionary<string, EntityState> _states = new(StringComparer.Ordinal);

	IObservable<Event> IHaContext.Events => _events;

	/// <summary>Every service call made through this context. Nothing in this module should ever add to it.</summary>
	public List<string> Calls { get; } = [];

	/// <summary>Puts an entity in the house with a Home Assistant timestamp.</summary>
	public void Set(string entityId, string state, DateTimeOffset lastUpdated, string? deviceClass = null)
	{
		Dictionary<string, object> attributes = [];

		if (deviceClass is { Length: > 0 })
			attributes["device_class"] = deviceClass;

		JsonElement json = JsonSerializer.SerializeToElement(new
		{
			entity_id = entityId,
			state,
			attributes,
			last_changed = Wire(lastUpdated),
			last_updated = Wire(lastUpdated)
		});

		_states[entityId] = json.Deserialize<EntityState>()!;
	}

	/// <summary>Puts an entity in the house with no timestamp, which Home Assistant never actually does.</summary>
	public void SetWithoutStamp(string entityId, string state)
	{
		JsonElement json = JsonSerializer.SerializeToElement(new
		{
			entity_id = entityId,
			state,
			attributes = new Dictionary<string, object>()
		});

		_states[entityId] = json.Deserialize<EntityState>()!;
	}

	/// <summary>Moves every entity's timestamp to one instant, which is what a Home Assistant restart does.</summary>
	public void RestartHomeAssistant(DateTimeOffset startedAt)
	{
		foreach (string entityId in _states.Keys.ToList())
			Set(entityId, _states[entityId].State ?? "on", startedAt, DeviceClassOf(entityId));
	}

	public void Remove(string entityId) => _states.Remove(entityId);

	/// <summary>Fires a raw Home Assistant event, for the <c>homeassistant_start</c> path.</summary>
	public void FireEvent(string eventType) => _events.OnNext(new Event { EventType = eventType });

	public string? DeviceClassOf(string entityId) => _states.TryGetValue(entityId, out EntityState? state)
		? state.AttrString("device_class")
		: null;

	public IObservable<StateChange> StateAllChanges() => _changes;

	public EntityState? GetState(string entityId) => _states.GetValueOrDefault(entityId);

	public IReadOnlyList<Entity> GetAllEntities() => [.. _states.Keys.Select(id => new Entity(this, id))];

	public void CallService(string domain, string service, ServiceTarget? target = null, object? data = null) =>
		Calls.Add($"{domain}.{service}");

	public Task<JsonElement?> CallServiceWithResponseAsync(string domain, string service, ServiceTarget? target = null, object? data = null) =>
		Task.FromResult<JsonElement?>(null);

	public Area? GetAreaFromEntityId(string entityId) => null;

	public Entity Entity(string entityId) => new(this, entityId);

	public EntityRegistration? GetEntityRegistration(string entityId) => null;

	public void SendEvent(string eventType, object? data) => Calls.Add($"event:{eventType}");

	/// <summary>Home Assistant's wire format for a timestamp, offset and all.</summary>
	private static string Wire(DateTimeOffset value) =>
		value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);
}
