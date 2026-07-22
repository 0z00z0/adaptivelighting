using YamlDotNet.Serialization;

namespace AdaptiveLighting.Configuration;

/// <summary>
///     Root of the adaptive lighting configuration document. One document per NetDaemon host,
///     bound by <c>IAppConfig&lt;AdaptiveLightingConfig&gt;</c> from a YAML file whose top-level key is
///     the fully qualified name of this class.
/// </summary>
public class AdaptiveLightingConfig
{
	/// <summary>Free-form label for this document; used in log and notification text only.</summary>
	public string? ConfigName { get; set; }

	/// <summary>House-wide settings shared by every area.</summary>
	public GlobalConfig Global { get; set; } = new();

	/// <summary>Baseline for every per-area knob. A <see cref="AreaConfig"/> overrides only what differs.</summary>
	public AreaSettings Defaults { get; set; } = new();

	/// <summary>The house-wide circadian table. Ordered by <see cref="TimePeriodConfig.Start"/> at resolution time, not here.</summary>
	public List<TimePeriodConfig> Periods { get; set; } = [];

	/// <summary>The areas the engine manages. Areas are opt-in: an HA area absent from this list is never touched.</summary>
	public List<AreaConfig> Areas { get; set; } = [];

	/// <summary>
	///     The document a fresh installation starts from: valid, runnable, and naming nothing.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Deliberately <b>empty of entities</b>. A seed full of <c>REPLACE_ME</c> placeholders looks helpful and
	///         is not: every placeholder is an id Home Assistant does not know, so a brand-new installation starts
	///         with a document-level error and refuses to run. Worse, a placeholder <i>overrides</i> the discovery
	///         that would otherwise fill the same field in — an empty <see cref="GlobalConfig.Persons"/> finds every
	///         person by itself, while <c>person.REPLACE_ME</c> finds nothing and blocks the engine.
	///     </para>
	///     <para>
	///         So: no persons, no areas, no house mode, no kill switch (the built-in app switch is used). Only the
	///         circadian table is filled in, because a sensible day/night curve is the one thing that is the same in
	///         every house. Areas arrive on their own — see <see cref="Engine.AreaAutoDiscovery"/>.
	///     </para>
	/// </remarks>
	public static AdaptiveLightingConfig CreateDefault() => new()
	{
		ConfigName = "Adaptive lighting",
		Global = new GlobalConfig(),
		Defaults = new AreaSettings(),
		Periods =
		[
			new() { Name = "morning", Start = "06:30", BrightnessPct = 60, ColorTempKelvin = 3000 },
			new() { Name = "day",     Start = "09:00", BrightnessPct = 90, ColorTempKelvin = 4500 },
			new() { Name = "evening", Start = "sunset-01:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new() { Name = "night",   Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200, MaxBrightnessPct = 30 },
		],
		Areas = [],
	};
}

/// <summary>
///     Settings that apply to the whole house rather than to a single area.
/// </summary>
public class GlobalConfig
{
	/// <summary><c>person.*</c> / <c>device_tracker.*</c> ids to watch. Empty means "discover every person entity".</summary>
	public List<string> Persons { get; set; } = [];

	/// <summary>
	///     Entity gating the whole engine. <c>null</c> now means "use the built-in switch" (09 §7): the engine
	///     resolves it in memory to this app's own enable <c>input_boolean</c>. Read through
	///     <see cref="EffectiveKillSwitchEntity"/>, never directly.
	/// </summary>
	public string? KillSwitchEntity { get; set; }

	/// <summary>
	///     When <c>true</c> (the default) the kill switch is read as an <i>enabled</i> flag: state <c>off</c> kills
	///     the engine. Set <c>false</c> for an entity named as a true kill switch, where <c>on</c> kills. Forced to
	///     the enabled-flag reading while the built-in switch is defaulted in.
	/// </summary>
	public bool KillSwitchActiveWhenOff { get; set; } = true;

	/// <summary>
	///     The app's built-in enable switch, set once at application start by the engine host (09 §7). Never
	///     serialised — populated in memory only, so the document keeps saying "no opinion".
	/// </summary>
	[YamlIgnore]
	public string? DefaultKillSwitchEntity { get; set; }

	/// <summary>
	///     The kill switch actually read: <see cref="KillSwitchEntity"/> when set, else
	///     <see cref="DefaultKillSwitchEntity"/>. All readers (ModeMonitor, ModeService, validator) go through this.
	/// </summary>
	[YamlIgnore]
	public string? EffectiveKillSwitchEntity =>
		KillSwitchEntity is { Length: > 0 } ? KillSwitchEntity : DefaultKillSwitchEntity;

	/// <summary>
	///     Whether the effective kill switch is the built-in default rather than an operator's own entity:
	///     <see cref="KillSwitchEntity"/> is blank and <see cref="DefaultKillSwitchEntity"/> resolved one in.
	/// </summary>
	/// <remarks>
	///     The single truth both <c>ModeMonitor</c> and the web <c>ModeService</c> read so their kill-switch
	///     polarity agrees: while defaulted the built-in switch is always an <i>enabled</i> flag (off = muzzled),
	///     whatever <see cref="KillSwitchActiveWhenOff"/> happens to say — that flag only governs an explicit entity.
	/// </remarks>
	[YamlIgnore]
	public bool KillSwitchIsDefaulted =>
		string.IsNullOrWhiteSpace(KillSwitchEntity) && DefaultKillSwitchEntity is { Length: > 0 };

	/// <summary>
	///     The house-mode select and its option kinds. <c>null</c> when unconfigured — leaves today's documents
	///     visually untouched under OmitNull.
	/// </summary>
	public HouseModeConfig? HouseMode { get; set; }

	/// <summary>
	///     A house-wide outdoor lux sensor, used as the default darkness reading for any area that resolves no lux
	///     sensor of its own. One outdoor sensor can then drive "is it dark" across every room, instead of each area
	///     needing its own or falling back to sun elevation. An area with its own lux sensor keeps using that; an area
	///     whose <see cref="AreaSettings.Darkness"/> is <c>Sun</c> or <c>Always</c> ignores lux entirely. <c>null</c>
	///     leaves today's behaviour (per-area lux, else the sun-elevation fallback).
	/// </summary>
	public string? OutdoorLuxSensor { get; set; }

	/// <summary>
	///     Whether areas have already been discovered from the Home Assistant area registry once.
	/// </summary>
	/// <remarks>
	///     Set the first time the engine auto-populates an empty area list, and never reset. It is what stops a
	///     household that has deliberately removed every area from having them silently grow back on the next
	///     restart: discovery is a one-time convenience on a fresh install, not a standing policy.
	/// </remarks>
	public bool AreasAutoDiscovered { get; set; }

	/// <summary>HA user id of the NetDaemon token. Optional; a belt-and-braces input to override detection.</summary>
	public string? NetDaemonUserId { get; set; }

	/// <summary>How long everyone must be gone before the house is considered <c>Away</c>.</summary>
	public int AwayDebounceMinutes { get; set; } = 5;

	/// <summary>
	///     How often an area re-evaluates the world: an active area's circadian target, and — for every area,
	///     whatever its state — darkness, period and house mode, so a snapshot that has stopped being true is
	///     replaced rather than left standing. A tick that finds nothing changed publishes nothing.
	/// </summary>
	public int CircadianTickSeconds { get; set; } = 60;

	/// <summary>How long after a command a state change on the same light is still attributed to us.</summary>
	public int SelfEchoWindowSeconds { get; set; } = 8;

	/// <summary>Whether a change carrying a parent context (another automation) counts as a manual override.</summary>
	public bool TreatAutomationsAsManual { get; set; } = true;

	/// <summary>Whether circadian targets are blended across period boundaries instead of stepping.</summary>
	public bool SmoothTransitions { get; set; } = true;

	/// <summary>Width of the blend window following each period boundary.</summary>
	public int BlendMinutes { get; set; } = 30;

	/// <summary>Registry label marking an entity the engine must never touch.</summary>
	public string ExcludeLabel { get; set; } = "adaptive-exclude";

	/// <summary>Registry label marking an entity as a motion source regardless of its device class.</summary>
	public string MotionLabel { get; set; } = "adaptive-motion";

	/// <summary>The device classes motion discovery uses when <see cref="MotionDeviceClasses"/> is left empty.</summary>
	public static readonly IReadOnlyList<string> DefaultMotionDeviceClasses = ["motion", "occupancy", "presence"];

	/// <summary>
	///     Device classes that qualify a <c>binary_sensor</c> as a motion source during discovery. Empty — the
	///     default — means <see cref="DefaultMotionDeviceClasses"/>; read <see cref="EffectiveMotionDeviceClasses"/>
	///     rather than this list.
	/// </summary>
	/// <remarks>
	///     The default is empty rather than the three real values because the .NET configuration binder <i>appends</i>
	///     bound list items to a non-empty default instead of replacing it. With the real values here, a YAML list of
	///     three device classes bound to six entries — the defaults plus the household's — so the config said one
	///     thing and the engine did another, and no amount of editing the YAML could remove a default. An empty
	///     default makes the binder's append indistinguishable from a replace, and the fallback moves to the reader.
	/// </remarks>
	public List<string> MotionDeviceClasses { get; set; } = [];

	/// <summary>
	///     The device classes motion discovery actually matches on: the configured list, or
	///     <see cref="DefaultMotionDeviceClasses"/> when nothing was configured.
	/// </summary>
	/// <remarks>
	///     <see cref="YamlIgnoreAttribute"/> because this is a view over <see cref="MotionDeviceClasses"/>, not a
	///     setting. Serialised it would write the resolved fallback back into the file as if it had been chosen,
	///     turning "no opinion" into three device classes on the first save; and it has no setter, so reading it
	///     back would fail. The document must say what the household configured, never what the code inferred.
	/// </remarks>
	[YamlIgnore]
	public IReadOnlyList<string> EffectiveMotionDeviceClasses =>
		MotionDeviceClasses.Count > 0 ? MotionDeviceClasses : DefaultMotionDeviceClasses;

	/// <summary>Device class that qualifies a <c>sensor</c> as the area's lux source during discovery.</summary>
	public string IlluminanceDeviceClass { get; set; } = "illuminance";

	/// <summary>Brightness difference below which a light is considered already at target, and no command is sent.</summary>
	public double BrightnessTolerancePct { get; set; } = 2;

	/// <summary>Colour temperature difference below which a light is considered already at target.</summary>
	public int ColorTempToleranceKelvin { get; set; } = 50;
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

	/// <summary>Fraction of the circadian brightness used for the pre-off warning level.</summary>
	public double PreOffBrightnessFactor { get; set; } = 0.5;

	/// <summary>How long a manual change holds the area before automatic control resumes.</summary>
	public int OverrideDurationMinutes { get; set; } = 120;

	/// <summary>Motion-free time after a manual turn-off before the suppression is lifted.</summary>
	public int VacancyResetMinutes { get; set; } = 10;

	/// <summary>Which signal decides whether the area is dark enough to light.</summary>
	public DarknessSource Darkness { get; set; } = DarknessSource.Either;

	/// <summary>Lux at or below which the area counts as dark.</summary>
	public double LuxThreshold { get; set; } = 40;

	/// <summary>Extra lux required to leave the dark state, so a sensor sitting on the threshold cannot flap.</summary>
	public double LuxHysteresis { get; set; } = 10;

	/// <summary>Sun elevation in degrees below which the area counts as dark.</summary>
	public double SunElevationThreshold { get; set; } = 3.0;

	/// <summary>The sun entity supplying elevation and the sunrise/sunset boundaries for this area.</summary>
	public string SunEntity { get; set; } = "sun.sun";

	/// <summary>Fade length used while the area is not dark.</summary>
	public double DayTransitionSeconds { get; set; } = 1;

	/// <summary>Fade length used while the area is dark — gentler, because eyes are dark-adapted.</summary>
	public double NightTransitionSeconds { get; set; } = 15;

	/// <summary>Whether the area clamps to the night period's caps while sleep mode is on.</summary>
	public bool RespectSleepMode { get; set; }

	/// <summary>Whether the area refuses to auto-on at all while sleep mode is on.</summary>
	public bool SleepBlocksAutoOn { get; set; }

	/// <summary>Whether the area opts out of the leaving sweep. Outdoor and security lights set this.</summary>
	public bool SkipAwaySweep { get; set; }

	/// <summary>Whether the area lights up on first arrival when it is dark.</summary>
	public bool WelcomeHome { get; set; }

	/// <summary>Whether the engine commands this area at all. A disabled area is still observed and published.</summary>
	public bool Enabled { get; set; } = true;
}

/// <summary>Which signal an area consults to decide it is dark enough to light.</summary>
public enum DarknessSource
{
	/// <summary>Lux sensor only. Falls back to the sun when no lux sensor resolves.</summary>
	Lux,

	/// <summary>Sun elevation only.</summary>
	Sun,

	/// <summary>Dark when either the lux sensor or the sun says so.</summary>
	Either,

	/// <summary>Always dark. For rooms without daylight.</summary>
	Always
}
