using System.Globalization;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.LastSeen;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>Answers the one question that gates auto-on: is this area dark enough to be worth lighting?</summary>
// Open-loop. The lights are part of what the sensor reads, so a closed loop oscillates.
public sealed class IlluminanceGate
{
	private const string ElevationAttribute = "elevation";
	private const string NoSensorDetail = "no light sensor here, so the room counts as dark";
	private const string NoReadingDetail = "no light sensor here is still reporting, so the room counts as dark";

	private readonly IHaContext _ha;
	private readonly IReadOnlyList<string> _luxEntityIds;
	private readonly AreaSettings _settings;
	private readonly TimeSpan _staleAfter;
	private readonly Func<DateTimeOffset> _now;
	private readonly DateTimeOffset _startedAt;
	private readonly ILogger _logger;

	// Home Assistant resets every entity's timestamp on restart, so its own fields cannot tell a sensor that died
	// last week from one that reported a minute before the restart. The tracker survives a restart and answers
	// false when it has no record.
	private readonly IEntityLastSeen? _lastSeen;

	// Guards _isDark and the _last* readings below. Held across a whole verdict so the detail matches it.
	private readonly object _gate = new();

	private bool _isDark;
	private bool _warnedAboutUnreadableLuxSensors;
	private bool _saidTheAreaHasNoLuxSensor;

	// What IsDarkEnough last read, so DarknessDetail can explain the verdict without asking Home Assistant again.
	private double? _lastLux;
	private int _lastLuxUsed;
	private int _lastLuxOffered;
	private double? _lastElevation;

	// luxEntityIds may be empty, and several are averaged. A staleAfter of zero or less switches the staleness rule
	// off; without a lastSeen it falls back to Home Assistant's own timestamps.
	public IlluminanceGate(
		IHaContext ha,
		IReadOnlyList<string> luxEntityIds,
		AreaSettings settings,
		TimeSpan staleAfter,
		Func<DateTimeOffset> now,
		ILogger logger,
		IEntityLastSeen? lastSeen = null)
	{
		ArgumentNullException.ThrowIfNull(luxEntityIds);

		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_luxEntityIds = [.. luxEntityIds.Where(id => id is { Length: > 0 })];
		_settings = settings ?? throw new ArgumentNullException(nameof(settings));
		_staleAfter = staleAfter;
		_now = now ?? throw new ArgumentNullException(nameof(now));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_lastSeen = lastSeen;
		_startedAt = _now();
	}

	/// <summary>Whether the area currently counts as dark.</summary>
	// Polled at decision time and never driven by a subscription, so nothing turns lights off because it got bright.
	public bool IsDarkEnough()
	{
		lock (_gate)
		{
			_isDark = _settings.Darkness switch
			{
				DarknessSource.Always => true,
				DarknessSource.Sun => IsSunDown(),

				// Either answers identically to Lux, and is named here so it cannot fall to the default arm, which
				// would hold the previous verdict forever.
				DarknessSource.Lux or DarknessSource.Either => EvaluateLux() ?? DarkBecauseNoLuxSensorIsReporting(),
				_ => _isDark
			};

			return _isDark;
		}
	}

	/// <summary>The readings and thresholds behind the values <see cref="IsDarkEnough"/> last took.</summary>
	public string DarknessDetail() => _settings.Darkness switch
	{
		DarknessSource.Always => "darkness source is Always",
		DarknessSource.Sun => SunDetail(),

		// Ahead of the readings: with no sensor there is nothing to report, and the absence is itself the reason.
		DarknessSource.Lux or DarknessSource.Either when HasNoLuxSensor => NoSensorDetail,

		DarknessSource.Lux or DarknessSource.Either => _lastLux is { } lux ? LuxDetail(lux) : NoReadingDetail,
		_ => "unknown darkness source"
	};


	private bool HasNoLuxSensor => _luxEntityIds.Count == 0;

	// "3 of 4 sensors" is the only place a household hears that one of its sensors stopped reporting; a
	// single-sensor room is left off, where "1 of 1" is noise.
	private string LuxDetail(double lux) => _lastLuxOffered > 1
		? string.Create(CultureInfo.InvariantCulture,
			$"lux {lux:0} (mean of {_lastLuxUsed} of {_lastLuxOffered} sensors), dark below {_settings.LuxThreshold:0}")
		: string.Create(CultureInfo.InvariantCulture, $"lux {lux:0}, dark below {_settings.LuxThreshold:0}");

	private string SunDetail() => _lastElevation is { } degrees
		? string.Create(CultureInfo.InvariantCulture, $"sun elevation {degrees:0.#}°, dark below {_settings.SunElevationThreshold:0.#}°")
		: $"no sun elevation from {_settings.SunEntity}";

	/// <summary>The area's current illuminance, or <c>null</c> when no sensor resolved or none is reporting a usable number.</summary>
	// The one place the area's lux is read, so the darkness verdict and LuxBrightnessCurve consult the same
	// sensors. Several are averaged geometrically, because perceived brightness goes with the logarithm: 170 lx
	// and 3000 lx mean 714, where an arithmetic 1585 lands on the other side of a 1000 lx threshold. Free of side
	// effects, so reading the number cannot disturb what DarknessDetail says about the last verdict.
	public double? ReadLux() => AverageLux(out _, out _);

	private double? AverageLux(out int used, out int offered)
	{
		offered = _luxEntityIds.Count;
		used = 0;

		if (offered == 0)
			return null;

		List<double> readings = [];

		foreach (string entityId in _luxEntityIds)
		{
			EntityState? state = _ha.GetState(entityId);

			if (state.StateAsDouble() is not { } lux || IsStale(entityId, state))
				continue;

			readings.Add(lux);
		}

		used = readings.Count;

		if (used == 0)
			return null;

		// Non-positive readings are dropped: one 0 lx would drag a geometric mean to 0, and a negative has no log.
		List<double> positive = [.. readings.Where(lux => lux > 0)];

		// Nothing left means every sensor says pitch dark, which is a reading of 0 and not an absent reading.
		if (positive.Count == 0)
			return 0;

		return GeometricMean(positive);
	}

	// Computed in log space so a room with many sensors cannot overflow the product.
	private static double GeometricMean(List<double> readings) =>
		readings.Count == 1 ? readings[0] : Math.Exp(readings.Sum(Math.Log) / readings.Count);

	/// <summary>Whether a sensor has gone quiet for longer than the house allows.</summary>
	// LastUpdated, never LastChanged: a sensor sitting at a steady 3 lx all night would be condemned for being
	// consistent. A missing timestamp counts as fresh.
	private bool IsStale(string entityId, EntityState? state)
	{
		if (_staleAfter <= TimeSpan.Zero)
			return false;

		// The tracker outranks Home Assistant's fields because it survives the restart that resets them.
		if (_lastSeen is not null)
			return _lastSeen.HasBeenSilentFor(entityId, _staleAfter);

		if (state?.LastUpdated is not { } reported)
			return false;

		DateTimeOffset now = _now();

		// Grace period of one window. Home Assistant resets timestamps on restart, so before it elapses "silent
		// since start-up" and "only just started" look the same.
		if (now - _startedAt < _staleAfter)
			return false;

		return now - reported > _staleAfter;
	}

	/// <summary>The lux verdict: dark with no sensor, <c>null</c> when sensors exist but none reports, otherwise the average against the threshold.</summary>
	// No sensor is dark: with no reading the lux gate has nothing to refuse on. Hysteresis is one-sided,
	// LuxThreshold to become dark and LuxThreshold + LuxHysteresis to stop being dark, or a sensor resting on the
	// threshold strobes.
	private bool? EvaluateLux()
	{
		if (HasNoLuxSensor)
		{
			_lastLux = null;
			_lastLuxUsed = 0;
			_lastLuxOffered = 0;
			NoteThatTheAreaHasNoLuxSensor();
			return true;
		}

		double? reading = AverageLux(out int used, out int offered);
		_lastLuxUsed = used;
		_lastLuxOffered = offered;

		if (reading is not { } lux)
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


	/// <summary>Every one of the area's lux sensors is dead or silent, which counts as dark, and warns once.</summary>
	// The warning is the only thing separating this from a room that never had a sensor: no sensor is a supported
	// arrangement, while sensors that all stopped answering is a battery or an integration. Nothing consults the
	// sun here, and Either is answered like any other lux room.
	private bool DarkBecauseNoLuxSensorIsReporting()
	{
		if (!_warnedAboutUnreadableLuxSensors)
		{
			_warnedAboutUnreadableLuxSensors = true;
			_logger.LogWarning(
				"Darkness source is Lux and the area has {Count} lux sensor(s) ({Sensors}), but none of them is reporting a "
				+ "usable number: unavailable, unknown, or silent for longer than the staleness window. The area counts as "
				+ "dark until one of them answers again, so movement will light it.",
				_luxEntityIds.Count, string.Join(", ", _luxEntityIds));
		}

		return true;
	}

	// Information, never a warning: most rooms in most houses have no lux sensor.
	private void NoteThatTheAreaHasNoLuxSensor()
	{
		if (_saidTheAreaHasNoLuxSensor)
			return;

		_saidTheAreaHasNoLuxSensor = true;
		_logger.LogInformation(
			"No lux sensor for this area, so the lux gate never holds it back and it counts as dark. Give it a "
			+ "LuxSensor, or set FollowOutdoorLux to read the house's outdoor sensor, to gate it on a reading.");
	}
}
