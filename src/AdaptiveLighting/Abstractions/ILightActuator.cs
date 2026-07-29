using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Abstractions;

/// <summary>
///     The engine's only way to change a light. Narrow on purpose: it keeps <c>light.turn_on</c> out of the
///     state machine, and it means an area test needs a recording fake rather than a whole HA fake.
/// </summary>
public interface ILightActuator
{
	/// <summary>
	///     Brings <paramref name="entityId"/> to <paramref name="command"/>. Implementations may drop the call
	///     when the light already matches — callers must not assume a service call happened.
	/// </summary>
	void Apply(string entityId, LightCommand command);

	/// <summary>
	///     Activates <paramref name="sceneId"/> (a <c>scene.*</c> entity) via <c>scene.turn_on</c>. Applied once on
	///     entry to an Away/Guest mode that names a scene (09 §3.3); the engine never re-asserts it.
	/// </summary>
	void ActivateScene(string sceneId);
}
