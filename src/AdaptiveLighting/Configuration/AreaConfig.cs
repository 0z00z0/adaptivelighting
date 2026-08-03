using YamlDotNet.Serialization;

namespace AdaptiveLighting.Configuration;

/// <summary>
///     One managed area. In the common case an area declares nothing but an <see cref="AreaId"/> and lets
///     discovery find its entities; the explicit lists exist for when HA's area assignments are wrong.
///     Every settings property is a nullable twin of <see cref="AreaSettings"/>: <c>null</c> means "inherit".
/// </summary>
public class AreaConfig
{
	/// <summary>
	///     Display name, stated only when the household wants one of its own. Left <c>null</c>, the room is called
	///     whatever Home Assistant calls its area, so a rename there still arrives here.
	/// </summary>
	public string? Name { get; set; }

	/// <summary>HA registry area id (the slug), not the display name. Drives discovery.</summary>
	public string? AreaId { get; set; }

	public List<string>? Lights { get; set; }

	public List<string>? MotionSensors { get; set; }

	/// <summary>Explicit lux sensor id. When present, fully replaces discovery for this slot.</summary>
	public string? LuxSensor { get; set; }

	/// <summary>
	///     Whether this room reads <see cref="GlobalConfig.OutdoorLuxSensor"/> when it has no lux sensor of its own.
	/// </summary>
	/// <remarks>
	///     An opt-in, not a fallback: a room that says nothing has no lux reading and counts as dark. The room's own
	///     <see cref="LuxSensor"/> still wins. The reading is taken whatever <see cref="AreaSettings.Darkness"/>
	///     consults, because the daylight curve follows it too. Nullable so <c>OmitNull</c> keeps an unasked room out
	///     of the file; <c>null</c> and <c>false</c> mean the same thing.
	/// </remarks>
	public bool? FollowOutdoorLux { get; set; }

	/// <summary>Entities that block auto-on while they are on: a projector, a "do not disturb" flag.</summary>
	public List<string>? IgnoreWhenOn { get; set; }

	/// <summary>
	///     Entity ids discovery must skip for this room, such as a fridge's internal illuminance sensor sitting in
	///     the room's HA area.
	/// </summary>
	/// <remarks>
	///     Filters discovery only. An explicit <see cref="Lights"/> or <see cref="MotionSensors"/> list is untouched
	///     by it.
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

	/// <summary>
	///     What this room does instead of the schedule, period by period. Empty means it follows the schedule
	///     everywhere, which is the overwhelming majority of rooms.
	/// </summary>
	/// <remarks>A room names only the periods it disagrees about; a row it does not write still follows the schedule.</remarks>
	public List<RoomLevelOverride> Levels { get; set; } = [];

	/// <inheritdoc cref="AreaSettings.LuxThreshold"/>
	public double? LuxThreshold { get; set; }

	/// <inheritdoc cref="AreaSettings.LuxHysteresis"/>
	public double? LuxHysteresis { get; set; }

	/// <inheritdoc cref="AreaSettings.LuxBrightnessEnabled"/>
	public bool? LuxBrightnessEnabled { get; set; }

	/// <inheritdoc cref="AreaSettings.LuxBrightnessStartLux"/>
	public double? LuxBrightnessStartLux { get; set; }

	/// <inheritdoc cref="AreaSettings.LuxBrightnessFullLux"/>
	public double? LuxBrightnessFullLux { get; set; }

	/// <inheritdoc cref="AreaSettings.LuxBrightnessMaxPct"/>
	public double? LuxBrightnessMaxPct { get; set; }

	/// <inheritdoc cref="AreaSettings.LuxBrightnessGamma"/>
	public double? LuxBrightnessGamma { get; set; }

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

	/// <summary>The area's name as the document alone knows it: <see cref="Name"/>, the area id, then a placeholder.</summary>
	/// <remarks>
	///     A key into the document, not something to show a person: it cannot consult Home Assistant, so a room
	///     carrying only an area id reads as its slug. Anything holding a registry asks <see cref="Engine.AreaNaming"/>.
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
			LuxBrightnessEnabled = LuxBrightnessEnabled ?? defaults.LuxBrightnessEnabled,
			LuxBrightnessStartLux = LuxBrightnessStartLux ?? defaults.LuxBrightnessStartLux,
			LuxBrightnessFullLux = LuxBrightnessFullLux ?? defaults.LuxBrightnessFullLux,
			LuxBrightnessMaxPct = LuxBrightnessMaxPct ?? defaults.LuxBrightnessMaxPct,
			LuxBrightnessGamma = LuxBrightnessGamma ?? defaults.LuxBrightnessGamma,
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
