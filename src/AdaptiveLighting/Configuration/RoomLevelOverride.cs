using YamlDotNet.Serialization;

namespace AdaptiveLighting.Configuration;

/// <summary>What one room does instead of the schedule, during one period.</summary>
/// <remarks>
///     Replaces the schedule instead of offsetting it, and the two values are independent: a room naming only
///     brightness keeps inheriting the schedule's colour. Only the 0-100 clamp holds the value down, plus the sleep
///     clamp for a room with <see cref="AreaSettings.RespectSleepMode"/> set, so 100 % at three in the morning stands.
/// </remarks>
public class RoomLevelOverride
{
	/// <summary>The period this replaces, by <see cref="TimePeriodConfig.Id"/>.</summary>
	/// <remarks>An id matching no period is kept and reported, never dropped: it is almost always a deleted period.</remarks>
	public string PeriodId { get; set; } = "";

	public double? BrightnessPct { get; set; }

	public int? ColorTempKelvin { get; set; }

	/// <summary>Whether this row says anything, so <see cref="ConfigNormalizer"/> can drop it on save.</summary>
	[YamlIgnore]
	public bool IsEmpty => BrightnessPct is null && ColorTempKelvin is null;
}
