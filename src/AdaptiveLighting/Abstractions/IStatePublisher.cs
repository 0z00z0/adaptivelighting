using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Abstractions;

/// <summary>
///     Everything worth knowing about an area at one instant. Published on every transition, and whenever the
///     published truth moves without one, so the state machine is observable from outside the process.
/// </summary>
/// <remarks>Anything the engine has not evaluated yet is <c>null</c>, never a default dressed as a fact.</remarks>
/// <param name="AreaName">The area's display name.</param>
/// <param name="State">The state the area has just entered, or re-entered.</param>
/// <param name="Reason">Why the area entered the state.</param>
/// <param name="Mode">The house mode at that instant.</param>
/// <param name="KillSwitchActive">Whether the engine was muzzled.</param>
/// <param name="IsDark">What the darkness gate said when last consulted, or <c>null</c> if it never has been.</param>
/// <param name="PeriodName">The circadian period in effect, or <c>null</c> when none resolves.</param>
/// <param name="BrightnessPct">The brightness of the standing command, or <c>null</c> when that command was "off" or none was sent.</param>
/// <param name="ColorTempKelvin">The colour temperature of the standing command, or <c>null</c>.</param>
/// <param name="Timestamp">Scheduler time, not wall-clock time, so tests read what they set.</param>
/// <param name="LastCommandAt">When the engine last commanded these lights. Distinguishes "sent off" from "left as found".</param>
/// <param name="LastMotionAt">When motion was last seen, or <c>null</c> if none has been seen since start-up.</param>
/// <param name="NextChangeAt">When the area's armed timer will act, or <c>null</c> when nothing is scheduled. What it will do is implied by <paramref name="State"/>.</param>
/// <param name="NextChangeFrom">When that countdown was armed. Not derivable from the other timestamps: <paramref name="Timestamp"/> and <paramref name="LastMotionAt"/> both move on republishes that re-arm nothing.</param>
/// <param name="HouseModeValue">The raw option string in effect (<c>Sover</c>, <c>Borte</c>), or <c>null</c> when no select is configured.</param>
/// <param name="DarknessDetail">The gate's reading in words (<c>lux 86, dark below 40</c>). Descriptive, so excluded from <see cref="AreaSnapshot.HasSameMeaningAs"/>.</param>
/// <param name="AreaId">The stable join between a snapshot and the document that produced it: <paramref name="AreaName"/> is editable mid-session and an id is not.</param>
/// <param name="AutoOnBlockedBy">Which gate would refuse to light this area for movement, <see cref="AutoOnBlock.None"/> when none would, or <c>null</c> from a build predating the field. A sleeping house and an <c>IgnoreWhenOn</c> entity both leave the area in <see cref="AreaState.AutoVacant"/> looking like one merely waiting.</param>
/// <param name="AutoOnBlockingEntity">The entity holding auto-on off when <paramref name="AutoOnBlockedBy"/> is <see cref="AutoOnBlock.EntityOn"/>, and <c>null</c> otherwise.</param>
/// <param name="LevelsFromRoom">Which of the room's two levels it names for itself during <paramref name="PeriodName"/>. A statement about the period, not the standing command. <c>null</c> from a build predating the field.</param>
/// <param name="IsAnyoneHome">What presence said. <paramref name="Mode"/> cannot answer it, because <see cref="HouseMode.Away"/> is presence or an away-kind option. <c>null</c> from a build predating the field.</param>
/// <param name="Forced">What holds the house on its mode when the select's value is not the answer, and <c>null</c> when nothing does. Carried, never re-derived: only the engine knows which entity it read.</param>
public sealed record AreaSnapshot(
	string AreaName,
	AreaState State,
	TransitionReason Reason,
	HouseMode Mode,
	bool KillSwitchActive,
	bool? IsDark,
	string? PeriodName,
	double? BrightnessPct,
	int? ColorTempKelvin,
	DateTimeOffset Timestamp,
	DateTimeOffset? LastCommandAt,
	DateTimeOffset? LastMotionAt,
	DateTimeOffset? NextChangeAt,
	DateTimeOffset? NextChangeFrom,
	string? HouseModeValue = null,
	string? DarknessDetail = null,
	string? AreaId = null,
	AutoOnBlock? AutoOnBlockedBy = null,
	string? AutoOnBlockingEntity = null,
	RoomLevelSource? LevelsFromRoom = null,
	bool? IsAnyoneHome = null,
	ForcedMode? Forced = null)
{
	/// <summary>Whether <paramref name="other"/> carries the same news about the area as this snapshot does.</summary>
	/// <remarks>
	///     Not record equality: <c>==</c> compares the "as of" fields, every one of which moves on every tick, so
	///     diffing on it would suppress nothing and the periodic evaluation would degenerate into a heartbeat.
	///     <see cref="Timestamp"/>, <see cref="LastCommandAt"/>, <see cref="LastMotionAt"/> and <see cref="Reason"/>
	///     are excluded: they date the snapshot, they do not describe the area. <see cref="DarknessDetail"/> and
	///     <see cref="AutoOnBlockedBy"/> are excluded so a drifting lux reading, or a television switching on, does
	///     not republish every area it touches; movement into a blocked room is published past this comparison, by
	///     <c>AreaController.ReportDeclinedMotion</c>, which bounds itself to one row per change of gate.
	/// </remarks>
	public bool HasSameMeaningAs(AreaSnapshot? other) =>
		other is not null &&
		State == other.State &&
		Mode == other.Mode &&
		string.Equals(HouseModeValue, other.HouseModeValue, StringComparison.Ordinal) &&
		KillSwitchActive == other.KillSwitchActive &&
		IsDark == other.IsDark &&
		string.Equals(PeriodName, other.PeriodName, StringComparison.Ordinal) &&
		Nullable.Equals(BrightnessPct, other.BrightnessPct) &&
		ColorTempKelvin == other.ColorTempKelvin &&
		Nullable.Equals(NextChangeAt, other.NextChangeAt) &&
		Nullable.Equals(NextChangeFrom, other.NextChangeFrom) &&
		Nullable.Equals(LevelsFromRoom, other.LevelsFromRoom) &&
		Nullable.Equals(IsAnyoneHome, other.IsAnyoneHome) &&
		Forced == other.Forced;
}

/// <summary>Where area snapshots go. A no-op implementation is a legitimate choice.</summary>
public interface IStatePublisher
{
	/// <summary>Publishes <paramref name="snapshot"/>. Must not throw: it is called from inside the area's lock.</summary>
	void Publish(AreaSnapshot snapshot);
}
