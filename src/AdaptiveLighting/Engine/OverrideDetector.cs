using System.Reactive.Concurrency;

using AdaptiveLighting.Configuration;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>Who caused a light to change.</summary>
public enum ChangeOrigin
{
	/// <summary>The engine itself. Never a reason to transition.</summary>
	Self,

	/// <summary>A wall switch, dimmer or remote acting on the light directly. Unambiguously a human.</summary>
	PhysicalDevice,

	/// <summary>A person driving the HA app or UI. Also a human.</summary>
	HaUser,

	/// <summary>Another automation. A human's proxy, and configurably treated as one.</summary>
	Automation,

	/// <summary>Nothing useful could be determined.</summary>
	Unknown
}

/// <summary>Decides whether a light changed because of us or because of somebody else.</summary>
/// <remarks>
///     <c>IHaContext.CallService</c> is fire-and-forget and hands back no context id, so two heuristics are
///     combined: an expectation declared before each command, and <see cref="EntityState.Context"/>. They fail in
///     the safe direction, mistaking a human change within seconds of ours for our own echo.
/// </remarks>
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

	/// <summary>
	///     Declares a command about to be sent to <paramref name="entityId"/>. Must be called before every
	///     <see cref="Abstractions.ILightActuator.Apply"/>, or the engine will mistake its own work for a human's.
	/// </summary>
	/// <remarks>
	///     The window covers the transition as well as the echo window. A 15-second fade reports attribute changes
	///     for 15 seconds, and a shorter window would read the tail of the engine's own fade as a human.
	/// </remarks>
	public void ExpectCommand(string entityId, LightCommand command)
	{
		ArgumentNullException.ThrowIfNull(command);

		TimeSpan window = TimeSpan.FromSeconds(_global.SelfEchoWindowSeconds + (command.TransitionSeconds ?? 0));
		lock (_gate)
			_expectations[entityId] = new Expectation(command.On, _scheduler.Now + window);
	}

	/// <summary>
	///     Declares a scene about to be run on <paramref name="entityId"/>, without saying where it will leave it.
	/// </summary>
	/// <remarks>
	///     A scene's own light changes carry neither a user nor a parent, which is
	///     <see cref="ChangeOrigin.PhysicalDevice"/>. The polarity is unknown because the scene's contents are, so
	///     this expectation matches on or off alike, and only for the length of the window.
	/// </remarks>
	public void ExpectScene(string entityId, double transitionSeconds)
	{
		TimeSpan window = TimeSpan.FromSeconds(_global.SelfEchoWindowSeconds + transitionSeconds);
		lock (_gate)
			_expectations[entityId] = new Expectation(null, _scheduler.Now + window);
	}

	/// <summary>Attributes <paramref name="change"/> to an origin.</summary>
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
