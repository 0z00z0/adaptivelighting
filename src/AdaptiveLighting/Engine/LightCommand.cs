namespace AdaptiveLighting.Engine;

/// <summary>
///     What the engine wants a light to be. Deliberately smaller than <c>light.turn_on</c>: colour, effects
///     and flash are not the engine's business, and every field it does not set is a field a human still owns.
/// </summary>
/// <param name="On">Whether the light should be on.</param>
/// <param name="BrightnessPct">Target brightness, or <c>null</c> to leave brightness alone.</param>
/// <param name="ColorTempKelvin">Target colour temperature, or <c>null</c> to leave it alone.</param>
/// <param name="TransitionSeconds">Fade length, or <c>null</c> for the light's own default.</param>
public sealed record LightCommand(
	bool On,
	double? BrightnessPct = null,
	int? ColorTempKelvin = null,
	double? TransitionSeconds = null)
{
	/// <summary>A command to turn the light off, optionally fading.</summary>
	public static LightCommand TurnOff(double? transitionSeconds = null) => new(false, TransitionSeconds: transitionSeconds);
}
