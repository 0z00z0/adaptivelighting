using System.Collections.Concurrent;

using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>
///     The one object standing between the period <c>input_select</c> and the engine, in whichever direction
///     <see cref="PeriodAuthority"/> names.
/// </summary>
/// <remarks>
///     <para>
///         <b>The two directions are kept apart by construction, not by a check at each use site.</b>
///         <see cref="ReadPeriod"/> and <see cref="OptionForPeriod"/> are assigned once, in the constructor, from
///         the single <see cref="PeriodSelectConfig.Authority"/> value, and exactly one of them is ever non-null.
///         A reader that could be asked "may I read?" and "may I write?" separately is a reader two call sites are
///         free to answer differently — and the failure that would produce is the worst one this feature has: the
///         engine writing the select while also following it, chasing its own tail through Home Assistant with the
///         household unable to move it at all.
///     </para>
///     <para>
///         <b>It reads, and it never decides.</b> Folding <c>unknown</c> and <c>unavailable</c> to <c>null</c> is
///         the whole of its judgement: a select that cannot be read leaves the engine on its own schedule, which is
///         what every house did before the feature existed. An option nothing maps is the same answer, said once —
///         after a Home Assistant restart an <c>input_select</c> can sit unreadable for a while, and a warning per
///         evaluation would be one per area per tick for as long as that lasted.
///     </para>
/// </remarks>
public sealed class PeriodSelectReader
{
	private readonly IHaContext _ha;
	private readonly PeriodSelectConfig _config;
	private readonly ILogger _logger;

	// Every area's calculator calls through the same reader, on whatever thread its tick or its motion arrived on,
	// so the warn-once set is touched concurrently. TryAdd is the atomic bit; the byte value is unused. Same shape,
	// and for the same reason, as ModeMonitor's own unclassified-value tripwire.
	private readonly ConcurrentDictionary<string, byte> _warnedValues = new(StringComparer.OrdinalIgnoreCase);

	private PeriodSelectReader(IHaContext ha, PeriodSelectConfig config, ILogger logger)
	{
		_ha = ha;
		_config = config;
		_logger = logger;

		Entity = config.Entity!.Trim();

		// The single branch. Everything downstream reads which delegate it was handed, and nothing anywhere asks
		// the authority a second time.
		if (config.Authority is PeriodAuthority.HomeAssistant)
			ReadPeriod = CurrentPeriodName;
		else
			OptionForPeriod = config.OptionFor;
	}

	/// <summary>
	///     Builds the reader for <paramref name="global"/>, or <c>null</c> when no period select is configured.
	/// </summary>
	/// <remarks>
	///     A <c>null</c> return rather than an inert instance, so "there is no period select" is one condition a
	///     caller checks once rather than a live object every consumer has to keep asking whether it means anything.
	/// </remarks>
	/// <param name="ha">Where the select's state is read from.</param>
	/// <param name="global">Supplies <see cref="GlobalConfig.PeriodSelect"/>.</param>
	/// <param name="logger">Where the unmapped-value warning goes.</param>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static PeriodSelectReader? For(IHaContext ha, GlobalConfig global, ILogger logger)
	{
		ArgumentNullException.ThrowIfNull(ha);
		ArgumentNullException.ThrowIfNull(global);
		ArgumentNullException.ThrowIfNull(logger);

		return global.PeriodSelect is { Entity: { Length: > 0 } } select
			? new PeriodSelectReader(ha, select, logger)
			: null;
	}

	/// <summary>The select's entity id. Never blank: a configuration without one produces no reader at all.</summary>
	public string Entity { get; }

	/// <summary>
	///     The period override every <see cref="CircadianCalculator"/> installs, or <c>null</c> when the engine owns
	///     the select and there is nothing to follow.
	/// </summary>
	/// <remarks>
	///     Non-null on exactly the authority <see cref="OptionForPeriod"/> is null on. Handed to the calculators as
	///     a delegate rather than as this object so the calculator stays pure with respect to Home Assistant, the
	///     same way its sun times already arrive.
	/// </remarks>
	public Func<string?>? ReadPeriod { get; }

	/// <summary>
	///     Maps a period name to the option the select should be showing, or <c>null</c> when Home Assistant owns
	///     the select and the engine has no business writing it.
	/// </summary>
	/// <remarks>Non-null on exactly the authority <see cref="ReadPeriod"/> is null on.</remarks>
	public Func<string, string?>? OptionForPeriod { get; }

	/// <summary>
	///     The select's current option, or <c>null</c> when it is absent, <c>unknown</c> or <c>unavailable</c>.
	/// </summary>
	/// <remarks>
	///     Public because the writing direction needs it too: a mirror write is only idempotent if it can see what
	///     the select already reads. This is a plain read of an entity and grants nobody the authority to act on it —
	///     the two delegates above are what does that.
	/// </remarks>
	public string? CurrentValue() => _ha.GetState(Entity).AsUsableState();

	/// <summary>
	///     The period the select currently names, or <c>null</c> when it names nothing the document maps.
	/// </summary>
	/// <remarks>
	///     <c>null</c> is the safe answer in every one of its cases — unreadable select, unmapped option — because
	///     the calculator falls back to its own schedule on it. The house keeps working; it simply stops following
	///     the dropdown, and the log says which option it could not place.
	/// </remarks>
	public string? CurrentPeriodName()
	{
		if (CurrentValue() is not { } value)
			return null;

		if (_config.PeriodFor(value) is { Length: > 0 } period)
			return period;

		// Once per distinct value, for the run. The select is read on every area's every evaluation, so an option
		// nobody has mapped would otherwise write a line per area per tick until somebody noticed it.
		if (_warnedValues.TryAdd(value, 0))
			_logger.LogWarning(
				"Period select {Entity} reports '{Value}', which no PeriodSelect option maps to a period; the rooms "
				+ "keep following the schedule until it does.",
				Entity, value);

		return null;
	}
}
