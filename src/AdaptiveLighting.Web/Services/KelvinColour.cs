namespace AdaptiveLighting.Web.Services;

/// <summary>
///     A commanded colour temperature as a CSS colour.
/// </summary>
/// <remarks>
///     The one conversion in the UI: the state chip, the board's blocks and the room lamp all report a room's
///     warmth, and three approximations of the blackbody curve would disagree about 2700 K. The Tanner Helland
///     approximation, clamped to what a lamp can be told to produce. Near white above 6000 K, which
///     <c>--kelvin-weight</c> in <c>app.css</c> keeps legible in the light theme.
/// </remarks>
public static class KelvinColour
{
	public const int Warmest = 1500;

	public const int Coolest = 6600;

	/// <summary>A commanded temperature as an <c>rgb(…)</c> string, clamped to the two ends above.</summary>
	public static string Css(int kelvin)
	{
		double k = Math.Clamp(kelvin, Warmest, Coolest) / 100.0;

		int g = (int)Math.Clamp(99.4708025861 * Math.Log(k) - 161.1195681661, 0, 255);
		int b = (int)Math.Clamp(138.5177312231 * Math.Log(k - 10) - 305.0447927307, 0, 255);

		return $"rgb(255, {g}, {b})";
	}
}
