namespace AdaptiveLighting.LastSeen;

/// <summary>The knobs behind <see cref="LastSeenTracker"/>. None of these is exposed in the configuration document.</summary>
public sealed class LastSeenOptions
{
	/// <summary>How often the whole entity population is sampled.</summary>
	public TimeSpan CensusInterval { get; init; } = TimeSpan.FromSeconds(60);

	/// <summary>How often changed records are written to disk. A hard kill costs this much freshness, never history.</summary>
	public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMinutes(5);

	/// <summary>
	///     How long after Home Assistant starts a timestamp is refused as restore evidence.
	/// </summary>
	/// <remarks>
	///     For this long after a restart, nothing advances. The two errors are not symmetric: refusing a real report
	///     costs minutes of apparent staleness, accepting a restore declares a week-dead sensor fresh. Err long.
	/// </remarks>
	public TimeSpan StartupGrace { get; init; } = TimeSpan.FromMinutes(5);

	/// <summary>
	///     How tightly the population's timestamps must cluster before it counts as a restart restore.
	/// </summary>
	/// <remarks>
	///     A running house spreads timestamps over hours or days. After a restart the whole spread collapses, because
	///     nothing can carry a timestamp older than the restart.
	/// </remarks>
	public TimeSpan CollapseWindow { get; init; } = TimeSpan.FromMinutes(10);

	/// <summary>
	///     The smallest population the collapse test is allowed to draw a conclusion from.
	/// </summary>
	/// <remarks>
	///     Below this the test abstains and only the homeassistant_start event detects a restart. Without it a handful
	///     of chatty sensors would satisfy the collapse test permanently.
	/// </remarks>
	public int MinimumPopulation { get; init; } = 10;

	/// <summary>
	///     How long a record outlives the entity it belongs to.
	/// </summary>
	/// <remarks>Applies only to entities Home Assistant has stopped reporting; a quiet device is never dropped.</remarks>
	public TimeSpan Retention { get; init; } = TimeSpan.FromDays(30);

	/// <summary>
	///     The ceiling on tracked entities, enforced by dropping the oldest absent ones early.
	/// </summary>
	/// <remarks>
	///     Only absent entities count against it. Dropping a present one would have the next census re-add it and
	///     rewrite every file every minute, so a house larger than the ceiling is tracked in full.
	/// </remarks>
	public int MaxTracked { get; init; } = 5000;

	/// <summary>The label that files an entity as motion, mirroring the engine's escape hatch for odd presence hardware.</summary>
	public string MotionLabel { get; init; } = "adaptive-motion";

	/// <summary>The binary-sensor device classes filed as motion. Mirrors the engine's discovery defaults.</summary>
	public IReadOnlyList<string> MotionDeviceClasses { get; init; } = ["motion", "occupancy", "presence"];

	/// <summary>The sensor device class filed as illuminance. Mirrors the engine's discovery default.</summary>
	public string IlluminanceDeviceClass { get; init; } = "illuminance";
}
