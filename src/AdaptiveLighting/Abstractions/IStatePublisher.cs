using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Abstractions;

/// <summary>
///     Everything worth knowing about an area at one instant. Published on every transition — and whenever the
///     published truth moves without one, such as motion pushing a vacancy deadline back — so the state machine
///     is observable from outside the process. An invisible state machine is one nobody can trust.
/// </summary>
/// <remarks>
///     Fields that the engine has not evaluated yet are <c>null</c>, never a default dressed as a fact. The
///     first snapshot after start-up knows almost nothing, and it must say so: a dashboard that renders an
///     unevaluated darkness verdict as "too light" while the room sits dark teaches people not to read it.
/// </remarks>
/// <param name="AreaName">The area's display name.</param>
/// <param name="State">The state the area has just entered, or re-entered.</param>
/// <param name="Reason">Why.</param>
/// <param name="Mode">The house mode at that instant.</param>
/// <param name="KillSwitchActive">Whether the engine was muzzled.</param>
/// <param name="IsDark">What the darkness gate said when last consulted, or <c>null</c> if it never has been.</param>
/// <param name="PeriodName">The circadian period in effect at this instant, or <c>null</c> when none resolves.</param>
/// <param name="BrightnessPct">The brightness of the engine's standing command, or <c>null</c> when that command was "off" or none was ever sent.</param>
/// <param name="ColorTempKelvin">The colour temperature of the standing command, or <c>null</c>.</param>
/// <param name="Timestamp">Scheduler time of the snapshot — not wall-clock time, so tests read what they set.</param>
/// <param name="LastCommandAt">When the engine last commanded this area's lights, or <c>null</c> if it has not since it started. Distinguishes "the lights were sent off" from "the lights were left exactly as found".</param>
/// <param name="LastMotionAt">When motion was last seen, or <c>null</c> if none has been seen since start-up.</param>
/// <param name="NextChangeAt">When the area's armed timer will act — the vacancy timeout, the pre-off grace, the override expiry or the suppression reset, whichever the current state carries — or <c>null</c> when nothing is scheduled. What it will do is implied by <paramref name="State"/>.</param>
/// <param name="NextChangeFrom">When the countdown behind <paramref name="NextChangeAt"/> was armed, or <c>null</c> when nothing is scheduled. Together they are both ends of the countdown, which is what lets a reader render elapsed-versus-remaining rather than a bare deadline. Not derivable from the other timestamps: <paramref name="Timestamp"/> and <paramref name="LastMotionAt"/> both move on republishes that re-arm nothing.</param>
/// <param name="HouseModeValue">The raw house-mode option string in effect (<c>Sover</c>, <c>Borte</c>), or <c>null</c> when no select is configured. Beside <paramref name="Mode"/> so a card can say "Sover", not just "Sleep".</param>
/// <param name="DarknessDetail">The darkness gate's reading in words the last time it was consulted (e.g. <c>lux 86, dark below 40</c>), or <c>null</c> if it never has been. Lets a card say <i>why</i> a bright vacant area is waiting rather than just that it is. Descriptive, like <paramref name="Reason"/>: excluded from <see cref="AreaSnapshot.HasSameMeaningAs"/> so a drifting lux reading does not republish on its own.</param>
/// <param name="AreaId">The HA registry area id this area was built from, or <c>null</c> when it was configured with explicit entity lists and no area. This is the stable join between a snapshot and the document that produced it: <paramref name="AreaName"/> is editable mid-session and an id is not, so a reader that matched on the name alone lost the area the moment somebody renamed a room. Excluded from <see cref="AreaSnapshot.HasSameMeaningAs"/>: it identifies the area rather than describing its state, and cannot change without the area being rebuilt anyway.</param>
/// <param name="AutoOnBlockedBy">Which gate would refuse to light this area for movement at this instant, <see cref="AutoOnBlock.None"/> when none would, or <c>null</c> from a build that predates the field. Two of the refusals — a sleeping house, and an <c>IgnoreWhenOn</c> entity that is on — leave the area in <see cref="AreaState.AutoVacant"/> looking exactly like an area that is merely waiting, so a reader holding only <paramref name="State"/> and <paramref name="IsDark"/> would confidently promise a light that will not come on. Descriptive, like <paramref name="DarknessDetail"/>: excluded from <see cref="AreaSnapshot.HasSameMeaningAs"/>, so a television switching on does not by itself republish every area it blocks.</param>
/// <param name="AutoOnBlockingEntity">The entity id holding auto-on off when <paramref name="AutoOnBlockedBy"/> is <see cref="AutoOnBlock.EntityOn"/>, and <c>null</c> otherwise. Named rather than counted: "something is on" leaves the reader hunting through the room, which is the dead end this field exists to end.</param>
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
	string? AutoOnBlockingEntity = null)
{
	/// <summary>
	///     Whether <paramref name="other"/> says the same thing about the area as this snapshot does — the
	///     state, the conditions the engine read, what it is holding the lights at, and what it is waiting for.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>Not record equality, deliberately.</b> A <see cref="AreaSnapshot"/> is a record, so <c>==</c>
	///         compares the "as of" fields too — and every one of those moves on every tick. Diffing on record
	///         equality would therefore never suppress anything, and the periodic evaluation would degenerate
	///         into exactly the fixed-rate heartbeat it exists to avoid. This compares meaning: <see cref="Timestamp"/>,
	///         <see cref="LastCommandAt"/> and <see cref="LastMotionAt"/> are excluded because they date the
	///         snapshot rather than describe the area.
	///     </para>
	///     <para>
	///         <see cref="Reason"/> is excluded for the same reason: it explains how the area arrived somewhere,
	///         not where it is. Two snapshots reached by different routes that agree on everything else are the
	///         same news, and the second is not worth publishing.
	///     </para>
	///     <para>
	///         <see cref="LastMotionAt"/> being excluded has one honest consequence: motion in an area too bright
	///         to light updates the engine's record of it, and no tick will republish for that alone. That is the
	///         intended trade — the alternative is an event every time anyone walks through a sunlit room.
	///     </para>
	///     <para>
	///         <see cref="AutoOnBlockedBy"/> is excluded on the same trade. It is a verdict rather than an "as of"
	///         field, but including it would republish every area a television blocks the moment it is switched on,
	///         and again when it goes off. Each report still carries the verdict that held when it was published,
	///         which is what a timeline row needs; what it does not do is generate rows of its own.
	///     </para>
	/// </remarks>
	/// <param name="other">The snapshot to compare against, typically the last one published for this area.</param>
	/// <returns><c>true</c> when the two carry the same news; <c>false</c> when something worth publishing moved.</returns>
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
		Nullable.Equals(NextChangeFrom, other.NextChangeFrom);
}

/// <summary>
///     Where area snapshots go. A no-op implementation is a legitimate choice; the interface exists so the
///     transport (HA event today, an MQTT entity later) can change without the engine noticing.
/// </summary>
public interface IStatePublisher
{
	/// <summary>Publishes <paramref name="snapshot"/>. Must not throw: it is called from inside the area's lock.</summary>
	void Publish(AreaSnapshot snapshot);
}
