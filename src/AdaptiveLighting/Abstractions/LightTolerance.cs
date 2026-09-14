namespace AdaptiveLighting.Abstractions;

/// <summary>How far a light may sit from its target and still count as there, so no command is sent.</summary>
internal static class LightTolerance
{
	// HA reports brightness as a 0-255 integer against the engine's per cent, so a round trip lands about 1 % off.
	public const double BrightnessPct = 2;

	public const int ColorTempKelvin = 50;
}
