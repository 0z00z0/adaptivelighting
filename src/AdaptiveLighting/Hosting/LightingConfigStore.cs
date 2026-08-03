using System.Text;

using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Hosting;

/// <summary>The one file the lighting UI is allowed to write, and the only way it writes it.</summary>
/// <remarks>
///     The path is resolved once, server-side, and is immutable. Nothing on the write path takes a path or a
///     fragment of one from a request. Writes go to a temp file and are moved into place, so a process death
///     mid-write cannot leave a half-written config; one previous generation is kept as <c>.bak</c>.
/// </remarks>
public sealed class LightingConfigStore
{
	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

	private readonly ILogger<LightingConfigStore> _logger;

	// Only about the bytes on disk. The engine rebuild that follows a save is guarded separately, in
	// LightingEngineHost.
	private readonly Lock _gate = new();

	/// <summary>Creates a store over one fixed file. The host resolves <c>filePath</c> server-side.</summary>
	public LightingConfigStore(string filePath, ILogger<LightingConfigStore> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

		FilePath = Path.GetFullPath(filePath);
		BackupPath = FilePath + ".bak";
	}

	/// <summary>The document's absolute path. A path, not a secret: safe to show in the UI.</summary>
	public string FilePath { get; }

	/// <summary>Where <see cref="Save"/> leaves the previous version.</summary>
	public string BackupPath { get; }

	/// <summary>Whether the document exists yet.</summary>
	public bool Exists => File.Exists(FilePath);

	/// <summary>Whether a previous version is on disk to fall back to by hand.</summary>
	public bool HasBackup => File.Exists(BackupPath);

	/// <summary>When the document was last written, or <c>null</c> when it does not exist.</summary>
	public DateTimeOffset? LastWrittenUtc => Exists ? File.GetLastWriteTimeUtc(FilePath) : null;

	/// <exception cref="LightingConfigException">The file is missing, unreadable, or not a valid document.</exception>
	public AdaptiveLightingConfig Load() => Read().Config;

	/// <summary>
	///     Reads the document and reports whether it had to be translated out of the pre-2.0 schema to be read at
	///     all. Only <see cref="LightingEngineHost.Reload"/> acts on the flag.
	/// </summary>
	/// <exception cref="LightingConfigException">The file is missing, unreadable, or not a valid document.</exception>
	public DocumentReadResult Read()
	{
		lock (_gate)
		{
			string text;

			try
			{
				text = File.ReadAllText(FilePath);
			}
			catch (FileNotFoundException exception)
			{
				throw new LightingConfigException(
					$"No configuration file at '{FilePath}'. The host resolves this path from its NetDaemon application configuration folder.",
					exception);
			}
			catch (DirectoryNotFoundException exception)
			{
				throw new LightingConfigException(
					$"No configuration file at '{FilePath}': the directory does not exist.", exception);
			}
			catch (IOException exception)
			{
				throw new LightingConfigException($"Could not read '{FilePath}': {exception.Message}", exception);
			}
			catch (UnauthorizedAccessException exception)
			{
				throw new LightingConfigException($"Not allowed to read '{FilePath}': {exception.Message}", exception);
			}

			return LightingConfigDocument.Deserialize(text, _logger);
		}
	}

	/// <summary>Writes <paramref name="config"/> to disk, keeping one backup of what was there before.</summary>
	/// <remarks>Does not validate: that is <see cref="LightingEngineHost.Save"/>'s job, so a write is not a working engine.</remarks>
	/// <exception cref="LightingConfigException">The file could not be written.</exception>
	public void Save(AdaptiveLightingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		string yaml = LightingConfigDocument.Serialize(config);

		lock (_gate)
		{
			// GetRandomFileName, not a fixed ".tmp": on a fixed name two racing saves truncate each other, which is
			// the failure the temp file exists to prevent.
			string directory = Path.GetDirectoryName(FilePath)
				?? throw new LightingConfigException($"'{FilePath}' has no directory to write into.");
			string temporary = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Path.GetRandomFileName()}.tmp");

			try
			{
				Directory.CreateDirectory(directory);
				File.WriteAllText(temporary, yaml, Utf8NoBom);

				if (File.Exists(FilePath))
					// One call: replace the target and move the old contents to the backup, leaving no window in
					// which the backup is the only copy.
					File.Replace(temporary, FilePath, BackupPath, ignoreMetadataErrors: true);
				else
					File.Move(temporary, FilePath);

				_logger.LogInformation(
					"Wrote lighting configuration to {Path} ({Areas} areas, {Periods} periods). Previous version kept at {Backup}.",
					FilePath, config.Areas.Count, config.Periods.Count, BackupPath);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				TryDelete(temporary);
				throw new LightingConfigException($"Could not write '{FilePath}': {exception.Message}", exception);
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
			// A leftover temp file is litter, not a failure, and it must not mask the real write error.
			_logger.LogWarning(exception, "Could not remove the temporary file {Path}.", path);
		}
	}
}
