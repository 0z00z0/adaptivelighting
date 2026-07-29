using System.Text.Json.Serialization;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     The wire shape of an <c>adaptive_lighting_area</c> event, as
///     <see cref="AdaptiveLighting.Ha.HaStatePublisher"/> writes it.
/// </summary>
/// <remarks>
///     <para>
///         Every property carries an explicit <see cref="JsonPropertyNameAttribute"/> because NetDaemon's
///         <c>Event&lt;T&gt;.Data</c> calls <c>Deserialize&lt;T&gt;()</c> with <i>no</i> serializer options —
///         matching is case-sensitive and exact, so the names here must be the publisher's names verbatim.
///         This record is the contract between the two halves; if the publisher's payload changes, this
///         changes with it.
///     </para>
///     <para>
///         This is a deliberately lossy read of an HA event rather than a direct hand-off from the engine.
///         See <see cref="AreaSnapshotCache"/> for why.
///     </para>
/// </remarks>
public sealed record AreaSnapshotEvent
{
	/// <summary>The area's display name.</summary>
	[JsonPropertyName("area")]
	public string? Area { get; init; }

	/// <summary>
	///     The registry area id behind the display name, or <c>null</c> when the area was configured with explicit
	///     entity lists and no area. Absent from events published by builds that predate it, which deserializes as
	///     <c>null</c>: a reader then falls back to matching on <see cref="Area"/>, which is what it did before.
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

	/// <summary>The active circadian period, or <c>null</c>.</summary>
	[JsonPropertyName("period")]
	public string? Period { get; init; }

	/// <summary>The brightness last commanded, or <c>null</c>.</summary>
	[JsonPropertyName("brightness_pct")]
	public double? BrightnessPct { get; init; }

	/// <summary>The colour temperature last commanded, or <c>null</c>.</summary>
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

	/// <summary>When the area's armed timer will act, or <c>null</c> when nothing is scheduled.</summary>
	[JsonPropertyName("next_change_at")]
	public DateTimeOffset? NextChangeAt { get; init; }

	/// <summary>
	///     When that countdown was armed, or <c>null</c> when nothing is scheduled — the other end of the bar.
	///     Absent from events published by builds that predate it, which deserializes as <c>null</c>: the card
	///     then shows the deadline without a bar rather than a bar with an invented start.
	/// </summary>
	[JsonPropertyName("next_change_from")]
	public DateTimeOffset? NextChangeFrom { get; init; }

	/// <summary>
	///     The raw house-mode option string in effect (<c>Sover</c>, <c>Borte</c>), or <c>null</c> when no select
	///     is configured. Absent from events published by builds that predate it, which deserializes as <c>null</c>.
	/// </summary>
	[JsonPropertyName("house_mode_value")]
	public string? HouseModeValue { get; init; }

	/// <summary>
	///     The darkness gate's reading in words (e.g. <c>lux 86, dark below 40</c>), or <c>null</c> if it never
	///     consulted the gate. Absent from events published by builds that predate it, which deserializes as <c>null</c>.
	/// </summary>
	[JsonPropertyName("darkness_detail")]
	public string? DarknessDetail { get; init; }

	/// <summary>
	///     Which gate was holding auto-on off (an <see cref="AutoOnBlock"/> name), or <c>null</c>. Absent from
	///     events published by builds that predate it, which deserializes as <c>null</c> — and null here means
	///     "this report cannot say", never "nothing was blocking", which is a claim it has no grounds for.
	/// </summary>
	[JsonPropertyName("auto_on_blocked_by")]
	public string? AutoOnBlockedBy { get; init; }

	/// <summary>The entity id behind an <see cref="AutoOnBlock.EntityOn"/> block, or <c>null</c>.</summary>
	[JsonPropertyName("auto_on_blocking_entity")]
	public string? AutoOnBlockingEntity { get; init; }

	/// <summary>
	///     Rebuilds an <see cref="AreaSnapshot"/> from the wire shape, or returns <c>null</c> when the payload
	///     does not name an area.
	/// </summary>
	/// <remarks>
	///     Unparseable enum names degrade to their zero value rather than throwing: a UI that goes blank
	///     because one event carried a state name it did not recognise would be worse than one that says
	///     <c>Disabled</c> for a moment.
	/// </remarks>
	/// <returns>The reconstructed snapshot, or <c>null</c>.</returns>
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
			// Unlike the enums above, an unreadable or absent value degrades to null rather than to the zero
			// value: AutoOnBlock's zero is "nothing is blocking", and a report that never carried the field
			// made no such statement.
			Enum.TryParse(AutoOnBlockedBy, out AutoOnBlock block) ? block : null,
			AutoOnBlockingEntity);
	}
}
