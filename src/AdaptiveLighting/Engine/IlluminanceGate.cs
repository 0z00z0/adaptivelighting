using System.Globalization;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.LastSeen;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>
///     Answers the one question that gates auto-on: is this area dark enough to be worth lighting?
/// </summary>
/// <remarks>
///     Deliberately open-loop. A closed loop — raise the lights until the lux sensor is satisfied — oscillates,
///     because the lights are what the sensor is reading. Closing it needs a control design and per-sensor
///     calibration that v1 does not have.
/// </remarks>
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

	/// <summary>
	///     Who to ask when a sensor was last heard from, or <c>null</c> when nobody is tracking.
	/// </summary>
	/// <remarks>
	///     Home Assistant resets every entity's timestamps when it restarts, so its own fields cannot tell a
	///     sensor that died last week from one that reported a minute before the restart — measured on a live
	///     instance where the oldest timestamp in the house was the restart, 2.3 hours old. This tracker keeps its
	///     own record across both restarts and answers <c>false</c> when it has no opinion, so an unwatched house
	///     can never condemn a sensor.
	/// </remarks>
	private readonly IEntityLastSeen? _lastSeen;
	private readonly object _gate = new();

	private bool _isDark;
	private bool _warnedAboutUnreadableLuxSensors;
	private bool _saidTheAreaHasNoLuxSensor;

	// The last readings taken by IsDarkEnough, kept so DarknessDetail can explain a verdict — the actual lux and
	// sun-elevation numbers against the configured thresholds — without reading Home Assistant a second time.
	// _lastLuxUsed/_lastLuxOffered are how many sensors contributed to that average and how many were on offer,
	// which is the difference between "the room is bright" and "one of the room's three sensors has died".
	private double? _lastLux;
	private int _lastLuxUsed;
	private int _lastLuxOffered;
	private double? _lastElevation;

	// Which half settled the verdict, recorded rather than re-derived: hysteresis means a lux reading
	// cannot afterwards be re-tested against the threshold and give the same answer, and the || short-circuits the
	// sun away entirely on the path where lux decided. Written on every path through IsDarkByEither, and read by
	// nothing else, so no other source can leave a stale opinion behind.
	private bool _eitherLuxSaidDark;
	private bool _eitherSunSaidDark;

	/// <summary>
	///     Creates a gate for one area.
	/// </summary>
	/// <param name="ha">Used only to read state; the gate never commands anything.</param>
	/// <param name="luxEntityIds">
	///     The area's illuminance sensors, empty when it resolved none. More than one is averaged rather than
	///     chosen between — see <see cref="ReadLux"/>.
	/// </param>
	/// <param name="settings">Supplies the thresholds, the hysteresis and the choice of source.</param>
	/// <param name="staleAfter">
	///     How long a lux sensor may go without reporting before it is treated as dead. Zero or less switches the
	///     rule off. Illuminance only: see <see cref="GlobalConfig.LuxSensorStaleAfterMinutes"/> for why motion
	///     gets no such rule.
	/// </param>
	/// <param name="now">
	///     The gate's clock, so that "has this sensor reported lately" is asked against the same time base the
	///     rest of the area runs on and a test can answer it deterministically.
	/// </param>
	/// <param name="logger">Where the notes about missing and dead sensors go.</param>
	/// <param name="lastSeen">
	///     Tracks when each entity was genuinely last heard from, across both a Home Assistant restart and an
	///     engine restart. Optional: when absent the lux staleness rule falls back to Home Assistant's own
	///     timestamps, which reset on its restart and cannot tell a dead sensor from a quiet one.
	/// </param>
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

	/// <summary>
	///     Whether the area currently counts as dark. Polled at decision time rather than driven by a
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
				DarknessSource.Lux => EvaluateLux() ?? DarkBecauseNoLuxSensorIsReporting(),
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

		// Ahead of the readings: with no sensor there is no reading to report, and the reason the room counts as
		// dark is the absence itself rather than anything the sun is doing.
		DarknessSource.Lux when HasNoLuxSensor => NoSensorDetail,

		DarknessSource.Lux => _lastLux is { } lux ? LuxDetail(lux) : NoReadingDetail,
		_ => "unknown darkness source"
	};


	/// <summary>Whether the area resolved no lux sensor at all, as opposed to sensors that will not read.</summary>
	private bool HasNoLuxSensor => _luxEntityIds.Count == 0;

	/// <summary>
	///     The reading, the threshold, and — only when the room has more than one sensor — how many of them the
	///     figure came from.
	/// </summary>
	/// <remarks>
	///     "3 of 4 sensors" is the line that turns a puzzling average into a diagnosis, and it is the only place a
	///     household is told that one of its sensors stopped reporting. Left off a single-sensor room because
	///     "1 of 1" is noise on the overwhelming majority of rows.
	/// </remarks>
	private string LuxDetail(double lux) => _lastLuxOffered > 1
		? string.Create(CultureInfo.InvariantCulture,
			$"lux {lux:0} (mean of {_lastLuxUsed} of {_lastLuxOffered} sensors), dark below {_settings.LuxThreshold:0}")
		: string.Create(CultureInfo.InvariantCulture, $"lux {lux:0}, dark below {_settings.LuxThreshold:0}");

	private string SunDetail() => _lastElevation is { } degrees
		? string.Create(CultureInfo.InvariantCulture, $"sun elevation {degrees:0.#}°, dark below {_settings.SunElevationThreshold:0.#}°")
		: $"no sun elevation from {_settings.SunEntity}";

	/// <summary>
	///     The area's current illuminance, or <c>null</c> when it resolved no sensor or none of its sensors is
	///     still reporting a usable number.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The one place the area's lux is read, exposed so that anything else needing the number — the
	///         daylight brightness adjustment, <see cref="LuxBrightnessCurve"/> — consults the same sensors the
	///         darkness verdict does. The alternative, a second resolution path, is how a room ends up gating on
	///         one sensor and dimming to another.
	///     </para>
	///     <para>
	///         <b>Several sensors are averaged, not chosen between.</b> The area used to refuse to use any of them,
	///         on the ground that it could not tell which was the room's; averaging uses what the room actually
	///         has, and no single eccentric reading carries the decision on its own.
	///     </para>
	///     <para>
	///         <b>Geometrically, at the owner's decision.</b> Perceived brightness goes with the logarithm of
	///         illuminance, so the arithmetic mean of 170 lx and 3000 lx is 1585 — which is above a 1000 lx
	///         threshold — while the geometric mean is 714, which is below it. The second is the answer a person
	///         standing in that room would give.
	///     </para>
	///     <para>
	///         <b>Non-positive readings are dropped before the mean, and a room of nothing but them is 0.</b> A
	///         geometric mean multiplies, so one 0 lx reading would drag the whole room to 0 however bright its
	///         other sensors were, and a negative reading has no logarithm at all. Dropping them is not the same as
	///         ignoring darkness: if <i>every</i> surviving reading is at or below zero the answer is 0 — pitch
	///         dark, which is what those sensors are actually saying.
	///     </para>
	///     <para>
	///         Deliberately free of side effects, unlike <see cref="EvaluateLux"/>: it records nothing, so a caller
	///         asking only for the number cannot disturb what <see cref="DarknessDetail"/> reports about the last
	///         actual verdict.
	///     </para>
	/// </remarks>
	public double? ReadLux() => AverageLux(out _, out _);

	/// <summary>
	///     <see cref="ReadLux"/>, and how much of the room it came from.
	/// </summary>
	/// <remarks>
	///     A separate name rather than an overload, so that every <c>cref</c> to <see cref="ReadLux"/> — here and
	///     in <see cref="LuxBrightnessCurve"/> — keeps pointing at exactly one method.
	/// </remarks>
	/// <param name="used">How many sensors the returned figure came from.</param>
	/// <param name="offered">How many the area holds, whether or not they answered.</param>
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

		List<double> positive = [.. readings.Where(lux => lux > 0)];

		// Every sensor reading zero or less is a room that is genuinely pitch dark, not a room with no reading.
		if (positive.Count == 0)
			return 0;

		return GeometricMean(positive);
	}

	/// <summary>The geometric mean, computed in log space so a room with many sensors cannot overflow the product.</summary>
	private static double GeometricMean(List<double> readings) =>
		readings.Count == 1 ? readings[0] : Math.Exp(readings.Sum(Math.Log) / readings.Count);

	/// <summary>
	///     Whether a sensor has gone quiet for longer than the house allows.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b><see cref="EntityState.LastUpdated"/>, not <see cref="EntityState.LastChanged"/>.</b>
	///         <c>LastChanged</c> moves only when the value moves, so a sensor sitting at a steady 3 lx all night
	///         would be condemned for being consistent — which is the one thing a working sensor in a dark room is
	///         guaranteed to be. <c>LastUpdated</c> moves whenever Home Assistant heard from the entity at all,
	///         attributes included, which is as close as this HassModel version gets to "still reporting".
	///     </para>
	///     <para>
	///         <b>Nothing is stale before the engine has been watching for as long as the window.</b> Home
	///         Assistant resets every entity's timestamps when it restarts, so shortly after a restart "it has not
	///         reported since we started watching" is indistinguishable from "we have not been watching long" —
	///         measured on one live instance 2.3 hours after an HA restart, where a two-hour rule would have
	///         condemned most of the house. The grace period is the window itself, which is the shortest span that
	///         cannot produce a false positive.
	///     </para>
	///     <para>
	///         A state with no timestamp at all is not stale. Absence of a timestamp is absence of evidence, and
	///         reading it as death would kill a sensor over a payload shape rather than over its behaviour.
	///     </para>
	/// </remarks>
	private bool IsStale(string entityId, EntityState? state)
	{
		if (_staleAfter <= TimeSpan.Zero)
			return false;

		// The tracker outranks Home Assistant's own fields whenever there is one, because it survives the restart
		// that resets them. It answers false when it has no record, which is the safe reading and needs no guard.
		if (_lastSeen is not null)
			return _lastSeen.HasBeenSilentFor(entityId, _staleAfter);

		if (state?.LastUpdated is not { } reported)
			return false;

		DateTimeOffset now = _now();

		if (now - _startedAt < _staleAfter)
			return false;

		return now - reported > _staleAfter;
	}

	/// <summary>
	///     The lux verdict: dark when the area has no sensor at all, <c>null</c> when it has sensors but none of
	///     them is still reporting a number, and otherwise the average reading against the threshold.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>No sensor is dark, and that is the substantive rule.</b> A sensorless area used to be handed the
	///         house-wide outdoor sensor without anyone asking for it, and one shaded outdoor sensor reads hundreds
	///         of lux while every room behind it is dark — so the lux gate held off the whole house through
	///         daylight hours. The owner's rule replaced it: no reading means nothing for the lux gate to refuse
	///         on, so it refuses nothing. Following the outdoor sensor is now a room's own decision
	///         (<see cref="Configuration.AreaConfig.FollowOutdoorLux"/>), and a room that made it arrives here with
	///         a sensor id like any other.
	///     </para>
	///     <para>
	///         Sensors that <i>exist</i> and will not read still return <c>null</c> rather than <c>true</c>, so the
	///         caller decides what an absent verdict means: <see cref="DarknessSource.Lux"/> now reads it as dark, the
	///         same as having no sensor (<see cref="DarkBecauseNoLuxSensorIsReporting"/>), while
	///     </para>
	///     <para>
	///         Hysteresis is applied about the configured threshold: it takes <c>LuxThreshold</c> to become dark but
	///         <c>LuxThreshold + LuxHysteresis</c> to stop being dark. Without it a sensor resting on the threshold
	///         makes the area strobe.
	///     </para>
	/// </remarks>
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


	/// <summary>
	///     Every one of the area's lux sensors is dead or silent, which counts as dark — the same verdict as a room
	///     that never had one — and warns once on the way past.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The two cases are still worth telling apart, and the warning is now the whole of what tells them
	///         apart: a room that never had a sensor is a supported arrangement, whereas a room whose sensors have
	///         all stopped answering is somebody's battery, radio or integration, and nobody discovers that unless
	///         they are told. That reasoning is why this method exists at all.
	///     </para>
	///     <para>
	///         What changed is what follows from it. This used to hand the verdict to the sun, on the ground that the
	///         area had been told to gate on something real that was merely broken. The owner's rule overrules it: a
	///         room with nothing to read is dark, whether it has nothing because it was given nothing or because what
	///         it was given has died. Broken hardware is not a reason to leave a household unlit through a bright
	///         midsummer evening. <see cref="DarknessSource.Either"/> still lets the sun answer, because somebody who
	///         chose it asked for the sun.
	///     </para>
	/// </remarks>
	private bool DarkBecauseNoLuxSensorIsReporting()
	{
		if (!_warnedAboutUnreadableLuxSensors)
		{
			_warnedAboutUnreadableLuxSensors = true;
			_logger.LogWarning(
				"Darkness source is Lux and the area has {Count} lux sensor(s) ({Sensors}), but none of them is reporting a "
				+ "usable number — unavailable, unknown, or silent for longer than the staleness window. The area counts as "
				+ "dark until one of them answers again, so movement will light it.",
				_luxEntityIds.Count, string.Join(", ", _luxEntityIds));
		}

		return true;
	}

	/// <summary>
	///     Says once that the area has no lux sensor, and what that now means for it.
	/// </summary>
	/// <remarks>
	///     Information rather than a warning: an area with no lux sensor is an ordinary, supported arrangement —
	///     most rooms in most houses — and it is the case the new rule exists to serve. What it is worth saying is
	///     that the lux gate is therefore inert here, because a household reading "Darkness: Lux" in the document
	///     would otherwise expect a reading to be holding the room back.
	/// </remarks>
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
