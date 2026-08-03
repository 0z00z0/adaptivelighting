using YamlDotNet.Serialization;

namespace AdaptiveLighting.Configuration;

/// <summary>Which side owns the time of day: this application's schedule, or a Home Assistant dropdown.</summary>
/// <remarks>
///     Ordinals are pinned and no member may be renamed or removed. Two readers bind
///     <see cref="AdaptiveLightingConfig"/>: <see cref="LightingConfigDocument.Deserialize"/>, which has a legacy
///     pre-pass, and NetDaemon's binder on the app YAML, which cannot have one. An unknown key is silence in both;
///     an unknown enum value is a <see cref="FormatException"/> at start-up. <c>Enum.Parse</c> also accepts the bare
///     numeral, so <c>Authority: 1</c> in a hand-written file must keep meaning what it meant.
/// </remarks>
public enum PeriodAuthority
{
	/// <summary>
	///     This application decides, and the default. The engine resolves the period from its own schedule and writes
	///     the select as a mirror of that decision.
	/// </summary>
	AdaptiveLighting = 0,

	/// <summary>
	///     Home Assistant decides. The engine reads the select and never writes it; the option it reports maps to a
	///     period name, and that period is what every room runs.
	/// </summary>
	/// <remarks>The day/night blend has no boundary time under this authority and becomes a step.</remarks>
	HomeAssistant = 1
}

/// <summary>
///     A Home Assistant <c>input_select</c> tied to the engine's period table, in whichever direction
///     <see cref="Authority"/> names.
/// </summary>
/// <remarks>
///     One entity, two jobs, never both at once: an input under <see cref="PeriodAuthority.HomeAssistant"/>, an
///     output under <see cref="PeriodAuthority.AdaptiveLighting"/>. <see cref="Engine.PeriodSelectReader"/> holds one
///     delegate per direction and assigns one of them, so the split is by construction and not by a check per use.
/// </remarks>
public class PeriodSelectConfig
{
	public string? Entity { get; set; }

	/// <summary>
	///     <see cref="Entity"/> as anything reading Home Assistant should ask for it: trimmed, or <c>null</c> when
	///     it is blank.
	/// </summary>
	/// <remarks>
	///     Every reader goes through here, not <see cref="Entity"/>. A trailing space in a hand-edited file once left
	///     the engine on <c>night</c> while the pages badged <c>day</c>, with the document passing validation.
	/// </remarks>
	[YamlIgnore]
	public string? EntityId => Entity is { Length: > 0 } entity && entity.Trim() is { Length: > 0 } trimmed
		? trimmed
		: null;

	public PeriodAuthority Authority { get; set; } = PeriodAuthority.AdaptiveLighting;

	public List<PeriodSelectOptionConfig> Options { get; set; } = [];

	/// <summary>The period <paramref name="value"/> stands for, or <c>null</c> when nothing maps it.</summary>
	/// <remarks>Trimmed and case-insensitive: the options are display strings a person typed into an HA helper.</remarks>
	public string? PeriodFor(string? value)
	{
		if (value is not { Length: > 0 })
			return null;

		// Trimmed once, outside the predicate: this runs per area per evaluation once a house follows the select.
		string needle = value.Trim();

		return Options
			.FirstOrDefault(option => string.Equals(option.Value?.Trim(), needle, StringComparison.OrdinalIgnoreCase))
			?.Period?.Trim();
	}

	/// <summary>
	///     The select option standing for <paramref name="periodName"/>, or <c>null</c> when no row names it. First
	///     row wins on a duplicate, as every other name lookup in this document does.
	/// </summary>
	public string? OptionFor(string? periodName)
	{
		if (periodName is not { Length: > 0 })
			return null;

		string needle = periodName.Trim();

		return Options
			.FirstOrDefault(option => string.Equals(option.Period?.Trim(), needle, StringComparison.OrdinalIgnoreCase))
			?.Value?.Trim();
	}
}

/// <summary>One select option, and the engine period it stands for.</summary>
public class PeriodSelectOptionConfig
{
	/// <summary>The exact option string as the select reports it. Compared case-insensitively, whitespace-trimmed.</summary>
	public string Value { get; set; } = "";

	/// <summary>The engine period this option means, by name, matching <see cref="TimePeriodConfig.Name"/>.</summary>
	/// <remarks>
	///     A name matching no period is a validation error here, where the same shape is only a warning on a room's
	///     levels: an unresolvable mapping leaves the whole house unable to place the selected time of day.
	/// </remarks>
	public string Period { get; set; } = "";

	/// <summary>Whether this row says nothing, so <see cref="ConfigNormalizer"/> can drop it on save.</summary>
	[YamlIgnore]
	public bool IsEmpty => string.IsNullOrWhiteSpace(Value) && string.IsNullOrWhiteSpace(Period);
}
