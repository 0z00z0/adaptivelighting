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

		if (global.PeriodSelect!.PeriodFor(selectValue) is not { Length: > 0 } periodId)
			return null;

		// Matched on Key, exactly as CircadianCalculator.OverriddenPeriod does, so no page can badge a period the
		// engine leaves unresolved.
		return periods.FirstOrDefault(period =>
			string.Equals(period.Key, periodId.Trim(), StringComparison.OrdinalIgnoreCase));
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

/// <summary>
///     The engine's own house-mode rules that a document configures but the authority has stood down, counted so
///     a page can name them.
/// </summary>
/// <param name="SetsModePeriods">Periods carrying a <see cref="TimePeriodConfig.SetsModeId"/>.</param>
/// <param name="ActivateWhileOnOptions">Options carrying a <c>ActivateWhileOn</c> entity.</param>
/// <param name="AutoAwayOptions">Options carrying a positive <c>ActivateAfterNoMotionMinutes</c>.</param>
/// <param name="ResetTriggerOptions">Options carrying a reset trigger, which returns the select to Normal.</param>
public sealed record DormantModeRules(
	int SetsModePeriods,
	int ActivateWhileOnOptions,
	int AutoAwayOptions,
	int ResetTriggerOptions)
{
	/// <summary>Nothing is standing down, either because adaptive lighting decides or because nothing is configured.</summary>
	public static readonly DormantModeRules None = new(0, 0, 0, 0);

	public bool Any =>
		SetsModePeriods > 0 || ActivateWhileOnOptions > 0 || AutoAwayOptions > 0 || ResetTriggerOptions > 0;

	/// <summary>Each dormant rule as the page that carries the control names it.</summary>
	public IReadOnlyList<string> Names
	{
		get
		{
			List<string> names = new(4);

			if (SetsModePeriods > 0)
				names.Add(SetsModePeriods == 1
					? "1 period that also switches the house mode"
					: $"{SetsModePeriods} periods that also switch the house mode");

			if (ActivateWhileOnOptions > 0)
				names.Add($"{ActivateWhileOnOptions} {Plural(ActivateWhileOnOptions, "mode")} turned on by a switch or sensor");

			if (AutoAwayOptions > 0)
				names.Add($"{AutoAwayOptions} {Plural(AutoAwayOptions, "mode")} set after no movement");

			if (ResetTriggerOptions > 0)
				names.Add($"{ResetTriggerOptions} {Plural(ResetTriggerOptions, "mode")} with a reset trigger");

			return names;
		}
	}

	private static string Plural(int count, string noun) => count == 1 ? noun : noun + "s";
}

/// <summary>Which side owns the house mode, and what that leaves dormant.</summary>
public static class ModeAuthority
{
	/// <summary>Whether Home Assistant owns the house mode, so nothing in the engine may move it.</summary>
	/// <remarks>
	///     Asked through <see cref="HouseModeConfig.HomeAssistantDecides"/>, which tests the trimmed
	///     <c>EntityId</c>: an authority naming no entity, or an entity of nothing but spaces, leaves the engine
	///     deciding as it always did.
	/// </remarks>
	public static bool HomeAssistantDecides(GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		return global.HouseMode?.HomeAssistantDecides is true;
	}

	/// <summary>What the document still configures that Home Assistant's authority has stood down.</summary>
	/// <remarks>Counts what is configured, so a house that sets none of the three has nothing to report.</remarks>
	public static DormantModeRules Dormant(GlobalConfig global, IReadOnlyList<TimePeriodConfig> periods)
	{
		ArgumentNullException.ThrowIfNull(global);
		ArgumentNullException.ThrowIfNull(periods);

		if (!HomeAssistantDecides(global))
			return DormantModeRules.None;

		List<HouseModeOptionConfig> options = global.HouseMode!.Options;

		// A reset writes the select back to Normal, so ModeMonitor stands it down with the three that set it.
		// Normal is exempt from no-motion and reset in the engine, so counting it here would name a rule that was
		// never firing under either authority.
		return new DormantModeRules(
			periods.Count(period => period.SetsModeId is { Length: > 0 }),
			options.Count(option => option.ActivateWhileOn.Count > 0),
			options.Count(option => option.Kind != ModeKind.Normal && option.ActivateAfterNoMotionMinutes is > 0),
			options.Count(option => option.Kind != ModeKind.Normal && option.HasResetTrigger));
	}
}
