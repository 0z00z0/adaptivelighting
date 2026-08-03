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
	///     Whether Home Assistant owns the time of day, so nothing on any page may resolve a period from the clock.
	/// </summary>
	/// <remarks>
	///     <b>The entity is part of the test, not an extra check the caller adds.</b>
	///     <see cref="PeriodSelectReader.For"/> builds no reader at all without one, so a document that names an
	///     authority and no select is a document the engine runs entirely off its own schedule — and a page that
	///     read the authority alone would announce that start times were dead while the engine was still obeying
	///     them.
	///     <para>
	///         Tested through <see cref="PeriodSelectConfig.EntityId"/> and not the raw <c>Entity</c>, so this asks
	///         the identical question <see cref="PeriodSelectReader.For"/> asks. <c>Entity.Length: &gt; 0</c> accepts
	///         an entity of nothing but spaces, which is precisely the document where the two would have answered
	///         differently.
	///     </para>
	/// </remarks>
	/// <param name="global">Supplies <see cref="GlobalConfig.PeriodSelect"/>.</param>
	/// <exception cref="ArgumentNullException"><paramref name="global"/> is <c>null</c>.</exception>
	public static bool HomeAssistantDecides(GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		return global.PeriodSelect is { Authority: PeriodAuthority.HomeAssistant, EntityId: not null };
	}

	/// <summary>
	///     The period actually in force right now — the select's under Home Assistant's authority, the schedule's
	///     otherwise.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>The one answer every page asks for, so no page can disagree with the engine.</b> Under
	///         <see cref="PeriodAuthority.HomeAssistant"/> the engine stops resolving periods from the clock
	///         entirely — <see cref="CircadianCalculator"/> takes the name from
	///         <see cref="PeriodSelectReader.ReadPeriod"/> — and a room page still badging "the schedule says
	///         evening" would be describing a rule that is switched off. It was <see cref="InForceAt"/> being
	///         called directly from two components and a service that made that possible; this is the funnel.
	///     </para>
	///     <para>
	///         <b>It falls back for exactly the reasons the engine falls back, and no others.</b> An unreadable
	///         select, an option no row maps, and a mapping naming a period the schedule no longer has all leave
	///         <see cref="PeriodSelectReader.CurrentPeriodName"/> answering <c>null</c>, and the calculator then
	///         resolves from the clock. Doing anything else here — showing nothing, or showing the mapping's dead
	///         name — would make the page's degraded state a different one from the engine's.
	///     </para>
	/// </remarks>
	/// <param name="periods">The document's period list.</param>
	/// <param name="global">Supplies the period select and its authority.</param>
	/// <param name="sun">Today's sun times, for the sun-anchored boundaries of the fallback.</param>
	/// <param name="now">The wall-clock time to ask about, for the fallback.</param>
	/// <param name="selectValue">
	///     The select's current option as Home Assistant reports it, or <c>null</c> when it is absent, unknown or
	///     unavailable. Passed in rather than read here so this stays pure, exactly as <paramref name="sun"/> is.
	/// </param>
	/// <returns>The period in force, or <c>null</c> when none resolves.</returns>
	/// <exception cref="ArgumentNullException">Any argument other than <paramref name="selectValue"/> is <c>null</c>.</exception>
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

	/// <summary>
	///     The period the select is naming, or <c>null</c> when the schedule is still the answer.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Resolved through <see cref="PeriodSelectConfig.PeriodFor"/> and matched to the table by name, which
	///         is the pair the engine itself uses: the mapping stores a name so it follows a period that is
	///         reordered, and a name matching nothing is an error the validator reports rather than something to
	///         guess around here.
	///     </para>
	///     <para>
	///         <b>The match is the engine's, character for character.</b> <c>CircadianCalculator.OverriddenPeriod</c>
	///         compares <see cref="TimePeriodConfig.Name"/> as it stands, so trimming it here would resolve a period
	///         named with a stray space that the engine leaves on the schedule — the page and the lights disagreeing
	///         about which row is running, which is the whole thing this class exists to prevent. The option string
	///         is trimmed, because <see cref="PeriodSelectConfig.PeriodFor"/> trims it on both sides too.
	///     </para>
	/// </remarks>
	/// <param name="periods">The document's period list.</param>
	/// <param name="global">Supplies the period select and its authority.</param>
	/// <param name="selectValue">The select's current option, or <c>null</c>.</param>
	/// <exception cref="ArgumentNullException"><paramref name="periods"/> or <paramref name="global"/> is <c>null</c>.</exception>
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

		return periods.FirstOrDefault(period =>
			string.Equals(period.Name, name, StringComparison.OrdinalIgnoreCase));
	}

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
