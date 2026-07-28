using System.Text;
using System.Text.Json;

namespace AdaptiveLighting.LastSeen;

/// <summary>
///     One entity as it came off disk, with the bucket it was filed in and the file's own write time.
/// </summary>
/// <param name="EntityId">The entity.</param>
/// <param name="Kind">Where it was filed last time. Re-derived from Home Assistant on the next census.</param>
/// <param name="Entry">Its record.</param>
/// <param name="SavedAt">When the file holding it was written, which is what settles a duplicate.</param>
public sealed record LoadedEntity(string EntityId, LastSeenKind Kind, LastSeenEntry Entry, DateTimeOffset SavedAt);

/// <summary>
///     Everything a load produced, including what it could not read.
/// </summary>
/// <param name="Entities">The merged records, one per entity id.</param>
/// <param name="HomeAssistantStarted">The newest restart estimate found in any file, or <c>null</c>.</param>
/// <param name="FilesRead">How many cache files were read successfully.</param>
/// <param name="FilesUnreadable">How many existed but could not be read or parsed. Their entities are simply unknown.</param>
/// <param name="DuplicatesResolved">How many entities were found in more than one file. Normally zero.</param>
public sealed record LastSeenCacheLoad(
	IReadOnlyDictionary<string, LoadedEntity> Entities,
	DateTimeOffset? HomeAssistantStarted,
	int FilesRead,
	int FilesUnreadable,
	int DuplicatesResolved)
{
	/// <summary>An empty load: what a first run, a deleted cache and a completely unreadable one all produce.</summary>
	public static LastSeenCacheLoad Empty { get; } =
		new(new Dictionary<string, LoadedEntity>(StringComparer.Ordinal), null, 0, 0, 0);
}

/// <summary>
///     The cache's files: where they live, how they are written, and how a torn set is read back.
/// </summary>
/// <remarks>
///     <para>
///         <b>The write is the one from <see cref="Hosting.LightingConfigStore"/>, per file.</b> New contents go to a
///         uniquely named temp file in the same directory and are then moved into place, with the previous version
///         kept as <c>.bak</c> — so a process death mid-write cannot leave a half-written file, and a bad write
///         leaves one generation to fall back to.
///     </para>
///     <para>
///         <b>Where the files live is derived, never constant.</b> They sit beside the configuration document and
///         take their name from it, because two houses run different document names and a hardcoded path would work
///         in one and quietly fail in the other. That directory is also the only place on a Home Assistant box that
///         survives a redeploy.
///     </para>
///     <para>
///         <b>A torn set is recoverable in a way a torn configuration would not be.</b> Per-entity data is
///         independent: a file that failed to write costs the entities in it and nothing else, and they degrade to
///         "unknown" rather than to "dead". That is what makes splitting the cache across files safe at all, and it
///         is why the loader reads every file it can and shrugs at the rest instead of refusing the whole load.
///     </para>
/// </remarks>
public sealed class LastSeenStore
{
	private const string NameInfix = ".last-seen.";
	private const string Extension = ".json";

	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

	private readonly ILogger<LastSeenStore> _logger;
	private readonly Dictionary<LastSeenKind, string> _paths = [];

	// Serialises the flush against a load, which only overlap at start-up but would overlap badly.
	private readonly Lock _gate = new();

	/// <summary>
	///     Creates a store whose files sit beside <paramref name="configFilePath"/>.
	/// </summary>
	/// <param name="configFilePath">
	///     The configuration document's path, from <see cref="Hosting.LightingConfigStore.FilePath"/>. Only its
	///     directory and its file name stem are used; the file itself is never read or written by this class.
	/// </param>
	/// <param name="logger">Where read and write failures are reported. They are always warnings, never faults.</param>
	/// <exception cref="ArgumentNullException"><paramref name="logger"/> is <c>null</c>.</exception>
	/// <exception cref="ArgumentException"><paramref name="configFilePath"/> is blank or has no directory.</exception>
	public LastSeenStore(string configFilePath, ILogger<LastSeenStore> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);

		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

		string full = Path.GetFullPath(configFilePath);

		DirectoryPath = Path.GetDirectoryName(full)
			?? throw new ArgumentException($"'{configFilePath}' has no directory to write beside.", nameof(configFilePath));

		// The stem, so b1.yaml gets b1.last-seen.motion.json: the files sort next to the document they belong to,
		// and two hosts sharing a directory cannot collide.
		string stem = Path.GetFileNameWithoutExtension(full);
		if (stem.Length == 0)
			stem = "adaptive-lighting";

		foreach (LastSeenKind kind in LastSeenKinds.All)
			_paths[kind] = Path.Combine(DirectoryPath, stem + NameInfix + kind.Token() + Extension);
	}

	/// <summary>The directory the cache files live in — the configuration document's own.</summary>
	public string DirectoryPath { get; }

	/// <summary>Every cache file's path, in bucket order. A path, not a secret: safe to log and worth logging.</summary>
	public IReadOnlyList<string> FilePaths => [.. LastSeenKinds.All.Select(kind => _paths[kind])];

	/// <summary>The file a bucket is written to.</summary>
	/// <param name="kind">The bucket.</param>
	/// <returns>Its absolute path.</returns>
	public string PathFor(LastSeenKind kind) => _paths.TryGetValue(kind, out string? path) ? path : _paths[LastSeenKind.Other];

	/// <summary>
	///     Reads every cache file and merges them.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>Never throws.</b> A missing file is a first run. An unreadable or corrupt one is a warning and its
	///         entities are simply unknown — which is the safe degradation, because a caller that reads "unknown" as
	///         "dead" would turn a deleted file into a dark house, and <see cref="IEntityLastSeen"/> says so in as
	///         many words.
	///     </para>
	///     <para>
	///         <b>Duplicates are settled by last write.</b> An entity whose kind changed — a device class can move
	///         when an integration is updated — is written to its new file and removed from its old one, but a crash
	///         between those two writes can leave it in both. The entry from the more recently written file wins, and
	///         the next flush removes the loser, so the state is self-healing rather than sticky.
	///     </para>
	/// </remarks>
	/// <returns>The merged records and what it took to get them.</returns>
	public LastSeenCacheLoad Load()
	{
		lock (_gate)
		{
			Dictionary<string, LoadedEntity> merged = new(StringComparer.Ordinal);
			DateTimeOffset? started = null;
			int read = 0;
			int unreadable = 0;
			int duplicates = 0;

			foreach (LastSeenKind kind in LastSeenKinds.All)
			{
				string path = _paths[kind];

				if (!File.Exists(path))
					continue;

				LastSeenDocument? document = TryRead(path);

				if (document is null)
				{
					unreadable++;
					continue;
				}

				read++;

				if (document.HomeAssistantStarted is { } candidate && (started is null || candidate > started))
					started = candidate;

				foreach (KeyValuePair<string, LastSeenEntry> pair in document.Entities)
				{
					if (pair.Key is not { Length: > 0 } || pair.Value is null)
						continue;

					LoadedEntity loaded = new(pair.Key, kind, pair.Value, document.SavedAt);

					if (!merged.TryGetValue(pair.Key, out LoadedEntity? existing))
					{
						merged[pair.Key] = loaded;
						continue;
					}

					duplicates++;

					if (loaded.SavedAt > existing.SavedAt)
						merged[pair.Key] = loaded;
				}
			}

			if (unreadable > 0)
				_logger.LogWarning(
					"{Count} of the last-seen cache files under {Directory} could not be read; the entities in them start again as "
					+ "unknown. Nothing is treated as dead because of it.",
					unreadable, DirectoryPath);

			if (duplicates > 0)
				_logger.LogInformation(
					"{Count} entities appeared in more than one last-seen cache file, which happens when an entity's kind changes "
					+ "and the process stopped between the two writes. The most recently written record wins and the next flush "
					+ "tidies it up.",
					duplicates);

			return new LastSeenCacheLoad(merged, started, read, unreadable, duplicates);
		}
	}

	/// <summary>
	///     Writes one bucket, keeping the previous version as <c>.bak</c>.
	/// </summary>
	/// <remarks>
	///     Reports failure rather than throwing. This is a cache: a read-only <c>/config</c>, a full disk or a
	///     permissions mistake must cost the history and nothing else — never the lighting, and never the timer this
	///     is called from.
	/// </remarks>
	/// <param name="kind">Which bucket is being written.</param>
	/// <param name="document">Its contents.</param>
	/// <returns><c>true</c> when the bytes reached disk.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="document"/> is <c>null</c>.</exception>
	public bool TrySave(LastSeenKind kind, LastSeenDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		string path = PathFor(kind);

		lock (_gate)
		{
			// A random temp name rather than a fixed ".tmp": two writers on a fixed name would have the second
			// truncate the first's file out from under it, which is the exact failure the temp file prevents.
			string temporary = Path.Combine(DirectoryPath, $".{Path.GetFileName(path)}.{Path.GetRandomFileName()}.tmp");

			try
			{
				Directory.CreateDirectory(DirectoryPath);
				File.WriteAllText(temporary, JsonSerializer.Serialize(document, LastSeenDocument.SerializerOptions), Utf8NoBom);

				if (File.Exists(path))
					// One call: replace the target and move the old contents to the backup. Copying to .bak first
					// would leave a window in which the backup is the only copy.
					File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
				else
					File.Move(temporary, path);

				return true;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				TryDelete(temporary);

				_logger.LogWarning(
					exception,
					"Could not write the last-seen cache file {Path}. The record is still correct in memory; it will be written "
					+ "again at the next flush, and losing it costs only the history.",
					path);

				return false;
			}
		}
	}

	private LastSeenDocument? TryRead(string path)
	{
		try
		{
			return JsonSerializer.Deserialize<LastSeenDocument>(File.ReadAllText(path), LastSeenDocument.SerializerOptions);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
		{
			_logger.LogWarning(exception, "Could not read the last-seen cache file {Path}; its entities start again as unknown.", path);
			return null;
		}
	}

	private void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A leftover temp file is litter, not a failure, and must not mask the real write error.
			_logger.LogDebug(exception, "Could not remove the temporary file {Path}.", path);
		}
	}
}
