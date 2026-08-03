using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>
///     Which periods wait for movement before they begin, and which of those have begun on a given local day.
/// </summary>
/// <remarks>
///     One instance per engine. <see cref="ModeMonitor"/> is the only writer; every area's
///     <see cref="CircadianCalculator"/> reads it through <see cref="IsHeldBack"/> and leaves a period out of the
///     table until the house has moved. Holding nothing stands the whole rule down, which is what
///     <see cref="For"/> returns under <see cref="PeriodAuthority.HomeAssistant"/>.
/// </remarks>
public sealed class MotionPeriodLatch
{
	// Settled in the constructor and never written again, so it is read without the gate.
	private readonly HashSet<string> _held;

	// Guards _begunOn only. Written from Home Assistant's threads, read from every area's tick.
	private readonly object _gate = new();

	// One entry per held period, overwritten as the days go by.
	private readonly Dictionary<string, DateOnly> _begunOn = new(StringComparer.OrdinalIgnoreCase);

	public MotionPeriodLatch(IEnumerable<string>? heldPeriods = null)
	{
		_held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (string name in heldPeriods ?? [])
			if (name?.Trim() is { Length: > 0 } trimmed)
				_held.Add(trimmed);
	}

	/// <summary>
	///     The latch for <paramref name="periods"/>, holding back every one that asks to start on motion.
	/// </summary>
	/// <remarks>
	///     The single branch on period authority, mirroring <see cref="PeriodSelectReader"/>'s: under Home
	///     Assistant's the dropdown is the only boundary, so nothing is held and nothing can be started by movement.
	/// </remarks>
	public static MotionPeriodLatch For(IReadOnlyList<TimePeriodConfig>? periods, GlobalConfig? global)
	{
		if (global?.PeriodSelect is { EntityId: not null, Authority: PeriodAuthority.HomeAssistant })
			return new MotionPeriodLatch();

		return new MotionPeriodLatch(
			(periods ?? []).Where(period => period.StartsOnMotion).Select(period => period.Name));
	}

	/// <summary>The periods that do not begin on the clock.</summary>
	public IReadOnlyCollection<string> HeldPeriods => _held;

	/// <summary>Whether <paramref name="periodName"/> waits for movement instead of beginning on its <c>Start</c>.</summary>
	public bool Holds(string? periodName) => periodName is { Length: > 0 } && _held.Contains(periodName);

	/// <summary>Whether the instance of <paramref name="periodName"/> that began on <paramref name="day"/> has started.</summary>
	public bool HasBegun(string? periodName, DateOnly day)
	{
		if (periodName is not { Length: > 0 })
			return false;

		lock (_gate)
			return _begunOn.TryGetValue(periodName, out DateOnly begun) && begun == day;
	}

	/// <summary>
	///     Whether the calculator must leave <paramref name="periodName"/> out of the table for the instance that
	///     would have begun on <paramref name="instanceDay"/>.
	/// </summary>
	public bool IsHeldBack(string? periodName, DateOnly instanceDay) =>
		Holds(periodName) && !HasBegun(periodName, instanceDay);

	/// <summary>Records that <paramref name="periodName"/> began on <paramref name="day"/>, whatever started it.</summary>
	public void MarkBegun(string? periodName, DateOnly day)
	{
		if (periodName is not { Length: > 0 })
			return;

		lock (_gate)
			_begunOn[periodName] = day;
	}

	/// <summary>
	///     Claims the day's one start of <paramref name="periodName"/>, or answers <c>false</c> when it is already
	///     spent.
	/// </summary>
	/// <remarks>Atomic, so two sensors tripping at once cannot both start the period.</remarks>
	public bool TryBegin(string? periodName, DateOnly day)
	{
		if (periodName is not { Length: > 0 })
			return false;

		lock (_gate)
		{
			if (_begunOn.TryGetValue(periodName, out DateOnly begun) && begun == day)
				return false;

			_begunOn[periodName] = day;
			return true;
		}
	}
}
