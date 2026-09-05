namespace AdaptiveLighting.Web.Services;

/// <summary>Converts between a shown percentage and the 0-255 raw value Home Assistant actually stores, for the
/// brightness satellite handle's fine steps. Kept apart from <see cref="Components.PresetSlider"/> so the boundary
/// arithmetic is reachable without rendering anything.</summary>
// 255 mirrors AdaptiveLighting.Engine.LightAttributes.MaxRawBrightness, which is internal to that assembly and
// not exposed to this Razor class library. Duplicated rather than widening that visibility for one constant that
// is Home Assistant's own protocol, not something this codebase chooses.
public static class RawBrightnessStep
{
	private const int MaxRaw = 255;

	/// <summary>The nearest raw value a shown percentage rounds to.</summary>
	public static int FromPercent(double percent) =>
		Math.Clamp((int)Math.Round(percent / 100.0 * MaxRaw, MidpointRounding.AwayFromZero), 0, MaxRaw);

	/// <summary>The percentage a raw value reports as, clamped to a real raw byte first.</summary>
	public static double ToPercent(int raw) => Math.Clamp(raw, 0, MaxRaw) / (double)MaxRaw * 100.0;

	/// <summary>Moves a raw value by whole 8-bit steps, clamped so the satellite cannot push it past 0 or 255.</summary>
	public static int Nudge(int raw, int steps) => Math.Clamp(raw + steps, 0, MaxRaw);
}
