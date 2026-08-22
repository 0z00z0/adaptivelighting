using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Abstractions;

/// <summary>Everything worth knowing about an area at one instant.</summary>
/// <remarks>
///     Anything the engine has not evaluated yet is <c>null</c>, never a default dressed as a fact, and so is any
///     field a snapshot from an older build never carried. <c>Timestamp</c> is scheduler time, not wall-clock, so
///     tests read what they set. <c>AreaId</c> is the stable join back to the document, since <c>AreaName</c> is
///     editable mid-session. <c>Forced</c> is carried, never re-derived: only the engine knows which entity it read.
/// </remarks>
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
	ForcedMode? Forced = null,
	bool? IsHeldLit = null,
	string? HeldLitBy = null,
	string? SceneApplied = null)
{
	/// <summary>Whether <paramref name="other"/> carries the same news about the area as this snapshot does.</summary>
	/// <remarks>
	///     Not record equality: <c>==</c> compares the "as of" fields, every one of which moves on every tick, so
	///     diffing on it would suppress nothing. Timestamps, <see cref="Reason"/>, <see cref="DarknessDetail"/>,
	///     <see cref="AutoOnBlockedBy"/> and <see cref="HeldLitBy"/> date or describe the snapshot; they say nothing
	///     about the area.
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
		Nullable.Equals(IsHeldLit, other.IsHeldLit) &&
		string.Equals(SceneApplied, other.SceneApplied, StringComparison.Ordinal) &&
		Forced == other.Forced;
}

public interface IStatePublisher
{
	/// <summary>Publishes <paramref name="snapshot"/>. Must not throw: it is called from inside the area's lock.</summary>
	void Publish(AreaSnapshot snapshot);
}
