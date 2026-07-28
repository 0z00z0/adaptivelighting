namespace AdaptiveLighting.LastSeen;

/// <summary>
///     When each Home Assistant entity was last genuinely heard from — the question Home Assistant's own fields
///     cannot answer.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists at all.</b> <c>last_updated</c> and <c>last_changed</c> are Home Assistant's own
///         state-machine bookkeeping, and Home Assistant resets them on every restart: each entity is restored and
///         re-announced, so every timestamp in the house collapses to the same instant. Measured on the live house
///         on 2026-07-28: of 51 motion sensors, the <i>oldest</i> timestamp of any of them was 2.30 hours, because
///         Home Assistant had restarted 2.3 hours earlier. A sensor that had been dead for a week was indistinguishable
///         from one that reported five minutes before the restart. Anything that wants to ignore sensors which have
///         stopped reporting therefore cannot ask Home Assistant; it has to ask something that remembers.
///     </para>
///     <para>
///         <b>The contract every caller must honour.</b> <c>null</c> means <i>we do not know</i>. It never means
///         "this entity is dead". A deleted cache, a fresh installation, and the first minutes after a Home
///         Assistant restart all legitimately produce <c>null</c>, and a caller that reads <c>null</c> as "dead"
///         turns a missing file into a dark house. <see cref="HasBeenSilentFor"/> exists so that the safe reading
///         is also the easy one: it answers <c>false</c> when the answer is unknown.
///     </para>
///     <para>
///         <b>What counts as being heard from.</b> Any movement of Home Assistant's own timestamp for the entity —
///         a new value, a changed attribute, a forced update — that cannot be attributed to a restart restore. It is
///         deliberately <i>not</i> "the value changed": a light-level sensor sitting at a constant 3 lx all night is
///         healthy and quiet, and counting only value changes would rediscover this very bug one level down.
///     </para>
/// </remarks>
public interface IEntityLastSeen
{
	/// <summary>
	///     The last moment there was trustworthy evidence that <paramref name="entityId"/> is alive, or <c>null</c>
	///     when there has never been any.
	/// </summary>
	/// <remarks>
	///     In UTC, and expressed on Home Assistant's clock rather than this process's: the value is the Home
	///     Assistant timestamp that was accepted as evidence, so it is directly comparable with what the Home
	///     Assistant UI shows for an entity when Home Assistant has not restarted since.
	/// </remarks>
	/// <param name="entityId">The entity to ask about. An id nothing has ever tracked answers <c>null</c>.</param>
	/// <returns>The moment, or <c>null</c> for "we do not know".</returns>
	DateTimeOffset? LastSeenUtc(string entityId);

	/// <summary>
	///     How long <paramref name="entityId"/> has been silent, or <c>null</c> when that is not known.
	/// </summary>
	/// <param name="entityId">The entity to ask about.</param>
	/// <returns>The silence, or <c>null</c> for "we do not know".</returns>
	TimeSpan? SilenceOf(string entityId);

	/// <summary>
	///     Whether <paramref name="entityId"/> has been silent for longer than <paramref name="threshold"/>.
	/// </summary>
	/// <remarks>
	///     <b>Answers <c>false</c> when the answer is unknown</b>, which is the whole point of having this rather
	///     than making every caller write the null check. An unknown entity is one this module has no history for;
	///     treating that as "stale" would mean a deleted cache file silently disqualified every sensor in the house.
	///     Not knowing is a reason to carry on as before, never a reason to act.
	/// </remarks>
	/// <param name="entityId">The entity to ask about.</param>
	/// <param name="threshold">How much silence is too much. A non-positive threshold never matches.</param>
	/// <returns><c>true</c> only when there is a known last-seen time and it is older than <paramref name="threshold"/>.</returns>
	bool HasBeenSilentFor(string entityId, TimeSpan threshold);

	/// <summary>
	///     When Home Assistant is believed to have last started, or <c>null</c> when no restart has been observed.
	/// </summary>
	/// <remarks>
	///     Exposed for diagnostics, because "why is everything unknown?" is nearly always answered by "Home
	///     Assistant restarted a few minutes ago". Not a substitute for Home Assistant's own uptime sensor: this is
	///     an estimate derived from the entity population, and it moves only forwards.
	/// </remarks>
	DateTimeOffset? HomeAssistantStartedUtc { get; }

	/// <summary>How many entities are currently being tracked. Zero before the first census.</summary>
	int TrackedCount { get; }
}
