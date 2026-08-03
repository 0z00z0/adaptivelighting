using YamlDotNet.Serialization;

namespace AdaptiveLighting.Configuration;

/// <summary>The one behaviour a house-mode option carries.</summary>
public enum ModeKind
{
	Normal,

	/// <summary>Respecting areas clamp to the sleep-clamp period; <see cref="AreaSettings.SleepBlocksAutoOn"/> refuses auto-on.</summary>
	Sleep,

	/// <summary>Applies a scene (or the classic sweep) and pauses the engine house-wide until a reset trigger fires.</summary>
	Away,

	Guest
}

/// <summary>Which side owns the house mode: this application, or the Home Assistant dropdown.</summary>
/// <remarks>
///     Ordinals are pinned and no member may be renamed or removed, for the reason
///     <see cref="PeriodAuthority"/> gives: two binders read this document, an unknown key is silence in both,
///     and an unknown enum value is a <see cref="FormatException"/> at startup.
/// </remarks>
public enum HouseModeAuthority
{
	/// <summary>
	///     This application decides, and the default, so a document that says nothing behaves as every document
	///     did before the authority existed. A period's <c>SetsMode</c>, the no-motion rule and
	///     <c>ActivateWhileOn</c> all still write the mode, and the select is kept in step as a mirror.
	/// </summary>
	AdaptiveLighting = 0,

	/// <summary>
	///     Home Assistant decides. The engine reads the select and never writes it, and its own mode rules stand
	///     down: <see cref="TimePeriodConfig.SetsMode"/> and <c>ActivateAfterNoMotionMinutes</c> stop firing, so
	///     the dropdown is the only thing that moves the house.
	/// </summary>
	HomeAssistant = 1
}

/// <summary>The house-mode select and what each of its options means to the engine.</summary>
public class HouseModeConfig
{
	public string? Entity { get; set; }

	/// <summary>
	///     <see cref="Entity"/> as anything reading Home Assistant should ask for it: trimmed, or <c>null</c>
	///     when blank.
	/// </summary>
	/// <remarks>
	///     Same accessor, and the same reason, as <see cref="PeriodSelectConfig.EntityId"/>: a trailing space in
	///     a hand-edited file otherwise puts the engine on one entity and the pages on another.
	/// </remarks>
	[YamlIgnore]
	public string? EntityId => Entity is { Length: > 0 } entity && entity.Trim() is { Length: > 0 } trimmed
		? trimmed
		: null;

	/// <summary>
	///     Which side decides. Defaults to <see cref="HouseModeAuthority.AdaptiveLighting"/>, so adding the key
	///     changes nothing on its own.
	/// </summary>
	public HouseModeAuthority Authority { get; set; } = HouseModeAuthority.AdaptiveLighting;

	/// <summary>Whether Home Assistant owns the mode, so nothing in the engine may write the select.</summary>
	[YamlIgnore]
	public bool HomeAssistantDecides => Authority is HouseModeAuthority.HomeAssistant && EntityId is not null;

	public List<HouseModeOptionConfig> Options { get; set; } = [];

	/// <summary>The configured option whose value equals <paramref name="value"/> (ordinal-insensitive, trimmed), or null.</summary>
	public HouseModeOptionConfig? OptionFor(string? value) =>
		value is { Length: > 0 }
			? Options.FirstOrDefault(o => string.Equals(o.Value?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase))
			: null;

	/// <summary>
	///     The single reset target: the first option marked <see cref="ModeKind.Normal"/>, or <c>null</c> when none
	///     is. It never falls back to a tagged option, so with nothing Normal every reset is a no-op.
	/// </summary>
	[YamlIgnore]
	public HouseModeOptionConfig? NormalOption =>
		Options.FirstOrDefault(o => o.Kind == ModeKind.Normal);

	/// <summary>
	///     The period a sleep option clamps to, by the one chain the engine and the UI both use: the option's
	///     <see cref="HouseModeOptionConfig.ClampPeriod"/>, else the first period whose
	///     <see cref="TimePeriodConfig.SetsMode"/> sets this option, else a period named <c>night</c>, else <c>null</c>.
	/// </summary>
	public static string? SleepClampPeriodFor(HouseModeOptionConfig option, IReadOnlyList<TimePeriodConfig> periods)
	{
		ArgumentNullException.ThrowIfNull(option);
		ArgumentNullException.ThrowIfNull(periods);

		if (option.ClampPeriod is { Length: > 0 } clamp)
			return clamp;

		TimePeriodConfig? bySetsMode = periods.FirstOrDefault(p =>
			p.SetsMode is { Length: > 0 } &&
			string.Equals(p.SetsMode.Trim(), option.Value?.Trim(), StringComparison.OrdinalIgnoreCase));
		if (bySetsMode is not null)
			return bySetsMode.Name;

		TimePeriodConfig? night = periods.FirstOrDefault(p => string.Equals(p.Name, "night", StringComparison.OrdinalIgnoreCase));
		return night?.Name;
	}
}

/// <summary>One option value of the house-mode select, and the behaviour and reset triggers it carries.</summary>
public class HouseModeOptionConfig
{
	/// <summary>The exact option string as the select reports it. Compared case-insensitively, whitespace-trimmed.</summary>
	public string Value { get; set; } = "";

	/// <summary>The option's one behaviour. One option should be <see cref="ModeKind.Normal"/>, the reset target.</summary>
	public ModeKind Kind { get; set; } = ModeKind.Normal;

	/// <summary><c>scene.*</c> applied on mode entry. Meaningful for Away and Guest; inert elsewhere.</summary>
	public string? Scene { get; set; }

	/// <summary>Sleep only: the period sleep-respecting areas clamp to. Optional; see <see cref="HouseModeConfig.SleepClampPeriodFor"/>.</summary>
	public string? ClampPeriod { get; set; }

	/// <summary>Period name. When that period starts, reset to Normal. Only meaningful on a non-Normal option.</summary>
	public string? ResetOnPeriodStart { get; set; }

	public bool ResetOnPresence { get; set; }

	/// <summary>
	///     The presence sensors whose turn-on resets this mode. Empty means auto: the union of every motion sensor
	///     configured across all areas. Members are <c>binary_sensor.*</c> and/or <c>person.*</c>.
	/// </summary>
	public List<string> ResetPresenceSensors { get; set; } = [];

	/// <summary>Presence events within this many minutes of the mode being set are ignored, so leaving cannot cancel its own Away.</summary>
	public int ResetPresenceGraceMinutes { get; set; } = 15;

	/// <summary>
	///     Entities that force this option to be the effective house mode while any of them is on. Empty means the
	///     select alone decides. An overlay on the select, never written back to it, re-evaluated on any state change.
	/// </summary>
	public List<string> ActivateWhileOn { get; set; } = [];

	/// <summary>
	///     Switches the house to this option once the whole house has had no motion for this many minutes.
	///     <c>null</c> disables it. Watches the same motion union the presence reset defaults to.
	/// </summary>
	public int? ActivateAfterNoMotionMinutes { get; set; }

	/// <summary>Whether this option carries any reset trigger. Used by the normaliser to keep non-trivial rows.</summary>
	/// <remarks>
	///     Presence is gated on <see cref="ResetOnPresence"/> alone, never on a non-empty
	///     <see cref="ResetPresenceSensors"/> list, so sensors listed with the toggle off are inert everywhere.
	/// </remarks>
	[YamlIgnore]
	public bool HasResetTrigger => ResetOnPeriodStart is { Length: > 0 } || ResetOnPresence;
}
