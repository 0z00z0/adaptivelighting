using System.Text;

using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Hosting;

/// <summary>
///     The one file the lighting UI is allowed to write, and the only way it writes it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The path is not a parameter of anything a browser can reach.</b> It is resolved once, server-side,
///         when this store is registered, and it is immutable thereafter. Nothing on the write path takes a path,
///         a file name, or a fragment of one from a request. That is the whole reason this class exists rather
///         than a <c>File.WriteAllText</c> at the call site: the write surface is exactly one file, and it is
///         decided here.
///     </para>
///     <para>
///         <b>Writes are crash-safe.</b> The new document goes to a temp file in the same directory and is then
///         moved into place, so a process death mid-write cannot leave a half-written config that would stop the
///         host booting. The previous version is kept as <c>.bak</c> — one generation, which is what you want when
///         a save turns out to have been a mistake and what you do not want to grow without bound in
///         <c>/config</c>.
///     </para>
/// </remarks>
public sealed class LightingConfigStore
{
	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

	private readonly ILogger<LightingConfigStore> _logger;

	// Serialises concurrent saves from two browser tabs. The engine rebuild that follows a save is separately
	// guarded in LightingEngineHost; this lock is only about the bytes on disk.
	private readonly Lock _gate = new();

	/// <summary>Creates a store over one fixed file.</summary>
	/// <param name="filePath">Absolute path of the configuration document. Resolved server-side by the host.</param>
	/// <param name="logger">Where reads and writes are recorded.</param>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	/// <exception cref="ArgumentException"><paramref name="filePath"/> is blank.</exception>
	public LightingConfigStore(string filePath, ILogger<LightingConfigStore> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

		FilePath = Path.GetFullPath(filePath);
		BackupPath = FilePath + ".bak";
	}

	/// <summary>The document's absolute path. A path, not a secret: safe to show in the UI, and worth showing.</summary>
	public string FilePath { get; }

	/// <summary>Where <see cref="Save"/> leaves the previous version.</summary>
	public string BackupPath { get; }

	/// <summary>Whether the document exists yet.</summary>
	public bool Exists => File.Exists(FilePath);

	/// <summary>Whether a previous version is on disk to fall back to by hand.</summary>
	public bool HasBackup => File.Exists(BackupPath);

	/// <summary>When the document was last written, or <c>null</c> when it does not exist.</summary>
	public DateTimeOffset? LastWrittenUtc => Exists ? File.GetLastWriteTimeUtc(FilePath) : null;

	/// <summary>
	///     Reads the document from disk, for callers that do not care how it was written.
	/// </summary>
	/// <returns>The parsed document.</returns>
	/// <exception cref="LightingConfigException">The file is missing, unreadable, or not a valid document.</exception>
	public AdaptiveLightingConfig Load() => Read().Config;

	/// <summary>
	///     Reads the document from disk and reports whether it had to be translated out of the pre-2.0 schema
	///     to be read at all.
	/// </summary>
	/// <remarks>
	///     Separate from <see cref="Load"/> because exactly one caller acts on the flag —
	///     <see cref="LightingEngineHost.Reload"/>, which rewrites such a file in the current schema — and every
	///     other read would only have to say <c>.Config</c> to ignore it.
	/// </remarks>
	/// <returns>The parsed document and the translation flag.</returns>
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

	/// <summary>
	///     Writes <paramref name="config"/> to disk, keeping one backup of what was there before.
	/// </summary>
	/// <remarks>
	///     This does not validate. Validation is <see cref="LightingEngineHost.Save"/>'s job, because refusing an
	///     invalid document is a decision about the engine, not about the file system — and a store that validated
	///     would tempt a caller into thinking a successful write meant a working engine.
	/// </remarks>
	/// <param name="config">The document to write.</param>
	/// <exception cref="ArgumentNullException"><paramref name="config"/> is <c>null</c>.</exception>
	/// <exception cref="LightingConfigException">The file could not be written.</exception>
	public void Save(AdaptiveLightingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		string yaml = LightingConfigDocument.Serialize(config);

		lock (_gate)
		{
			// GetRandomFileName rather than a fixed ".tmp": two saves racing on a fixed temp name would have the
			// second one truncate the first one's file out from under it, which is the exact failure the temp
			// file was there to prevent.
			string directory = Path.GetDirectoryName(FilePath)
				?? throw new LightingConfigException($"'{FilePath}' has no directory to write into.");
			string temporary = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Path.GetRandomFileName()}.tmp");

			try
			{
				Directory.CreateDirectory(directory);
				File.WriteAllText(temporary, yaml, Utf8NoBom);

				if (File.Exists(FilePath))
					// One call: replace the target and move the old contents to the backup. The alternative —
					// copy to .bak, then write the target — has a window where the config is gone and the backup
					// is the only copy.
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
