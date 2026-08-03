namespace AdaptiveLighting.Engine;

/// <summary>
///     What the engine wants a light to be. Smaller than <c>light.turn_on</c>: colour, effects and flash stay
///     with the human. A null field is left alone.
/// </summary>
/// <remarks>
///     <c>EqualChannels</c> drives every colour channel at one value, for fixtures with no colour temperature to
///     command. It and <c>ColorTempKelvin</c> are never both set; whoever composes the command picks one.
/// </remarks>
public sealed record LightCommand(
	bool On,
	double? BrightnessPct = null,
	int? ColorTempKelvin = null,
	double? TransitionSeconds = null,
	bool EqualChannels = false)
{
	public static LightCommand TurnOff(double? transitionSeconds = null) => new(false, TransitionSeconds: transitionSeconds);
}
