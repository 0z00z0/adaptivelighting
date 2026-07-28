using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdaptiveLighting.LastSeen;

/// <summary>
///     One entity's line in the cache.
/// </summary>
/// <param name="LastSeen">
///     When there was last trustworthy evidence the entity is alive, or <c>null</c> for "never had any". A record
///     with no last-seen time is still worth keeping: it says the module has been watching this entity since
///     <paramref name="TrackedSince"/> and has heard nothing it could believe, which is a different statement from
///     never having met it.
/// </param>
/// <param name="TrackedSince">
///     When this module first met the entity. Not evidence of anything — it is what ageing and eviction fall back
///     to for a record that has no last-seen time, so that an entity which never reports cannot linger for ever.
/// </param>
public sealed record LastSeenEntry(
	[property: JsonPropertyName("lastSeen")] DateTimeOffset? LastSeen,
	[property: JsonPropertyName("trackedSince")] DateTimeOffset TrackedSince);

/// <summary>
///     One cache file: the records for a single <see cref="LastSeenKind"/>, plus enough context for a person who
///     opens it to know what they are looking at.
/// </summary>
/// <remarks>
///     JSON rather than the engine's YAML, and deliberately: this is machine-written, high-churn and disposable,
///     the opposite of the configuration document in every respect that matters. It sits in the same directory
///     because that directory is the one thing on a Home Assistant box that survives a redeploy — the deploy folder
///     is wiped and re-copied every time, so a cache kept there would be destroyed on exactly the schedule it exists
///     to survive.
/// </remarks>
public sealed class LastSeenDocument
{
	/// <summary>What this file is, written into the file, for whoever opens it looking for their settings.</summary>
	public const string Explanation =
		"Machine-written cache: when Adaptive Lighting last had trustworthy evidence that each Home Assistant entity "
		+ "is alive. Home Assistant resets its own last_updated on every restart, so this is kept separately. "
		+ "Not configuration - nothing here is edited by hand, and deleting this file is safe: the only cost is the "
		+ "history, and every entity in it reverts to 'unknown' until it reports again.";

	/// <summary>The current document version. Bumped only when an older file could be misread.</summary>
	public const int CurrentVersion = 1;

	/// <summary>The explanation above, first in the file so it is the first thing a reader sees.</summary>
	[JsonPropertyName("_comment")]
	[JsonPropertyOrder(-3)]
	public string Comment { get; set; } = Explanation;

	/// <summary>The document version this file was written in.</summary>
	[JsonPropertyName("version")]
	[JsonPropertyOrder(-2)]
	public int Version { get; set; } = CurrentVersion;

	/// <summary>Which bucket this file holds, as a word. See <see cref="LastSeenKinds.FromToken"/> for why it is a hint.</summary>
	[JsonPropertyName("kind")]
	[JsonPropertyOrder(-1)]
	public string Kind { get; set; } = LastSeenKind.Other.Token();

	/// <summary>When this file was written. Also the tie-breaker when an entity turns up in two files.</summary>
	[JsonPropertyName("savedAt")]
	public DateTimeOffset SavedAt { get; set; }

	/// <summary>
	///     The module's estimate of when Home Assistant last started, repeated in every file.
	/// </summary>
	/// <remarks>
	///     Repeated rather than kept in a file of its own, so that no bucket depends on another bucket existing. It
	///     only ever moves forwards, which makes merging trivial: the loader takes the newest value it finds across
	///     all the files and is right regardless of which of them was written most recently.
	/// </remarks>
	[JsonPropertyName("homeAssistantStarted")]
	public DateTimeOffset? HomeAssistantStarted { get; set; }

	/// <summary>The records, keyed by entity id and sorted, so a diff between two versions is readable.</summary>
	[JsonPropertyName("entities")]
	public SortedDictionary<string, LastSeenEntry> Entities { get; set; } = new(StringComparer.Ordinal);

	/// <summary>How the cache files are written and read. Indented because a person is expected to open them.</summary>
	public static readonly JsonSerializerOptions SerializerOptions = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		// A hand-edited file, or one from a build that knew a field this one does not, must not cost the history.
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};
}
