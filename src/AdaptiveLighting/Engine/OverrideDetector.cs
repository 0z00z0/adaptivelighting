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

/// <summary>
///     Decides whether a light changed because of us or because of somebody else.
/// </summary>
/// <remarks>
///     <para>
///         There is no clean answer available. <c>IHaContext.CallService</c> is fire-and-forget and does not hand
///         back the context id of the call it made, so the engine cannot simply compare context ids and know. Two
///         imperfect heuristics are therefore combined.
///     </para>
///     <para>
///         The primary one is expectation correlation: the controller declares each command before sending it, and
///         a change on that light consistent with the declaration and arriving inside the echo window is ours. The
///         secondary one reads <see cref="EntityState.Context"/>. Neither is sufficient alone; together they are
///         wrong mostly in the safe direction, where a human's change within a few seconds of ours is mistaken for
///         our own echo and the area simply keeps automating.
///     </para>
/// </remarks>
public sealed class OverrideDetector
{
	private readonly GlobalConfig _global;
	private readonly IScheduler _scheduler;
	private readonly Dictionary<string, Expectation> _expectations = new(StringComparer.OrdinalIgnoreCase);
	private readonly object _gate = new();

	/// <summary>Creates a detector reading its window and policy from <paramref name="global"/>.</summary>
	/// <param name="global">Supplies the echo window, the NetDaemon user id and the automation policy.</param>
	/// <param name="scheduler">The clock. The engine has no other.</param>
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
	///     The window covers the command's own transition as well as the echo window. A light fading over 15
	///     seconds reports attribute changes for those 15 seconds, and every one of them is ours; a window that
	///     closed first would have the engine mistake the tail of its own fade for a human at the dimmer and
	///     override itself — on every night retarget.
	/// </remarks>
	public void ExpectCommand(string entityId, LightCommand command)
	{
		ArgumentNullException.ThrowIfNull(command);

		TimeSpan window = TimeSpan.FromSeconds(_global.SelfEchoWindowSeconds + (command.TransitionSeconds ?? 0));
		lock (_gate)
			_expectations[entityId] = new Expectation(command, _scheduler.Now + window);
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

		// A parent context means something else caused this, and that something is an automation — whether or
		// not a user id came along for the ride. Checked before the user id precisely because a script started
		// by a person carries both, and it is the script that set the level.
		if (context.ParentId is not null)
			return ChangeOrigin.Automation;

		// No user and no parent: nothing created this on anyone's behalf, so the device reported it itself.
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

			// The expectation is kept until it expires rather than consumed: one turn_on produces a burst of
			// changes as brightness and colour settle, and every one of them is still our own echo.
			return expectation.Command.On == (newState?.IsOn() ?? false);
		}
	}

	private sealed record Expectation(LightCommand Command, DateTimeOffset ExpiresAt);
}
