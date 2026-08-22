using AdaptiveLighting.LastSeen;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>Reads one illuminance figure from a set of sensors, dropping whatever has gone quiet.</summary>
// The single implementation of "what is the light level here", shared by the darkness gate and the daylight
// curve, which read different sensors and must not average them differently. Free of side effects, so reading
// the number cannot disturb what a caller last decided.
public sealed class LuxReader
{
	private readonly IHaContext _ha;
	private readonly IReadOnlyList<string> _entityIds;
	private readonly TimeSpan _staleAfter;
	private readonly Func<DateTimeOffset> _now;
	private readonly DateTimeOffset _startedAt;

	// Home Assistant resets every entity's timestamp on restart, so its own fields cannot tell a sensor that died
	// last week from one that reported a minute before the restart. The tracker survives a restart and answers
	// false when it has no record.
	private readonly IEntityLastSeen? _lastSeen;

	// entityIds may be empty, and several are averaged. A staleAfter of zero or less switches the staleness rule
	// off; without a lastSeen it falls back to Home Assistant's own timestamps.
	public LuxReader(
		IHaContext ha,
		IReadOnlyList<string> entityIds,
		TimeSpan staleAfter,
		Func<DateTimeOffset> now,
		IEntityLastSeen? lastSeen = null)
	{
		ArgumentNullException.ThrowIfNull(entityIds);

		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_entityIds = [.. entityIds.Where(id => id is { Length: > 0 })];
		_staleAfter = staleAfter;
		_now = now ?? throw new ArgumentNullException(nameof(now));
		_lastSeen = lastSeen;
		_startedAt = _now();
	}

	/// <summary>The sensors this reader was given, with the empty ids dropped.</summary>
	public IReadOnlyList<string> EntityIds => _entityIds;

	/// <summary>The reading, or <c>null</c> when no sensor was given or none is reporting a usable number.</summary>
	public double? Read() => Read(out _, out _);

	/// <summary>The reading, plus how many sensors answered out of how many were offered.</summary>
	// Several are averaged geometrically, because perceived brightness goes with the logarithm: 170 lx and
	// 3000 lx mean 714, where an arithmetic 1585 lands on the other side of a 1000 lx threshold.
	public double? Read(out int used, out int offered)
	{
		offered = _entityIds.Count;
		used = 0;

		if (offered == 0)
			return null;

		List<double> readings = [];

		foreach (string entityId in _entityIds)
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
}
