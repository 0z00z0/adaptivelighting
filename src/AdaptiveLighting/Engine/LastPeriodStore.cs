using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdaptiveLighting.Engine;

/// <summary>
///     Remembers which circadian period the engine was last running in, so a restart can tell whether a boundary
///     went by while it was down.
/// </summary>
/// <remarks>
///     An interface so <see cref="ModeMonitor"/> can be tested without a disk, and so a host that cannot resolve a
///     writable directory can simply pass <c>null</c> — the monitor treats an absent store exactly as it treats an
///     empty one, which is as "we do not know", never as "a boundary was crossed".
/// </remarks>
public interface ILastPeriodStore
{
	/// <summary>
	///     The period name the previous run recorded, or <c>null</c> when there is none to be had.
	/// </summary>
	/// <remarks>
	///     A first run, a deleted file and a corrupt one all answer <c>null</c>, deliberately: the caller's
	///     question is "do I know which period we were in", and all three answer no. Telling them apart is the
	///     log's job, not the verdict's.
	/// </remarks>
	/// <returns>The recorded period name, or <c>null</c>.</returns>
	string? Load();

	/// <summary>Records the period now current. Reports failure rather than throwing.</summary>
	/// <param name="periodName">The active period's name.</param>
	/// <returns><c>true</c> when the disk agrees afterwards.</returns>
	bool TrySave(string periodName);
}

/// <summary>The file's contents: the period name, and enough context for whoever opens it.</summary>
/// <remarks>
///     Shaped like <see cref="LastSeen.LastSeenDocument"/> — a leading comment, a version, a write time — because a
///     person who opens one of these files should recognise the other.
/// </remarks>
public sealed class LastPeriodDocument
{
	/// <summary>What this file is, written into the file, for whoever opens it looking for their settings.</summary>
	public const string Explanation =
		"Machine-written note: which circadian period Adaptive Lighting was last running in. Read once at start-up, "
		+ "to work out whether a period boundary went by while the engine was stopped. Not configuration - nothing "
		+ "here is edited by hand, and deleting this file is safe: the only cost is that the first start after "
		+ "deleting it will not re-apply a period's house mode.";

	/// <summary>The current document version. Bumped only when an older file could be misread.</summary>
	public const int CurrentVersion = 1;

	/// <summary>The explanation above, first in the file so it is the first thing a reader sees.</summary>
	[JsonPropertyName("_comment")]
	[JsonPropertyOrder(-2)]
	public string Comment { get; set; } = Explanation;

	/// <summary>The document version this file was written in.</summary>
	[JsonPropertyName("version")]
	[JsonPropertyOrder(-1)]
	public int Version { get; set; } = CurrentVersion;

	/// <summary>When this file was written. Context for a human reading it; nothing reasons from it.</summary>
	[JsonPropertyName("savedAt")]
	public DateTimeOffset SavedAt { get; set; }

	/// <summary>
	///     The period's <i>name</i>, not the moment its boundary fell.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>A name, because a boundary is not a fixed time.</b> A period's <c>Start</c> may be sun-anchored —
	///         <c>sunset-01:00</c> on the owner's house — so <see cref="CircadianCalculator.ActivePeriodName"/>
	///         re-resolves the whole table against the day's sun times on every call, and yesterday's 20:14 is not
	///         today's. A recorded timestamp would have to be re-interpreted against a table that has since moved,
	///         and a sun-anchored period the sun entity could not place today is dropped from that table altogether.
	///         The name survives all of it: comparing the name recorded then with the name current now asks exactly
	///         the question that matters and asks nothing else.
	///     </para>
	///     <para>
	///         <b>And because the clock is not trustworthy across an outage.</b> A Home Assistant box that has been
	///         off may come back with a clock that has not yet been corrected, so any reasoning from elapsed time
	///         starts from a number nobody should believe. Comparing two names never touches the clock at all.
	///     </para>
	/// </remarks>
	[JsonPropertyName("period")]
	public string? Period { get; set; }

	/// <summary>How the file is written and read. Indented because a person is expected to open it.</summary>
	public static readonly JsonSerializerOptions SerializerOptions = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		// A hand-edited file, or one from a build that knew a field this one does not, must not cost the note.
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};
}

/// <summary>
///     <see cref="ILastPeriodStore"/> over a single small JSON file beside the configuration document.
/// </summary>
/// <remarks>
///     <para>
///         <b>The conventions are <see cref="LastSeen.LastSeenStore"/>'s, and for its reasons.</b> The path is
///         derived from the configuration document's own rather than configured, because two houses run different
///         document names and a hardcoded path would work in one and quietly fail in the other — and because that
///         directory is the only place on a Home Assistant box that survives a redeploy, which for a note whose
///         entire purpose is to outlive a restart is the whole point. The write goes to a uniquely named temp file
///         and is moved into place with the previous version kept as <c>.bak</c>, so a process death mid-write
///         cannot leave a half-written file.
///     </para>
///     <para>
///         <b>Its own file, not a bucket in the last-seen cache.</b> Those files are segmented by sensor class and
///         re-filed wholesale when a census re-derives a device class; a record that is not about an entity at all
///         has no class to be filed under and would be swept along by machinery that is not about it.
///     </para>
///     <para>
///         <b>Written when it changes, not on a flush timer — which is the one convention deliberately not
///         carried over.</b> The last-seen cache batches because it is high-churn and losing a few minutes of it
///         costs nothing. This changes a handful of times a day, and the write that a batching interval would
///         delay is exactly the one a restart is about to need. So each change is written as it happens.
///     </para>
///     <para>
///         <b>Nothing here throws.</b> A read-only <c>/config</c>, a full disk or a corrupt file must cost this
///         note and nothing else — never the lighting, and never the host. Every failure is a warning and an
///         answer of "we do not know".
///     </para>
/// </remarks>
public sealed class LastPeriodStore : ILastPeriodStore
{
	private const string NameSuffix = ".last-period.json";
	private const string BackupSuffix = ".bak";

	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

	private readonly ILogger<LastPeriodStore> _logger;
	private readonly Lock _gate = new();

	/// <summary>
	///     Creates a store whose file sits beside <paramref name="configFilePath"/>.
	/// </summary>
	/// <param name="configFilePath">
	///     The configuration document's path, from <see cref="Hosting.LightingConfigStore.FilePath"/>. Only its
	///     directory and its file name stem are used; the file itself is never read or written by this class.
	/// </param>
	/// <param name="logger">Where read and write failures are reported. They are always warnings, never faults.</param>
	/// <exception cref="ArgumentNullException"><paramref name="logger"/> is <c>null</c>.</exception>
	/// <exception cref="ArgumentException"><paramref name="configFilePath"/> is blank or has no directory.</exception>
	public LastPeriodStore(string configFilePath, ILogger<LastPeriodStore> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);

		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

		string full = Path.GetFullPath(configFilePath);

		DirectoryPath = Path.GetDirectoryName(full)
			?? throw new ArgumentException($"'{configFilePath}' has no directory to write beside.", nameof(configFilePath));

		// The stem, so b1.yaml gets b1.last-period.json: two hosts sharing a directory cannot collide, and the
		// file sorts next to the document it belongs to.
		string stem = Path.GetFileNameWithoutExtension(full);
		if (stem.Length == 0)
			stem = "adaptive-lighting";

		FilePath = Path.Combine(DirectoryPath, stem + NameSuffix);
	}

	/// <summary>The directory the file lives in — the configuration document's own.</summary>
	public string DirectoryPath { get; }

	/// <summary>The file itself. A path, not a secret: safe to log and worth logging.</summary>
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
	public bool TrySave(string periodName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(periodName);

		LastPeriodDocument document = new() { SavedAt = DateTimeOffset.UtcNow, Period = periodName.Trim() };

		lock (_gate)
		{
			// A random temp name rather than a fixed ".tmp": two writers on a fixed name would have the second
			// truncate the first's file out from under it, which is the exact failure the temp file prevents.
			string temporary = Path.Combine(DirectoryPath, $".{Path.GetFileName(FilePath)}.{Path.GetRandomFileName()}.tmp");

			try
			{
				Directory.CreateDirectory(DirectoryPath);
				File.WriteAllText(temporary, JsonSerializer.Serialize(document, LastPeriodDocument.SerializerOptions), Utf8NoBom);

				if (File.Exists(FilePath))
					// One call: replace the target and move the old contents to the backup. Copying to .bak first
					// would leave a window in which the backup is the only copy.
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
			// A leftover temp file is litter, not a failure, and must not mask the real write error.
			_logger.LogDebug(exception, "Could not remove the temporary file {Path}.", path);
		}
	}
}
