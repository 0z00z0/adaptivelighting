namespace AdaptiveLighting.LastSeen;

/// <summary>
///     The knobs behind <see cref="LastSeenTracker"/>. Every default is chosen here rather than in the
///     configuration document, because none of them is a household preference: they are the tuning of a
///     measurement, and a house that needs to change one has a diagnosis to report, not a taste to express.
/// </summary>
public sealed class LastSeenOptions
{
	/// <summary>
	///     How often the whole entity population is sampled.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The module is a sampler, not a subscriber, and that is deliberate. Home Assistant retains its own
	///         <c>last_updated</c> until it restarts, so a census a minute loses nothing that a state-change
	///         subscription would have caught — every report that happened between two censuses is still visible
	///         in the timestamp when the second one runs.
	///     </para>
	///     <para>
	///         Subscribing instead would be actively worse. After a restart, Home Assistant's restore burst arrives
	///         <i>before</i> anything could have noticed the restart, so an event-driven design would advance every
	///         record and only afterwards work out that it should not have — which then needs a rollback mechanism
	///         to undo. Sampling has no such window: each census works out whether Home Assistant restarted and only
	///         then decides what to believe.
	///     </para>
	/// </remarks>
	public TimeSpan CensusInterval { get; init; } = TimeSpan.FromSeconds(60);

	/// <summary>
	///     How often changed records are written to disk.
	/// </summary>
	/// <remarks>
	///     A write per state change across ~300 entities would hammer the card Home Assistant boots from for no
	///     benefit. Writes are therefore coalesced: one timer, and only the files that actually changed. What a
	///     hard kill costs is bounded by this interval and is measured in minutes of freshness on entities that
	///     reported in that window — never a lost history, because the file that survives is a complete one.
	///     A graceful shutdown flushes on the way out and loses nothing.
	/// </remarks>
	public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMinutes(5);

	/// <summary>
	///     How long after Home Assistant starts a timestamp is refused as restore evidence.
	/// </summary>
	/// <remarks>
	///     <para>
	///         This is the module's whole defence, so it is worth stating plainly: for this long after a restart,
	///         <b>nothing advances</b>. Home Assistant restores its entities over the first minutes of start-up, and
	///         a restored timestamp is indistinguishable from a real report except by when it lands.
	///     </para>
	///     <para>
	///         The two errors are not symmetric. Refusing a genuine report costs a few minutes of apparent staleness
	///         on a sensor that will report again shortly. Accepting a restore costs the entire point of the module:
	///         a sensor dead for a week is declared fresh. So this errs long, and raising it is cheap.
	///     </para>
	/// </remarks>
	public TimeSpan StartupGrace { get; init; } = TimeSpan.FromMinutes(5);

	/// <summary>
	///     How tightly the population's timestamps must cluster before it counts as a restart restore.
	/// </summary>
	/// <remarks>
	///     A house that has been running has timestamps spread over hours or days — a door nobody opened since
	///     Tuesday sits well behind a power meter reporting every ten seconds. Immediately after a restart the entire
	///     spread collapses, because nothing can carry a timestamp older than the restart. A whole house fitting
	///     inside one window is therefore the restart's signature, and it is the one signal that needs no cooperation
	///     from Home Assistant.
	/// </remarks>
	public TimeSpan CollapseWindow { get; init; } = TimeSpan.FromMinutes(10);

	/// <summary>
	///     The smallest population the collapse test is allowed to draw a conclusion from.
	/// </summary>
	/// <remarks>
	///     The test is statistical, and a handful of chatty sensors would satisfy it permanently — they all report
	///     inside the window every minute of their lives. Below this count the collapse test simply abstains, and
	///     the module falls back to Home Assistant's own <c>homeassistant_start</c> event. A real house is in the
	///     hundreds, so this never binds in practice; it exists so the degenerate case degrades to "no restart
	///     protection" instead of to "everything is a restart".
	/// </remarks>
	public int MinimumPopulation { get; init; } = 10;

	/// <summary>
	///     How long a record outlives the entity it belongs to.
	/// </summary>
	/// <remarks>
	///     Applies only to entities Home Assistant has stopped reporting at all: a device that is merely quiet is
	///     never dropped, because its silence is the measurement. A removed entity stops advancing and so ages out
	///     on its own, which is why nothing has to record when it vanished. Long enough that a device switched off
	///     for a holiday comes back to its own history; short enough that a house re-plumbed a year ago is not still
	///     carrying entity ids nobody remembers naming.
	/// </remarks>
	public TimeSpan Retention { get; init; } = TimeSpan.FromDays(30);

	/// <summary>
	///     The ceiling on tracked entities, enforced by dropping the oldest <i>absent</i> ones early.
	/// </summary>
	/// <remarks>
	///     Catches the case retention is too slow for: an instance being rebuilt, minting new entity ids faster than
	///     the old ones age out. Entities Home Assistant still reports are never dropped to satisfy this — they would
	///     simply be re-added by the next census, and the resulting churn would rewrite every file every minute — so
	///     a house genuinely larger than the ceiling is tracked in full and the ceiling does nothing.
	/// </remarks>
	public int MaxTracked { get; init; } = 5000;

	/// <summary>
	///     The label that marks an entity as a motion source for filing purposes, mirroring the engine's own
	///     escape hatch.
	/// </summary>
	/// <remarks>
	///     mmWave and other presence hardware routinely reports a device class nobody predicted, which is why the
	///     engine lets a household say "this one counts" with a label. Filing follows the same rule so that the
	///     motion file holds what the household considers motion, not what the manufacturer happened to declare.
	/// </remarks>
	public string MotionLabel { get; init; } = "adaptive-motion";

	/// <summary>The binary-sensor device classes filed as motion. Mirrors the engine's discovery defaults.</summary>
	public IReadOnlyList<string> MotionDeviceClasses { get; init; } = ["motion", "occupancy", "presence"];

	/// <summary>The sensor device class filed as illuminance. Mirrors the engine's discovery default.</summary>
	public string IlluminanceDeviceClass { get; init; } = "illuminance";
}
