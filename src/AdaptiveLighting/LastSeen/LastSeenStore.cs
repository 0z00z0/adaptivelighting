using System.Text;
using System.Text.Json;

namespace AdaptiveLighting.LastSeen;

/// <summary>
///     One entity as it came off disk, with the bucket it was filed in and the file's own write time.
/// </summary>
/// <remarks><c>SavedAt</c> is what settles a duplicate.</remarks>
public sealed record LoadedEntity(string EntityId, string Bucket, LastSeenEntry Entry, DateTimeOffset SavedAt);

/// <summary>
///     Everything a load produced, including what it could not read.
/// </summary>
/// <remarks>
///     <c>HomeAssistantStarted</c> is the newest restart estimate found in any file. <c>PreSplitRecords</c> counts
///     records from a pre-split catch-all file, all keyed <c>other</c> until the next census re-derives their class.
/// </remarks>
public sealed record LastSeenCacheLoad(
	IReadOnlyDictionary<string, LoadedEntity> Entities,
	DateTimeOffset? HomeAssistantStarted,
	int FilesRead,
	int FilesUnreadable,
	int DuplicatesResolved,
	int PreSplitRecords)
{
	/// <summary>What a first run, a deleted cache and a completely unreadable one all produce.</summary>
	public static LastSeenCacheLoad Empty { get; } =
		new(new Dictionary<string, LoadedEntity>(StringComparer.Ordinal), null, 0, 0, 0, 0);
}

/// <summary>
///     The cache's files: where they live, how they are written, and how a torn set is read back.
/// </summary>
/// <remarks>
///     Files are discovered from the directory, never composed from a list of buckets; a bucket is a device class, so
///     the names are not known in advance. They sit beside the configuration document and take their stem from it,
///     because two houses run different document names and that directory is the only path on a Home Assistant box
///     that survives a redeploy.
/// </remarks>
public sealed class LastSeenStore
{
	private const string NameInfix = ".last-seen.";
	private const string Extension = ".json";
	private const string BackupSuffix = ".bak";

	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

	private readonly ILogger<LastSeenStore> _logger;

	// Everything before the bucket token, e.g. "b1.last-seen.". Two hosts sharing a directory cannot collide.
	private readonly string _prefix;

	// Serialises the flush against a load, which only overlap at start-up but would overlap badly.
	private readonly Lock _gate = new();

	/// <summary>
	///     Creates a store whose files sit beside <paramref name="configFilePath"/>.
	/// </summary>
	/// <remarks>Only the directory and stem of the path are used. The configuration file itself is never touched here.</remarks>
	/// <exception cref="ArgumentException"><paramref name="configFilePath"/> is blank or has no directory.</exception>
	public LastSeenStore(string configFilePath, ILogger<LastSeenStore> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);

		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

		string full = Path.GetFullPath(configFilePath);

		DirectoryPath = Path.GetDirectoryName(full)
			?? throw new ArgumentException($"'{configFilePath}' has no directory to write beside.", nameof(configFilePath));

		// b1.yaml gives b1.last-seen.motion.json, so the files sort next to the document they belong to.
		string stem = Path.GetFileNameWithoutExtension(full);
		if (stem.Length == 0)
			stem = "adaptive-lighting";

		_prefix = stem + NameInfix;
	}

	/// <summary>The directory the cache files live in: the configuration document's own.</summary>
	public string DirectoryPath { get; }

	/// <summary>Every cache file currently on disk, in name order.</summary>
	public IReadOnlyList<string> FilePaths => [.. EnumerateFiles()];

	/// <summary>The absolute path a bucket is written to. Distinct buckets always give distinct paths.</summary>
	public string PathFor(string? bucket) =>
		Path.Combine(DirectoryPath, _prefix + LastSeenBuckets.FileToken(bucket) + Extension);

	/// <summary>
	///     Reads every cache file and merges them. Never throws; an unreadable file just leaves its entities unknown.
	/// </summary>
	/// <remarks>
	///     An entity found in two files is settled by the newer file's SavedAt. That happens when a crash lands between
	///     the write to its new bucket and the rewrite of its old one; the next flush removes the loser.
	/// </remarks>
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
	///     Writes one bucket via temp file and <c>.bak</c>, or removes its file when the document is empty.
	/// </summary>
	/// <remarks>
	///     An empty bucket has no file. Buckets come and go with the hardware, and that removal is also how the
	///     pre-split catch-all file disappears once the census has re-filed its records. Reports failure, never throws:
	///     it is called from a timer, and a read-only /config must cost history and nothing else.
	/// </remarks>
	public bool TrySave(string bucket, LastSeenDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		string path = PathFor(bucket);

		lock (_gate)
		{
			if (document.Entities.Count == 0)
				return TryRemove(path);

			// Random temp name, not a fixed ".tmp": on a fixed name a second writer truncates the first's file.
			string temporary = Path.Combine(DirectoryPath, $".{Path.GetFileName(path)}.{Path.GetRandomFileName()}.tmp");

			try
			{
				Directory.CreateDirectory(DirectoryPath);
				File.WriteAllText(temporary, JsonSerializer.Serialize(document, LastSeenDocument.SerializerOptions), Utf8NoBom);

				if (File.Exists(path))
					// One call replaces the target and moves the old contents to .bak. Copying to .bak first would
					// leave a window in which the backup is the only copy.
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
	///     The pattern excludes the two neighbours of a cache file: a .bak does not end in .json, and an in-flight temp
	///     file starts with a dot instead of the stem. Matched case-insensitively; the file systems this runs on are.
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
			// An unreadable directory reads downstream as a first run: unknown, not dead.
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
	///     Which bucket a loaded file holds: what the document says, falling back to what its name suggests.
	/// </summary>
	/// <remarks>The document wins because the file name is sanitised and possibly fingerprinted, so it is lossy.</remarks>
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

	/// <summary>Takes an emptied bucket's file away, and its backup with it. Reports failure, never throws.</summary>
	private bool TryRemove(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);

			// The in-memory set already holds every record the emptied bucket had, so its .bak backs up nothing.
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
