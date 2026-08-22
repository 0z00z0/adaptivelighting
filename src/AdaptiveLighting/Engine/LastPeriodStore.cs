using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdaptiveLighting.Engine;

/// <summary>Remembers the circadian period last run in, so a restart can tell whether a boundary went by.</summary>
// A null store reads the same as an empty one: unknown, never "a boundary was crossed".
public interface ILastPeriodStore
{
	/// <summary>The period the previous run recorded, or <c>null</c> when there is none to be had.</summary>
	// A first run, a deleted file and a corrupt one all answer null. The value is a TimePeriodConfig.Id, or a
	// period name in a file written before ids existed; ModeMonitor translates the older shape.
	string? Load();

	/// <summary>Records the period now current, by key, reporting failure and never throwing.</summary>
	bool TrySave(string periodKey);
}

/// <summary>The file's contents: the period name, and enough context for whoever opens it.</summary>
// Shaped like LastSeenDocument, so the two files read alike.
public sealed class LastPeriodDocument
{
	public const string Explanation =
		"Machine-written note: which circadian period Adaptive Lighting was last running in. Read once at start-up, "
		+ "to work out whether a period boundary went by while the engine was stopped. Not configuration - nothing "
		+ "here is edited by hand, and deleting this file is safe: the only cost is that the first start after "
		+ "deleting it will not re-apply a period's house mode.";

	// Bumped only when an older file could be misread.
	public const int CurrentVersion = 1;

	// Ordered first so it is the first thing a reader of the file sees.
	[JsonPropertyName("_comment")]
	[JsonPropertyOrder(-2)]
	public string Comment { get; set; } = Explanation;

	[JsonPropertyName("version")]
	[JsonPropertyOrder(-1)]
	public int Version { get; set; } = CurrentVersion;

	// Context for a human reading the file; nothing reasons from it.
	[JsonPropertyName("savedAt")]
	public DateTimeOffset SavedAt { get; set; }

	/// <summary>The period's key, never the moment its boundary fell.</summary>
	// A sun-anchored Start resolves to a different time every day, so a stored timestamp would have to be re-read
	// against a table that has moved, and a box back from an outage may have an uncorrected clock. Comparing two
	// keys touches neither. A file from before ids holds a period name here.
	[JsonPropertyName("period")]
	public string? Period { get; set; }

	// Indented because a person is expected to open the file.
	public static readonly JsonSerializerOptions SerializerOptions = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		// A hand-edited file, or one from a build that knew a field this one does not, must not cost the note.
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};
}

/// <summary><see cref="ILastPeriodStore"/> over a single small JSON file beside the configuration document.</summary>
// The configuration document's directory is the only one on a Home Assistant box that survives a redeploy.
// Writes go to a uniquely named temp file and are moved into place with the previous version kept as .bak.
// Written on every change and never batched: the write a flush timer would delay is the one a restart is about
// to need. Nothing here throws; every failure is a warning and an answer of "unknown".
public sealed class LastPeriodStore : ILastPeriodStore
{
	private const string NameSuffix = ".last-period.json";
	private const string BackupSuffix = ".bak";

	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

	private readonly ILogger<LastPeriodStore> _logger;
	private readonly Lock _gate = new();

	// Only the path's directory and file name stem are used; the document itself is never touched here.
	/// <summary>Creates a store whose file sits beside <paramref name="configFilePath"/>.</summary>
	/// <exception cref="ArgumentException"><paramref name="configFilePath"/> is blank or has no directory.</exception>
	public LastPeriodStore(string configFilePath, ILogger<LastPeriodStore> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);

		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

		string full = Path.GetFullPath(configFilePath);

		DirectoryPath = Path.GetDirectoryName(full)
			?? throw new ArgumentException($"'{configFilePath}' has no directory to write beside.", nameof(configFilePath));

		// The stem, so b1.yaml gets b1.last-period.json and two hosts sharing a directory cannot collide.
		string stem = Path.GetFileNameWithoutExtension(full);
		if (stem.Length == 0)
			stem = "adaptive-lighting";

		FilePath = Path.Combine(DirectoryPath, stem + NameSuffix);
	}

	/// <summary>The directory the file lives in, which is the configuration document's own.</summary>
	public string DirectoryPath { get; }

	public string FilePath { get; }

	/// <inheritdoc/>
	public string? Load()
	{
		lock (_gate)
		{
			try
			{
				if (!File.Exists(FilePath))
					return null;   // a first run, and not a boundary crossing

				LastPeriodDocument? document =
					JsonSerializer.Deserialize<LastPeriodDocument>(File.ReadAllText(FilePath), LastPeriodDocument.SerializerOptions);

				return document?.Period is { Length: > 0 } period ? period.Trim() : null;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
			{
				_logger.LogWarning(
					exception,
					"Could not read {Path}, so the engine does not know which period it was last running in. Nothing is "
					+ "assumed from that: a period's house mode is left alone until the next boundary comes round with the "
					+ "engine running, and the file is rewritten as soon as the period changes.",
					FilePath);

				return null;
			}
		}
	}

	/// <inheritdoc/>
	public bool TrySave(string periodKey)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);

		LastPeriodDocument document = new() { SavedAt = DateTimeOffset.UtcNow, Period = periodKey.Trim() };

		lock (_gate)
		{
			// A random temp name, not a fixed ".tmp": two writers on a fixed name truncate each other.
			string temporary = Path.Combine(DirectoryPath, $".{Path.GetFileName(FilePath)}.{Path.GetRandomFileName()}.tmp");

			try
			{
				Directory.CreateDirectory(DirectoryPath);
				File.WriteAllText(temporary, JsonSerializer.Serialize(document, LastPeriodDocument.SerializerOptions), Utf8NoBom);

				if (File.Exists(FilePath))
					// One call. Copying to .bak first leaves a window where the backup is the only copy.
					File.Replace(temporary, FilePath, FilePath + BackupSuffix, ignoreMetadataErrors: true);
				else
					File.Move(temporary, FilePath);

				return true;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				TryDelete(temporary);

				_logger.LogWarning(
					exception,
					"Could not write {Path}. The engine still knows which period it is in; the cost is only that the next "
					+ "restart will not be able to tell whether a boundary went by while it was down.",
					FilePath);

				return false;
			}
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
			// Litter, not a failure. Must not mask the real write error.
			_logger.LogDebug(exception, "Could not remove the temporary file {Path}.", path);
		}
	}
}
