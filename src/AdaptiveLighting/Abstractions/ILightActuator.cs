using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Abstractions;

/// <summary>The engine's only way to change a light, keeping <c>light.turn_on</c> out of the state machine.</summary>
public interface ILightActuator
{
	/// <summary>
	///     Brings <paramref name="entityId"/> to <paramref name="command"/>. Implementations may drop the call when
	///     the light already matches, so callers must not assume a service call happened.
	/// </summary>
	void Apply(string entityId, LightCommand command);

	/// <summary>
	///     Activates <paramref name="sceneId"/> via <c>scene.turn_on</c>. Applied once on entry to a mode that names
	///     a scene; the engine never re-asserts it.
	/// </summary>
	void ActivateScene(string sceneId);
}
