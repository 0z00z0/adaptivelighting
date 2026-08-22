namespace AdaptiveLighting.Web.Services;

/// <summary>A commanded colour temperature as a CSS colour.</summary>
/// <remarks>
///     The one conversion in the UI, so every surface reporting a room's warmth agrees. The Tanner Helland
///     blackbody approximation, clamped to what a lamp can be told to produce; near white above 6000 K, which
///     <c>--kelvin-weight</c> in <c>app.css</c> keeps legible in the light theme.
/// </remarks>
public static class KelvinColour
{
	public const int Warmest = 1500;

	public const int Coolest = 6600;

	public static string Css(int kelvin)
	{
		double k = Math.Clamp(kelvin, Warmest, Coolest) / 100.0;

		int g = (int)Math.Clamp(99.4708025861 * Math.Log(k) - 161.1195681661, 0, 255);
		int b = (int)Math.Clamp(138.5177312231 * Math.Log(k - 10) - 305.0447927307, 0, 255);

		return $"rgb(255, {g}, {b})";
	}
}
