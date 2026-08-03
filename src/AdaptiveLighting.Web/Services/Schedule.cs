using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>Questions about the circadian table that more than one surface asks.</summary>
public static class Schedule
{
	/// <summary>Whether Home Assistant owns the time of day, so nothing on any page may resolve from the clock.</summary>
	/// <remarks>
	///     Tested through <see cref="PeriodSelectConfig.EntityId"/>, which is the identical question
	///     <see cref="PeriodSelectReader.For"/> asks before it builds a reader at all. Testing the raw <c>Entity</c>
	///     accepts an entity of nothing but spaces, and the page would then call the schedule dead while the engine
	///     was still running off it.
	/// </remarks>
	public static bool HomeAssistantDecides(GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		return global.PeriodSelect is { Authority: PeriodAuthority.HomeAssistant, EntityId: not null };
	}

	/// <summary>
	///     The period in force right now: the select's under Home Assistant's authority, the schedule's otherwise.
	/// </summary>
	/// <remarks>
	///     Every page asks this, never <see cref="InForceAt"/>, so none can badge a period the engine is not
	///     running. The fallback fires on the same three cases the engine falls back on: an unreadable select, an
	///     option no row maps, and a mapping naming a period the schedule no longer has.
	/// </remarks>
	/// <param name="sun">Today's sun times, for the sun-anchored boundaries of the fallback.</param>
	/// <param name="selectValue">
	///     The select's current option as Home Assistant reports it, or <c>null</c> when it is absent, unknown or
	///     unavailable. Passed in so this stays pure.
	/// </param>
	public static TimePeriodConfig? InForceNow(
		IReadOnlyList<TimePeriodConfig> periods,
		GlobalConfig global,
		SunTimes sun,
		TimeOnly now,
		string? selectValue)
	{
		ArgumentNullException.ThrowIfNull(periods);
		ArgumentNullException.ThrowIfNull(global);
		ArgumentNullException.ThrowIfNull(sun);

		return NamedBySelect(periods, global, selectValue) ?? InForceAt(periods, sun, now);
	}

	/// <summary>The period the select is naming, or <c>null</c> when the schedule is still the answer.</summary>
	public static TimePeriodConfig? NamedBySelect(
		IReadOnlyList<TimePeriodConfig> periods,
		GlobalConfig global,
		string? selectValue)
	{
		ArgumentNullException.ThrowIfNull(periods);
		ArgumentNullException.ThrowIfNull(global);

		if (!HomeAssistantDecides(global))
			return null;

		if (global.PeriodSelect!.PeriodFor(selectValue) is not { Length: > 0 } name)
			return null;

		// Name untrimmed and OrdinalIgnoreCase, matching CircadianCalculator.OverriddenPeriod character for
		// character. Trimming here would resolve a period the engine leaves unresolved.
		return periods.FirstOrDefault(period =>
			string.Equals(period.Name, name, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>The period whose start is the most recent at or before <paramref name="now"/>.</summary>
	/// <remarks>
	///     A period whose start is blank, unparseable, or unplaceable today (a sun anchor during polar night) can
	///     never be the answer, because the engine cannot place it either.
	/// </remarks>
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

		// With every start still ahead of now, the latest start wins: it began yesterday and wrapped past midnight.
		List<(TimePeriodConfig Period, TimeOnly Start)> started = [.. resolved.Where(entry => entry.Start <= now)];
		IEnumerable<(TimePeriodConfig Period, TimeOnly Start)> pool = started.Count > 0 ? started : resolved;

		return pool.OrderBy(entry => entry.Start).Last().Period;
	}
}
