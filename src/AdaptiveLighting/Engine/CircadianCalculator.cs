using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>
///     Which of a target's two values the room replaced, rather than taking from the schedule.
/// </summary>
/// <remarks>
///     Flags rather than a three-way choice because the two values are independent: a room that only wants to be
///     dimmer goes on inheriting every later change to the schedule's colour, and a reader that could only say
///     "this room has overrides" would have to guess which half it was looking at.
/// </remarks>
[Flags]
public enum RoomLevelSource
{
	/// <summary>Neither: the schedule's own levels, which is what every room without an override runs.</summary>
	None = 0,

	/// <summary>The room named its own brightness for this period.</summary>
	Brightness = 1,

	/// <summary>The room named its own colour temperature for this period.</summary>
	ColorTemp = 2
}

/// <summary>
///     What the lights should be right now, and the caps that bound anything derived from it.
/// </summary>
/// <param name="PeriodName">The circadian period this came from.</param>
/// <param name="BrightnessPct">Target brightness.</param>
/// <param name="ColorTempKelvin">Target colour temperature.</param>
/// <param name="FromRoom">
///     Which of the two values above came from the room's own <see cref="AreaConfig.Levels"/> rather than from the
///     schedule. Read from the active period alone: it is the rule <paramref name="PeriodName"/> promises. Carried
///     on the target rather than re-derived by whoever renders it, because a second reading of the room's
///     overrides is how the board, the sentence and the lamp end up disagreeing about which is in charge.
/// </param>
public sealed record LightTarget(
	string PeriodName,
	double BrightnessPct,
	int ColorTempKelvin,
	RoomLevelSource FromRoom = RoomLevelSource.None)
{
	/// <summary>
	///     Holds <paramref name="brightnessPct"/> to what a lamp can actually be set to.
	/// </summary>
	/// <remarks>
	///     Once this also applied the period's own floor and ceiling. Those were removed in the 2026-07
	///     simplification — a period's target is already the answer to "how bright at this hour", and a second
	///     pair of numbers qualifying it was a rule about a rule. What is left is the physical bound, which is
	///     not a preference and cannot be configured away.
	/// </remarks>
	public double Clamp(double brightnessPct) => Math.Clamp(brightnessPct, 0, 100);
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
///     Turns the configured period table into the target for a given instant, for one room.
/// </summary>
/// <remarks>
///     <para>
///         Pure with respect to time and I/O: the instant is an argument and the day's sun times arrive through an
///         injected delegate, so a test supplies both and reads a deterministic answer. Nothing here reads a clock —
///         and, deliberately, nothing here logs. Periods it cannot use are surfaced through <see cref="DroppedPeriods"/>
///         and <see cref="PeriodDropped"/> for the constructing caller to log, rather than by taking an
///         <c>ILogger</c> that would drag I/O into an otherwise pure evaluation.
///     </para>
///     <para>
///         <b>Room-scoped, not house-scoped.</b> One of these is built per area already, because the period table is
///         house-wide but the sun entity is an area setting — so a room's own levels belong here too, and applying
///         them anywhere else would be strictly worse. The reason is the blend: <see cref="GetTarget"/> interpolates
///         between the two periods either side of a boundary, and a room that replaces one side and not the other
///         has to arrive from <i>its</i> level rather than the house's. Replacing an already-blended value after the
///         fact turns a smooth arrival into a step, and re-running the blend outside would mean a second copy of the
///         boundary resolution. So <see cref="LevelsOf"/> is the single place a room's effective level is decided,
///         and everything downstream reads what it returned.
///     </para>
///     <para>
///         <b>The clock is not always what decides which period is in force.</b> Under
///         <see cref="PeriodAuthority.HomeAssistant"/> a Home Assistant dropdown does, through the optional
///         override delegate, and <see cref="OverriddenPeriod"/> is the one point both public answers go through
///         so they cannot disagree. <see cref="GetPeriodTarget"/> is deliberately outside that: it is asked for a
///         period <i>by name</i> — the sleep clamp reaching for the night rules — and a caller that named a period
///         must get the one it named.
///     </para>
/// </remarks>
public sealed class CircadianCalculator
{
	private readonly IReadOnlyList<TimePeriodConfig> _periods;
	private readonly GlobalConfig _global;
	private readonly Func<SunTimes> _sunTimes;

	// Names the period to run instead of resolving one from the clock. Null on every calculator except those built
	// under HomeAssistant period authority, so a house that has never heard of the select resolves exactly as it
	// always did — the delegate is not installed, rather than installed and returning null.
	private readonly Func<string?>? _periodOverride;

	// The room's overrides, indexed the way every other period-name lookup in the engine matches: by name,
	// case-insensitively. First row wins on a duplicate, matching how the validator reports one (and how the
	// house-mode Normal rows behave) — the alternative is a silent last-write-wins nobody can see in the file.
	private readonly Dictionary<string, RoomLevelOverride> _roomLevels;

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
	/// <param name="roomLevels">
	///     What one room runs instead of the schedule, period by period, or <c>null</c>/empty for a calculator that
	///     answers for the house rather than for a room — which is what the configuration page's preview wants, and
	///     what every existing caller gets by saying nothing. Rows naming no configured period are simply never
	///     matched: the validator reports the rename, and dropping somebody's levels here would make that report the
	///     only trace they ever existed.
	/// </param>
	/// <param name="periodOverride">
	///     Names the period to run <i>instead of</i> resolving one from the clock, or <c>null</c> — the default and
	///     the behaviour of every caller that says nothing — to resolve from the schedule exactly as before. Supplied
	///     by <see cref="PeriodSelectReader.ReadPeriod"/> under <see cref="PeriodAuthority.HomeAssistant"/> and by
	///     nothing else; a delegate rather than a value because it is read live, and a delegate rather than an
	///     <c>IHaContext</c> for the same reason <paramref name="sunTimes"/> is one.
	/// </param>
	public CircadianCalculator(
		IReadOnlyList<TimePeriodConfig> periods,
		GlobalConfig global,
		Func<SunTimes> sunTimes,
		IReadOnlyList<RoomLevelOverride>? roomLevels = null,
		Func<string?>? periodOverride = null)
	{
		_periods = periods ?? throw new ArgumentNullException(nameof(periods));
		_global = global ?? throw new ArgumentNullException(nameof(global));
		_sunTimes = sunTimes ?? throw new ArgumentNullException(nameof(sunTimes));
		_periodOverride = periodOverride;

		Dictionary<string, RoomLevelOverride> levels = new(StringComparer.OrdinalIgnoreCase);

		// An empty row says nothing, so it is not allowed to shadow a later row that does — the normaliser drops
		// these on save, but a hand-edited file reaches here without ever passing through it.
		foreach (RoomLevelOverride level in roomLevels ?? [])
			if (level is { IsEmpty: false, Period: { Length: > 0 } period })
				levels.TryAdd(period, level);

		_roomLevels = levels;

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
	/// <remarks>
	///     <para>There is one shared table now (09 §3.5): every period is a candidate, full stop.</para>
	///     <para>
	///         <b>Under an override the blend is gone and the change is a step.</b> That is accepted and intended:
	///         a period the household selected has no boundary <i>time</i> to interpolate away from — it began the
	///         instant somebody moved the dropdown — so there is nothing to blend across. Inventing one would mean
	///         the engine picking a boundary nobody configured and then easing toward a period that was already in
	///         force, which is a smoother lie rather than a truer answer. See <see cref="PeriodAuthority"/>.
	///     </para>
	/// </remarks>
	public LightTarget? GetTarget(DateTimeOffset now)
	{
		// Asked before the clock is consulted at all, so the override and ActivePeriodName can never disagree about
		// which period is in force — the caps, the reported name and the levels are one answer or none.
		if (OverriddenPeriod() is { } forced)
			return GetPeriodTarget(forced.Name);

		List<(TimeOnly Start, TimePeriodConfig Period)> boundaries = ResolveBoundaries();
		if (boundaries.Count == 0)
			return null;

		TimeOnly timeOfDay = TimeOnly.FromTimeSpan(now.TimeOfDay);
		int index = ActiveIndex(boundaries, timeOfDay);
		(TimeOnly Start, TimePeriodConfig Period) active = boundaries[index];
		PeriodLevels arriving = LevelsOf(active.Period);

		if (!_global.SmoothTransitions || _global.BlendMinutes <= 0 || boundaries.Count == 1)
			return ToTarget(active.Period, arriving);

		(TimeOnly Start, TimePeriodConfig Period) previous = boundaries[(index - 1 + boundaries.Count) % boundaries.Count];
		double blend = BlendFraction(active.Start, timeOfDay);

		// Outside the window the boundary has already fully taken effect; inside it we are still arriving.
		if (blend >= 1)
			return ToTarget(active.Period, arriving);

		// Both ends are read through LevelsOf, so what is interpolated is this room's own two levels rather than the
		// house's. A room that replaces one side of a boundary and not the other therefore still arrives smoothly:
		// blending the house's endpoints and replacing the result afterwards would put a step exactly where the
		// blend exists to remove one.
		PeriodLevels leaving = LevelsOf(previous.Period);

		double brightness = Interpolate(leaving.BrightnessPct, arriving.BrightnessPct, blend);
		double kelvin = Interpolate(leaving.ColorTempKelvin, arriving.ColorTempKelvin, blend);

		// The caps — and which values this room replaced — come from the active period alone: they are the rule the
		// reported period name promises.
		return ToTarget(active.Period, arriving with { BrightnessPct = brightness, ColorTempKelvin = (int)Math.Round(kelvin) });
	}

	/// <summary>
	///     The raw target of the period called <paramref name="periodName"/>, ignoring the clock entirely, or
	///     <c>null</c> when no such period exists. Sleep mode uses this to reach for the night rules at any hour.
	/// </summary>
	/// <remarks>
	///     The room's own levels apply here too. A room that runs the night period dimmer than the house means it
	///     when the sleep clamp reaches for that period at 03:00 — the clamp is "hold this room to its night rules",
	///     and a version that read the house's night instead would quietly hand the room a ceiling it had already
	///     said was too bright.
	/// </remarks>
	public LightTarget? GetPeriodTarget(string periodName)
	{
		TimePeriodConfig? period = _periods.FirstOrDefault(p => string.Equals(p.Name, periodName, StringComparison.OrdinalIgnoreCase));
		return period is null ? null : ToTarget(period, LevelsOf(period));
	}

	/// <summary>
	///     What <paramref name="period"/> runs at in this room: the schedule's levels, with whichever of the two the
	///     room replaced put in their place.
	/// </summary>
	/// <remarks>
	///     <b>The single place a room's effective level is decided.</b> The blend reads it for both of its endpoints,
	///     <see cref="GetPeriodTarget"/> reads it for the sleep clamp, and <see cref="LightTarget.FromRoom"/> carries
	///     the answer out so nothing downstream has to look at the room's overrides a second time. A replacement, not
	///     an offset: a room asking for 8 % during a period the house later raises to 100 % still asks for 8 %.
	/// </remarks>
	private PeriodLevels LevelsOf(TimePeriodConfig period)
	{
		if (!_roomLevels.TryGetValue(period.Name, out RoomLevelOverride? level))
			return new PeriodLevels(period.BrightnessPct, period.ColorTempKelvin, RoomLevelSource.None);

		RoomLevelSource fromRoom =
			(level.BrightnessPct is null ? RoomLevelSource.None : RoomLevelSource.Brightness)
			| (level.ColorTempKelvin is null ? RoomLevelSource.None : RoomLevelSource.ColorTemp);

		return new PeriodLevels(
			level.BrightnessPct ?? period.BrightnessPct,
			level.ColorTempKelvin ?? period.ColorTempKelvin,
			fromRoom);
	}

	/// <summary>One period's levels as this room runs them.</summary>
	private readonly record struct PeriodLevels(double BrightnessPct, int ColorTempKelvin, RoomLevelSource FromRoom);

	/// <summary>
	///     Labels <paramref name="levels"/> with the period it came from, and holds it to what a lamp can be set to.
	/// </summary>
	/// <remarks>
	///     A room's own replacement passes through here exactly as the schedule's value does, so there is one
	///     path and one answer. The clamp is the physical 0–100 bound only — the period's configurable floor and
	///     ceiling were removed in the 2026-07 simplification, and a room's value now stands as written.
	/// </remarks>
	private static LightTarget ToTarget(TimePeriodConfig period, PeriodLevels levels)
	{
		LightTarget target = new(period.Name, levels.BrightnessPct, levels.ColorTempKelvin, levels.FromRoom);

		return target with { BrightnessPct = target.Clamp(levels.BrightnessPct) };
	}

	/// <summary>
	///     The name of the period active at <paramref name="now"/>, or <c>null</c> when none can be placed. A
	///     name-only view over the same boundary resolution <see cref="GetTarget"/> uses, so
	///     <see cref="ModeMonitor"/>'s period-entry detection and the target maths can never disagree (09 §3.5).
	/// </summary>
	public string? ActivePeriodName(DateTimeOffset now)
	{
		if (OverriddenPeriod() is { } forced)
			return forced.Name;

		List<(TimeOnly Start, TimePeriodConfig Period)> boundaries = ResolveBoundaries();
		if (boundaries.Count == 0)
			return null;

		int index = ActiveIndex(boundaries, TimeOnly.FromTimeSpan(now.TimeOfDay));
		return boundaries[index].Period.Name;
	}

	/// <summary>
	///     The period an override names, or <c>null</c> when there is no override or it names nothing this table has.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>The single point every consumer of an override goes through.</b> <see cref="GetTarget"/> and
	///         <see cref="ActivePeriodName"/> both ask it, first, before touching the clock — so the period a room is
	///         lit for, the name a card shows and the boundary <see cref="ModeMonitor"/> watches for cannot come from
	///         two different answers. Splitting the check would put the disagreement exactly where nobody looks.
	///     </para>
	///     <para>
	///         A name matching no configured period falls through to the schedule rather than stopping the house.
	///         The validator refuses that mapping at document level and <see cref="PeriodSelectReader"/> says the
	///         value once, so both halves are already reported; commanding nothing on top of that would take a room
	///         dark over a typo in a dropdown.
	///     </para>
	/// </remarks>
	private TimePeriodConfig? OverriddenPeriod() =>
		_periodOverride?.Invoke() is { Length: > 0 } name
			? _periods.FirstOrDefault(period => string.Equals(period.Name, name, StringComparison.OrdinalIgnoreCase))
			: null;

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
