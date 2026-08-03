using System.Collections.Concurrent;

using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>
///     The one object standing between the period <c>input_select</c> and the engine, in whichever direction
///     <see cref="PeriodAuthority"/> names.
/// </summary>
public sealed class PeriodSelectReader
{
	private readonly IHaContext _ha;
	private readonly PeriodSelectConfig _config;
	private readonly ILogger _logger;

	// Touched from whatever thread an area's tick or motion arrived on. TryAdd is the atomic bit; the value is unused.
	private readonly ConcurrentDictionary<string, byte> _warnedValues = new(StringComparer.OrdinalIgnoreCase);

	private PeriodSelectReader(IHaContext ha, PeriodSelectConfig config, ILogger logger)
	{
		_ha = ha;
		_config = config;
		_logger = logger;

		Entity = config.EntityId!;

		// The single branch on authority. Exactly one delegate is ever assigned, and nothing downstream re-asks.
		if (config.Authority is PeriodAuthority.HomeAssistant)
			ReadPeriod = CurrentPeriodName;
		else
			OptionForPeriod = config.OptionFor;
	}

	/// <summary>
	///     Builds the reader for <paramref name="global"/>, or <c>null</c> when no period select is configured.
	/// </summary>
	public static PeriodSelectReader? For(IHaContext ha, GlobalConfig global, ILogger logger)
	{
		ArgumentNullException.ThrowIfNull(ha);
		ArgumentNullException.ThrowIfNull(global);
		ArgumentNullException.ThrowIfNull(logger);

		return global.PeriodSelect is { EntityId: not null } select
			? new PeriodSelectReader(ha, select, logger)
			: null;
	}

	/// <summary>The select's entity id, trimmed. Never blank: a configuration without one produces no reader at all.</summary>
	public string Entity { get; }

	/// <summary>The period override the calculators install, or <c>null</c> when the engine owns the select.</summary>
	// Non-null on the authority OptionForPeriod is null on. Never both.
	public Func<string?>? ReadPeriod { get; }

	/// <summary>Maps a period name to the option the select should show, or <c>null</c> when Home Assistant owns it.</summary>
	// Non-null on the authority ReadPeriod is null on. Never both.
	public Func<string, string?>? OptionForPeriod { get; }

	/// <summary>
	///     The select's current option, or <c>null</c> when it is absent, <c>unknown</c> or <c>unavailable</c>.
	/// </summary>
	public string? CurrentValue() => _ha.GetState(Entity).AsUsableState();

	/// <summary>
	///     The period the select currently names, or <c>null</c> when it names nothing the document maps.
	/// </summary>
	public string? CurrentPeriodName()
	{
		if (CurrentValue() is not { } value)
			return null;

		if (_config.PeriodFor(value) is { Length: > 0 } period)
			return period;

		// Once per distinct value for the run: this is read per area per evaluation.
		if (_warnedValues.TryAdd(value, 0))
			_logger.LogWarning(
				"Period select {Entity} reports '{Value}', which no PeriodSelect option maps to a period; the rooms "
				+ "keep following the schedule until it does.",
				Entity, value);

		return null;
	}
}
