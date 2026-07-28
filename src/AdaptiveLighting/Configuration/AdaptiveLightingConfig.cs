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
	///     The house's outdoor lux sensor, offered to the rooms that ask for it by name.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>It is no longer a silent fallback.</b> This used to be handed automatically to every area that
	///         resolved no lux sensor of its own, which meant a room's darkness could be decided by a sensor
	///         nobody in that room had ever chosen — and, because one shaded outdoor sensor reads hundreds of lux
	///         while the rooms behind it are dark, decided wrongly. A room now says so explicitly with
	///         <see cref="AreaConfig.FollowOutdoorLux"/>; a room that says nothing simply has no lux reading, and
	///         the lux half of its darkness gate stops holding it back (<see cref="Engine.IlluminanceGate"/>).
	///     </para>
	///     <para>
	///         Naming it here rather than repeating the id on every room is the whole point of the setting: change
	///         the house's outdoor sensor once and every room following it moves with it. A room that wants some
	///         other sensor names it under <see cref="AreaConfig.LuxSensor"/> instead, and a room's own sensor
	///         always wins over this one.
	///     </para>
	/// </remarks>
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

	/// <summary>
	///     Registry label a light must carry to be managed. Null — the default and the meaning of every
	///     pre-existing document — manages every light discovery finds. Applied to light discovery only:
	///     sensors are inputs, not things the engine commands, and filtering them too would make a
	///     half-labelled house silently deaf. The exclude label always wins over this one.
	/// </summary>
	public string? IncludeLabel { get; set; }

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

	/// <summary>
	///     How long a <b>light-level</b> sensor may go without reporting before the engine stops believing it.
	/// </summary>
	/// <remarks>
	///     <para>
	///         A room reads the average of its illuminance sensors, so one dead sensor stuck on its last value
	///         drags that average with it for ever. Two hours is the default: an illuminance sensor reports a
	///         continuously varying number, so on any ordinary day it has something new to say every few minutes,
	///         and two hours of silence from one is a fault rather than a quiet afternoon.
	///     </para>
	///     <para>
	///         <b>Illuminance only, and the narrowness is deliberate.</b> The obvious generalisation — cull any
	///         sensor that has not reported — is wrong for motion and would break the house: a motion sensor
	///         reports on change, and a battery PIR reports on nothing else, so silence from one means nobody
	///         walked through that room. Measured on one live instance (2026-07-28), 30 of 51 motion sensors had
	///         not reported in over two hours and every one of them was healthy. Motion's only test for death stays
	///         the one that cannot be wrong: no state, <c>unavailable</c> or <c>unknown</c>.
	///     </para>
	///     <para>
	///         Zero or less switches the rule off, for a house whose illuminance sensors genuinely report rarely.
	///     </para>
	/// </remarks>
	public int LuxSensorStaleAfterMinutes { get; set; } = 120;

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

	/// <summary>
	///     Lux below which the area counts as dark.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>A daylight threshold, not an indoor one — which is why it is 1000 and not 40.</b> The reading a
	///         room gates on is very often not a reading of that room at all: most houses have one outdoor lux
	///         sensor and a good many rooms with none of their own. One live instance's outdoor sensor, measured
	///         over 30 hours, sits at 1–3 lx at night, 9–47 at 04:00, 102–570 at 05:00 and 1000–3706 through the
	///         day — and it is shaded, so an unobstructed one would read 10 000–50 000. Against a threshold of 40
	///         every room in that house read "not dark" from first light until dusk while sitting genuinely dark;
	///         the owner's office reports 170 lx and is dark.
	///     </para>
	///     <para>
	///         So the number answers "is the sun still doing this room's lighting for it", and the rule behind it
	///         is the owner's: better to light up too early than never. A room whose sensor really does measure the
	///         room — a bathroom probe, a windowless hallway — is exactly the case for overriding this per room
	///         with a low number, which costs one line and is a decision somebody has made rather than a default
	///         everybody inherits.
	///     </para>
	/// </remarks>
	public double LuxThreshold { get; set; } = 1000;

	/// <summary>Extra lux required to leave the dark state, so a sensor sitting on the threshold cannot flap.</summary>
	public double LuxHysteresis { get; set; } = 10;

	/// <summary>
	///     Whether the light outside also raises this area's brightness, on top of the circadian schedule.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>Off, and off is the whole point.</b> Every house that predates this setting must behave exactly as
	///         it did, so the default is <c>false</c> and a disabled area never touches the schedule's brightness at
	///         all — not "raises it by zero", but leaves the target object untouched. See
	///         <see cref="Engine.LuxBrightnessCurve"/>.
	///     </para>
	///     <para>
	///         Deliberately independent of <see cref="Darkness"/>, which answers a different question. The darkness
	///         gate decides <i>whether</i> the engine may light the area; this decides <i>how bright</i> given how
	///         bright it is outside. A hallway that gates on the sun still looks gloomy against a bright window at
	///         noon, and that is precisely the case this exists for — so the reading is taken whatever the gate is
	///         configured to consult.
	///     </para>
	/// </remarks>
	public bool LuxBrightnessEnabled { get; set; }

	/// <summary>
	///     The illuminance at which the daylight adjustment starts. At or below it the schedule's brightness is
	///     used unchanged.
	/// </summary>
	/// <remarks>
	///     Must be positive: the curve interpolates on <c>log10</c>, which has no value at or below zero. The
	///     default of 100 lx is roughly deep twilight outdoors — dim enough that a room genuinely wants only what
	///     the schedule asked for.
	/// </remarks>
	public double LuxBrightnessStartLux { get; set; } = 100;

	/// <summary>
	///     The illuminance at which the adjustment is fully applied. At or above it the area holds
	///     <see cref="LuxBrightnessMaxPct"/>, subject to the active period's own cap.
	/// </summary>
	/// <remarks>
	///     The default of 10 000 lx is a bright overcast day outdoors, two decades above the start anchor. Direct
	///     sun is another decade beyond that; anchoring "full" at the bright-overcast point means an ordinary day
	///     reaches the top of the curve rather than sitting halfway up it.
	/// </remarks>
	public double LuxBrightnessFullLux { get; set; } = 10000;

	/// <summary>
	///     The brightness the area is raised <i>toward</i> at <see cref="LuxBrightnessFullLux"/> and beyond.
	/// </summary>
	/// <remarks>
	///     A ceiling, never a replacement: the adjustment interpolates from whatever the schedule asked for up to
	///     this value, so it can only ever add light. A period whose brightness already exceeds this is left alone
	///     rather than dimmed — dimming on a bright reading would fight the circadian intent instead of serving it.
	/// </remarks>
	public double LuxBrightnessMaxPct { get; set; } = 100;

	/// <summary>
	///     Shapes the curve between the two anchors: the normalised 0–1 position is raised to this power.
	/// </summary>
	/// <remarks>
	///     1 is a straight line in log space and is the default. Above 1 holds the level back until it is properly
	///     bright out — the adjustment then arrives late and quickly. Below 1 does the opposite, lifting the room
	///     as soon as the light outside starts climbing. It exists because "which decade matters to me" is a
	///     genuinely per-room judgement that neither anchor can express on its own.
	/// </remarks>
	public double LuxBrightnessGamma { get; set; } = 1.0;

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

/// <summary>
///     Which signal an area consults to decide it is dark enough to light.
/// </summary>
/// <remarks>
///     This chooses the <i>signals</i>; which entity supplies the lux one is a separate question, answered by
///     <see cref="AreaConfig.LuxSensor"/>, by discovery, or by <see cref="AreaConfig.FollowOutdoorLux"/>. Keeping
///     the two apart is what lets a room follow the outdoor sensor for its brightness curve while gating darkness
///     on the sun, and what stops "which sensor" needing a value of its own in here for every combination.
/// </remarks>
public enum DarknessSource
{
	/// <summary>
	///     Lux only. A room whose sensor cannot be read falls back to the sun; a room with no lux sensor at all is
	///     simply dark, because a gate with nothing to read is not a gate — see <see cref="Engine.IlluminanceGate"/>.
	/// </summary>
	Lux,

	/// <summary>Sun elevation only. No lux sensor is consulted, so a room with none is unaffected by having none.</summary>
	Sun,

	/// <summary>
	///     Dark when either the lux sensor or the sun says so. With no lux sensor the lux half says dark, so this
	///     behaves as <see cref="Always"/> until the room is given a reading to gate on.
	/// </summary>
	Either,

	/// <summary>Always dark. For rooms without daylight.</summary>
	Always
}
