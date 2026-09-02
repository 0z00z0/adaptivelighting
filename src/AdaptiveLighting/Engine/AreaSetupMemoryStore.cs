using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdaptiveLighting.Engine;

/// <summary>A room that is switched on but could not be set up, and what was wrong with it.</summary>
/// <remarks><c>Key</c> identifies the room across restarts; <c>Area</c> is what a person is shown.</remarks>
public sealed record AreaSetupFault(string Key, string Area, string Problem);

/// <summary>Remembers which rooms have already been reported as impossible to set up.</summary>
// The whole point is to survive a restart, since a restart is when the repeat happens. A null memory reads as
// "nothing remembered", which notifies every start: the behaviour without one.
public interface IAreaSetupMemory
{
	/// <summary>Records the problems standing now, and answers which of them have not been reported before.</summary>
	/// <remarks>
	///     Always call it, an empty list included: a room that resolves is what clears its memory, and without that
	///     a problem could never be reported twice.
	/// </remarks>
	IReadOnlyList<AreaSetupFault> Record(IReadOnlyList<AreaSetupFault> standing);
}

/// <summary>The file's contents: one line per room that could not be set up, and enough context for whoever opens it.</summary>
// Shaped like LastPeriodDocument, so the notes beside the configuration document all read alike.
public sealed class AreaSetupMemoryDocument
{
	public const string Explanation =
		"Machine-written note: which rooms Adaptive Lighting could not set up, and what was wrong with each. It "
		+ "exists so a standing problem is reported once instead of at every start. Not configuration - nothing "
		+ "here is edited by hand, and deleting this file is safe: the only cost is that every standing problem "
		+ "is reported once more.";

	// Bumped only when an older file could be misread.
	public const int CurrentVersion = 1;

	[JsonPropertyName("_comment")]
	[JsonPropertyOrder(-2)]
	public string Comment { get; set; } = Explanation;

	[JsonPropertyName("version")]
	[JsonPropertyOrder(-1)]
	public int Version { get; set; } = CurrentVersion;

	// Context for a human reading the file; nothing reasons from it.
	[JsonPropertyName("savedAt")]
	public DateTimeOffset SavedAt { get; set; }

	/// <summary>Room key to the problem reported for it.</summary>
	// The problem's own words are the value, so a room whose problem changes reads as a different entry and is
	// reported again.
	[JsonPropertyName("rooms")]
	public Dictionary<string, string> Rooms { get; set; } = new(StringComparer.OrdinalIgnoreCase);

	// Indented because a person is expected to open the file.
	public static readonly JsonSerializerOptions SerializerOptions = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};
}

/// <summary><see cref="IAreaSetupMemory"/> over a single small JSON file beside the configuration document.</summary>
// The configuration document's directory is the only one on a Home Assistant box that survives a redeploy.
// Every failure degrades to "notify": an unreadable file, a failed write and a missing directory all report the
// standing problems as unreported, so the cost is a repeated card and never a silence.
public sealed class AreaSetupMemoryStore : IAreaSetupMemory
{
	private const string NameSuffix = ".setup-faults.json";
	private const string BackupSuffix = ".bak";

	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

	private readonly ILogger<AreaSetupMemoryStore> _logger;
	private readonly Lock _gate = new();

	/// <summary>Creates a memory whose file sits beside <paramref name="configFilePath"/>.</summary>
	/// <remarks>Only the directory and stem of the path are used; the configuration file itself is never touched here.</remarks>
	/// <exception cref="ArgumentException"><paramref name="configFilePath"/> is blank or has no directory.</exception>
	public AreaSetupMemoryStore(string configFilePath, ILogger<AreaSetupMemoryStore> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);

		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

		string full = Path.GetFullPath(configFilePath);

		DirectoryPath = Path.GetDirectoryName(full)
			?? throw new ArgumentException($"'{configFilePath}' has no directory to write beside.", nameof(configFilePath));

		// The stem, so b1.yaml gets b1.setup-faults.json and two hosts sharing a directory cannot collide.
		string stem = Path.GetFileNameWithoutExtension(full);
		if (stem.Length == 0)
			stem = "adaptive-lighting";

		FilePath = Path.Combine(DirectoryPath, stem + NameSuffix);
	}

	/// <summary>The directory the file lives in, which is the configuration document's own.</summary>
	public string DirectoryPath { get; }

	public string FilePath { get; }

	/// <inheritdoc/>
	public IReadOnlyList<AreaSetupFault> Record(IReadOnlyList<AreaSetupFault> standing)
	{
		ArgumentNullException.ThrowIfNull(standing);

		lock (_gate)
		{
			Dictionary<string, string> remembered = Read();
			Dictionary<string, string> now = new(StringComparer.OrdinalIgnoreCase);
			List<AreaSetupFault> unreported = [];

			foreach (AreaSetupFault fault in standing)
			{
				if (fault.Key is not { Length: > 0 })
					continue;

				// Last write wins on a duplicate key, which two areas configured under one name would give.
				now[fault.Key] = fault.Problem;

				if (!remembered.TryGetValue(fault.Key, out string? seen) || !string.Equals(seen, fault.Problem, StringComparison.Ordinal))
					unreported.Add(fault);
			}

			// Only what is standing now is written, so a room that has resolved is forgotten and a later regression
			// counts as new again.
			if (!SameAs(remembered, now))
			{
				if (now.Count == 0)
					TryRemove();
				else
					TryWrite(now);
			}

			return unreported;
		}
	}

	private static bool SameAs(Dictionary<string, string> remembered, Dictionary<string, string> now) =>
		remembered.Count == now.Count
		&& now.All(pair => remembered.TryGetValue(pair.Key, out string? seen) && string.Equals(seen, pair.Value, StringComparison.Ordinal));

	/// <summary>What is on disk, or nothing at all; a first run, an absent file and a corrupt one all read the same.</summary>
	private Dictionary<string, string> Read()
	{
		Dictionary<string, string> recalled = new(StringComparer.OrdinalIgnoreCase);

		try
		{
			if (!File.Exists(FilePath))
				return recalled;

			AreaSetupMemoryDocument? document =
				JsonSerializer.Deserialize<AreaSetupMemoryDocument>(File.ReadAllText(FilePath), AreaSetupMemoryDocument.SerializerOptions);

			if (document?.Rooms is not { Count: > 0 } rooms)
				return recalled;

			// Rebuilt rather than used as deserialised: the comparer does not survive deserialisation.
			foreach (KeyValuePair<string, string> pair in rooms)
				if (pair.Key is { Length: > 0 } && pair.Value is not null)
					recalled[pair.Key] = pair.Value;

			return recalled;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
		{
			_logger.LogWarning(
				exception,
				"Could not read {Path}, so the engine does not know which room problems it has already reported. Every "
				+ "problem standing now is reported once more, and the file is rewritten with them.",
				FilePath);

			return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		}
	}

	private void TryWrite(Dictionary<string, string> rooms)
	{
		AreaSetupMemoryDocument document = new() { SavedAt = DateTimeOffset.UtcNow, Rooms = rooms };

		// A random temp name, not a fixed ".tmp": two writers on a fixed name truncate each other.
		string temporary = Path.Combine(DirectoryPath, $".{Path.GetFileName(FilePath)}.{Path.GetRandomFileName()}.tmp");

		try
		{
			Directory.CreateDirectory(DirectoryPath);
			File.WriteAllText(temporary, JsonSerializer.Serialize(document, AreaSetupMemoryDocument.SerializerOptions), Utf8NoBom);

			if (File.Exists(FilePath))
				// One call. Copying to .bak first leaves a window where the backup is the only copy.
				File.Replace(temporary, FilePath, FilePath + BackupSuffix, ignoreMetadataErrors: true);
			else
				File.Move(temporary, FilePath);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			TryDelete(temporary);

			_logger.LogWarning(
				exception,
				"Could not write {Path}. The room problems were still reported; the cost is only that the next start "
				+ "reports the same ones again.",
				FilePath);
		}
	}

	/// <summary>Takes the file away once every room resolves, and its backup with it.</summary>
	private void TryRemove()
	{
		try
		{
			if (File.Exists(FilePath))
				File.Delete(FilePath);

			if (File.Exists(FilePath + BackupSuffix))
				File.Delete(FilePath + BackupSuffix);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			_logger.LogWarning(
				exception,
				"Could not remove {Path} now that every room is set up. It is stale rather than wrong: the rooms named in "
				+ "it are running, and the next start with a problem rewrites it.",
				FilePath);
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
