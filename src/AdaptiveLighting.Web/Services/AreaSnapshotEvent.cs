using System.Text.Json.Serialization;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     The wire shape of an <c>adaptive_lighting_area</c> event, as
///     <see cref="AdaptiveLighting.Ha.HaStatePublisher"/> writes it.
/// </summary>
/// <remarks>
///     Every property names itself explicitly: NetDaemon's <c>Event&lt;T&gt;.Data</c> deserializes with no
///     serializer options, so matching is case-sensitive and these are the publisher's names verbatim. A field an
///     older build did not publish arrives as <c>null</c>.
/// </remarks>
public sealed record AreaSnapshotEvent
{
	[JsonPropertyName("area")]
	public string? Area { get; init; }

	/// <summary>
	///     The registry area id behind the display name, or <c>null</c> when the area was configured with explicit
	///     entity lists and no area. A reader then falls back to matching on <see cref="Area"/>.
	/// </summary>
	[JsonPropertyName("area_id")]
	public string? AreaId { get; init; }

	/// <summary>The <see cref="AreaState"/> name.</summary>
	[JsonPropertyName("state")]
	public string? State { get; init; }

	/// <summary>The <see cref="TransitionReason"/> name.</summary>
	[JsonPropertyName("reason")]
	public string? Reason { get; init; }

	/// <summary>The <see cref="HouseMode"/> name.</summary>
	[JsonPropertyName("mode")]
	public string? Mode { get; init; }

	[JsonPropertyName("kill_switch_active")]
	public bool KillSwitchActive { get; init; }

	/// <summary>What the darkness gate said when last consulted, or <c>null</c> if it never has been.</summary>
	[JsonPropertyName("is_dark")]
	public bool? IsDark { get; init; }

	[JsonPropertyName("period")]
	public string? Period { get; init; }

	[JsonPropertyName("brightness_pct")]
	public double? BrightnessPct { get; init; }

	[JsonPropertyName("color_temp_kelvin")]
	public int? ColorTempKelvin { get; init; }

	/// <summary>Scheduler time of the transition.</summary>
	[JsonPropertyName("timestamp")]
	public DateTimeOffset Timestamp { get; init; }

	[JsonPropertyName("last_command_at")]
	public DateTimeOffset? LastCommandAt { get; init; }

	[JsonPropertyName("last_motion_at")]
	public DateTimeOffset? LastMotionAt { get; init; }

	/// <summary>When the area's armed timer will act, or <c>null</c> when nothing is armed.</summary>
	[JsonPropertyName("next_change_at")]
	public DateTimeOffset? NextChangeAt { get; init; }

	/// <summary>
	///     When that countdown was armed, the other end of the bar. <c>null</c> makes the card show the deadline
	///     without a bar, in place of a bar with an invented start.
	/// </summary>
	[JsonPropertyName("next_change_from")]
	public DateTimeOffset? NextChangeFrom { get; init; }

	/// <summary>The raw house-mode option in effect, or <c>null</c> when no select is configured.</summary>
	[JsonPropertyName("house_mode_value")]
	public string? HouseModeValue { get; init; }

	/// <summary>The darkness gate's reading in words, or <c>null</c> if the gate was never consulted.</summary>
	[JsonPropertyName("darkness_detail")]
	public string? DarknessDetail { get; init; }

	/// <summary>
	///     Which gate was holding auto-on off, as an <see cref="AutoOnBlock"/> name. <c>null</c> means this report
	///     cannot say, never that nothing was blocking.
	/// </summary>
	[JsonPropertyName("auto_on_blocked_by")]
	public string? AutoOnBlockedBy { get; init; }

	[JsonPropertyName("auto_on_blocking_entity")]
	public string? AutoOnBlockingEntity { get; init; }

	/// <summary>
	///     Whether a <c>KeepLitWhenOn</c> entity was suspending the engine's own off-command. <c>null</c> from a
	///     build that did not publish it.
	/// </summary>
	[JsonPropertyName("is_held_lit")]
	public bool? IsHeldLit { get; init; }

	[JsonPropertyName("held_lit_by")]
	public string? HeldLitBy { get; init; }

	/// <summary>The room's own scene the area is sitting on, or <c>null</c> when the engine is aiming it itself.</summary>
	[JsonPropertyName("scene_applied")]
	public string? SceneApplied { get; init; }

	/// <summary>Whether the house has anybody in it, or <c>null</c> from a build that never published it.</summary>
	[JsonPropertyName("is_anyone_home")]
	public bool? IsAnyoneHome { get; init; }

	/// <summary>The <see cref="ForcedMode.Kind"/> name, or <c>null</c> when the select's value is the whole story.</summary>
	[JsonPropertyName("mode_forced_kind")]
	public string? ModeForcedKind { get; init; }

	[JsonPropertyName("mode_forced_option")]
	public string? ModeForcedOption { get; init; }

	/// <summary>The <see cref="ModeForceSource"/> name.</summary>
	[JsonPropertyName("mode_forced_source")]
	public string? ModeForcedSource { get; init; }

	[JsonPropertyName("mode_forced_by")]
	public string? ModeForcedBy { get; init; }

	[JsonPropertyName("mode_forced_by_state")]
	public string? ModeForcedByState { get; init; }

	/// <summary>
	///     Rebuilds an <see cref="AreaSnapshot"/>, or <c>null</c> when the payload names no area. An unparseable
	///     enum name degrades to its zero value; nothing throws.
	/// </summary>
	public AreaSnapshot? ToSnapshot()
	{
		if (string.IsNullOrWhiteSpace(Area))
			return null;

		return new AreaSnapshot(
			Area,
			Enum.TryParse<AreaState>(State, out AreaState state) ? state : default,
			Enum.TryParse<TransitionReason>(Reason, out TransitionReason reason) ? reason : default,
			Enum.TryParse<HouseMode>(Mode, out HouseMode mode) ? mode : default,
			KillSwitchActive,
			IsDark,
			Period,
			BrightnessPct,
			ColorTempKelvin,
			Timestamp,
			LastCommandAt,
			LastMotionAt,
			NextChangeAt,
			NextChangeFrom,
			HouseModeValue,
			DarknessDetail,
			AreaId,
			// Not default, unlike the enums above: AutoOnBlock's zero is "nothing is blocking", which a report
			// that never carried the field did not say.
			Enum.TryParse(AutoOnBlockedBy, out AutoOnBlock block) ? block : null,
			AutoOnBlockingEntity,
			IsAnyoneHome: IsAnyoneHome,
			// Both the kind and the source have to parse: a Forced sentence half-recovered would misdescribe it.
			Forced: Enum.TryParse(ModeForcedKind, out ModeKind kind) && Enum.TryParse(ModeForcedSource, out ModeForceSource source)
				? new ForcedMode(kind, ModeForcedOption ?? "", source, ModeForcedBy, ModeForcedByState)
				: null,
			IsHeldLit: IsHeldLit,
			HeldLitBy: HeldLitBy,
			SceneApplied: SceneApplied);
	}
}
