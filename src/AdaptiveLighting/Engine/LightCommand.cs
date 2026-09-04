namespace AdaptiveLighting.Engine;

/// <summary>What the engine wants a light to be; a null field is left alone.</summary>
// EqualChannels drives every colour channel at one value, for fixtures with no colour temperature to command.
// It and ColorTempKelvin are never both set. Channels is the other way in: an explicit vector read back off a
// colour-channel fixture (a hand-set colour, captured for a period test's return), sent as-is instead of 255s.
public sealed record LightCommand(
	bool On,
	double? BrightnessPct = null,
	int? ColorTempKelvin = null,
	double? TransitionSeconds = null,
	bool EqualChannels = false,
	IReadOnlyList<int>? Channels = null)
{
	public static LightCommand TurnOff(double? transitionSeconds = null) => new(false, TransitionSeconds: transitionSeconds);
}
