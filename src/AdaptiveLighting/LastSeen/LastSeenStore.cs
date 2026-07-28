using System.Text;
using System.Text.Json;

namespace AdaptiveLighting.LastSeen;

/// <summary>
///     One entity as it came off disk, with the bucket it was filed in and the file's own write time.
/// </summary>
/// <param name="EntityId">The entity.</param>
/// <param name="Bucket">Where it was filed last time. Re-derived from Home Assistant on the next census.</param>
/// <param name="Entry">Its record.</param>
/// <param name="SavedAt">When the file holding it was written, which is what settles a duplicate.</param>
public sealed record LoadedEntity(string EntityId, string Bucket, LastSeenEntry Entry, DateTimeOffset SavedAt);

/// <summary>
///     Everything a load produced, including what it could not read.
/// </summary>
/// <param name="Entities">The merged records, one per entity id.</param>
/// <param name="HomeAssistantStarted">The newest restart estimate found in any file, or <c>null</c>.</param>
/// <param name="FilesRead">How many cache files were read successfully.</param>
/// <param name="FilesUnreadable">How many existed but could not be read or parsed. Their entities are simply unknown.</param>
/// <param name="DuplicatesResolved">How many entities were found in more than one file. Normally zero.</param>
/// <param name="PreSplitRecords">
///     How many records came out of a pre-split catch-all file. They are read exactly like any other record — the
///     history is not the thing that changed — but they are all keyed <c>other</c> until the next census re-derives
///     their class, which is worth counting so the migration is visible in the log rather than inferred from file
///     sizes a week later.
/// </param>
public sealed record LastSeenCacheLoad(
	IReadOnlyDictionary<string, LoadedEntity> Entities,
	DateTimeOffset? HomeAssistantStarted,
	int FilesRead,
	int FilesUnreadable,
	int DuplicatesResolved,
	int PreSplitRecords)
{
	/// <summary>An empty load: what a first run, a deleted cache and a completely unreadable one all produce.</summary>
	public static LastSeenCacheLoad Empty { get; } =
		new(new Dictionary<string, LoadedEntity>(StringComparer.Ordinal), null, 0, 0, 0, 0);
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
///     <para>
///         <b>The set of files is discovered, not declared.</b> Buckets are device classes now, so the store cannot
///         know the names in advance and does not try: a load reads whatever matches the naming convention in the
///         directory. That is also what makes the upgrade from the pre-split layout free — the old
///         <c>.last-seen.other.json</c> is simply one of the files found, its records are read, and once the census
///         has re-keyed them by class the emptied bucket takes its own file away.
///     </para>
/// </remarks>
public sealed class LastSeenStore
{
	private const string NameInfix = ".last-seen.";
	private const string Extension = ".json";
	private const string BackupSuffix = ".bak";

	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

	private readonly ILogger<LastSeenStore> _logger;

	// "b1.last-seen." — everything before the bucket token. Two hosts sharing a directory cannot collide.
	private readonly string _prefix;

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

		// The stem, so b1.yaml gets b1.last-seen.motion.json: the files sort next to the document they belong to.
		string stem = Path.GetFileNameWithoutExtension(full);
		if (stem.Length == 0)
			stem = "adaptive-lighting";

		_prefix = stem + NameInfix;
	}

	/// <summary>The directory the cache files live in — the configuration document's own.</summary>
	public string DirectoryPath { get; }

	/// <summary>
	///     Every cache file currently on disk, in name order. A path, not a secret: safe to log and worth logging.
	/// </summary>
	/// <remarks>
	///     Read from the directory rather than composed from a list of buckets, because there is no such list any
	///     more — a bucket exists when an entity is in it and its file exists only while that is true.
	/// </remarks>
	public IReadOnlyList<string> FilePaths => [.. EnumerateFiles()];

	/// <summary>
	///     The file a bucket is written to.
	/// </summary>
	/// <param name="bucket">The bucket key. Sanitised on the way into the name; see <see cref="LastSeenBuckets.FileToken"/>.</param>
	/// <returns>Its absolute path. Distinct buckets always give distinct paths.</returns>
	public string PathFor(string? bucket) =>
		Path.Combine(DirectoryPath, _prefix + LastSeenBuckets.FileToken(bucket) + Extension);

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
	///         <b>Duplicates are settled by last write.</b> An entity whose bucket changed — a device class can move
	///         when an integration is updated, and every record in a pre-split <c>other</c> file changes bucket on
	///         the first census after an upgrade — is written to its new file and removed from its old one, but a
	///         crash between those two writes can leave it in both. The entry from the more recently written file
	///         wins, and the next flush removes the loser, so the state is self-healing rather than sticky.
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
			int preSplit = 0;

			foreach (string path in EnumerateFiles())
			{
				LastSeenDocument? document = TryRead(path);

				if (document is null)
				{
					unreadable++;
					continue;
				}

				read++;

				string bucket = BucketOf(document, path);

				if (document.Version <= LastSeenDocument.PreSplitVersion
					&& string.Equals(bucket, LastSeenBuckets.Unclassified, StringComparison.Ordinal))
					preSplit += document.Entities.Count;

				if (document.HomeAssistantStarted is { } candidate && (started is null || candidate > started))
					started = candidate;

				foreach (KeyValuePair<string, LastSeenEntry> pair in document.Entities)
				{
					if (pair.Key is not { Length: > 0 } || pair.Value is null)
						continue;

					LoadedEntity loaded = new(pair.Key, bucket, pair.Value, document.SavedAt);

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
					"{Count} entities appeared in more than one last-seen cache file, which happens when an entity's bucket changes "
					+ "and the process stopped between the two writes. The most recently written record wins and the next flush "
					+ "tidies it up.",
					duplicates);

			if (preSplit > 0)
				_logger.LogInformation(
					"{Count} last-seen records came from a pre-split '{Bucket}' cache file. Their history is kept exactly as it was; "
					+ "the next census re-files each one under its own device class, and the old file removes itself once it is empty.",
					preSplit, LastSeenBuckets.Unclassified);

			return new LastSeenCacheLoad(merged, started, read, unreadable, duplicates, preSplit);
		}
	}

	/// <summary>
	///     Writes one bucket, keeping the previous version as <c>.bak</c> — or removes its file when it is empty.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>An empty bucket has no file.</b> Buckets are device classes now, so they come and go with the
	///         hardware: the last battery sensor leaving the house would otherwise leave a husk of a file behind for
	///         ever, and a folder of husks is exactly the thing splitting the cache was meant to make readable. It is
	///         also how the pre-split <c>other</c> file disappears — once the census has re-filed its records by
	///         class, the bucket is empty and the file goes with it.
	///     </para>
	///     <para>
	///         Reports failure rather than throwing. This is a cache: a read-only <c>/config</c>, a full disk or a
	///         permissions mistake must cost the history and nothing else — never the lighting, and never the timer
	///         this is called from.
	///     </para>
	/// </remarks>
	/// <param name="bucket">Which bucket is being written.</param>
	/// <param name="document">Its contents. An empty one removes the file instead of writing a husk.</param>
	/// <returns><c>true</c> when the disk agrees with the bucket afterwards.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="document"/> is <c>null</c>.</exception>
	public bool TrySave(string bucket, LastSeenDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		string path = PathFor(bucket);

		lock (_gate)
		{
			if (document.Entities.Count == 0)
				return TryRemove(path);

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
					File.Replace(temporary, path, path + BackupSuffix, ignoreMetadataErrors: true);
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

	/// <summary>
	///     Every file in the directory that looks like one of this store's.
	/// </summary>
	/// <remarks>
	///     The pattern excludes both of the things that live alongside a cache file: a <c>.bak</c> does not end in
	///     <c>.json</c>, and an in-flight temp file starts with a dot rather than with the stem. Names are matched
	///     case-insensitively because most file systems this runs on are, and a file the operating system will hand
	///     back under a different case is still this store's file.
	/// </remarks>
	private IEnumerable<string> EnumerateFiles()
	{
		string[] candidates;

		try
		{
			candidates = Directory.Exists(DirectoryPath)
				? Directory.GetFiles(DirectoryPath, _prefix + "*" + Extension)
				: [];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// An unreadable directory is a first run as far as anything downstream is concerned: unknown, not dead.
			_logger.LogWarning(exception, "Could not list the last-seen cache files under {Directory}; every entity starts as unknown.", DirectoryPath);
			return [];
		}

		Array.Sort(candidates, StringComparer.OrdinalIgnoreCase);

		return candidates.Where(path => IsCacheFile(Path.GetFileName(path)));
	}

	private bool IsCacheFile(string fileName) =>
		fileName.Length > _prefix.Length + Extension.Length
		&& fileName.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)
		&& fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	///     Which bucket a loaded file holds: what it says it holds, or failing that what its name suggests.
	/// </summary>
	/// <remarks>
	///     The document is preferred because the file name is a sanitised form of the key and can be fingerprinted,
	///     so it is lossy by design. The name is the fallback for a file whose body lost the field — hand-edited, or
	///     written by a build that spelled it differently — because filing such a record under its own name is
	///     better than sweeping it into the catch-all.
	/// </remarks>
	private string BucketOf(LastSeenDocument document, string path) =>
		LastSeenBuckets.NormaliseKey(document.Bucket) is { Length: > 0 } declared
			? declared
			: LastSeenBuckets.FromToken(TokenOf(Path.GetFileName(path)));

	private string TokenOf(string fileName) =>
		fileName[_prefix.Length..^Extension.Length];

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

	/// <summary>Takes an emptied bucket's file away, and its backup with it. Reports failure rather than throwing.</summary>
	private bool TryRemove(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);

			// The backup of an empty bucket is a backup of nothing anybody can use: the in-memory set already holds
			// every record that was in it, so keeping it would only be a file nobody reads and nobody dares delete.
			if (File.Exists(path + BackupSuffix))
				File.Delete(path + BackupSuffix);

			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			_logger.LogWarning(
				exception,
				"Could not remove the emptied last-seen cache file {Path}. Its entities are filed elsewhere now, so the file is stale "
				+ "rather than wrong, and the next flush tries again.",
				path);

			return false;
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
