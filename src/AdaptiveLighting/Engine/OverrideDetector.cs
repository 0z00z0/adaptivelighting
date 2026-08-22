using System.Reactive.Concurrency;

using AdaptiveLighting.Configuration;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>Who caused a light to change.</summary>
public enum ChangeOrigin
{
	/// <summary>The engine itself, never a reason to transition.</summary>
	Self,

	/// <summary>A wall switch, dimmer or remote acting on the light directly, so unambiguously a human.</summary>
	PhysicalDevice,

	/// <summary>A person driving the HA app or UI.</summary>
	HaUser,

	/// <summary>Another automation, a human's proxy and configurably treated as one.</summary>
	Automation,

	/// <summary>Nothing useful could be determined.</summary>
	Unknown
}

/// <summary>Decides whether a light changed because of the engine or because of a person.</summary>
// IHaContext.CallService is fire-and-forget and hands back no context id, so two heuristics are combined: an
// expectation declared before each command, and EntityState.Context. They fail in the safe direction, reading a
// human change within seconds of a command as the engine's own echo.
public sealed class OverrideDetector
{
	private readonly GlobalConfig _global;
	private readonly IScheduler _scheduler;
	private readonly Dictionary<string, Expectation> _expectations = new(StringComparer.OrdinalIgnoreCase);
	private readonly object _gate = new();

	public OverrideDetector(GlobalConfig global, IScheduler scheduler)
	{
		_global = global ?? throw new ArgumentNullException(nameof(global));
		_scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
	}

	/// <summary>Declares a command about to be sent to <paramref name="entityId"/>.</summary>
	// Must be called before every ILightActuator.Apply, or the engine reads its own work as a human's. The window
	// covers the transition on top of the echo window: a 15-second fade reports attribute changes for 15 seconds,
	// and a shorter window would read the tail of that fade as a human.
	public void ExpectCommand(string entityId, LightCommand command)
	{
		ArgumentNullException.ThrowIfNull(command);

		TimeSpan window = TimeSpan.FromSeconds(_global.SelfEchoWindowSeconds + (command.TransitionSeconds ?? 0));
		lock (_gate)
			_expectations[entityId] = new Expectation(command.On, _scheduler.Now + window);
	}

	/// <summary>Declares a scene about to be run on <paramref name="entityId"/>, without saying where it will leave it.</summary>
	// A scene's own light changes carry neither a user nor a parent, which classifies as PhysicalDevice. The
	// scene's contents are unreadable, so the expectation matches on or off alike for the length of the window.
	public void ExpectScene(string entityId, double transitionSeconds)
	{
		TimeSpan window = TimeSpan.FromSeconds(_global.SelfEchoWindowSeconds + transitionSeconds);
		lock (_gate)
			_expectations[entityId] = new Expectation(null, _scheduler.Now + window);
	}

	public ChangeOrigin Classify(StateChange change)
	{
		ArgumentNullException.ThrowIfNull(change);

		string? entityId = change.EntityId();
		if (entityId is not null && MatchesExpectation(entityId, change.New))
			return ChangeOrigin.Self;

		Context? context = change.New?.Context;
		if (context is null)
			return ChangeOrigin.Unknown;

		if (_global.NetDaemonUserId is { Length: > 0 } ourUserId &&
			string.Equals(context.UserId, ourUserId, StringComparison.Ordinal))
			return ChangeOrigin.Self;

		// Checked before the user id: a script started by a person carries both, and the script set the level.
		if (context.ParentId is not null)
			return ChangeOrigin.Automation;

		// No user and no parent: the device reported it itself.
		return context.UserId is null ? ChangeOrigin.PhysicalDevice : ChangeOrigin.HaUser;
	}

	/// <summary>Whether <paramref name="origin"/> is one the area must yield to.</summary>
	public bool IsManual(ChangeOrigin origin) => origin switch
	{
		ChangeOrigin.PhysicalDevice or ChangeOrigin.HaUser => true,
		ChangeOrigin.Automation => _global.TreatAutomationsAsManual,
		_ => false
	};

	private bool MatchesExpectation(string entityId, EntityState? newState)
	{
		lock (_gate)
		{
			if (!_expectations.TryGetValue(entityId, out Expectation? expectation))
				return false;

			if (_scheduler.Now > expectation.ExpiresAt)
			{
				_expectations.Remove(entityId);
				return false;
			}

			// Kept until it expires, not consumed: one turn_on produces a burst of changes as the light settles.
			return expectation.On is not { } on || on == (newState?.IsOn() ?? false);
		}
	}

	// On is null for a scene, whose contents the engine cannot read, so either polarity is its own echo.
	private sealed record Expectation(bool? On, DateTimeOffset ExpiresAt);
}
