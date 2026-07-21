using YamlDotNet.Serialization;

namespace AdaptiveLighting.Configuration;

/// <summary>The one behaviour a house-mode option carries. Replaces the three combinable booleans (09 §2.1).</summary>
public enum ModeKind
{
	/// <summary>The baseline, and the single reset target. No special behaviour.</summary>
	Normal,

	/// <summary>Respecting zones clamp to a sleep-clamp period's caps; <see cref="ZoneSettings.SleepBlocksAutoOn"/> refuses auto-on.</summary>
	Sleep,

	/// <summary>Applies a scene (or the classic sweep) and pauses the engine house-wide until a reset trigger fires.</summary>
	Away,

	/// <summary>Applies a scene and holds the zones; with no scene, a dashboard flag only.</summary>
	Guest
}

/// <summary>The house-mode select and what each of its options means to the engine.</summary>
public class HouseModeConfig
{
	/// <summary>The <c>input_select</c> whose state is the house mode. The owner creates the helper in HA.</summary>
	public string? Entity { get; set; }

	/// <summary>One entry per option value the owner has classified, each carrying a <see cref="HouseModeOptionConfig.Kind"/>.</summary>
	public List<HouseModeOptionConfig> Options { get; set; } = [];

	/// <summary>The configured option whose value equals <paramref name="value"/> (ordinal-insensitive, trimmed), or null.</summary>
	public HouseModeOptionConfig? OptionFor(string? value) =>
		value is { Length: > 0 }
			? Options.FirstOrDefault(o => string.Equals(o.Value?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase))
			: null;

	/// <summary>
	///     The single reset target: the first option with <see cref="ModeKind.Normal"/>, or <c>null</c> when no
	///     option is marked Normal (09 §2.2). It never falls back to a tagged option — resetting to a Sleep/Away/Guest
	///     option is exactly the clobber this must not do; when nothing is Normal, every reset is a no-op.
	/// </summary>
	/// <remarks>
	///     <see cref="YamlIgnoreAttribute"/> because it is a view over <see cref="Options"/>, not a stored field.
	///     Multiple Normals → the first wins (the validator warns).
	/// </remarks>
	[YamlIgnore]
	public HouseModeOptionConfig? NormalOption =>
		Options.FirstOrDefault(o => o.Kind == ModeKind.Normal);

	/// <summary>
	///     The period a sleep option clamps to, resolved by the one chain both the engine and the UI use (09 §4.1):
	///     the option's explicit <see cref="HouseModeOptionConfig.ClampPeriod"/>, else the first period whose
	///     <see cref="TimePeriodConfig.SetsMode"/> sets this option, else a period literally named <c>night</c>,
	///     else <c>null</c>.
	/// </summary>
	/// <param name="option">The sleep option whose clamp period is wanted.</param>
	/// <param name="periods">The circadian table to resolve the clamp period name against.</param>
	/// <returns>The resolved clamp-period name, or <c>null</c> when nothing resolves.</returns>
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

/// <summary>One option value of the house-mode select, and the single behaviour and reset triggers it carries (09 §2.1).</summary>
public class HouseModeOptionConfig
{
	/// <summary>The exact option string as the select reports it. Compared case-insensitively, whitespace-trimmed.</summary>
	public string Value { get; set; } = "";

	/// <summary>The option's one behaviour. Exactly one option should be <see cref="ModeKind.Normal"/> — the reset target.</summary>
	public ModeKind Kind { get; set; } = ModeKind.Normal;

	/// <summary><c>scene.*</c> applied on mode entry. Meaningful for <see cref="ModeKind.Away"/>/<see cref="ModeKind.Guest"/>; inert elsewhere.</summary>
	public string? Scene { get; set; }

	/// <summary>Sleep only: the period whose caps sleep-respecting zones clamp to. Optional — see <see cref="HouseModeConfig.SleepClampPeriodFor"/>.</summary>
	public string? ClampPeriod { get; set; }

	/// <summary>Period name. When that period starts, reset to Normal. Only meaningful on a non-Normal option.</summary>
	public string? ResetOnPeriodStart { get; set; }

	/// <summary>Whether an arrival on <see cref="ResetPresenceSensors"/> resets to Normal after the grace window.</summary>
	public bool ResetOnPresence { get; set; }

	/// <summary>
	///     The presence sensors whose turn-on resets this mode. <b>Empty means auto</b>: the union of every motion
	///     sensor configured across all zones (09 owner refinement). A non-empty list is exactly those sensors —
	///     <c>binary_sensor.*</c> and/or <c>person.*</c>.
	/// </summary>
	public List<string> ResetPresenceSensors { get; set; } = [];

	/// <summary>Presence events within this many minutes of the mode being set are ignored (you leaving must not cancel your own Borte).</summary>
	public int ResetPresenceGraceMinutes { get; set; } = 15;

	/// <summary>An <c>input_datetime.*</c>. When its moment passes (after activation), reset to Normal. Time-only helpers mean "daily at that time".</summary>
	public string? ResetAtTime { get; set; }

	/// <summary>
	///     Boolean-ish entities that force this option to be the effective house mode while any of them is on
	///     (generalises the removed legacy sleep switch). While any listed entity is on, this option wins over the
	///     select's value; <b>empty means the select alone decides</b>. Members are <c>input_boolean.*</c>,
	///     <c>switch.*</c> and/or <c>binary_sensor.*</c>. The engine never writes the select from this — it is an
	///     overlay on top of the select, re-evaluated when any listed entity changes state.
	/// </summary>
	public List<string> ActivateWhileOn { get; set; } = [];

	/// <summary>
	///     When set, the house switches <b>to</b> this option once the whole house has had no motion for this many
	///     minutes — auto-away by inactivity. <c>null</c> disables it. It watches the same house-wide motion union the
	///     presence reset defaults to; any motion, or the mode already standing here, resets the idle clock. Only
	///     meaningful on a non-Normal option. Paired with a presence reset, this gives "empty for 6 h → Away, someone
	///     moves → Normal".
	/// </summary>
	public int? ActivateAfterNoMotionMinutes { get; set; }

	/// <summary>Whether this option carries any reset trigger. Used by the normaliser to keep non-trivial rows.</summary>
	/// <remarks>
	///     Presence is gated on <see cref="ResetOnPresence"/> alone, never on a non-empty
	///     <see cref="ResetPresenceSensors"/> list: the toggle is authoritative everywhere (the engine subscribes only
	///     when it is on, the validator judges the branch by it), so "sensors listed but the toggle off" is
	///     consistently inert rather than half-armed.
	/// </remarks>
	[YamlIgnore]
	public bool HasResetTrigger =>
		ResetOnPeriodStart is { Length: > 0 }
		|| ResetOnPresence
		|| ResetAtTime is { Length: > 0 };
}
