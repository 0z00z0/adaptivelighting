namespace AdaptiveLighting.Engine;

/// <summary>
///     What the engine wants a light to be. Smaller than <c>light.turn_on</c>: colour, effects and flash stay
///     with the human. A null field is left alone.
/// </summary>
public sealed record LightCommand(
	bool On,
	double? BrightnessPct = null,
	int? ColorTempKelvin = null,
	double? TransitionSeconds = null)
{
	public static LightCommand TurnOff(double? transitionSeconds = null) => new(false, TransitionSeconds: transitionSeconds);
}
