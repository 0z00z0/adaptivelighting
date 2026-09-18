using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Abstractions;

/// <summary>Everything worth knowing about an area at one instant.</summary>
/// <remarks>
///     Anything the engine has not evaluated yet is <c>null</c>, never a default dressed as a fact, and so is any
///     field a snapshot from an older build never carried. <c>Timestamp</c> is scheduler time, not wall-clock, so
///     tests read what they set. <c>AreaId</c> is the stable join back to the document, since <c>AreaName</c> is
///     editable mid-session. <c>Forced</c> is carried, never re-derived: only the engine knows which entity it read.
///     <c>IsLeadIn</c> is carried for the whole dim light and cleared on leaving <c>PreOff</c>, unlike
///     <see cref="Reason"/>, which only names the publish that changed something.
/// </remarks>
public sealed record AreaSnapshot(
	string AreaName,
	AreaState State,
	TransitionReason Reason,
	ModeKind Mode,
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
	string? SceneApplied = null,
	string? TestingPeriodId = null,
	DateTimeOffset? TestEndsAt = null,
	IReadOnlyList<LightStanding>? LightLevels = null,
	string? TestingLightId = null,
	IReadOnlyList<string>? LightsMoved = null,
	int? LightsNotResponding = null,
	int? LightCount = null,
	string? ChangedBy = null,
	DateTimeOffset? ChangedAt = null,
	bool? IsLeadIn = null,
	IReadOnlyList<SensorBattery>? LowBatteries = null)
{
	/// <summary>Whether the room's lights are on: lit by the engine, dimming before off, or held on by hand, at a
	/// brightness above zero.</summary>
	/// <remarks>Computed, never published: the event carries the state and the brightness it is read from.</remarks>
	public bool IsLit =>
		(State is AreaState.AutoActive or AreaState.PreOff or AreaState.OverriddenOn) && BrightnessPct > 0;

	/// <summary>Whether <paramref name="other"/> carries the same news about the area as this snapshot does.</summary>
	/// <remarks>
	///     Not record equality: <c>==</c> compares the "as of" fields, every one of which moves on every tick, so
	///     diffing on it would suppress nothing. Timestamps, <see cref="Reason"/>, <see cref="DarknessDetail"/>,
	///     <see cref="AutoOnBlockedBy"/>, <see cref="HeldLitBy"/>, <see cref="ChangedBy"/> and <see cref="ChangedAt"/> date
	///     or describe the snapshot; they say nothing about the area. <see cref="TestingPeriodId"/> and <see cref="TestEndsAt"/> are compared, unlike those:
	///     a level test starting or ending is real news, and it is the only news a snapshot carries while nothing
	///     else about the area moves. A suppressed publish would leave a fresh page load with nothing to redraw.
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
		Nullable.Equals(IsLeadIn, other.IsLeadIn) &&
		string.Equals(SceneApplied, other.SceneApplied, StringComparison.Ordinal) &&
		string.Equals(TestingPeriodId, other.TestingPeriodId, StringComparison.Ordinal) &&
		Nullable.Equals(TestEndsAt, other.TestEndsAt) &&
		string.Equals(TestingLightId, other.TestingLightId, StringComparison.Ordinal) &&
		LightsNotResponding == other.LightsNotResponding &&
		LightCount == other.LightCount &&
		SameLights(LightLevels, other.LightLevels) &&
		SameBatteries(LowBatteries, other.LowBatteries) &&
		Forced == other.Forced;

	// LightsMoved is left out: it says what this publish was about, like Reason. A light moving on its own is still
	// news through LightLevels, or the tick that retunes one lamp would be suppressed as a repeat.
	private static bool SameLights(IReadOnlyList<LightStanding>? left, IReadOnlyList<LightStanding>? right) =>
		left is null || right is null ? left is null && right is null : left.SequenceEqual(right);

	private static bool SameBatteries(IReadOnlyList<SensorBattery>? left, IReadOnlyList<SensorBattery>? right) =>
		(left ?? []).SequenceEqual(right ?? []);
}
