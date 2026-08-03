namespace AdaptiveLighting.LastSeen;

/// <summary>
///     When each Home Assistant entity was last genuinely heard from, which Home Assistant's own fields cannot say.
/// </summary>
/// <remarks>
///     Across this interface, null means "we do not know" and never "this entity is dead". A deleted cache, a fresh
///     install and the first minutes after a Home Assistant restart all produce null; a caller reading that as dead
///     turns a missing file into a dark house.
/// </remarks>
public interface IEntityLastSeen
{
	/// <summary>
	///     The last moment there was trustworthy evidence that <paramref name="entityId"/> is alive, or <c>null</c>.
	/// </summary>
	/// <remarks>
	///     UTC, and on Home Assistant's clock, not this process's, so it is comparable with what the Home Assistant
	///     UI shows when Home Assistant has not restarted since.
	/// </remarks>
	DateTimeOffset? LastSeenUtc(string entityId);

	/// <summary>How long <paramref name="entityId"/> has been silent, or <c>null</c> when that is not known.</summary>
	TimeSpan? SilenceOf(string entityId);

	/// <summary>
	///     Whether <paramref name="entityId"/> has been silent for longer than <paramref name="threshold"/>.
	/// </summary>
	/// <remarks>False when the answer is unknown, and for a non-positive threshold. Callers rely on that.</remarks>
	bool HasBeenSilentFor(string entityId, TimeSpan threshold);

	/// <summary>
	///     When Home Assistant is believed to have last started, or <c>null</c> when no restart has been observed.
	/// </summary>
	/// <remarks>An estimate derived from the entity population, not Home Assistant's uptime sensor. Moves forwards only.</remarks>
	DateTimeOffset? HomeAssistantStartedUtc { get; }

	/// <summary>How many entities are currently being tracked. Zero before the first census.</summary>
	int TrackedCount { get; }
}
