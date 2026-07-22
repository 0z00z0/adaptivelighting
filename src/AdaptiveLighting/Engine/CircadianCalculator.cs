using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>
///     What the lights should be right now, and the caps that bound anything derived from it.
/// </summary>
/// <param name="PeriodName">The circadian period this came from.</param>
/// <param name="BrightnessPct">Target brightness, already clamped to the period's caps.</param>
/// <param name="ColorTempKelvin">Target colour temperature.</param>
/// <param name="MinBrightnessPct">The period's floor, or <c>null</c>. Callers deriving a dimmer level must respect it.</param>
/// <param name="MaxBrightnessPct">The period's ceiling, or <c>null</c>.</param>
public sealed record LightTarget(
	string PeriodName,
	double BrightnessPct,
	int ColorTempKelvin,
	double? MinBrightnessPct,
	double? MaxBrightnessPct)
{
	/// <summary>Clamps <paramref name="brightnessPct"/> to this target's caps and to the physical 0–100 range.</summary>
	public double Clamp(double brightnessPct)
	{
		double clamped = Math.Clamp(brightnessPct, MinBrightnessPct ?? 0, MaxBrightnessPct ?? 100);
		return Math.Clamp(clamped, 0, 100);
	}
}

/// <summary>Why a configured period could not be placed in the circadian table.</summary>
public enum PeriodDropReason
{
	/// <summary>The period's <c>Start</c> string could not be parsed as a clock time or a sun anchor.</summary>
	Unparseable,

	/// <summary>The period's sun-anchored <c>Start</c> has no sun time to resolve against for the day (polar night, a missing sun entity).</summary>
	Unresolvable
}

/// <summary>
///     A period the calculator had to leave out of the table, and why. Surfaced so a vanished period is a logged
///     warning rather than a silent hole the table wraps over — the failure mode behind "the area shows night at
///     04:16" when a sun-anchored morning boundary could not be placed.
/// </summary>
/// <param name="PeriodName">The dropped period's name.</param>
/// <param name="Start">Its raw <c>Start</c> string, as written in the configuration.</param>
/// <param name="Reason">Why it was dropped.</param>
public sealed record DroppedPeriod(string PeriodName, string Start, PeriodDropReason Reason);

/// <summary>
///     Turns the configured period table into the target for a given instant.
/// </summary>
/// <remarks>
///     Pure with respect to time and I/O: the instant is an argument and the day's sun times arrive through an
///     injected delegate, so a test supplies both and reads a deterministic answer. Nothing here reads a clock —
///     and, deliberately, nothing here logs. Periods it cannot use are surfaced through <see cref="DroppedPeriods"/>
///     and <see cref="PeriodDropped"/> for the constructing caller to log, rather than by taking an
///     <c>ILogger</c> that would drag I/O into an otherwise pure evaluation.
/// </remarks>
public sealed class CircadianCalculator
{
	private readonly IReadOnlyList<TimePeriodConfig> _periods;
	private readonly GlobalConfig _global;
	private readonly Func<SunTimes> _sunTimes;

	// The Start strings are parsed once here rather than on every GetTarget/ActivePeriodName call: the parse is
	// pure over the period table, only the sun-anchor resolution depends on the day, so per tick we do just
	// Resolve() + sort. Unparseable starts are dropped now, matching the old per-call skip.
	private readonly IReadOnlyList<(PeriodStart Start, TimePeriodConfig Period)> _parsedStarts;

	// Deduplicates the drop reporting: each distinct (period, start, reason) is surfaced once, so a sun-anchored
	// boundary that cannot be placed all day logs once rather than on all 1440 of the day's ticks.
	private readonly HashSet<DroppedPeriod> _dropped = [];

	/// <summary>
	///     Raised the first time a period is dropped during evaluation — a sun-anchored boundary the day's sun
	///     times cannot place. Deduplicated: a persistently-unresolvable period raises this once, not per tick.
	///     Parse failures are known before any subscriber can attach, so they arrive through
	///     <see cref="DroppedPeriods"/> instead; the constructing caller reads that and subscribes to this.
	/// </summary>
	public event Action<DroppedPeriod>? PeriodDropped;

	/// <summary>
	///     Every period dropped so far, each once. Populated with parse failures at construction and with
	///     resolution failures as evaluation discovers them, so a caller that reads this right after constructing
	///     sees the unparseable periods, and one that also subscribes to <see cref="PeriodDropped"/> sees the rest.
	/// </summary>
	public IReadOnlyCollection<DroppedPeriod> DroppedPeriods => _dropped;

	/// <summary>
	///     Creates a calculator over <paramref name="periods"/>.
	/// </summary>
	/// <param name="periods">The circadian table. Order is irrelevant; boundaries are sorted at each evaluation.</param>
	/// <param name="global">Supplies the blending knobs.</param>
	/// <param name="sunTimes">
	///     Supplies the day's sun times on demand. A delegate rather than a value because sunrise moves daily
	///     and a long-lived calculator would otherwise go stale; a delegate rather than an <c>IHaContext</c>
	///     because a test should not need one.
	/// </param>
	public CircadianCalculator(IReadOnlyList<TimePeriodConfig> periods, GlobalConfig global, Func<SunTimes> sunTimes)
	{
		_periods = periods ?? throw new ArgumentNullException(nameof(periods));
		_global = global ?? throw new ArgumentNullException(nameof(global));
		_sunTimes = sunTimes ?? throw new ArgumentNullException(nameof(sunTimes));

		List<(PeriodStart Start, TimePeriodConfig Period)> parsed = new(_periods.Count);
		foreach (TimePeriodConfig period in _periods)
			if (PeriodStart.TryParse(period.Start, out PeriodStart? start))
				parsed.Add((start!, period));
			else
				// Known now, before any subscriber can attach: recorded, not raised, so it reaches the caller
				// through DroppedPeriods.
				RecordDrop(new DroppedPeriod(period.Name, period.Start, PeriodDropReason.Unparseable), raiseEvent: false);

		_parsedStarts = parsed;
	}

	// Surfaces a dropped period once. The dedup set is what keeps a persistently-unresolvable sun-anchored
	// boundary from raising 1440 times a day; raiseEvent is false only for the parse failures found in the
	// constructor, which have no subscriber yet and travel through DroppedPeriods instead.
	private void RecordDrop(DroppedPeriod drop, bool raiseEvent)
	{
		if (!_dropped.Add(drop))
			return;

		if (raiseEvent)
			PeriodDropped?.Invoke(drop);
	}

	/// <summary>
	///     The target at <paramref name="now"/>, or <c>null</c> when no period can be placed — an all-sun-anchored
	///     table during polar night, say. A caller that gets <c>null</c> must command nothing rather than guess.
	/// </summary>
	/// <remarks>There is one shared table now (09 §3.5): every period is a candidate, full stop.</remarks>
	public LightTarget? GetTarget(DateTimeOffset now)
	{
		List<(TimeOnly Start, TimePeriodConfig Period)> boundaries = ResolveBoundaries();
		if (boundaries.Count == 0)
			return null;

		TimeOnly timeOfDay = TimeOnly.FromTimeSpan(now.TimeOfDay);
		int index = ActiveIndex(boundaries, timeOfDay);
		(TimeOnly Start, TimePeriodConfig Period) active = boundaries[index];

		if (!_global.SmoothTransitions || _global.BlendMinutes <= 0 || boundaries.Count == 1)
			return ToTarget(active.Period, active.Period.BrightnessPct, active.Period.ColorTempKelvin);

		(TimeOnly Start, TimePeriodConfig Period) previous = boundaries[(index - 1 + boundaries.Count) % boundaries.Count];
		double blend = BlendFraction(active.Start, timeOfDay);

		// Outside the window the boundary has already fully taken effect; inside it we are still arriving.
		if (blend >= 1)
			return ToTarget(active.Period, active.Period.BrightnessPct, active.Period.ColorTempKelvin);

		double brightness = Interpolate(previous.Period.BrightnessPct, active.Period.BrightnessPct, blend);
		double kelvin = Interpolate(previous.Period.ColorTempKelvin, active.Period.ColorTempKelvin, blend);

		// The caps come from the active period alone: they are the rule the reported period name promises.
		return ToTarget(active.Period, brightness, (int)Math.Round(kelvin));
	}

	/// <summary>
	///     The raw target of the period called <paramref name="periodName"/>, ignoring the clock entirely, or
	///     <c>null</c> when no such period exists. Sleep mode uses this to reach for the night rules at any hour.
	/// </summary>
	public LightTarget? GetPeriodTarget(string periodName)
	{
		TimePeriodConfig? period = _periods.FirstOrDefault(p => string.Equals(p.Name, periodName, StringComparison.OrdinalIgnoreCase));
		return period is null ? null : ToTarget(period, period.BrightnessPct, period.ColorTempKelvin);
	}

	private static LightTarget ToTarget(TimePeriodConfig period, double brightnessPct, int kelvin)
	{
		LightTarget target = new(period.Name, brightnessPct, kelvin, period.MinBrightnessPct, period.MaxBrightnessPct);
		return target with { BrightnessPct = target.Clamp(brightnessPct) };
	}

	/// <summary>
	///     The name of the period active at <paramref name="now"/>, or <c>null</c> when none can be placed. A
	///     name-only view over the same boundary resolution <see cref="GetTarget"/> uses, so
	///     <see cref="ModeMonitor"/>'s period-entry detection and the target maths can never disagree (09 §3.5).
	/// </summary>
	public string? ActivePeriodName(DateTimeOffset now)
	{
		List<(TimeOnly Start, TimePeriodConfig Period)> boundaries = ResolveBoundaries();
		if (boundaries.Count == 0)
			return null;

		int index = ActiveIndex(boundaries, TimeOnly.FromTimeSpan(now.TimeOfDay));
		return boundaries[index].Period.Name;
	}

	private List<(TimeOnly Start, TimePeriodConfig Period)> ResolveBoundaries()
	{
		SunTimes sunTimes = _sunTimes();
		List<(TimeOnly Start, TimePeriodConfig Period)> boundaries = new(_parsedStarts.Count);

		// One shared table (09 §3.5): every period is a candidate, full stop. The Start strings are already parsed
		// (in the constructor); only the sun-anchor resolution depends on the day.
		foreach ((PeriodStart? start, TimePeriodConfig? period) in _parsedStarts)
			// A sun-anchored boundary the sun entity cannot place is dropped, not guessed at. The remaining
			// periods still cover the day, because the table wraps — but the drop is surfaced (once) so a
			// vanished period is a warning a human can act on, not a silent wrap to some other period.
			if (start.Resolve(sunTimes) is { } resolved)
				boundaries.Add((resolved, period));
			else
				RecordDrop(new DroppedPeriod(period.Name, period.Start, PeriodDropReason.Unresolvable), raiseEvent: true);

		boundaries.Sort((left, right) => left.Start.CompareTo(right.Start));
		return boundaries;
	}

	/// <summary>The last boundary at or before <paramref name="timeOfDay"/>, wrapping to yesterday's last period.</summary>
	private static int ActiveIndex(List<(TimeOnly Start, TimePeriodConfig Period)> boundaries, TimeOnly timeOfDay)
	{
		int index = -1;
		for (int i = 0; i < boundaries.Count; i++)
			if (boundaries[i].Start <= timeOfDay)
				index = i;

		return index < 0 ? boundaries.Count - 1 : index;
	}

	/// <summary>
	///     How far the blend away from <paramref name="boundary"/> has progressed at <paramref name="timeOfDay"/>:
	///     0 at the boundary, 1 once <c>BlendMinutes</c> have passed.
	/// </summary>
	/// <remarks>
	///     The window runs forward from the boundary rather than straddling it. Straddling would mean drifting
	///     toward a period that has not begun, while the reported period name — and the caps derived from it —
	///     still named the old one. Trailing the boundary keeps the name, the caps and the levels describing the
	///     same period at every instant.
	/// </remarks>
	private double BlendFraction(TimeOnly boundary, TimeOnly timeOfDay)
	{
		TimeSpan elapsed = timeOfDay.ToTimeSpan() - boundary.ToTimeSpan();

		// The active boundary may lie before midnight while we are already after it.
		if (elapsed < TimeSpan.Zero)
			elapsed += TimeSpan.FromDays(1);

		return Math.Clamp(elapsed.TotalMinutes / _global.BlendMinutes, 0, 1);
	}

	private static double Interpolate(double from, double to, double fraction) => from + ((to - from) * fraction);
}
