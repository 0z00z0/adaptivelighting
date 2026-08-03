using System.Text.Json.Serialization;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     The wire shape of an <c>adaptive_lighting_area</c> event, as
///     <see cref="AdaptiveLighting.Ha.HaStatePublisher"/> writes it.
/// </summary>
/// <remarks>
///     Every property names itself explicitly because NetDaemon's <c>Event&lt;T&gt;.Data</c> deserializes with no
///     serializer options: matching is case-sensitive and exact, so these are the publisher's names verbatim.
///     A field an older build did not publish arrives as <c>null</c>.
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

	/// <summary>Whether the engine was muzzled at that instant.</summary>
	[JsonPropertyName("kill_switch_active")]
	public bool KillSwitchActive { get; init; }

	/// <summary>What the darkness gate said when last consulted, or <c>null</c> if it never has been.</summary>
	[JsonPropertyName("is_dark")]
	public bool? IsDark { get; init; }

	[JsonPropertyName("period")]
	public string? Period { get; init; }

	/// <summary>The brightness last commanded, or <c>null</c>.</summary>
	[JsonPropertyName("brightness_pct")]
	public double? BrightnessPct { get; init; }

	[JsonPropertyName("color_temp_kelvin")]
	public int? ColorTempKelvin { get; init; }

	/// <summary>Scheduler time of the transition.</summary>
	[JsonPropertyName("timestamp")]
	public DateTimeOffset Timestamp { get; init; }

	/// <summary>When the engine last commanded this area's lights, or <c>null</c> if it has not since start-up.</summary>
	[JsonPropertyName("last_command_at")]
	public DateTimeOffset? LastCommandAt { get; init; }

	/// <summary>When motion was last seen, or <c>null</c> if none has been seen since start-up.</summary>
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

	/// <summary>The entity id behind an <see cref="AutoOnBlock.EntityOn"/> block, or <c>null</c>.</summary>
	[JsonPropertyName("auto_on_blocking_entity")]
	public string? AutoOnBlockingEntity { get; init; }

	/// <summary>
	///     Whether a <c>KeepLitWhenOn</c> entity was suspending the engine's own off-command. <c>null</c> from a
	///     build that did not publish it.
	/// </summary>
	[JsonPropertyName("is_held_lit")]
	public bool? IsHeldLit { get; init; }

	/// <summary>The entity doing the holding, or <c>null</c>.</summary>
	[JsonPropertyName("held_lit_by")]
	public string? HeldLitBy { get; init; }

	/// <summary>The room's own scene the area is sitting on, or <c>null</c> when the engine is aiming it itself.</summary>
	[JsonPropertyName("scene_applied")]
	public string? SceneApplied { get; init; }

	/// <summary>
	///     Rebuilds an <see cref="AreaSnapshot"/> from the wire shape, or <c>null</c> when the payload names no
	///     area. An unparseable enum name degrades to its zero value; nothing throws.
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
			// Not default, unlike the enums above: AutoOnBlock's zero is "nothing is blocking", which a report that
			// never carried the field did not say.
			Enum.TryParse(AutoOnBlockedBy, out AutoOnBlock block) ? block : null,
			AutoOnBlockingEntity,
			IsHeldLit: IsHeldLit,
			HeldLitBy: HeldLitBy,
			SceneApplied: SceneApplied);
	}
}
