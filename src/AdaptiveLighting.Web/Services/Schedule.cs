using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Hosting;

namespace AdaptiveLighting.Web.Services;

/// <summary>Which rule put the period in force.</summary>
public enum PeriodInForceRule
{
	None = 0,

	/// <summary>The clock placed it, at its own start.</summary>
	Clock = 1,

	/// <summary>Home Assistant's dropdown named it.</summary>
	Select = 2,

	/// <summary>The clock's period waits for movement and has not begun, so the previous one is still running.</summary>
	HeldBack = 3
}

/// <summary>The period in force, and which rule decided it.</summary>
public sealed record PeriodInForce(TimePeriodConfig? Period, PeriodInForceRule Rule)
{
	public static readonly PeriodInForce None = new(null, PeriodInForceRule.None);
}

/// <summary>Questions about the circadian table that more than one surface asks.</summary>
public static class Schedule
{
	/// <summary>Whether Home Assistant owns the time of day, so nothing on any page may resolve from the clock.</summary>
	/// <remarks>
	///     Tested through <see cref="PeriodSelectConfig.EntityId"/>, the same question <see cref="PeriodSelectReader.For"/>
	///     asks before it builds a reader. The raw <c>Entity</c> accepts an entity of nothing but spaces, and the
	///     page would then call the schedule dead while the engine was still running off it.
	/// </remarks>
	public static bool HomeAssistantDecides(GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		return global.PeriodSelect is { Authority: PeriodAuthority.HomeAssistant, EntityId: not null };
	}

	/// <summary>Whether a period that waits for movement has begun yet, or <c>null</c> while nothing is running.</summary>
	/// <remarks>Asked per call: a save rebuilds the orchestrator with a new latch, and no page outlives that.</remarks>
	public static Func<string, DateOnly, bool>? HeldBackRule(LightingEngineHost engine)
	{
		ArgumentNullException.ThrowIfNull(engine);

		return engine.MotionPeriods is { } latch ? latch.IsHeldBack : null;
	}

	/// <summary>A calculator that resolves the period as the engine's does: from the select under Home Assistant's authority, from the clock otherwise.</summary>
	/// <remarks>
	///     Every surface builds its calculator here, so none of them can reach a different answer from the engine's.
	///     The select's period is resolved once and closed over, so every card describes one instant. A
	///     <c>periodHeldBack</c> is handed over, never rebuilt: a fresh latch has recorded nothing, so it answers
	///     "not begun" for every held period all day.
	/// </remarks>
	public static CircadianCalculator CalculatorFor(
		IReadOnlyList<TimePeriodConfig> periods,
		GlobalConfig global,
		SunTimes sun,
		string? selectValue = null,
		Func<string, DateOnly, bool>? periodHeldBack = null,
		TimeZoneInfo? zone = null)
	{
		ArgumentNullException.ThrowIfNull(periods);
		ArgumentNullException.ThrowIfNull(global);
		ArgumentNullException.ThrowIfNull(sun);

		// Key, never Name: CircadianCalculator.PeriodWithKey matches on Key, which is the id once one exists.
		string? forced = NamedBySelect(periods, global, selectValue)?.Key;

		return new CircadianCalculator(
			periods, global, () => sun, null, forced is null ? null : () => forced, periodHeldBack, zone);
	}

	/// <summary>The period in force at <paramref name="now"/>, and which rule decided it.</summary>
	/// <remarks>
	///     Every page asks this, so none can badge a period the engine is not running. The fallback to the clock
	///     fires on the same three cases the engine falls back on: an unreadable select, an option no row maps, and
	///     a mapping naming a period the schedule no longer has. A <c>null</c> <paramref name="periodHeldBack"/>
	///     places every period on its clock start.
	/// </remarks>
	public static PeriodInForce InForceNow(
		IReadOnlyList<TimePeriodConfig> periods,
		GlobalConfig global,
		SunTimes sun,
		DateTimeOffset now,
		string? selectValue,
		Func<string, DateOnly, bool>? periodHeldBack = null,
		TimeZoneInfo? zone = null)
	{
		ArgumentNullException.ThrowIfNull(periods);
		ArgumentNullException.ThrowIfNull(global);
		ArgumentNullException.ThrowIfNull(sun);

		if (NamedBySelect(periods, global, selectValue) is { } named)
			return new PeriodInForce(named, PeriodInForceRule.Select);

		// Built without the select: the branch above already took that case, and the calculator would only
		// re-resolve it.
		CircadianCalculator calculator = CalculatorFor(periods, global, sun, null, periodHeldBack, zone);

		string? activeKey = calculator.ActivePeriodId(now);
		if (calculator.PeriodWithKey(activeKey) is not { } active)
			return PeriodInForce.None;

		// ScheduledPeriodId is the same table with the hold ignored, so a different answer is the hold and nothing else.
		bool heldBack = !string.Equals(activeKey, calculator.ScheduledPeriodId(now), StringComparison.OrdinalIgnoreCase);

		return new PeriodInForce(active, heldBack ? PeriodInForceRule.HeldBack : PeriodInForceRule.Clock);
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

		// Matched on Key, as CircadianCalculator.OverriddenPeriod does, so no page can badge a period the engine
		// leaves unresolved.
		return periods.ByKey(periodId);
	}
}

/// <summary>The engine's own house-mode rules that a document configures but the authority has stood down, counted so a page can name them.</summary>
public sealed record DormantModeRules(
	int SetsModePeriods,
	int ActivateWhileOnOptions,
	int AutoAwayOptions,
	int ResetTriggerOptions)
{
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
	///     Asked through <see cref="HouseModeConfig.HomeAssistantDecides"/>, which tests the trimmed <c>EntityId</c>:
	///     an authority naming no entity, or an entity of nothing but spaces, leaves the engine deciding.
	/// </remarks>
	public static bool HomeAssistantDecides(GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		return global.HouseMode?.HomeAssistantDecides is true;
	}

	/// <summary>What the document still configures that Home Assistant's authority has stood down.</summary>
	public static DormantModeRules Dormant(GlobalConfig global, IReadOnlyList<TimePeriodConfig> periods)
	{
		ArgumentNullException.ThrowIfNull(global);
		ArgumentNullException.ThrowIfNull(periods);

		if (!HomeAssistantDecides(global))
			return DormantModeRules.None;

		List<HouseModeOptionConfig> options = global.HouseMode!.Options;

		// A reset writes the select back to Normal, so ModeMonitor stands it down with the three that set it. Normal
		// is exempt from no-motion and reset in the engine, so counting it here would name a rule that never fires.
		return new DormantModeRules(
			periods.Count(period => period.SetsModeId is { Length: > 0 }),
			options.Count(option => option.ActivateWhileOn.Count > 0),
			options.Count(option => option.Kind != ModeKind.Normal && option.ActivateAfterNoMotionMinutes is > 0),
			options.Count(option => option.Kind != ModeKind.Normal && option.HasResetTrigger));
	}
}
