namespace AdaptiveLighting.Configuration;

/// <summary>The 0-255 brightness a document stores, and its percentage view.</summary>
/// <remarks>
///     Home Assistant stores and reports brightness as a byte. A document holds that byte so a value set one raw
///     step at a time survives the save; the engine works in percent, because a blend, a dim factor and a curve are
///     all ratios and doing them in 8-bit steps would change what a house does.
/// </remarks>
public static class RawBrightness
{
	/// <summary>The ceiling Home Assistant accepts.</summary>
	public const int Max = 255;

	/// <summary>The byte a percentage lands on.</summary>
	// Away from zero, and the same arithmetic Home Assistant applies to brightness_pct, so a document written in
	// percent lands its lamps on the byte it always landed on. Verified over every whole percent in
	// RawBrightnessStorageTests.
	public static int FromPercent(double percent) =>
		double.IsFinite(percent) ? (int)Math.Round(percent / 100.0 * Max, MidpointRounding.AwayFromZero) : 0;

	/// <summary>The percentage a byte reports as.</summary>
	public static double ToPercent(int raw) => raw / (double)Max * 100.0;
}
