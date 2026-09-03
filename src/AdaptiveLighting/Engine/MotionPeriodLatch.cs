using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>Where a period that waits for movement stands on one local day.</summary>
/// <remarks>
///     One value and not two delegates: <see cref="BegunAt"/> is what the blend eases from, and a calculator
///     handed the hold without the instant would silently keep blending from the boundary.
/// </remarks>
// BegunAt is null both for a period nothing started and for one seeded by a restart, which is the same answer
// to the only question asked of it: there is no arrival to ease away from.
public readonly record struct PeriodHold(bool HeldBack, DateTimeOffset? BegunAt)
{
	/// <summary>A period on its clock start, which is every period where no latch is installed.</summary>
	public static PeriodHold OnTheClock => new(false, null);
}

/// <summary>Which periods wait for movement before they begin, and which of those have begun on a given local day.</summary>
// One instance per engine. ModeMonitor is the only writer; every area's CircadianCalculator reads it through
// StateOf and leaves a period out of the table until the house has moved. Holding nothing stands the whole
// rule down.
public sealed class MotionPeriodLatch
{
	// Settled in the constructor and never written again, so it is read without the gate.
	private readonly HashSet<string> _held;

	// Guards _begunOn only. Written from Home Assistant's threads, read from every area's tick.
	private readonly object _gate = new();

	private readonly Dictionary<string, Begun> _begunOn = new(StringComparer.OrdinalIgnoreCase);

	// At is null where the start was seeded from the note on disk: the run that began the period is gone and its
	// instant with it, so the blend falls back to the clock boundary rather than restarting at the restart.
	private readonly record struct Begun(DateOnly Day, DateTimeOffset? At);

	public MotionPeriodLatch(IEnumerable<string>? heldPeriods = null)
	{
		_held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (string key in heldPeriods ?? [])
			if (key?.Trim() is { Length: > 0 } trimmed)
				_held.Add(trimmed);
	}

	/// <summary>The latch for <paramref name="periods"/>, holding back every one that asks to start on motion.</summary>
	// The single branch on period authority, mirroring PeriodSelectReader's: under Home Assistant's the dropdown
	// is the only boundary, so nothing is held and nothing can be started by movement.
	public static MotionPeriodLatch For(IReadOnlyList<TimePeriodConfig>? periods, GlobalConfig? global)
	{
		if (global?.PeriodSelect is { EntityId: not null, Authority: PeriodAuthority.HomeAssistant })
			return new MotionPeriodLatch();

		return new MotionPeriodLatch(
			(periods ?? []).Where(period => period.StartsOnMotion).Select(period => period.Key));
	}

	/// <summary>The periods that do not begin on the clock.</summary>
	public IReadOnlyCollection<string> HeldPeriods => _held;

	/// <summary>Whether <paramref name="periodKey"/> waits for movement instead of beginning on its <c>Start</c>.</summary>
	public bool Holds(string? periodKey) => periodKey is { Length: > 0 } && _held.Contains(periodKey);

	/// <summary>Whether the instance of <paramref name="periodKey"/> that began on <paramref name="day"/> has started.</summary>
	public bool HasBegun(string? periodKey, DateOnly day) => Recorded(periodKey, day) is not null;

	/// <summary>The instant movement began that instance, or <c>null</c> when it has not begun or was seeded.</summary>
	public DateTimeOffset? BegunAt(string? periodKey, DateOnly day) => Recorded(periodKey, day)?.At;

	/// <summary>Whether the calculator must leave <paramref name="periodKey"/> out of the table for <paramref name="instanceDay"/>.</summary>
	public bool IsHeldBack(string? periodKey, DateOnly instanceDay) =>
		Holds(periodKey) && !HasBegun(periodKey, instanceDay);

	/// <summary>Where <paramref name="periodKey"/>'s instance on <paramref name="instanceDay"/> stands: what the calculator reads.</summary>
	// One read, so the hold and the arrival cannot come from either side of a concurrent start.
	public PeriodHold StateOf(string? periodKey, DateOnly instanceDay)
	{
		Begun? begun = Recorded(periodKey, instanceDay);
		return new PeriodHold(Holds(periodKey) && begun is null, begun?.At);
	}

	/// <summary>Records the start of <paramref name="periodKey"/> without an instant, for a run that inherited it.</summary>
	public void MarkBegun(string? periodKey, DateOnly day)
	{
		if (periodKey is not { Length: > 0 })
			return;

		lock (_gate)
			_begunOn[periodKey] = new Begun(day, null);
	}

	/// <summary>Claims the day's one start of <paramref name="periodKey"/>, or answers <c>false</c> when it is spent.</summary>
	// Atomic, so two sensors tripping at once cannot both start the period, and the instant recorded is the one
	// belonging to the movement that won.
	public bool TryBegin(string? periodKey, DateOnly day, DateTimeOffset at)
	{
		if (periodKey is not { Length: > 0 })
			return false;

		lock (_gate)
		{
			if (_begunOn.TryGetValue(periodKey, out Begun begun) && begun.Day == day)
				return false;

			_begunOn[periodKey] = new Begun(day, at);
			return true;
		}
	}

	private Begun? Recorded(string? periodKey, DateOnly day)
	{
		if (periodKey is not { Length: > 0 })
			return null;

		lock (_gate)
			return _begunOn.TryGetValue(periodKey, out Begun begun) && begun.Day == day ? begun : null;
	}
}
