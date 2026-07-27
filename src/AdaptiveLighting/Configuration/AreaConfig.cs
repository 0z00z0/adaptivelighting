using YamlDotNet.Serialization;

namespace AdaptiveLighting.Configuration;

/// <summary>
///     One managed area. In the common case an area declares nothing but an <see cref="AreaId"/> and lets
///     discovery find its entities; the explicit lists exist for when HA's area assignments are wrong.
///     Every settings property is a nullable twin of <see cref="AreaSettings"/>: <c>null</c> means "inherit".
/// </summary>
public class AreaConfig
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

	/// <summary>
	///     Entity ids discovery must skip for this room — the escape hatch for a sensor sitting in the room's HA
	///     area that should not drive its lighting, such as a fridge's internal illuminance sensor.
	/// </summary>
	/// <remarks>
	///     Distinct from <see cref="IgnoreWhenOn"/>, which blocks auto-on while something is on, and from the global
	///     exclude <i>label</i>, which hides an entity from every room: this is per-room and by id. It filters
	///     discovery only. An explicit <see cref="Lights"/> or <see cref="MotionSensors"/> list is already the owner
	///     overruling discovery by hand and is not touched by it.
	/// </remarks>
	public List<string>? ExcludeEntities { get; set; }

	/// <inheritdoc cref="AreaSettings.VacancyTimeoutSeconds"/>
	public int? VacancyTimeoutSeconds { get; set; }

	/// <inheritdoc cref="AreaSettings.PreOffSeconds"/>
	public int? PreOffSeconds { get; set; }

	/// <inheritdoc cref="AreaSettings.PreOffBrightnessFactor"/>
	public double? PreOffBrightnessFactor { get; set; }

	/// <inheritdoc cref="AreaSettings.OverrideDurationMinutes"/>
	public int? OverrideDurationMinutes { get; set; }

	/// <inheritdoc cref="AreaSettings.VacancyResetMinutes"/>
	public int? VacancyResetMinutes { get; set; }

	/// <inheritdoc cref="AreaSettings.Darkness"/>
	public DarknessSource? Darkness { get; set; }

	/// <inheritdoc cref="AreaSettings.LuxThreshold"/>
	public double? LuxThreshold { get; set; }

	/// <inheritdoc cref="AreaSettings.LuxHysteresis"/>
	public double? LuxHysteresis { get; set; }

	/// <inheritdoc cref="AreaSettings.SunElevationThreshold"/>
	public double? SunElevationThreshold { get; set; }

	/// <inheritdoc cref="AreaSettings.SunEntity"/>
	public string? SunEntity { get; set; }

	/// <inheritdoc cref="AreaSettings.DayTransitionSeconds"/>
	public double? DayTransitionSeconds { get; set; }

	/// <inheritdoc cref="AreaSettings.NightTransitionSeconds"/>
	public double? NightTransitionSeconds { get; set; }

	/// <inheritdoc cref="AreaSettings.RespectSleepMode"/>
	public bool? RespectSleepMode { get; set; }

	/// <inheritdoc cref="AreaSettings.SleepBlocksAutoOn"/>
	public bool? SleepBlocksAutoOn { get; set; }

	/// <inheritdoc cref="AreaSettings.SkipAwaySweep"/>
	public bool? SkipAwaySweep { get; set; }

	/// <inheritdoc cref="AreaSettings.WelcomeHome"/>
	public bool? WelcomeHome { get; set; }

	/// <inheritdoc cref="AreaSettings.Enabled"/>
	public bool? Enabled { get; set; }

	/// <summary>The area's display name, falling back to the area id and then to a fixed placeholder.</summary>
	/// <remarks>
	///     <see cref="YamlIgnoreAttribute"/>: computed from <see cref="Name"/> and <see cref="AreaId"/>, and
	///     get-only. Serialised it would silently promote the fallback into a real <c>Name</c> on the first save.
	/// </remarks>
	[YamlIgnore]
	public string DisplayName => Name ?? AreaId ?? "(unnamed area)";

	/// <summary>
	///     Merges this area's overrides onto <paramref name="defaults"/>, producing the settings the engine
	///     actually uses. Pure: neither argument is mutated.
	/// </summary>
	public AreaSettings Effective(AreaSettings defaults)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		return new AreaSettings
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
