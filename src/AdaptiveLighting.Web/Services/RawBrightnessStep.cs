using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>The satellite handle's view of the 0-255 brightness a document stores: how it is written, and how it
/// steps. Kept apart from <see cref="Components.PresetSlider"/> so the boundary arithmetic is reachable without
/// rendering anything.</summary>
// Clamping is this type's own job, not RawBrightness's: the handle must not push past a real byte, while the
// validator has to be able to see an out-of-range value in a hand-edited document.
public static class RawBrightnessStep
{
	/// <summary>The ceiling Home Assistant accepts, and the denominator the satellite's readout names.</summary>
	public const int MaxRaw = RawBrightness.Max;

	/// <summary>How a raw value is written beside the satellite handle, against the scale it is a step of.</summary>
	// Invariant: a raw byte is Home Assistant's own number, not a quantity to group or localise.
	public static string Text(int raw) =>
		$"( {Math.Clamp(raw, 0, MaxRaw).ToString(CultureInfo.InvariantCulture)} / {MaxRaw.ToString(CultureInfo.InvariantCulture)} )";

	/// <summary>The nearest raw value a percentage rounds to, clamped to a real raw byte.</summary>
	public static int FromPercent(double percent) => Math.Clamp(RawBrightness.FromPercent(percent), 0, MaxRaw);

	/// <summary>The percentage a raw value reports as, clamped to a real raw byte first.</summary>
	public static double ToPercent(int raw) => RawBrightness.ToPercent(Math.Clamp(raw, 0, MaxRaw));

	/// <summary>Moves a raw value by whole 8-bit steps, clamped so the satellite cannot push it past 0 or 255.</summary>
	public static int Nudge(int raw, int steps) => Math.Clamp(raw + steps, 0, MaxRaw);
}
