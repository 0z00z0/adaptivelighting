using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>
///     Raises an area's scheduled brightness toward a ceiling as the light outside climbs, so a room does not read
///     as gloomy against a bright window.
/// </summary>
/// <remarks>
///     Interpolation is on <c>log10(lux)</c>, so each decade gets an equal share of the curve. The output raises
///     the schedule's brightness and can only add light. Pure and open-loop: no clock, no Home Assistant, no servo.
///     Per-period ceilings were removed, so this room's <see cref="AreaSettings.LuxBrightnessMaxPct"/> is the only
///     ceiling left. A night period asking 15 % in a room reading 1 000 lx lands near 58 %; only sleep mode still
///     clamps it.
/// </remarks>
public sealed class LuxBrightnessCurve
{
	private readonly AreaSettings _settings;
	private readonly Func<double?> _readLux;

	/// <summary>
	///     Creates the curve for one area. In the engine <c>readLux</c> is <see cref="IlluminanceGate.ReadLux"/>,
	///     so the curve and the darkness gate read the same sensors.
	/// </summary>
	public LuxBrightnessCurve(AreaSettings settings, Func<double?> readLux)
	{
		_settings = settings ?? throw new ArgumentNullException(nameof(settings));
		_readLux = readLux ?? throw new ArgumentNullException(nameof(readLux));
	}

	/// <summary>
	///     <paramref name="target"/> with its brightness raised for the current reading. A disabled area gets the
	///     same instance back, not an equal one that went through a curve adding zero.
	/// </summary>
	public LightTarget Apply(LightTarget target)
	{
		ArgumentNullException.ThrowIfNull(target);

		if (!_settings.LuxBrightnessEnabled)
			return target;

		double raised = Raise(target.BrightnessPct, _readLux(), _settings);

		// Clamp() is the physical 0-100 range only. The period's floor and cap are gone.
		return target with { BrightnessPct = target.Clamp(raised) };
	}

	/// <summary>
	///     <paramref name="scheduleBrightnessPct"/> raised toward <see cref="AreaSettings.LuxBrightnessMaxPct"/> by
	///     however far <paramref name="lux"/> has travelled up the curve. Pure, and the whole of the maths.
	///     <paramref name="lux"/> is <c>null</c> when no sensor resolved.
	/// </summary>
	public static double Raise(double scheduleBrightnessPct, double? lux, AreaSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);

		// Headroom, never a signed difference: a ceiling below what the period asked for adds nothing, it does
		// not dim the room because the sun is out.
		double headroom = Math.Max(0, settings.LuxBrightnessMaxPct - scheduleBrightnessPct);
		double raised = scheduleBrightnessPct + (Position(lux, settings) * headroom);

		// A NaN or an infinity reaching a light command is an outage.
		return double.IsFinite(raised) ? raised : scheduleBrightnessPct;
	}

	/// <summary>
	///     How far up the curve <paramref name="lux"/> sits, 0 for no adjustment to 1 for the full ceiling, after
	///     the log-space interpolation and the gamma shaping. Every degenerate case answers 0.
	/// </summary>
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
		// same double, and the validator passes both. Without this the division is 0/0 and a NaN reaches whatever
		// is drawing the curve.
		double span = Math.Log10(full) - Math.Log10(start);
		if (span <= 0)
			return 0;

		double fraction = (Math.Log10(reading) - Math.Log10(start)) / span;
		return Shape(Math.Clamp(fraction, 0, 1), settings);
	}

	// A non-positive gamma passes through unshaped: Math.Pow(0, 0) is 1, so gamma zero would hand back the full
	// ceiling at the bottom of the curve, a pitch-dark night commanding the daylight level.
	private static double Shape(double fraction, AreaSettings settings)
	{
		double gamma = settings.LuxBrightnessGamma;
		if (!double.IsFinite(gamma) || gamma <= 0)
			return fraction;

		double shaped = Math.Pow(fraction, gamma);
		return double.IsFinite(shaped) ? Math.Clamp(shaped, 0, 1) : fraction;
	}
}
