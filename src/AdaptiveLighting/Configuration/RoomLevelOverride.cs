using YamlDotNet.Serialization;

namespace AdaptiveLighting.Configuration;

/// <summary>What one room does instead of the schedule, during one period.</summary>
/// <remarks>
///     A replacement, not an offset, and the two values are independent: a room that names only brightness keeps
///     inheriting the schedule's colour. Nothing holds the value down but the 0-100 clamp and, for a room with
///     <see cref="AreaSettings.RespectSleepMode"/> set, the sleep clamp. Per-period ceilings no longer exist, so
///     <c>{ Period: natt, BrightnessPct: 100 }</c> gets 100 % at three in the morning.
/// </remarks>
public class RoomLevelOverride
{
	/// <summary>The period this replaces, by name, matching <see cref="TimePeriodConfig.Name"/>.</summary>
	/// <remarks>A name matching no period is kept and reported, never dropped: it is almost always a rename.</remarks>
	public string Period { get; set; } = "";

	public double? BrightnessPct { get; set; }

	public int? ColorTempKelvin { get; set; }

	/// <summary>Whether this row says anything, so <see cref="ConfigNormalizer"/> can drop it on save.</summary>
	[YamlIgnore]
	public bool IsEmpty => BrightnessPct is null && ColorTempKelvin is null;
}
