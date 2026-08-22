using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Records what the controller wanted the lights to do, without the HA wire format in the way.</summary>
public sealed class FakeLightActuator : ILightActuator
{
	public List<(string EntityId, LightCommand Command)> Applied { get; } = [];

	public LightCommand? Last => Applied.Count == 0 ? null : Applied[^1].Command;

	public List<string> Scenes { get; } = [];

	public void Apply(string entityId, LightCommand command) => Applied.Add((entityId, command));

	public void ActivateScene(string sceneId) => Scenes.Add(sceneId);

	public void Clear()
	{
		Applied.Clear();
		Scenes.Clear();
	}
}

/// <summary>Records the area snapshots the controller publishes.</summary>
public sealed class FakeStatePublisher : IStatePublisher
{
	public List<AreaSnapshot> Snapshots { get; } = [];

	public void Publish(AreaSnapshot snapshot) => Snapshots.Add(snapshot);
}

/// <summary>Records notifications instead of showing them.</summary>
public sealed class FakeNotifier : INotifier
{
	public List<(string Title, string Message)> Notifications { get; } = [];

	public void Notify(string title, string message) => Notifications.Add((title, message));
}
