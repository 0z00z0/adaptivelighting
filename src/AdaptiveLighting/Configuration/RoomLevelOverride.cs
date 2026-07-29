using YamlDotNet.Serialization;

namespace AdaptiveLighting.Configuration;

/// <summary>
///     What one room does instead of the schedule, during one period.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> Until now brightness and colour temperature were the schedule's alone: every
///         room ran the same four rows, and a room that wanted to be dimmer had nowhere to say so. A cellar
///         corridor at the living room's 100 % is glare; a workshop at the evening's 2 700 K is unusable. The
///         schedule stays the description of the day — this is one room disagreeing with it, in one period.
///     </para>
///     <para>
///         <b>Both values are optional and independent.</b> A room that only wants to be dimmer sets brightness
///         and leaves the warmth alone, and inherits every later change to the schedule's colour. Filling both in
///         because the type has two fields is exactly the coupling that makes a per-room table rot: half of it
///         then silently pins values the owner never chose, and a schedule edit stops reaching the rooms.
///     </para>
///     <para>
///         <b>It replaces, it does not offset.</b> An offset is the tempting shape — "Kontor, always +10 %" — and
///         it reads well until the schedule moves: a +10 % on a period raised to 100 % is a room asking for 110,
///         and the answer has to be invented. A replacement is what it looks like, survives any edit to the row it
///         overrides, and the period's own caps still apply on top, so a room cannot escape a ceiling the house
///         set deliberately.
///     </para>
/// </remarks>
public class RoomLevelOverride
{
	/// <summary>
	///     The period this replaces, by name — the same string <see cref="TimePeriodConfig.Name"/> carries.
	/// </summary>
	/// <remarks>
	///     By name and not by index, because a period can be inserted, removed or reordered and a room's overrides
	///     must follow the period they were written about rather than whatever now sits in that position. A name
	///     that matches no period is kept and reported rather than dropped: it is almost always a rename, and
	///     silently deleting somebody's levels on a rename is the worse failure.
	/// </remarks>
	public string Period { get; set; } = "";

	/// <summary>
	///     What this room is set to during that period, instead of the schedule's brightness. <c>null</c> follows
	///     the schedule.
	/// </summary>
	public double? BrightnessPct { get; set; }

	/// <summary>
	///     The white this room uses during that period, instead of the schedule's. <c>null</c> follows the
	///     schedule.
	/// </summary>
	public int? ColorTempKelvin { get; set; }

	/// <summary>Whether this row says anything at all, so an empty one can be dropped on save rather than stored.</summary>
	[YamlIgnore]
	public bool IsEmpty => BrightnessPct is null && ColorTempKelvin is null;
}
