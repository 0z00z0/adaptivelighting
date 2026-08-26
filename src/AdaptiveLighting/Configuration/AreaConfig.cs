using YamlDotNet.Serialization;

namespace AdaptiveLighting.Configuration;

/// <summary>One managed area, declaring explicit entity lists only where HA's area assignments are wrong.</summary>
/// <remarks>Every settings property is a nullable twin of <see cref="AreaSettings"/>, where <c>null</c> means inherit.</remarks>
public class AreaConfig
{
	/// <summary>Display name of the household's own choosing; <c>null</c> keeps following the Home Assistant area name.</summary>
	public string? Name { get; set; }

	/// <summary>HA registry area id (the slug), not the display name. Drives discovery.</summary>
	public string? AreaId { get; set; }

	public List<string>? Lights { get; set; }

	public List<string>? MotionSensors { get; set; }

	/// <summary>Explicit lux sensor id. When present, fully replaces discovery for this slot.</summary>
	public string? LuxSensor { get; set; }

	/// <summary>Whether this room reads <see cref="GlobalConfig.OutdoorLuxSensor"/> when it has no lux sensor of its own.</summary>
	/// <remarks>
	///     An opt-in: a room that says nothing has no lux reading and counts as dark, and its own <see cref="LuxSensor"/>
	///     still wins. Darkness only; the daylight curve reads <see cref="DaylightSensor"/> instead. <c>null</c> and
	///     <c>false</c> mean the same thing; nullable only so <c>OmitNull</c> keeps an unasked room out of the file.
	/// </remarks>
	public bool? FollowOutdoorLux { get; set; }

	/// <summary>Which light-level sensor the daylight curve reads here; <c>null</c> reads <see cref="GlobalConfig.OutdoorLuxSensor"/>.</summary>
	/// <remarks>
	///     Separate from <see cref="LuxSensor"/>, which decides darkness. An indoor sensor measures the room's own
	///     lamps, so the curve would chase itself; the house's outdoor reading is the default for that reason.
	/// </remarks>
	public string? DaylightSensor { get; set; }

	/// <summary>Entities that block auto-on while they are on: a projector, a "do not disturb" flag.</summary>
	public List<string>? IgnoreWhenOn { get; set; }

	/// <summary>Read <see cref="IgnoreWhenOn"/> the other way: block while they are <i>off</i>.</summary>
	/// <remarks>An entity that cannot be read blocks under neither polarity, so a vanished helper never pins a room dark.</remarks>
	public bool? IgnoreWhenOnInverted { get; set; }

	/// <summary>Entities that stop the engine switching this room's lights <i>off</i> while they are on.</summary>
	/// <remarks>
	///     Holds off the vacancy timeout, the pre-off warning dim and the leaving sweep. It never turns anything on,
	///     and a person switching the lights off by hand is still obeyed.
	/// </remarks>
	public List<string>? KeepLitWhenOn { get; set; }

	/// <summary>Read <see cref="KeepLitWhenOn"/> the other way: hold the lights on while they are <i>off</i>.</summary>
	public bool? KeepLitWhenOnInverted { get; set; }

	/// <summary>A scene run in place of this room's own levels when movement lights it.</summary>
	// Replaces the command, never the gates: every auto-on gate is consulted first and a refusal runs nothing.
	public string? SceneOnMotion { get; set; }

	/// <summary>A scene run in place of switching this room off when it goes empty.</summary>
	// The room settles here and stays lit, so the pre-off warning dim does not run. The leaving sweep is a different
	// event and still switches the room off.
	public string? SceneWhenEmpty { get; set; }

	/// <summary>Entity ids discovery must skip for this room, such as a fridge's illuminance sensor sitting in its HA area.</summary>
	/// <remarks>Filters discovery only. An explicit <see cref="Lights"/> or <see cref="MotionSensors"/> list is untouched.</remarks>
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

	/// <inheritdoc cref="AreaSettings.ColorControl"/>
	public ColorControl? ColorControl { get; set; }

	/// <summary>What this room does instead of the schedule, period by period; a period it omits still follows the schedule.</summary>
	public List<RoomLevelOverride> Levels { get; set; } = [];

	/// <inheritdoc cref="AreaSettings.LuxThreshold"/>
	public double? LuxThreshold { get; set; }

	/// <inheritdoc cref="AreaSettings.LuxHysteresis"/>
	public double? LuxHysteresis { get; set; }

	/// <inheritdoc cref="AreaSettings.LuxBrightnessStartLux"/>
	public double? LuxBrightnessStartLux { get; set; }

	/// <inheritdoc cref="AreaSettings.LuxBrightnessFullLux"/>
	public double? LuxBrightnessFullLux { get; set; }

	/// <inheritdoc cref="AreaSettings.LuxBrightnessMinPct"/>
	public double? LuxBrightnessMinPct { get; set; }

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
	/// <remarks>A key into the document, not something to show a person: it cannot consult Home Assistant, so a room carrying only an area id reads as its slug.</remarks>
	[YamlIgnore]
	public string DisplayName => Name ?? AreaId ?? "(unnamed area)";

	/// <summary>Merges this area's overrides onto <paramref name="defaults"/>. Pure: neither argument is mutated.</summary>
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
			ColorControl = ColorControl ?? defaults.ColorControl,
			LuxThreshold = LuxThreshold ?? defaults.LuxThreshold,
			LuxHysteresis = LuxHysteresis ?? defaults.LuxHysteresis,
			LuxBrightnessStartLux = LuxBrightnessStartLux ?? defaults.LuxBrightnessStartLux,
			LuxBrightnessFullLux = LuxBrightnessFullLux ?? defaults.LuxBrightnessFullLux,
			LuxBrightnessMinPct = LuxBrightnessMinPct ?? defaults.LuxBrightnessMinPct,
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
