using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

using NetDaemon.HassModel;

namespace AdaptiveLighting.TestFakes;

/// <summary>Records what the controller wanted the lights to do, without the HA wire format in the way.</summary>
public sealed class FakeLightActuator : ILightActuator
{
	public List<(string EntityId, LightCommand Command)> Applied { get; } = [];

	public LightCommand? Last => Applied.Count == 0 ? null : Applied[^1].Command;

	public List<string> Scenes { get; } = [];

	private FakeHaContext? _echoInto;
	private string _echoUserId = "";
	private int _echoes;

	/// <summary>From now on, each command is reported back through <paramref name="ha"/> before <see cref="Apply"/> returns.</summary>
	// Opt-in: most tests arrange the light's state themselves. The echo is synchronous, which is the worst case for
	// a command sent before its expectation, and carries the app's own user as Home Assistant's would.
	public void EchoInto(FakeHaContext ha, string userId = "app-user")
	{
		_echoInto = ha;
		_echoUserId = userId;
	}

	public void Apply(string entityId, LightCommand command)
	{
		Applied.Add((entityId, command));

		if (_echoInto is null)
			return;

		Dictionary<string, object>? attributes = command is { On: true, BrightnessPct: double pct }
			? new() { ["brightness"] = (int)Math.Round(pct * 2.55) }
			: null;

		_echoInto.Trigger(entityId, command.On ? "on" : "off", attributes,
			new Context { Id = $"echo-{++_echoes}", UserId = _echoUserId });
	}

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
