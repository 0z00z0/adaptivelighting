using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdaptiveLighting.Persistence;

/// <summary>One small machine-written JSON note beside the configuration document, read and written without throwing.</summary>
// The configuration document's directory is the only one on a Home Assistant box that survives a redeploy.
// Not thread-safe: each store holds its own lock around every call.
internal sealed class JsonNoteFile
{
	// The header every note starts with, ordered first so it is the first thing a reader of the file sees.
	public const string CommentProperty = "_comment";
	public const int CommentOrder = -2;
	public const string VersionProperty = "version";
	public const int VersionOrder = -1;
	public const string SavedAtProperty = "savedAt";

	public const string BackupSuffix = ".bak";

	private const string FallbackStem = "adaptive-lighting";

	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

	private readonly ILogger _logger;

	// Only the path's directory and file name stem are used; the configuration document itself is never touched here.
	/// <summary>Places the note beside <paramref name="configFilePath"/>, named after its stem.</summary>
	/// <exception cref="ArgumentException"><paramref name="configFilePath"/> is blank or has no directory.</exception>
	public JsonNoteFile(string configFilePath, string nameSuffix, ILogger logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);

		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

		string full = Path.GetFullPath(configFilePath);

		DirectoryPath = Path.GetDirectoryName(full)
			?? throw new ArgumentException($"'{configFilePath}' has no directory to write beside.", nameof(configFilePath));

		// The stem, so home.yaml gets home + suffix and two hosts sharing a directory cannot collide.
		string stem = Path.GetFileNameWithoutExtension(full);
		if (stem.Length == 0)
			stem = FallbackStem;

		FilePath = Path.Combine(DirectoryPath, stem + nameSuffix);
	}

	/// <summary>The directory the file lives in, which is the configuration document's own.</summary>
	public string DirectoryPath { get; }

	public string FilePath { get; }

	public string BackupPath => FilePath + BackupSuffix;

	/// <summary>Serializer options for a note: indented because a person is expected to open the file.</summary>
	public static JsonSerializerOptions CreateSerializerOptions() => new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		// A hand-edited file, or one from a build that knew a field this one does not, must not cost the note.
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};

	/// <summary>Reads the note. A missing file succeeds with no document; an unreadable one fails with the reason.</summary>
	public bool TryRead<TDocument>(JsonSerializerOptions options, out TDocument? document, [NotNullWhen(false)] out Exception? failure)
		where TDocument : class
	{
		document = null;
		failure = null;

		try
		{
			if (!File.Exists(FilePath))
				return true;

			document = JsonSerializer.Deserialize<TDocument>(File.ReadAllText(FilePath), options);
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
		{
			failure = exception;
			return false;
		}
	}

	/// <summary>Writes the note in one step, keeping the previous one as the backup.</summary>
	public bool TryWrite<TDocument>(TDocument document, JsonSerializerOptions options, [NotNullWhen(false)] out Exception? failure)
	{
		// A random temp name, not a fixed ".tmp": two writers on a fixed name truncate each other.
		string temporary = Path.Combine(DirectoryPath, $".{Path.GetFileName(FilePath)}.{Path.GetRandomFileName()}.tmp");

		try
		{
			Directory.CreateDirectory(DirectoryPath);
			File.WriteAllText(temporary, JsonSerializer.Serialize(document, options), Utf8NoBom);

			if (File.Exists(FilePath))
				// One call. Copying to .bak first leaves a window where the backup is the only copy.
				File.Replace(temporary, FilePath, BackupPath, ignoreMetadataErrors: true);
			else
				File.Move(temporary, FilePath);

			failure = null;
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			TryDeleteTemporary(temporary);

			failure = exception;
			return false;
		}
	}

	/// <summary>Deletes the note and its backup.</summary>
	public bool TryRemove([NotNullWhen(false)] out Exception? failure)
	{
		try
		{
			if (File.Exists(FilePath))
				File.Delete(FilePath);

			if (File.Exists(BackupPath))
				File.Delete(BackupPath);

			failure = null;
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			failure = exception;
			return false;
		}
	}

	private void TryDeleteTemporary(string path)
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
