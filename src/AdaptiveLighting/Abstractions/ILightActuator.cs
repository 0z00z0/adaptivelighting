using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Abstractions;

/// <summary>The engine's only way to change a light, keeping <c>light.turn_on</c> out of the state machine.</summary>
public interface ILightActuator
{
	/// <summary>
	///     Brings <paramref name="entityId"/> to <paramref name="command"/>. Implementations may drop the call when
	///     the light already matches, so callers must not assume a service call happened.
	/// </summary>
	/// <returns>Whether a service call went out, and what it carried.</returns>
	ActuatorOutcome Apply(string entityId, LightCommand command);

	/// <summary>Applied once on entry to a mode naming a scene; the engine never re-asserts it.</summary>
	void ActivateScene(string sceneId);
}

/// <summary>What one <see cref="ILightActuator.Apply"/> did.</summary>
/// <param name="Sent">Whether a service call went out; <c>false</c> when the light already matched.</param>
/// <param name="Service">The <c>light</c> service called, or <c>null</c> when nothing was sent.</param>
/// <param name="Data">The service data as sent, or <c>null</c> when nothing was sent or the actuator does not say.</param>
public sealed record ActuatorOutcome(bool Sent, string? Service = null, IReadOnlyDictionary<string, object>? Data = null)
{
	public static ActuatorOutcome AlreadyMatches { get; } = new(Sent: false);
}
