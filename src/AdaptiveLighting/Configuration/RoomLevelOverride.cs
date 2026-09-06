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

	private double? _brightnessPct;

	/// <summary>The level this room holds for the period, as the 0-255 byte Home Assistant accepts.</summary>
	public int? Brightness
	{
		get => _brightnessPct is { } percent ? RawBrightness.FromPercent(percent) : null;
		set => _brightnessPct = value is { } raw ? RawBrightness.ToPercent(raw) : null;
	}

	/// <summary>The same level in percent: what the engine works in, and what every ordinary readout rounds.</summary>
	// Bound on load and never written back — LightingConfigDocument.Serialize suppresses it — so a document written
	// before brightness became raw commands its exact percentage until a save moves it onto the byte grid.
	public double? BrightnessPct
	{
		get => _brightnessPct;
		set => _brightnessPct = value;
	}

	public int? ColorTempKelvin { get; set; }

	/// <summary>Whether this room follows the daylight curve for this period instead of <see cref="BrightnessPct"/>.</summary>
	/// <remarks>
	///     Named apart from the retired period-level <c>UseDaylightCurve</c>: <see cref="LightingConfigDocument.RetiredKeys"/>
	///     matches key names on the raw document with no type context, so reusing that name here would report every
	///     legitimate opt-in as a stale key. <c>null</c> and <c>false</c> mean the same thing, matching
	///     <see cref="AreaConfig.FollowOutdoorLux"/>'s nullable-bool-opt-in shape.
	/// </remarks>
	public bool? FollowDaylightCurve { get; set; }

	/// <summary>Whether this row says anything, so <see cref="ConfigNormalizer"/> can drop it on save.</summary>
	[YamlIgnore]
	public bool IsEmpty => BrightnessPct is null && ColorTempKelvin is null && FollowDaylightCurve is not true;
}
