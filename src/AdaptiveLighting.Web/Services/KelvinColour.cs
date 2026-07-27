namespace AdaptiveLighting.Web.Services;

/// <summary>
///     A commanded colour temperature as a CSS colour.
/// </summary>
/// <remarks>
///     <para>
///         One conversion, because the warmth of a room is reported in three places at once — the state chip's
///         glyph, the board's blocks and the room lamp — and three approximations of the blackbody curve would
///         eventually disagree about what 2700 K looks like. A reader seeing two different oranges for one room
///         has no way to tell which is the light and which is the bug.
///     </para>
///     <para>
///         The curve is the usual Tanner Helland approximation, clamped to the range a lamp can actually be told
///         to produce. Above roughly 6000 K it returns something very near white, which is right as light and
///         nearly invisible as ink — see <c>--kelvin-weight</c> in <c>app.css</c> for how the light theme keeps
///         it legible without losing the hue.
///     </para>
/// </remarks>
public static class KelvinColour
{
	/// <summary>The warm end the curve is clamped to.</summary>
	public const int Warmest = 1500;

	/// <summary>The cool end the curve is clamped to.</summary>
	public const int Coolest = 6600;

	/// <summary>
	///     <paramref name="kelvin"/> as an <c>rgb(…)</c> string, clamped to <see cref="Warmest"/>–<see cref="Coolest"/>.
	/// </summary>
	/// <param name="kelvin">The colour temperature the engine commanded.</param>
	public static string Css(int kelvin)
	{
		double k = Math.Clamp(kelvin, Warmest, Coolest) / 100.0;

		int g = (int)Math.Clamp(99.4708025861 * Math.Log(k) - 161.1195681661, 0, 255);
		int b = (int)Math.Clamp(138.5177312231 * Math.Log(k - 10) - 305.0447927307, 0, 255);

		return $"rgb(255, {g}, {b})";
	}
}
