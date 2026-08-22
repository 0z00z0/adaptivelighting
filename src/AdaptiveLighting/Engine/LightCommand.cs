namespace AdaptiveLighting.Engine;

/// <summary>What the engine wants a light to be; a null field is left alone.</summary>
// EqualChannels drives every colour channel at one value, for fixtures with no colour temperature to command.
// It and ColorTempKelvin are never both set.
public sealed record LightCommand(
	bool On,
	double? BrightnessPct = null,
	int? ColorTempKelvin = null,
	double? TransitionSeconds = null,
	bool EqualChannels = false)
{
	public static LightCommand TurnOff(double? transitionSeconds = null) => new(false, TransitionSeconds: transitionSeconds);
}
