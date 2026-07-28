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
	///     whatever Home Assistant calls its area — see <see cref="Engine.AreaNaming"/> — which is what keeps a
	///     rename in Home Assistant arriving here instead of freezing the name at set-up time.
	/// </summary>
	public string? Name { get; set; }

	/// <summary>HA registry area <i>id</i> (the slug), not the display name. Drives discovery.</summary>
	public string? AreaId { get; set; }

	/// <summary>Explicit light ids. When present, fully replaces discovery for this slot.</summary>
	public List<string>? Lights { get; set; }

	/// <summary>Explicit motion sensor ids. When present, fully replaces discovery for this slot.</summary>
	public List<string>? MotionSensors { get; set; }

	/// <summary>Explicit lux sensor id. When present, fully replaces discovery for this slot.</summary>
	public string? LuxSensor { get; set; }

	/// <summary>
	///     Whether this room reads <see cref="GlobalConfig.OutdoorLuxSensor"/> when it has no lux sensor of its own.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>The opt-in that replaced a silent fallback.</b> Every sensorless room used to be handed the
	///         house's outdoor sensor without being asked, so a room's darkness was decided by a reading taken
	///         outside it — and one shaded outdoor sensor reads hundreds of lux while the rooms behind it are dark,
	///         which is how a whole house came to refuse to light itself. Saying nothing now means the room has no
	///         lux reading at all, and the lux half of its gate stops holding it back; a room that genuinely wants
	///         to follow the weather says so here.
	///     </para>
	///     <para>
	///         <b>It sits beside <see cref="LuxSensor"/> rather than among the settings on purpose.</b> This
	///         answers the same question that slot answers — which entity supplies this room's illuminance — and
	///         not "how should the room behave". That is the same line <see cref="Lights"/>,
	///         <see cref="MotionSensors"/> and <see cref="ExcludeEntities"/> already sit on: entity bindings live
	///         on the room, tunables live in <see cref="AreaSettings"/> and are inherited from the document
	///         defaults. A room's own sensor still wins over the outdoor one, so this only ever fills a gap.
	///     </para>
	///     <para>
	///         It composes with <see cref="AreaSettings.Darkness"/> rather than duplicating it. Under <c>Lux</c> or
	///         <c>Either</c> it decides whether there is a lux verdict to have; under <c>Sun</c> or <c>Always</c>
	///         darkness ignores lux entirely — but the reading is still taken, because
	///         <see cref="AreaSettings.LuxBrightnessEnabled"/> follows the daylight whatever the gate consults, and
	///         a hallway lit on motion at any hour whose <i>level</i> should follow the sun outside is precisely
	///         the case that feature exists for.
	///     </para>
	///     <para>
	///         Nullable, though <c>null</c> and <c>false</c> mean the same thing to the engine: it keeps the
	///         document silent for every room that has not been asked (<c>OmitNull</c>), so adding the setting
	///         rewrites nobody's file.
	///     </para>
	/// </remarks>
	public bool? FollowOutdoorLux { get; set; }

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

	/// <inheritdoc cref="AreaSettings.LuxBrightnessEnabled"/>
	/// <remarks>
	///     A nullable <c>bool</c> rather than "set the numbers to turn it on": a room must be able to override the
	///     house both ways. <c>false</c> here switches the feature off in one room while the defaults leave it on
	///     everywhere else — which a null-means-off scheme could not express without also losing the room's curve.
	/// </remarks>
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

	/// <summary>The area's display name as the <i>document alone</i> knows it: the area id, then a fixed placeholder.</summary>
	/// <remarks>
	///     <para>
	///         <see cref="YamlIgnoreAttribute"/>: computed from <see cref="Name"/> and <see cref="AreaId"/>, and
	///         get-only. Serialised it would silently promote the fallback into a real <c>Name</c> on the first save.
	///     </para>
	///     <para>
	///         <b>This is not what a surface should show.</b> It cannot consult Home Assistant, so a room proposed
	///         with only an area id reads as its slug. Anything with a registry to hand asks
	///         <see cref="Engine.AreaNaming"/>, which puts the registry's name between the two steps below and ends
	///         here. What this stays good for is a key into the document — the validator's per-area errors — where
	///         the point is to name a row rather than to be read aloud.
	///     </para>
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
