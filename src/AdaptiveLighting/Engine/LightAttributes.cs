namespace AdaptiveLighting.Engine;

/// <summary>The Home Assistant light attributes the engine reads back off a fixture.</summary>
// Shared with HaLightActuator on purpose: the capture converts raw brightness to a percentage and the actuator
// converts it back to compare, so two copies of the scale would make a restore either a no-op or a double send.
internal static class LightAttributes
{
	public const string Brightness = "brightness";
	public const string ColorTempKelvin = "color_temp_kelvin";

	/// <summary>Home Assistant reports brightness on 0-255 but accepts it as a percentage.</summary>
	public const double MaxRawBrightness = 255.0;
}
