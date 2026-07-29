using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>
///     Raises an area's scheduled brightness toward a ceiling as the light outside climbs, so a room does not read
///     as gloomy against a bright window.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why the interpolation is logarithmic.</b> Illuminance spans orders of magnitude — a hallway can read
///         5 lx after dark and 20 000 lx facing a bright window — while perceived brightness is roughly the
///         logarithm of luminance (Weber–Fechner). Interpolating on the raw lux value would spend almost the whole
///         adjustment inside the top decade and do nothing across the range a room actually moves through on an
///         ordinary day: with anchors at 100 and 10 000 lx, a linear map is still under a tenth of the way up at
///         1 000 lx, which is broad daylight. Interpolating on <c>log10(lux)</c> gives every decade an equal share
///         of the curve, so 1 000 lx lands squarely halfway — which is what makes the two anchors behave like
///         numbers a human would pick.
///     </para>
///     <para>
///         <b>Why it raises rather than replaces.</b> The output is the schedule's own brightness lifted toward
///         <see cref="AreaSettings.LuxBrightnessMaxPct"/>, never a level computed from the reading alone. So the
///         circadian intent survives: the adjustment starts from whatever the period asked for and it can only
///         ever add light. An absolute curve, ignoring the schedule, would instead make the low anchor
///         meaningless (the level at which it started to bite would drift with whatever the period happened to
///         ask for) and would hand the same level to a room the schedule wanted dim and one it wanted bright.
///     </para>
///     <para>
///         <b>The period no longer caps the result, and that is a change worth knowing about.</b> Per-period
///         ceilings were removed in the 2026-07 simplification, so the only ceiling left is
///         <see cref="AreaSettings.LuxBrightnessMaxPct"/> — this room's own, not the period's. A night period
///         asking for 15 % in a room whose sensor reads 1 000 lx now lands near 58 %, where a period capped at
///         30 % once held it. Sleep mode still clamps a room with <see cref="AreaSettings.RespectSleepMode"/>
///         set; nothing else does. A room that wants a quiet night wants that setting, or a lower
///         <see cref="AreaSettings.LuxBrightnessMaxPct"/>.
///     </para>
///     <para>
///         Pure and open-loop, in the same spirit as <see cref="IlluminanceGate"/>: the reading is an input,
///         nothing here reads a clock or Home Assistant, and no attempt is made to servo the room to a measured
///         level. A closed loop would oscillate, because indoors the lights are part of what the sensor reads.
///     </para>
/// </remarks>
public sealed class LuxBrightnessCurve
{
	private readonly AreaSettings _settings;
	private readonly Func<double?> _readLux;

	/// <summary>Creates the curve for one area.</summary>
	/// <param name="settings">Supplies the switch, the two anchors, the ceiling and the shaping exponent.</param>
	/// <param name="readLux">
	///     The current reading, or <c>null</c> when the area has no usable sensor. A delegate rather than an
	///     <see cref="IHaContext"/> so the sensor is resolved once, by whoever already knows how — in the engine
	///     that is <see cref="IlluminanceGate.ReadLux"/>, which is also what the darkness gate reads, so the two
	///     can never end up consulting different sensors.
	/// </param>
	public LuxBrightnessCurve(AreaSettings settings, Func<double?> readLux)
	{
		_settings = settings ?? throw new ArgumentNullException(nameof(settings));
		_readLux = readLux ?? throw new ArgumentNullException(nameof(readLux));
	}

	/// <summary>
	///     <paramref name="target"/> with its brightness raised for the current reading, or <paramref name="target"/>
	///     itself when the feature is off.
	/// </summary>
	/// <remarks>
	///     A disabled area returns the very same instance, not an equal one computed through a curve that happens
	///     to add zero. Two live houses run this engine, and "off behaves exactly as before" is a property worth
	///     being able to see rather than reason about.
	/// </remarks>
	public LightTarget Apply(LightTarget target)
	{
		ArgumentNullException.ThrowIfNull(target);

		if (!_settings.LuxBrightnessEnabled)
			return target;

		double raised = Raise(target.BrightnessPct, _readLux(), _settings);

		// Clamp() is now only the physical 0–100 range: the period's floor and cap were removed with the rest of
		// the per-period clamps, so this no longer holds the daylight adjustment to what the schedule asked for.
		// LuxBrightnessMaxPct is the ceiling that remains, and it is the room's rather than the period's.
		return target with { BrightnessPct = target.Clamp(raised) };
	}

	/// <summary>
	///     <paramref name="scheduleBrightnessPct"/> raised toward <see cref="AreaSettings.LuxBrightnessMaxPct"/> by
	///     however far <paramref name="lux"/> has travelled up the curve. Pure, and the whole of the maths.
	/// </summary>
	/// <param name="scheduleBrightnessPct">What the circadian period asked for.</param>
	/// <param name="lux">The reading, or <c>null</c> when no sensor resolved.</param>
	/// <param name="settings">The curve.</param>
	/// <returns>
	///     A brightness at or above <paramref name="scheduleBrightnessPct"/>, and at or below
	///     <see cref="AreaSettings.LuxBrightnessMaxPct"/> — the room's own ceiling, which since the caps cut is the
	///     only one there is. Callers holding a <see cref="LightTarget"/> should still go through
	///     <see cref="Apply"/>, but be clear about what that adds: the physical 0–100 clamp, not a period's cap.
	/// </returns>
	public static double Raise(double scheduleBrightnessPct, double? lux, AreaSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);

		// Headroom, never a signed difference: a ceiling below what the period asked for means "nothing to add
		// here", not "dim the room because the sun is out".
		double headroom = Math.Max(0, settings.LuxBrightnessMaxPct - scheduleBrightnessPct);
		double raised = scheduleBrightnessPct + (Position(lux, settings) * headroom);

		// A NaN or an infinity reaching a light command is an outage, so the last word is a finiteness check
		// rather than trust in the arithmetic above.
		return double.IsFinite(raised) ? raised : scheduleBrightnessPct;
	}

	/// <summary>
	///     How far up the curve <paramref name="lux"/> sits, from 0 (no adjustment) to 1 (the full ceiling), after
	///     the log-space interpolation and the gamma shaping.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Every degenerate case answers 0 — no reading, a reading that is not a number, a reading at or below
	///         zero, anchors that are inverted, equal or non-positive. That is the safe answer in all of them: it
	///         hands the room back exactly what the schedule asked for, which is the behaviour of a house that
	///         never switched this on. Sensors do report 0, and occasionally nonsense, and <c>log10</c> of either
	///         is a value no light should ever be commanded to.
	///     </para>
	///     <para>
	///         Public because it is the honest thing to show a preview or a chart: it is the curve, without the
	///         schedule value mixed in.
	///     </para>
	/// </remarks>
	public static double Position(double? lux, AreaSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);

		double start = settings.LuxBrightnessStartLux;
		double full = settings.LuxBrightnessFullLux;

		// A start anchor at or below zero has no logarithm, and a full anchor at or below the start leaves nothing
		// to interpolate across (and would divide by zero). Both are configuration errors the validator reports;
		// here they simply make the curve inert.
		if (!double.IsFinite(start) || !double.IsFinite(full) || start <= 0 || full <= start)
			return 0;

		if (lux is not { } reading || !double.IsFinite(reading) || reading <= start)
			return 0;

		if (reading >= full)
			return Shape(1, settings);

		// Two anchors can differ while their logarithms do not: log10(100) and log10(100.00000000000003) are the
		// same double, and the validator has no reason to object to either number. The span is then zero, the
		// numerator is zero too, and 0/0 is the one value this method promises never to hand back — so the
		// degenerate-anchor answer of 0 is given here as well, rather than a NaN travelling out through Position
		// into whatever is drawing the curve. (Raise survives it either way; its own finiteness check catches it.)
		double span = Math.Log10(full) - Math.Log10(start);
		if (span <= 0)
			return 0;

		double fraction = (Math.Log10(reading) - Math.Log10(start)) / span;
		return Shape(Math.Clamp(fraction, 0, 1), settings);
	}

	/// <summary>
	///     Applies the shaping exponent to a 0–1 position.
	/// </summary>
	/// <remarks>
	///     A non-positive gamma is passed through unshaped rather than obeyed, and the reason is a trap worth
	///     naming: <c>Math.Pow(0, 0)</c> is 1, so a gamma of zero would hand back the full ceiling at the bottom of
	///     the curve — a pitch-dark night commanding the daylight level. The validator rejects it; this makes sure
	///     a document that somehow carries it still behaves.
	/// </remarks>
	private static double Shape(double fraction, AreaSettings settings)
	{
		double gamma = settings.LuxBrightnessGamma;
		if (!double.IsFinite(gamma) || gamma <= 0)
			return fraction;

		double shaped = Math.Pow(fraction, gamma);
		return double.IsFinite(shaped) ? Math.Clamp(shaped, 0, 1) : fraction;
	}
}
