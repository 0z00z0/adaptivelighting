using YamlDotNet.Serialization;

namespace AdaptiveLighting.Configuration;

/// <summary>
///     One managed zone. In the common case a zone declares nothing but an <see cref="AreaId"/> and lets
///     discovery find its entities; the explicit lists exist for when HA's area assignments are wrong.
///     Every settings property is a nullable twin of <see cref="ZoneSettings"/>: <c>null</c> means "inherit".
/// </summary>
public class ZoneConfig
{
	/// <summary>Display name. Defaults to <see cref="AreaId"/> when omitted.</summary>
	public string? Name { get; set; }

	/// <summary>HA registry area <i>id</i> (the slug), not the display name. Drives discovery.</summary>
	public string? AreaId { get; set; }

	/// <summary>Explicit light ids. When present, fully replaces discovery for this slot.</summary>
	public List<string>? Lights { get; set; }

	/// <summary>Explicit motion sensor ids. When present, fully replaces discovery for this slot.</summary>
	public List<string>? MotionSensors { get; set; }

	/// <summary>Explicit lux sensor id. When present, fully replaces discovery for this slot.</summary>
	public string? LuxSensor { get; set; }

	/// <summary>Entities that block auto-on while they are on — a projector, a "do not disturb" flag.</summary>
	public List<string>? IgnoreWhenOn { get; set; }

	/// <inheritdoc cref="ZoneSettings.VacancyTimeoutSeconds"/>
	public int? VacancyTimeoutSeconds { get; set; }

	/// <inheritdoc cref="ZoneSettings.PreOffSeconds"/>
	public int? PreOffSeconds { get; set; }

	/// <inheritdoc cref="ZoneSettings.PreOffBrightnessFactor"/>
	public double? PreOffBrightnessFactor { get; set; }

	/// <inheritdoc cref="ZoneSettings.OverrideDurationMinutes"/>
	public int? OverrideDurationMinutes { get; set; }

	/// <inheritdoc cref="ZoneSettings.VacancyResetMinutes"/>
	public int? VacancyResetMinutes { get; set; }

	/// <inheritdoc cref="ZoneSettings.Darkness"/>
	public DarknessSource? Darkness { get; set; }

	/// <inheritdoc cref="ZoneSettings.LuxThreshold"/>
	public double? LuxThreshold { get; set; }

	/// <inheritdoc cref="ZoneSettings.LuxHysteresis"/>
	public double? LuxHysteresis { get; set; }

	/// <inheritdoc cref="ZoneSettings.SunElevationThreshold"/>
	public double? SunElevationThreshold { get; set; }

	/// <inheritdoc cref="ZoneSettings.SunEntity"/>
	public string? SunEntity { get; set; }

	/// <inheritdoc cref="ZoneSettings.DayTransitionSeconds"/>
	public double? DayTransitionSeconds { get; set; }

	/// <inheritdoc cref="ZoneSettings.NightTransitionSeconds"/>
	public double? NightTransitionSeconds { get; set; }

	/// <inheritdoc cref="ZoneSettings.RespectSleepMode"/>
	public bool? RespectSleepMode { get; set; }

	/// <inheritdoc cref="ZoneSettings.SleepBlocksAutoOn"/>
	public bool? SleepBlocksAutoOn { get; set; }

	/// <inheritdoc cref="ZoneSettings.SkipAwaySweep"/>
	public bool? SkipAwaySweep { get; set; }

	/// <inheritdoc cref="ZoneSettings.WelcomeHome"/>
	public bool? WelcomeHome { get; set; }

	/// <inheritdoc cref="ZoneSettings.Enabled"/>
	public bool? Enabled { get; set; }

	/// <summary>The zone's display name, falling back to the area id and then to a fixed placeholder.</summary>
	/// <remarks>
	///     <see cref="YamlIgnoreAttribute"/>: computed from <see cref="Name"/> and <see cref="AreaId"/>, and
	///     get-only. Serialised it would silently promote the fallback into a real <c>Name</c> on the first save.
	/// </remarks>
	[YamlIgnore]
	public string DisplayName => Name ?? AreaId ?? "(unnamed zone)";

	/// <summary>
	///     Merges this zone's overrides onto <paramref name="defaults"/>, producing the settings the engine
	///     actually uses. Pure: neither argument is mutated.
	/// </summary>
	public ZoneSettings Effective(ZoneSettings defaults)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		return new ZoneSettings
		{
			VacancyTimeoutSeconds = VacancyTimeoutSeconds ?? defaults.VacancyTimeoutSeconds,
			PreOffSeconds = PreOffSeconds ?? defaults.PreOffSeconds,
			PreOffBrightnessFactor = PreOffBrightnessFactor ?? defaults.PreOffBrightnessFactor,
			OverrideDurationMinutes = OverrideDurationMinutes ?? defaults.OverrideDurationMinutes,
			VacancyResetMinutes = VacancyResetMinutes ?? defaults.VacancyResetMinutes,
			Darkness = Darkness ?? defaults.Darkness,
			LuxThreshold = LuxThreshold ?? defaults.LuxThreshold,
			LuxHysteresis = LuxHysteresis ?? defaults.LuxHysteresis,
			SunElevationThreshold = SunElevationThreshold ?? defaults.SunElevationThreshold,
			SunEntity = SunEntity ?? defaults.SunEntity,
			DayTransitionSeconds = DayTransitionSeconds ?? defaults.DayTransitionSeconds,
			NightTransitionSeconds = NightTransitionSeconds ?? defaults.NightTransitionSeconds,
			RespectSleepMode = RespectSleepMode ?? defaults.RespectSleepMode,
			SleepBlocksAutoOn = SleepBlocksAutoOn ?? defaults.SleepBlocksAutoOn,
			SkipAwaySweep = SkipAwaySweep ?? defaults.SkipAwaySweep,
			WelcomeHome = WelcomeHome ?? defaults.WelcomeHome,
			Enabled = Enabled ?? defaults.Enabled
		};
	}
}
