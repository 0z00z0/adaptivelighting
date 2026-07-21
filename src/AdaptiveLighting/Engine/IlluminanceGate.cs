using System.Globalization;

using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>
///     Answers the one question that gates auto-on: is this zone dark enough to be worth lighting?
/// </summary>
/// <remarks>
///     Deliberately open-loop. A closed loop — raise the lights until the lux sensor is satisfied — oscillates,
///     because the lights are what the sensor is reading. Closing it needs a control design and per-sensor
///     calibration that v1 does not have.
/// </remarks>
public sealed class IlluminanceGate
{
	private const string ElevationAttribute = "elevation";

	private readonly IHaContext _ha;
	private readonly string? _luxEntityId;
	private readonly ZoneSettings _settings;
	private readonly ILogger _logger;
	private readonly object _gate = new();

	private bool _isDark;
	private bool _warnedAboutMissingLuxSensor;

	// The last readings taken by IsDarkEnough, kept so DarknessDetail can explain a verdict — the actual lux and
	// sun-elevation numbers against the configured thresholds — without reading Home Assistant a second time.
	private double? _lastLux;
	private double? _lastElevation;

	/// <summary>
	///     Creates a gate for one zone.
	/// </summary>
	/// <param name="ha">Used only to read state; the gate never commands anything.</param>
	/// <param name="luxEntityId">The zone's lux sensor, or <c>null</c> when none resolved.</param>
	/// <param name="settings">Supplies the thresholds, the hysteresis and the choice of source.</param>
	/// <param name="logger">Where the fallback warning goes.</param>
	public IlluminanceGate(IHaContext ha, string? luxEntityId, ZoneSettings settings, ILogger logger)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_luxEntityId = luxEntityId;
		_settings = settings ?? throw new ArgumentNullException(nameof(settings));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>
	///     Whether the zone currently counts as dark. Polled at decision time rather than driven by a
	///     subscription: nothing in the state machine turns lights <i>off</i> because it got bright, so a
	///     fresh reading is only ever needed when a decision is being made.
	/// </summary>
	public bool IsDarkEnough()
	{
		lock (_gate)
		{
			_isDark = _settings.Darkness switch
			{
				DarknessSource.Always => true,
				DarknessSource.Sun => IsSunDown(),
				DarknessSource.Either => (EvaluateLux() ?? false) || IsSunDown(),
				DarknessSource.Lux => EvaluateLux() ?? FallBackToSun(),
				_ => _isDark
			};

			return _isDark;
		}
	}

	/// <summary>
	///     A short human explanation of the current verdict — the readings and the thresholds they were compared
	///     against — for the auto-on block log, so "not dark enough" is diagnosable without reverse-engineering the
	///     config. Reflects the source actually in use, and reads the values <see cref="IsDarkEnough"/> last took.
	/// </summary>
	public string DarknessDetail() => _settings.Darkness switch
	{
		DarknessSource.Always => "darkness source is Always",
		DarknessSource.Sun => SunDetail(),
		DarknessSource.Lux => _lastLux is { } lux ? LuxDetail(lux) : $"no lux reading — {SunDetail()}",
		DarknessSource.Either => _lastLux is { } lux ? $"{LuxDetail(lux)}; {SunDetail()}" : SunDetail(),
		_ => "unknown darkness source"
	};

	private string LuxDetail(double lux) =>
		string.Create(CultureInfo.InvariantCulture, $"lux {lux:0}, dark below {_settings.LuxThreshold:0}");

	private string SunDetail() => _lastElevation is { } degrees
		? string.Create(CultureInfo.InvariantCulture, $"sun elevation {degrees:0.#}°, dark below {_settings.SunElevationThreshold:0.#}°")
		: $"no sun elevation from {_settings.SunEntity}";

	/// <summary>
	///     The lux verdict, or <c>null</c> when there is no sensor or its reading is not a number.
	/// </summary>
	/// <remarks>
	///     Hysteresis is applied about the configured threshold: it takes <c>LuxThreshold</c> to become dark but
	///     <c>LuxThreshold + LuxHysteresis</c> to stop being dark. Without it a sensor resting on the threshold
	///     makes the zone strobe.
	/// </remarks>
	private bool? EvaluateLux()
	{
		if (_luxEntityId is null)
		{
			_lastLux = null;
			return null;
		}

		if (_ha.GetState(_luxEntityId).StateAsDouble() is not { } lux)
		{
			_lastLux = null;
			return null;
		}

		_lastLux = lux;

		if (_isDark)
			return lux < _settings.LuxThreshold + _settings.LuxHysteresis;

		return lux < _settings.LuxThreshold;
	}

	private bool IsSunDown()
	{
		double? elevation = _ha.AttrDouble(_settings.SunEntity, ElevationAttribute);
		_lastElevation = elevation;

		// No sun entity is not a reason to floodlight the house at noon.
		return elevation is { } degrees && degrees < _settings.SunElevationThreshold;
	}

	private bool FallBackToSun()
	{
		if (!_warnedAboutMissingLuxSensor)
		{
			_warnedAboutMissingLuxSensor = true;
			_logger.LogWarning(
				"Darkness source is Lux but no usable lux sensor resolved; falling back to sun elevation from {SunEntity}.",
				_settings.SunEntity);
		}

		return IsSunDown();
	}
}
