using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>Sets the brightness from the light outside, for the periods that hand it that job.</summary>
// Interpolation is on log10(lux), so each decade gets an equal share of the curve. Pure and open-loop: no clock,
// no Home Assistant, no servo. It replaces the period's own level rather than adding to it, so both ends are free
// across 0-100 % and nothing about the schedule bounds them.
public sealed class LuxBrightnessCurve
{
	private readonly AreaSettings _settings;
	private readonly Func<double?> _readLux;

	// readLux is the daylight reading, which is the house's outdoor sensor unless the room named its own. Never
	// the darkness gate's sensor: an indoor one measures the lamps this curve is setting.
	public LuxBrightnessCurve(AreaSettings settings, Func<double?> readLux)
	{
		_settings = settings ?? throw new ArgumentNullException(nameof(settings));
		_readLux = readLux ?? throw new ArgumentNullException(nameof(readLux));
	}

	/// <summary><paramref name="target"/> with the curve's brightness where its period asked for the curve.</summary>
	// A period that specifies its own brightness gets the same instance back, so reference equality holds.
	public LightTarget Apply(LightTarget target)
	{
		ArgumentNullException.ThrowIfNull(target);

		if (!target.UsesDaylightCurve)
			return target;

		return target with { BrightnessPct = target.Clamp(Brightness(_readLux(), _settings)) };
	}

	/// <summary>The brightness the curve asks for at <paramref name="lux"/>.</summary>
	// lux is null when no sensor resolved, which reads as the dark end.
	public static double Brightness(double? lux, AreaSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);

		double dark = double.IsFinite(settings.LuxBrightnessMinPct) ? settings.LuxBrightnessMinPct : 0;
		double bright = double.IsFinite(settings.LuxBrightnessMaxPct) ? settings.LuxBrightnessMaxPct : dark;

		// Signed, not headroom: both ends are dragged freely, so a dark end above the bright one is a curve that
		// falls, not one that is ignored.
		double value = dark + (Position(lux, settings) * (bright - dark));

		// A NaN or an infinity reaching a light command is an outage.
		return Math.Clamp(double.IsFinite(value) ? value : dark, 0, 100);
	}

	/// <summary>How far up the curve <paramref name="lux"/> sits, 0 at the dark end to 1 at the bright end.</summary>
	// Every degenerate case answers 0.
	public static double Position(double? lux, AreaSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);

		double start = settings.LuxBrightnessStartLux;
		double full = settings.LuxBrightnessFullLux;

		// A start at or below zero has no logarithm; a full at or below start divides by zero. The validator
		// reports both; here they make the curve inert.
		if (!double.IsFinite(start) || !double.IsFinite(full) || start <= 0 || full <= start)
			return 0;

		if (lux is not { } reading || !double.IsFinite(reading) || reading <= start)
			return 0;

		if (reading >= full)
			return Shape(1, settings);

		// Two anchors can differ while their logarithms do not: log10(100) and log10(100.00000000000003) are the
		// same double, and the validator passes both. Without this the division is 0/0 and a NaN escapes.
		double span = Math.Log10(full) - Math.Log10(start);
		if (span <= 0)
			return 0;

		double fraction = (Math.Log10(reading) - Math.Log10(start)) / span;
		return Shape(Math.Clamp(fraction, 0, 1), settings);
	}

	// A non-positive gamma passes through unshaped: Math.Pow(0, 0) is 1, so gamma zero would hand back the bright
	// end at the bottom of the curve, a pitch-dark night commanding the daylight level.
	private static double Shape(double fraction, AreaSettings settings)
	{
		double gamma = settings.LuxBrightnessGamma;
		if (!double.IsFinite(gamma) || gamma <= 0)
			return fraction;

		double shaped = Math.Pow(fraction, gamma);
		return double.IsFinite(shaped) ? Math.Clamp(shaped, 0, 1) : fraction;
	}
}
