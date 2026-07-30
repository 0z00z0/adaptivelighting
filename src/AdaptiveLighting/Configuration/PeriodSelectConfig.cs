using YamlDotNet.Serialization;

namespace AdaptiveLighting.Configuration;

/// <summary>
///     Which side owns the time of day: this application's schedule, or a Home Assistant dropdown.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every member is pinned to an explicit ordinal, from birth, and no member may ever be renamed or
///         removed.</b> This is not tidiness. Two readers bind <see cref="AdaptiveLightingConfig"/> — the engine's
///         own <see cref="LightingConfigDocument.Deserialize"/>, which has a legacy pre-pass, and NetDaemon's
///         configuration binder on each house's app YAML, which can never have one. An unknown <i>key</i> is
///         silence in both; an unknown enum <i>value</i> is a <see cref="FormatException"/> that kills the app on
///         start. Deleting <c>DarknessSource.Either</c> took a live house's dashboard down with
///         <c>Either is not a valid value for DarknessSource</c>, and there is no version at which it becomes safe
///         to do again, because there is no way to prove no file still says the word.
///     </para>
///     <para>
///         The ordinals are written out for the second half of the same lesson: enum members are compile-time
///         constants, inlined into consuming assemblies, and <c>Enum.Parse</c> accepts the bare numeral — so
///         <c>Authority: 1</c> in a hand-written file has to keep meaning what it meant, whatever later editing
///         does to declaration order. A member that is regretted is documented as retired and left in place; it is
///         never taken out.
///     </para>
/// </remarks>
public enum PeriodAuthority
{
	/// <summary>
	///     This application decides, and the default — so a document that says nothing behaves exactly as every
	///     document did before the select existed. The engine resolves the period from its own schedule and
	///     <i>writes</i> the select as a mirror of that decision, for whatever else in the house wants to read it.
	/// </summary>
	AdaptiveLighting = 0,

	/// <summary>
	///     Home Assistant decides. The engine <i>reads</i> the select and never writes it: the option it reports
	///     maps to a period name, and that period is what every room runs, whatever the clock says.
	/// </summary>
	/// <remarks>
	///     The day/night blend has no boundary time under this authority and becomes a step — see
	///     <see cref="Engine.CircadianCalculator.GetTarget"/>. That is accepted and intended: the flip has no
	///     schedule behind it to interpolate from, and a synthetic blend would be the engine inventing a boundary
	///     nobody configured.
	/// </remarks>
	HomeAssistant = 1
}

/// <summary>
///     A Home Assistant <c>input_select</c> tied to the engine's period table, in whichever direction
///     <see cref="Authority"/> names.
/// </summary>
/// <remarks>
///     <para>
///         One entity, two entirely different jobs, and never both at once. Under
///         <see cref="PeriodAuthority.HomeAssistant"/> the select is an input: the household's own automations move
///         it and the engine follows. Under <see cref="PeriodAuthority.AdaptiveLighting"/> it is an output: the
///         engine keeps it pointing at the period its schedule resolved, so a dashboard or a template can read the
///         time of day the lighting is actually running on.
///     </para>
///     <para>
///         The two are kept apart by construction rather than by a check at each use site — see
///         <see cref="Engine.PeriodSelectReader"/>, which holds one delegate for each direction and assigns exactly
///         one of them.
///     </para>
/// </remarks>
public class PeriodSelectConfig
{
	/// <summary>The <c>input_select</c> holding the time of day. The owner creates the helper in Home Assistant.</summary>
	public string? Entity { get; set; }

	/// <summary>
	///     Which side decides. Defaults to <see cref="PeriodAuthority.AdaptiveLighting"/>, so adding the select
	///     without saying anything else changes nothing about how the lights behave.
	/// </summary>
	public PeriodAuthority Authority { get; set; } = PeriodAuthority.AdaptiveLighting;

	/// <summary>One row per select option the owner has tied to a period. An option nothing names is simply not mapped.</summary>
	public List<PeriodSelectOptionConfig> Options { get; set; } = [];

	/// <summary>
	///     The period the select's <paramref name="value"/> stands for, or <c>null</c> when nothing maps it.
	/// </summary>
	/// <remarks>
	///     Trimmed and case-insensitive, matching <see cref="HouseModeConfig.OptionFor"/> — the select's options are
	///     display strings a person typed into a Home Assistant helper, and a stray trailing space there must not
	///     silently stop the house following the time of day.
	/// </remarks>
	/// <param name="value">The select's current option string.</param>
	public string? PeriodFor(string? value) =>
		value is { Length: > 0 }
			? Options.FirstOrDefault(option =>
				string.Equals(option.Value?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase))?.Period?.Trim()
			: null;

	/// <summary>
	///     The select option standing for <paramref name="periodName"/>, or <c>null</c> when no row names it.
	/// </summary>
	/// <remarks>
	///     The reverse of <see cref="PeriodFor"/>, and the only thing the writing direction needs. First row wins on
	///     a duplicate period, matching how every other name lookup in this document resolves one.
	/// </remarks>
	/// <param name="periodName">The period the engine's own schedule resolved.</param>
	public string? OptionFor(string? periodName) =>
		periodName is { Length: > 0 }
			? Options.FirstOrDefault(option =>
				string.Equals(option.Period?.Trim(), periodName.Trim(), StringComparison.OrdinalIgnoreCase))?.Value?.Trim()
			: null;
}

/// <summary>One select option, and the engine period it stands for.</summary>
public class PeriodSelectOptionConfig
{
	/// <summary>The exact option string as the select reports it. Compared case-insensitively, whitespace-trimmed.</summary>
	public string Value { get; set; } = "";

	/// <summary>
	///     The engine period this option means, by name — the same string <see cref="TimePeriodConfig.Name"/>
	///     carries.
	/// </summary>
	/// <remarks>
	///     By name and not by index, for the reason <see cref="RoomLevelOverride.Period"/> gives: a period can be
	///     inserted, removed or reordered, and a mapping must follow the period it was written about. Unlike a room's
	///     levels, a name matching nothing is an <i>error</i> rather than a warning — a levels row that resolves to
	///     nothing costs one room one preference, whereas this one leaves the whole house unable to place the time of
	///     day the household just selected.
	/// </remarks>
	public string Period { get; set; } = "";

	/// <summary>Whether this row says nothing at all, so an empty one can be dropped on save rather than stored.</summary>
	/// <remarks>
	///     An editor that draws a row per select option produces one of these the moment somebody clears both
	///     fields. Same treatment, and the same reasoning, as <see cref="RoomLevelOverride.IsEmpty"/>.
	/// </remarks>
	[YamlIgnore]
	public bool IsEmpty => string.IsNullOrWhiteSpace(Value) && string.IsNullOrWhiteSpace(Period);
}
