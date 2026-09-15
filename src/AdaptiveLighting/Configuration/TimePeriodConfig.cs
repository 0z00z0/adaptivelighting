using YamlDotNet.Serialization;

namespace AdaptiveLighting.Configuration;

/// <summary>One entry in the circadian table: the target the lights hold from <see cref="Start"/> until the next period.</summary>
public class TimePeriodConfig
{
	/// <summary>What a period holds where nobody has said otherwise: 80 %.</summary>
	private const int DefaultBrightness = 204;

	/// <summary>What every reference to this period names. Minted once, never shown, never changed.</summary>
	/// <remarks>Filled in on load by <see cref="StableKeyMigration"/>. Editing it by hand orphans every reference to the old value.</remarks>
	public string? Id { get; set; }

	/// <summary>Free-form name: <c>morning</c>, <c>day</c>, <c>evening</c>, <c>night</c>. Reported in logs and snapshots. Two periods may share one.</summary>
	public string Name { get; set; } = "";

	/// <summary>What this period is resolved by, everywhere.</summary>
	// Falls back to Name for the one reader that can never be migrated: NetDaemon's ConfigurationBinder, against a
	// house's app YAML, has no pre-pass and so no ids.
	[YamlIgnore]
	public string Key => Id is { Length: > 0 } id ? id.Trim() : Name.Trim();

	/// <summary>
	///     The <see cref="HouseModeOptionConfig.Id"/> the house mode is set to when this period starts; <c>null</c>
	///     leaves the mode unchanged.
	/// </summary>
	/// <remarks>
	///     A value matching no configured option is written to the select verbatim, which is how a live option that
	///     nobody has classified yet still works.
	/// </remarks>
	public string? SetsModeId { get; set; }

	/// <summary>
	///     When this period begins: a clock time (<c>06:30</c>) or a sun event with an optional offset
	///     (<c>sunrise</c>, <c>sunset-01:00</c>). See <see cref="PeriodStart.TryParse"/>.
	/// </summary>
	public string Start { get; set; } = "";

	/// <summary>
	///     This period waits for movement instead of beginning at its <see cref="Start"/>, and then begins for the
	///     whole house: levels, warmth and <see cref="SetsModeId"/> together.
	/// </summary>
	/// <remarks>
	///     Bounded three ways. Movement can only start it once its own <see cref="Start"/> has come round, so morning
	///     cannot fire on a 02:00 trip to the kitchen; it starts once per local day, so walking back in at lunch does
	///     not restart it; and the next period's own <see cref="Start"/> overtakes it, so an empty house is never
	///     stranded on last night's levels.
	/// </remarks>
	public bool StartsOnMotion { get; set; }

	/// <summary>Which rooms' movement may start it, by area id. <c>null</c> and empty both mean any room the engine watches.</summary>
	/// <remarks>
	///     Naming the kitchen keeps a bedroom sensor at 06:05 from starting the morning for the whole house. Nullable
	///     only so <c>OmitNull</c> keeps the key out of a period that names no rooms; <see cref="ConfigNormalizer"/>
	///     writes <c>null</c> and never an empty list, so the two never both occur in a saved document.
	/// </remarks>
	public List<string>? StartsOnMotionAreas { get; set; }

	private double _brightnessPct = RawBrightness.ToPercent(DefaultBrightness);

	/// <summary>The level this period holds, house-wide, as the 0-255 byte Home Assistant accepts.</summary>
	// A room follows the daylight curve instead through its own Levels row for this period
	// (RoomLevelOverride.FollowDaylightCurve); this period never hands that decision away.
	public int Brightness
	{
		get => RawBrightness.FromPercent(_brightnessPct);
		set => _brightnessPct = RawBrightness.ToPercent(value);
	}

	/// <summary>The same level in percent: what the engine works in, and what every ordinary readout rounds.</summary>
	// Bound on load, never written back (LightingConfigDocument.Serialize suppresses it). A percent document keeps
	// its percentage until a save moves it onto the byte grid.
	public double BrightnessPct
	{
		get => _brightnessPct;
		set => _brightnessPct = value;
	}

	public int ColorTempKelvin { get; set; } = 3500;
}
