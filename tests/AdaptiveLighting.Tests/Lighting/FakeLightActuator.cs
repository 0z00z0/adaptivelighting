using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Records what the controller wanted the lights to do, without any of the HA wire format in the way.</summary>
public sealed class FakeLightActuator : ILightActuator
{
	/// <summary>Every command, in order.</summary>
	public List<(string EntityId, LightCommand Command)> Applied { get; } = [];

	/// <summary>The most recent command, or <c>null</c> when nothing was commanded.</summary>
	public LightCommand? Last => Applied.Count == 0 ? null : Applied[^1].Command;

	/// <summary>Every scene activated, in order.</summary>
	public List<string> Scenes { get; } = [];

	/// <inheritdoc/>
	public void Apply(string entityId, LightCommand command) => Applied.Add((entityId, command));

	/// <inheritdoc/>
	public void ActivateScene(string sceneId) => Scenes.Add(sceneId);

	/// <summary>Forgets everything so far, so an assertion can speak about what happened next.</summary>
	public void Clear()
	{
		Applied.Clear();
		Scenes.Clear();
	}
}

/// <summary>Records the area snapshots the controller publishes.</summary>
public sealed class FakeStatePublisher : IStatePublisher
{
	/// <summary>Every snapshot, in order.</summary>
	public List<AreaSnapshot> Snapshots { get; } = [];

	/// <inheritdoc/>
	public void Publish(AreaSnapshot snapshot) => Snapshots.Add(snapshot);
}

/// <summary>Records the notifications the engine would have shown the household.</summary>
public sealed class FakeNotifier : INotifier
{
	/// <summary>Every notification, in order.</summary>
	public List<(string Title, string Message)> Notifications { get; } = [];

	/// <inheritdoc/>
	public void Notify(string title, string message) => Notifications.Add((title, message));
}
