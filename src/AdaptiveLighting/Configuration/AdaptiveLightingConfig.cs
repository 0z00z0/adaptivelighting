using YamlDotNet.Serialization;

namespace AdaptiveLighting.Configuration;

/// <summary>
///     Root of the adaptive lighting configuration document. One document per NetDaemon host, bound from a YAML
///     file whose top-level key is the fully qualified name of this class.
/// </summary>
public class AdaptiveLightingConfig
{
	public string? ConfigName { get; set; }

	public GlobalConfig Global { get; set; } = new();

	/// <summary>Baseline for every per-area knob. A <see cref="AreaConfig"/> overrides only what differs.</summary>
	public AreaSettings Defaults { get; set; } = new();

	/// <summary>The house-wide circadian table. Ordered by <see cref="TimePeriodConfig.Start"/> at resolution time, not here.</summary>
	public List<TimePeriodConfig> Periods { get; set; } = [];

	/// <summary>Areas are opt-in: an HA area absent from this list is never touched.</summary>
	public List<AreaConfig> Areas { get; set; } = [];

	/// <summary>The document a fresh installation starts from.</summary>
	/// <remarks>
	///     Names no entities. A placeholder id is one Home Assistant does not know, so it fails validation on a fresh
	///     install and blocks the discovery that would otherwise have filled the same field in.
	///     The period ids are minted here, so a seed document is already in the shape the migration produces and a
	///     fresh install never takes the migrating write.
	/// </remarks>
	public static AdaptiveLightingConfig CreateDefault()
	{
		AdaptiveLightingConfig config = new()
		{
			ConfigName = "Adaptive lighting",
			Global = new GlobalConfig(),
			Defaults = new AreaSettings(),
			Periods =
			[
				new() { Name = "morning", Start = "06:30", BrightnessPct = 60, ColorTempKelvin = 3000 },
				new() { Name = "day",     Start = "09:00", BrightnessPct = 90, ColorTempKelvin = 4500 },
				new() { Name = "evening", Start = "sunset-01:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
				new() { Name = "night",   Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200 },
			],
			Areas = [],
		};

		StableKeyMigration.Apply(config);

		return config;
	}
}

/// <summary>Settings that apply to the whole house, not to a single area.</summary>
public class GlobalConfig
{
	/// <summary><c>person.*</c> / <c>device_tracker.*</c> ids to watch. Empty means "discover every person entity".</summary>
	public List<string> Persons { get; set; } = [];

	/// <summary>
	///     Entity gating the whole engine. <c>null</c> means the built-in app switch. Read through
	///     <see cref="EffectiveKillSwitchEntity"/>, never directly.
	/// </summary>
	public string? KillSwitchEntity { get; set; }

	/// <summary>When <c>true</c> the switch is an enabled flag, so <c>off</c> kills the engine.</summary>
	public bool KillSwitchActiveWhenOff { get; set; } = true;

	/// <summary>The app's built-in enable switch, set at start-up by the host. In memory only.</summary>
	[YamlIgnore]
	public string? DefaultKillSwitchEntity { get; set; }

	/// <summary>The kill switch actually read. Every reader goes through this, not the two fields behind it.</summary>
	[YamlIgnore]
	public string? EffectiveKillSwitchEntity =>
		KillSwitchEntity is { Length: > 0 } ? KillSwitchEntity : DefaultKillSwitchEntity;

	/// <summary>Whether the effective kill switch is the built-in default, not an operator's own entity.</summary>
	/// <remarks>
	///     While defaulted the switch is always an enabled flag, whatever <see cref="KillSwitchActiveWhenOff"/> says;
	///     that flag governs an explicit entity only. ModeMonitor and ModeService both read this so their polarity
	///     agrees.
	/// </remarks>
	[YamlIgnore]
	public bool KillSwitchIsDefaulted =>
		string.IsNullOrWhiteSpace(KillSwitchEntity) && DefaultKillSwitchEntity is { Length: > 0 };

	/// <summary>The house-mode select and its option kinds. <c>null</c> when unconfigured.</summary>
	public HouseModeConfig? HouseMode { get; set; }

	/// <summary>The <c>input_select</c> tied to the period table, and which side of it decides.</summary>
	/// <remarks>
	///     <c>null</c> when unconfigured, and <see cref="ConfigNormalizer"/> puts an empty one back to <c>null</c>:
	///     under OmitNull that keeps a document that never adopted the feature byte-identical through a save.
	/// </remarks>
	public PeriodSelectConfig? PeriodSelect { get; set; }

	/// <summary>The house's outdoor lux sensor, offered to the rooms that ask for it by name.</summary>
	/// <remarks>
	///     Not a fallback. A room reads it only when it sets <see cref="AreaConfig.FollowOutdoorLux"/>; a room that
	///     says nothing has no lux reading at all.
	/// </remarks>
	public string? OutdoorLuxSensor { get; set; }

	/// <summary>Whether areas have been discovered from the HA area registry once.</summary>
	/// <remarks>Set on the first auto-populate and never reset, so an emptied list stays empty.</remarks>
	public bool AreasAutoDiscovered { get; set; }

	public string? NetDaemonUserId { get; set; }

	public int AwayDebounceMinutes { get; set; } = 5;

	/// <summary>
	///     How often an area re-evaluates darkness, period and house mode, whatever state it is in. A tick that
	///     finds nothing changed publishes nothing.
	/// </summary>
	public int CircadianTickSeconds { get; set; } = 60;

	/// <summary>How long after a command a state change on the same light is still attributed to us.</summary>
	public int SelfEchoWindowSeconds { get; set; } = 8;

	/// <summary>Whether a change carrying a parent context (another automation) counts as a manual override.</summary>
	public bool TreatAutomationsAsManual { get; set; } = true;

	public bool SmoothTransitions { get; set; } = true;

	public int BlendMinutes { get; set; } = 30;

	/// <summary>Registry label marking an entity the engine must never touch.</summary>
	public string ExcludeLabel { get; set; } = "adaptive-exclude";

	/// <summary>
	///     Registry label a light must carry to be managed. <c>null</c> manages every light discovery finds.
	///     Applied to light discovery only, never to sensors. The exclude label always wins over this one.
	/// </summary>
	public string? IncludeLabel { get; set; }

	/// <summary>Registry label marking an entity as a motion source regardless of its device class.</summary>
	public string MotionLabel { get; set; } = "adaptive-motion";

	public static readonly IReadOnlyList<string> DefaultMotionDeviceClasses = ["motion", "occupancy", "presence"];

	/// <summary>
	///     Device classes that qualify a <c>binary_sensor</c> as a motion source. Read
	///     <see cref="EffectiveMotionDeviceClasses"/>, not this list.
	/// </summary>
	/// <remarks>
	///     The default stays empty because the .NET configuration binder appends bound list items to a non-empty
	///     default instead of replacing it, so real values here would leave a configured list of three binding to six.
	/// </remarks>
	public List<string> MotionDeviceClasses { get; set; } = [];

	/// <summary>The configured list, or <see cref="DefaultMotionDeviceClasses"/> when nothing was configured.</summary>
	/// <remarks>A view, not a setting: serialised it would write the fallback back into the file as a choice.</remarks>
	[YamlIgnore]
	public IReadOnlyList<string> EffectiveMotionDeviceClasses =>
		MotionDeviceClasses.Count > 0 ? MotionDeviceClasses : DefaultMotionDeviceClasses;

	public string IlluminanceDeviceClass { get; set; } = "illuminance";

	/// <summary>How long a light-level sensor may go without reporting before the engine stops believing it.</summary>
	/// <remarks>
	///     Illuminance only. A motion sensor reports on change, so silence from one is not a fault and this rule must
	///     never be generalised to it. Zero or less switches the rule off.
	/// </remarks>
	public int LuxSensorStaleAfterMinutes { get; set; } = 120;

	/// <summary>Brightness difference below which a light counts as already at target, so no command is sent.</summary>
	/// <remarks>HA reports brightness as a 0-255 integer and this app thinks in per cent, so a round trip lands about a per cent off.</remarks>
	public const double BrightnessTolerancePct = 2;

	public const int ColorTempToleranceKelvin = 50;
}

/// <summary>
///     Every knob an area can carry. <see cref="AdaptiveLightingConfig.Defaults"/> supplies the baseline;
///     <see cref="AreaConfig"/> holds a nullable twin of each property and merges via <see cref="AreaConfig.Effective"/>.
/// </summary>
public class AreaSettings
{
	/// <summary>Motion-free time after which an active area dims to its pre-off warning level.</summary>
	public int VacancyTimeoutSeconds { get; set; } = 600;

	/// <summary>Length of the pre-off warning: the grace in which motion still cancels darkness.</summary>
	public int PreOffSeconds { get; set; } = 30;

	public double PreOffBrightnessFactor { get; set; } = 0.5;

	public int OverrideDurationMinutes { get; set; } = 120;

	/// <summary>Motion-free time after a manual turn-off before the suppression is lifted.</summary>
	public int VacancyResetMinutes { get; set; } = 10;

	/// <summary>Which signal decides whether the area is dark enough to light.</summary>
	/// <remarks>Under <see cref="DarknessSource.Lux"/> a room with no sensor, or none still reporting, counts as dark.</remarks>
	public DarknessSource Darkness { get; set; } = DarknessSource.Lux;

	/// <summary>How the room's warmth is commanded, or whether it can be at all.</summary>
	/// <remarks>
	///     <see cref="ColorControl.Auto"/> reads the fixtures: a room whose resolved lights all lack
	///     <c>color_temp</c> is driven as <see cref="ColorControl.EqualChannels"/> without anybody configuring
	///     it. The other two members are a person overruling that, which is needed both ways because Home
	///     Assistant sometimes advertises a capability a fixture does not really have.
	/// </remarks>
	public ColorControl ColorControl { get; set; } = ColorControl.Auto;

	/// <summary>Lux below which the area counts as dark.</summary>
	/// <remarks>
	///     A daylight threshold, not an indoor one, because the reading is very often an outdoor sensor a room merely
	///     follows. A room whose sensor genuinely measures the room wants a much lower number set per room.
	/// </remarks>
	public double LuxThreshold { get; set; } = 1000;

	/// <summary>Extra lux required to leave the dark state, so a sensor sitting on the threshold cannot flap.</summary>
	public double LuxHysteresis { get; set; } = 10;

	/// <summary>Whether the light outside also raises this area's brightness, on top of the circadian schedule.</summary>
	/// <remarks>Off leaves the schedule's target object untouched, not raised by zero.</remarks>
	public bool LuxBrightnessEnabled { get; set; }

	/// <summary>
	///     The illuminance at which the daylight adjustment starts. Must be positive: the curve interpolates on
	///     <c>log10</c>.
	/// </summary>
	public double LuxBrightnessStartLux { get; set; } = 100;

	public double LuxBrightnessFullLux { get; set; } = 10000;

	/// <summary>The brightness the area is raised toward at <see cref="LuxBrightnessFullLux"/> and beyond.</summary>
	/// <remarks>A ceiling, never a replacement: the curve can only add light, and a period already above it is left alone.</remarks>
	public double LuxBrightnessMaxPct { get; set; } = 100;

	public double LuxBrightnessGamma { get; set; } = 1.0;

	public double SunElevationThreshold { get; set; } = 3.0;

	public string SunEntity { get; set; } = "sun.sun";

	public double DayTransitionSeconds { get; set; } = 1;

	public double NightTransitionSeconds { get; set; } = 15;

	public bool RespectSleepMode { get; set; }

	public bool SleepBlocksAutoOn { get; set; }

	/// <summary>Whether the area opts out of the leaving sweep. Outdoor and security lights set this.</summary>
	public bool SkipAwaySweep { get; set; }

	/// <summary>Whether the area lights up on first arrival when it is dark.</summary>
	public bool WelcomeHome { get; set; }

	/// <summary>Whether the engine commands this area at all. A disabled area is still observed and published.</summary>
	public bool Enabled { get; set; } = true;
}

/// <summary>How a room's warmth reaches its lights.</summary>
/// <remarks>
///     Ordinals pinned, no member renamed or removed, for the reason <see cref="DarknessSource"/> gives.
/// </remarks>
public enum ColorControl
{
	/// <summary>
	///     Decide from the fixtures, and the default. All of the room's lights lacking <c>color_temp</c> reads as
	///     <see cref="EqualChannels"/>; anything else reads as <see cref="Kelvin"/>.
	/// </summary>
	Auto = 0,

	/// <summary>Command <c>color_temp_kelvin</c>. What every colour-temperature fixture takes.</summary>
	Kelvin = 1,

	/// <summary>
	///     Command every colour channel at one value, giving neutral white at the target brightness. For a room
	///     whose lights have no colour temperature: the schedule's kelvin figure cannot reach them, so the UI
	///     stops offering a number that would do nothing.
	/// </summary>
	EqualChannels = 2
}

/// <summary>Which signal an area consults to decide it is dark enough to light.</summary>
/// <remarks>
///     Ordinals are pinned and no member may be renamed or removed. Two readers bind this type: the engine's own
///     deserializer, which has a legacy pre-pass, and NetDaemon's binder on the app YAML, which cannot have one. An
///     unknown key is silence; an unknown enum value is a <see cref="FormatException"/> at start-up. Which entity
///     supplies the lux reading is a separate question, answered by <see cref="AreaConfig.LuxSensor"/> or
///     <see cref="AreaConfig.FollowOutdoorLux"/>.
/// </remarks>
public enum DarknessSource
{
	/// <summary>Lux only, and the default. A room with no sensor, or with none still answering, is simply dark.</summary>
	Lux = 0,

	/// <summary>Sun elevation only. No lux sensor is consulted.</summary>
	Sun = 1,

	/// <summary>Always dark. For rooms without daylight.</summary>
	/// <remarks>
	///     3, not 2: retiring <see cref="Either"/> left this declared last, which would silently renumber it. Enum
	///     members are inlined into consuming assemblies, and <c>Enum.Parse</c> accepts the bare numeral, so
	///     <c>Darkness: 3</c> in a hand-written file has to keep meaning what it meant.
	/// </remarks>
	Always = 3,

	/// <summary>Retired. Behaves as <see cref="Lux"/>, and stays only so a document that names it still parses.</summary>
	/// <remarks>
	///     Deleting it took a live house down with <c>FormatException: Either is not a valid value</c>. The editor does
	///     not offer it and <see cref="ConfigNormalizer"/> rewrites it on the next save, but there is no way to prove
	///     no file still says the word. Keeps value 2 while declared last so the live members' ordinals are untouched.
	/// </remarks>
	Either = 2
}
