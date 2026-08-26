using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>Which of a target's two values the room replaced instead of taking from the schedule.</summary>
// Flags, because a room may replace one value and inherit the other.
[Flags]
public enum RoomLevelSource
{
	None = 0,
	Brightness = 1,
	ColorTemp = 2
}

/// <summary>What the lights should be right now.</summary>
// FromRoom says which values came from the room's own AreaConfig.Levels, and is carried here so nothing
// downstream re-reads the room's overrides and reaches a different answer.
public sealed record LightTarget(
	string PeriodName,
	double BrightnessPct,
	int ColorTempKelvin,
	RoomLevelSource FromRoom = RoomLevelSource.None)
{
	/// <summary>Whether the period in force hands its brightness to the daylight curve.</summary>
	// Carried from the active period alone, as FromRoom is, so a blend across a boundary cannot report one
	// period's name beside the other's rule.
	public bool UsesDaylightCurve { get; init; }

	// The physical bound a lamp can take, and nothing else: a period imposes no floor or ceiling.
	public double Clamp(double brightnessPct) => Math.Clamp(brightnessPct, 0, 100);
}

/// <summary>Why a configured period could not be placed in the circadian table.</summary>
public enum PeriodDropReason
{
	/// <summary>The <c>Start</c> string is neither a clock time nor a sun anchor.</summary>
	Unparseable,

	/// <summary>A sun-anchored <c>Start</c> with no sun time to resolve against today: polar night, or no sun entity.</summary>
	Unresolvable
}

/// <summary>A period the calculator had to leave out of the table, and why.</summary>
// Surfaced so a vanished period is a logged warning and never a silent hole the table wraps over.
public sealed record DroppedPeriod(string PeriodName, string Start, PeriodDropReason Reason);

/// <summary>Turns the configured period table into the target for a given instant, for one room.</summary>
// Pure with respect to time and I/O: the instant is an argument, the day's sun times arrive through a delegate,
// and nothing here logs. Every member is safe to call from any thread; the drop record is the only mutable state
// and is guarded here, so no caller has to hold a lock of its own.
// Room-scoped, because the blend has to interpolate this room's two levels, and LevelsOf is the single place a
// room's effective level is decided.
// Under PeriodAuthority.HomeAssistant a dropdown decides which period is in force, and OverriddenPeriod is the
// one point both public answers go through. A period that waits for movement is left out of the table until it
// has begun, so the previous period keeps running and the next period's start overtakes it. GetPeriodTarget sits
// outside both: a caller naming a period gets the one it named, which is what the sleep clamp needs.
public sealed class CircadianCalculator
{
	private readonly IReadOnlyList<TimePeriodConfig> _periods;
	private readonly GlobalConfig _global;
	private readonly Func<SunTimes> _sunTimes;

	// Absent except under HomeAssistant period authority, never installed and answering null.
	private readonly Func<string?>? _periodOverride;

	// Answers whether a period that waits for movement has still not begun on the local day its instance would
	// have started. Installed by the host, so this stays a predicate and never reads motion.
	private readonly Func<string, DateOnly, bool>? _heldBack;
	private readonly TimeZoneInfo _zone;

	// Keyed by TimePeriodConfig.Key. First row wins on a duplicate, matching what the validator reports.
	private readonly Dictionary<string, RoomLevelOverride> _roomLevels;

	// Parsed once: only the sun-anchor resolution depends on the day, so a tick is Resolve() plus a sort.
	private readonly IReadOnlyList<(PeriodStart Start, TimePeriodConfig Period)> _parsedStarts;

	// Dedup for the drop reporting, or an unplaceable boundary reports on all 1440 of the day's ticks.
	private readonly HashSet<DroppedPeriod> _dropped = [];

	// _dropped is the only mutable state here, and NextBoundary reaches it from BoundaryTimer's own thread.
	private readonly Lock _gate = new();

	/// <summary>Raised the first time a period is dropped during evaluation, deduplicated.</summary>
	// Parse failures are known before any subscriber can attach, so they arrive through DroppedPeriods instead.
	public event Action<DroppedPeriod>? PeriodDropped;

	/// <summary>The drops recorded so far, as a snapshot, so a reader cannot enumerate while an evaluation adds.</summary>
	public IReadOnlyCollection<DroppedPeriod> DroppedPeriods
	{
		get
		{
			lock (_gate)
				return [.. _dropped];
		}
	}

	// roomLevels is what one room runs instead of the schedule; null or empty answers for the house. Rows naming
	// no configured period are never matched. periodOverride comes from PeriodSelectReader.ReadPeriod alone, and
	// null resolves from the schedule. periodHeldBack comes from MotionPeriodLatch.IsHeldBack, and null places
	// every period on its own Start. zone is the household's: a boundary is a wall clock and the instants handed
	// in are not.
	/// <summary>Creates a calculator over <paramref name="periods"/>, whose order is irrelevant.</summary>
	public CircadianCalculator(
		IReadOnlyList<TimePeriodConfig> periods,
		GlobalConfig global,
		Func<SunTimes> sunTimes,
		IReadOnlyList<RoomLevelOverride>? roomLevels = null,
		Func<string?>? periodOverride = null,
		Func<string, DateOnly, bool>? periodHeldBack = null,
		TimeZoneInfo? zone = null)
	{
		_periods = periods ?? throw new ArgumentNullException(nameof(periods));
		_global = global ?? throw new ArgumentNullException(nameof(global));
		_sunTimes = sunTimes ?? throw new ArgumentNullException(nameof(sunTimes));
		_periodOverride = periodOverride;
		_heldBack = periodHeldBack;
		_zone = zone ?? TimeZoneInfo.Local;

		Dictionary<string, RoomLevelOverride> levels = new(StringComparer.OrdinalIgnoreCase);

		// An empty row must not shadow a later row that says something. The normaliser drops these on save, but a
		// hand-edited file never passes through it.
		foreach (RoomLevelOverride level in roomLevels ?? [])
			if (level is { IsEmpty: false, PeriodId: { Length: > 0 } periodId })
				levels.TryAdd(periodId.Trim(), level);

		_roomLevels = levels;

		List<(PeriodStart Start, TimePeriodConfig Period)> parsed = new(_periods.Count);
		foreach (TimePeriodConfig period in _periods)
			if (PeriodStart.TryParse(period.Start, out PeriodStart? start))
				parsed.Add((start!, period));
			else
				// No subscriber can exist yet, so record without raising and let DroppedPeriods carry it.
				RecordDrop(new DroppedPeriod(period.Name, period.Start, PeriodDropReason.Unparseable), raiseEvent: false);

		_parsedStarts = parsed;
	}

	// raiseEvent is false only for the constructor's parse failures.
	private void RecordDrop(DroppedPeriod drop, bool raiseEvent)
	{
		bool first;

		lock (_gate)
			first = _dropped.Add(drop);

		// Outside the lock: a subscriber logs, and holding this one across it would be a second lock order.
		if (first && raiseEvent)
			PeriodDropped?.Invoke(drop);
	}

	/// <summary>The target at <paramref name="now"/>, or <c>null</c> when no period can be placed.</summary>
	// A caller that gets null must command nothing. Under an override there is no boundary time and so no blend:
	// the change is a step.
	public LightTarget? GetTarget(DateTimeOffset now)
	{
		// Before the clock, so this and ActivePeriodId cannot disagree about which period is in force.
		if (OverriddenPeriod() is { } forced)
			return TargetOf(forced);

		List<(TimeOnly Start, TimePeriodConfig Period)> boundaries = ResolveBoundaries(now);
		if (boundaries.Count == 0)
			return null;

		TimeOnly timeOfDay = now.TimeIn(_zone);
		int index = ActiveIndex(boundaries, timeOfDay);
		(TimeOnly Start, TimePeriodConfig Period) active = boundaries[index];
		PeriodLevels arriving = LevelsOf(active.Period);

		if (!_global.SmoothTransitions || _global.BlendMinutes <= 0 || boundaries.Count == 1)
			return ToTarget(active.Period, arriving);

		(TimeOnly Start, TimePeriodConfig Period) previous = boundaries[(index - 1 + boundaries.Count) % boundaries.Count];
		double blend = BlendFraction(active.Start, timeOfDay);

		if (blend >= 1)
			return ToTarget(active.Period, arriving);

		// Both ends go through LevelsOf, so the interpolation runs on this room's two levels. Blending the house's
		// endpoints and replacing the result afterwards puts a step where the blend exists to remove one.
		PeriodLevels leaving = LevelsOf(previous.Period);

		double brightness = Interpolate(leaving.BrightnessPct, arriving.BrightnessPct, blend);
		double kelvin = Interpolate(leaving.ColorTempKelvin, arriving.ColorTempKelvin, blend);

		// FromRoom comes from the active period alone: it is the rule the reported period name promises.
		return ToTarget(active.Period, arriving with { BrightnessPct = brightness, ColorTempKelvin = (int)Math.Round(kelvin) });
	}

	/// <summary>The raw target of the period keyed <paramref name="periodKey"/>, ignoring the clock.</summary>
	// The room's own levels apply, so the sleep clamp holds a room to its own night rules and never the house's.
	public LightTarget? GetPeriodTarget(string periodKey)
	{
		TimePeriodConfig? period = PeriodWithKey(periodKey);
		return period is null ? null : TargetOf(period);
	}

	/// <summary>The period a reference names, or <c>null</c> when this table has none.</summary>
	public TimePeriodConfig? PeriodWithKey(string? periodKey) =>
		periodKey is { Length: > 0 } ? _periods.ByKey(periodKey) : null;

	private LightTarget TargetOf(TimePeriodConfig period) => ToTarget(period, LevelsOf(period));

	// The single place a room's effective level is decided; everything downstream reads LightTarget.FromRoom
	// instead of the overrides. A replacement, never an offset: a room asking 8 % still asks 8 % after the house
	// raises the period.
	private PeriodLevels LevelsOf(TimePeriodConfig period)
	{
		if (!_roomLevels.TryGetValue(period.Key, out RoomLevelOverride? level))
			return new PeriodLevels(period.BrightnessPct, period.ColorTempKelvin, RoomLevelSource.None);

		RoomLevelSource fromRoom =
			(level.BrightnessPct is null ? RoomLevelSource.None : RoomLevelSource.Brightness)
			| (level.ColorTempKelvin is null ? RoomLevelSource.None : RoomLevelSource.ColorTemp);

		return new PeriodLevels(
			level.BrightnessPct ?? period.BrightnessPct,
			level.ColorTempKelvin ?? period.ColorTempKelvin,
			fromRoom);
	}

	private readonly record struct PeriodLevels(double BrightnessPct, int ColorTempKelvin, RoomLevelSource FromRoom);

	// One path for both, so a room's own replacement is clamped the same way the schedule's value is.
	private static LightTarget ToTarget(TimePeriodConfig period, PeriodLevels levels)
	{
		LightTarget target = new(period.Name, levels.BrightnessPct, levels.ColorTempKelvin, levels.FromRoom)
		{
			UsesDaylightCurve = period.UseDaylightCurve
		};

		return target with { BrightnessPct = target.Clamp(levels.BrightnessPct) };
	}

	/// <summary>The key of the period active at <paramref name="now"/>, or <c>null</c> when none can be placed.</summary>
	// A key-only view over the same boundary resolution GetTarget uses, so ModeMonitor's period-entry detection
	// and the target maths cannot disagree.
	public string? ActivePeriodId(DateTimeOffset now)
	{
		if (OverriddenPeriod() is { } forced)
			return forced.Key;

		return KeyAt(ResolveBoundaries(now), now);
	}

	/// <summary>The period the clock alone places at <paramref name="now"/>, ignoring the override and any hold.</summary>
	// This is the period movement would be offered, the one ActivePeriodId is holding back, and says nothing
	// about what the lights are doing. Only ModeMonitor's motion rule asks.
	public string? ScheduledPeriodId(DateTimeOffset now) => KeyAt(ResolveBoundaries(now, respectHold: false), now);

	/// <summary>The instant the next boundary after <paramref name="now"/> falls on, wrapping to the next local day.</summary>
	// The moment a caller should ask its ordinary questions again; the answer it gets then still comes from
	// ActivePeriodId. A period still waiting for movement is absent, so nothing wakes for a boundary that will
	// not be crossed.
	public DateTimeOffset? NextBoundary(DateTimeOffset now)
	{
		List<(TimeOnly Start, TimePeriodConfig Period)> boundaries = ResolveBoundaries(now);
		if (boundaries.Count == 0)
			return null;

		TimeOnly timeOfDay = now.TimeIn(_zone);
		DateOnly today = now.DayIn(_zone);

		foreach ((TimeOnly start, TimePeriodConfig _) in boundaries)
			if (start > timeOfDay)
				return InstantOf(today, start);

		return InstantOf(today.AddDays(1), boundaries[0].Start);
	}

	/// <summary>A household wall clock on a household day, as the instant it happens at.</summary>
	// A wall clock the spring-forward gap swallows never happens, so the boundary arrives at the first minute
	// that does; an ambiguous autumn one takes ConvertTimeToUtc's standard-time answer, the rule the rest of the
	// table resolves under.
	private DateTimeOffset InstantOf(DateOnly day, TimeOnly boundary)
	{
		DateTime local = day.ToDateTime(boundary, DateTimeKind.Unspecified);

		while (_zone.IsInvalidTime(local))
			local = local.AddMinutes(1);

		return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, _zone));
	}

	private string? KeyAt(List<(TimeOnly Start, TimePeriodConfig Period)> boundaries, DateTimeOffset now) =>
		boundaries.Count == 0
			? null
			: boundaries[ActiveIndex(boundaries, now.TimeIn(_zone))].Period.Key;

	// The single point every consumer of an override goes through. An unmatched id falls through to the schedule,
	// which the validator and PeriodSelectReader already report, and taking a room dark over a stale mapping
	// would be worse.
	private TimePeriodConfig? OverriddenPeriod() => PeriodWithKey(_periodOverride?.Invoke());

	// The day's placeable boundaries, sorted. A period still waiting for movement is absent, so the wrap keeps the
	// previous period running and the next period's own start overtakes it.
	private List<(TimeOnly Start, TimePeriodConfig Period)> ResolveBoundaries(DateTimeOffset now, bool respectHold = true)
	{
		SunTimes sunTimes = _sunTimes();
		List<(TimeOnly Start, TimePeriodConfig Period)> boundaries = new(_parsedStarts.Count);
		TimeOnly timeOfDay = now.TimeIn(_zone);
		DateOnly today = now.DayIn(_zone);

		// A sun-anchored boundary the sun entity cannot place is dropped, never guessed at. The table still covers
		// the day by wrapping, so the drop has to be reported or the wrap is silent.
		foreach ((PeriodStart? start, TimePeriodConfig? period) in _parsedStarts)
			if (start.Resolve(sunTimes) is { } resolved)
			{
				// A boundary still ahead belongs to the instance that began yesterday, which is the one the wrap
				// puts in force. Asking about today would ask about a period that has not come round yet.
				if (respectHold
					&& _heldBack?.Invoke(period.Key, resolved <= timeOfDay ? today : today.AddDays(-1)) == true)
					continue;

				boundaries.Add((resolved, period));
			}
			else
				RecordDrop(new DroppedPeriod(period.Name, period.Start, PeriodDropReason.Unresolvable), raiseEvent: true);

		boundaries.Sort((left, right) => left.Start.CompareTo(right.Start));
		return boundaries;
	}

	// The last boundary at or before timeOfDay, wrapping to yesterday's last period.
	private static int ActiveIndex(List<(TimeOnly Start, TimePeriodConfig Period)> boundaries, TimeOnly timeOfDay)
	{
		int index = -1;
		for (int i = 0; i < boundaries.Count; i++)
			if (boundaries[i].Start <= timeOfDay)
				index = i;

		return index < 0 ? boundaries.Count - 1 : index;
	}

	// How far the blend away from the boundary has progressed: 0 at the boundary, 1 once BlendMinutes have passed.
	// The window trails the boundary and never straddles it, so the reported name and the levels describe the
	// same period at every instant.
	private double BlendFraction(TimeOnly boundary, TimeOnly timeOfDay)
	{
		TimeSpan elapsed = timeOfDay.ToTimeSpan() - boundary.ToTimeSpan();

		// The active boundary may lie before midnight while timeOfDay is already past it.
		if (elapsed < TimeSpan.Zero)
			elapsed += TimeSpan.FromDays(1);

		return Math.Clamp(elapsed.TotalMinutes / _global.BlendMinutes, 0, 1);
	}

	private static double Interpolate(double from, double to, double fraction) => from + ((to - from) * fraction);
}
