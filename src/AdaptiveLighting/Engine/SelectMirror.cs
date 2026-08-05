using AdaptiveLighting.Extensions;

using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>
///     One <c>input_select</c> the engine may drive: whether it is ours to write, whether it already shows what
///     we want, and the call itself.
/// </summary>
/// <remarks>
///     The house mode and the time of day are two selects with the same mechanics and different rules. The rules
///     stay with their callers; only the writing lives here, so a new rule cannot forget the ownership test or
///     re-announce a write that never happened.
/// </remarks>
internal sealed class SelectMirror
{
	private const string SelectDomain = "input_select";
	private const string SelectOptionService = "select_option";

	private readonly IHaContext _ha;
	private readonly bool _ours;

	internal SelectMirror(IHaContext ha, string? entity, bool ours)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		Entity = entity?.Trim() is { Length: > 0 } trimmed ? trimmed : null;
		_ours = ours;
	}

	/// <summary>The select's entity id, or <c>null</c> when the document names none.</summary>
	internal string? Entity { get; }

	/// <summary>Whether a write here reaches Home Assistant at all.</summary>
	internal bool Writes => _ours && Entity is not null;

	/// <summary>The option showing now, or <c>null</c> when absent, <c>unknown</c> or <c>unavailable</c>.</summary>
	internal string? CurrentValue() => Entity is null ? null : _ha.GetState(Entity).AsUsableState();

	/// <summary>Whether the select already shows <paramref name="option"/>.</summary>
	internal bool AlreadyShows(string? option) =>
		option?.Trim() is { Length: > 0 } wanted
		&& string.Equals(CurrentValue(), wanted, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	///     Points the select at <paramref name="option"/>, and answers whether it called.
	/// </summary>
	/// <remarks>
	///     <paramref name="announce"/> runs only when the call is actually made, so a caller's log line can never
	///     describe a write that did not happen. Nothing is written when Home Assistant owns the select, when the
	///     option is blank, or when the select already shows it.
	/// </remarks>
	internal bool Ensure(string? option, Action<string>? announce = null)
	{
		if (!Writes || option?.Trim() is not { Length: > 0 } wanted || AlreadyShows(wanted))
			return false;

		announce?.Invoke(Entity!);

		_ha.CallService(SelectDomain, SelectOptionService,
			new ServiceTarget { EntityIds = [Entity!] }, new { option = wanted });

		return true;
	}
}
