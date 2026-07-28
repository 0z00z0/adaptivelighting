namespace AdaptiveLighting.LastSeen;

/// <summary>
///     Which file a tracked entity's record is written to.
/// </summary>
/// <remarks>
///     Splitting the cache is for the person who opens the folder, not for the disk. Somebody diagnosing "are my
///     light-level sensors still reporting?" should be able to open one small file and see only those, rather than
///     reading past 250 motion entries to find four. The split therefore follows the distinction the engine itself
///     makes when it resolves a room — lights, motion, illuminance — with everything else in one bucket.
/// </remarks>
public enum LastSeenKind
{
	/// <summary>Light-level sensors: what the darkness decision reads, and the reason this module was asked for.</summary>
	Illuminance,

	/// <summary>Motion and presence sources: what the occupancy decision reads.</summary>
	Motion,

	/// <summary>Lights: what the engine commands.</summary>
	Light,

	/// <summary>
	///     Everything else, and — importantly — everything whose kind could not be determined.
	/// </summary>
	/// <remarks>
	///     A required fallback, not a leftovers pile. Device classes are absent or surprising often enough on real
	///     hardware that a design which dropped what it could not classify would quietly stop tracking exactly the
	///     entities most likely to misbehave. An unclassifiable entity is filed here and tracked in full.
	/// </remarks>
	Other
}

/// <summary>
///     Decides which file an entity belongs in, and translates that decision to and from the file name.
/// </summary>
/// <remarks>
///     Deliberately self-contained rather than calling the engine's resolver. The resolver answers a different
///     question — "what does <i>this room</i> resolve to", with area membership, include and exclude labels, light
///     groups and liveness all having a say — and it needs the area registry to do it. Filing needs none of that:
///     it is one entity, one bucket, no configuration. Sharing the code would couple a disposable cache's layout to
///     the engine's room-resolution rules, so the rules are mirrored instead, which is what the comments on
///     <see cref="LastSeenOptions.MotionDeviceClasses"/> and <see cref="LastSeenOptions.MotionLabel"/> record.
/// </remarks>
public static class LastSeenKinds
{
	private const string LightDomain = "light";
	private const string BinarySensorDomain = "binary_sensor";
	private const string SensorDomain = "sensor";

	/// <summary>Every bucket, in the order the files are read and written.</summary>
	public static readonly IReadOnlyList<LastSeenKind> All =
		[LastSeenKind.Illuminance, LastSeenKind.Motion, LastSeenKind.Light, LastSeenKind.Other];

	/// <summary>
	///     Works out which bucket <paramref name="entityId"/> belongs in.
	/// </summary>
	/// <remarks>
	///     Never fails and never returns "unknown": an entity whose kind cannot be determined is
	///     <see cref="LastSeenKind.Other"/>, which is tracked exactly as thoroughly as any other bucket. The label
	///     is checked before the device class for motion, because the label exists precisely for the hardware whose
	///     device class is wrong.
	/// </remarks>
	/// <param name="entityId">The entity id. Its domain is half the answer.</param>
	/// <param name="deviceClass">The entity's <c>device_class</c> attribute, or <c>null</c> when it has none.</param>
	/// <param name="labels">The labels the entity registration carries, if any.</param>
	/// <param name="options">Supplies the device-class and label conventions.</param>
	/// <returns>The bucket. Never throws.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
	public static LastSeenKind Classify(
		string? entityId,
		string? deviceClass,
		IEnumerable<string>? labels,
		LastSeenOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		if (entityId is not { Length: > 0 })
			return LastSeenKind.Other;

		if (entityId.HasDomain(LightDomain))
			return LastSeenKind.Light;

		if (labels is not null
			&& options.MotionLabel is { Length: > 0 } motionLabel
			&& labels.Contains(motionLabel, StringComparer.OrdinalIgnoreCase))
			return LastSeenKind.Motion;

		if (entityId.HasDomain(BinarySensorDomain)
			&& deviceClass is { Length: > 0 }
			&& options.MotionDeviceClasses.Contains(deviceClass, StringComparer.OrdinalIgnoreCase))
			return LastSeenKind.Motion;

		if (entityId.HasDomain(SensorDomain)
			&& string.Equals(deviceClass, options.IlluminanceDeviceClass, StringComparison.OrdinalIgnoreCase))
			return LastSeenKind.Illuminance;

		return LastSeenKind.Other;
	}

	/// <summary>The word that goes in the file name, and in the document's own <c>kind</c> field.</summary>
	public static string Token(this LastSeenKind kind) => kind switch
	{
		LastSeenKind.Illuminance => "illuminance",
		LastSeenKind.Motion => "motion",
		LastSeenKind.Light => "light",
		_ => "other"
	};

	/// <summary>
	///     Reads a bucket back from its token, falling back to <see cref="LastSeenKind.Other"/>.
	/// </summary>
	/// <remarks>
	///     Tolerant on purpose. The token in a file is a hint about where a record was last filed, not the truth
	///     about what the entity is — the truth is re-derived from Home Assistant on the next census — so an
	///     unrecognised token from an older or newer build must not cost the history in the file.
	/// </remarks>
	/// <param name="token">The token from a file name or document.</param>
	/// <returns>The bucket the token names, or <see cref="LastSeenKind.Other"/>.</returns>
	public static LastSeenKind FromToken(string? token) => token switch
	{
		"illuminance" => LastSeenKind.Illuminance,
		"motion" => LastSeenKind.Motion,
		"light" => LastSeenKind.Light,
		_ => LastSeenKind.Other
	};
}
