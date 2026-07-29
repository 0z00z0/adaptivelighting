using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     Questions about the circadian table that more than one surface asks.
/// </summary>
/// <remarks>
///     Pure, and here rather than inside a component, for the reason the rest of this namespace exists: two
///     surfaces already want to know which period is in force — the schedule editor, to badge the card, and the
///     room page, to say which row the room is running right now — and a second copy of the wrap-past-midnight
///     rule would be believed while it drifted.
/// </remarks>
public static class Schedule
{
	/// <summary>
	///     The period in force at <paramref name="now"/>: the one whose start is the most recent at or before it.
	/// </summary>
	/// <remarks>
	///     <para>
	///         When every start is still ahead of <paramref name="now"/> — the small hours, before the first
	///         boundary of the day — the period with the <i>latest</i> start is in force: it began yesterday and
	///         wrapped past midnight.
	///     </para>
	///     <para>
	///         Sun-anchored starts resolve through the engine's own <see cref="PeriodStart"/> grammar, so the
	///         running order can differ from the list order and this answers with the running one. A period whose
	///         start is blank, unparseable, or unplaceable today (a sun anchor during polar night) can never be
	///         "now", because the engine cannot place it either.
	///     </para>
	/// </remarks>
	/// <param name="periods">The document's period list.</param>
	/// <param name="sun">Today's sun times, for the sun-anchored boundaries.</param>
	/// <param name="now">The wall-clock time to ask about.</param>
	/// <returns>The period in force, or <c>null</c> when none of them resolves.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static TimePeriodConfig? InForceAt(IReadOnlyList<TimePeriodConfig> periods, SunTimes sun, TimeOnly now)
	{
		ArgumentNullException.ThrowIfNull(periods);
		ArgumentNullException.ThrowIfNull(sun);

		List<(TimePeriodConfig Period, TimeOnly Start)> resolved = [];

		foreach (TimePeriodConfig period in periods)
			if (PeriodStart.TryParse(period.Start, out PeriodStart? parsed) && parsed is not null
				&& parsed.Resolve(sun) is { } start)
				resolved.Add((period, start));

		if (resolved.Count == 0)
			return null;

		List<(TimePeriodConfig Period, TimeOnly Start)> started = [.. resolved.Where(entry => entry.Start <= now)];
		IEnumerable<(TimePeriodConfig Period, TimeOnly Start)> pool = started.Count > 0 ? started : resolved;

		return pool.OrderBy(entry => entry.Start).Last().Period;
	}
}
