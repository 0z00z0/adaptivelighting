using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Persistence;

/// <summary><see cref="IAreaSetupMemory"/> over a single small JSON file beside the configuration document.</summary>
// Every failure degrades to "notify": an unreadable file, a failed write and a missing directory all report the
// standing problems as unreported, so the cost is a repeated card and never a silence.
internal sealed class AreaSetupMemoryStore : IAreaSetupMemory
{
	private const string NameSuffix = ".setup-faults.json";

	private readonly ILogger<AreaSetupMemoryStore> _logger;
	private readonly JsonNoteFile _file;
	private readonly Lock _gate = new();

	/// <summary>Creates a memory whose file sits beside <paramref name="configFilePath"/>.</summary>
	/// <exception cref="ArgumentException"><paramref name="configFilePath"/> is blank or has no directory.</exception>
	public AreaSetupMemoryStore(string configFilePath, ILogger<AreaSetupMemoryStore> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);

		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_file = new JsonNoteFile(configFilePath, NameSuffix, _logger);
	}

	/// <summary>The directory the file lives in, which is the configuration document's own.</summary>
	public string DirectoryPath => _file.DirectoryPath;

	public string FilePath => _file.FilePath;

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

		if (!_file.TryRead(AreaSetupMemoryDocument.SerializerOptions, out AreaSetupMemoryDocument? document, out Exception? failure))
		{
			_logger.LogWarning(
				failure,
				"Could not read {Path}, so the engine does not know which room problems it has already reported. Every "
				+ "problem standing now is reported once more, and the file is rewritten with them.",
				FilePath);

			return recalled;
		}

		if (document?.Rooms is not { Count: > 0 } rooms)
			return recalled;

		// Copied into a new dictionary: the comparer does not survive deserialisation.
		foreach (KeyValuePair<string, string> pair in rooms)
			if (pair.Key is { Length: > 0 } && pair.Value is not null)
				recalled[pair.Key] = pair.Value;

		return recalled;
	}

	private void TryWrite(Dictionary<string, string> rooms)
	{
		AreaSetupMemoryDocument document = new() { SavedAt = DateTimeOffset.UtcNow, Rooms = rooms };

		if (_file.TryWrite(document, AreaSetupMemoryDocument.SerializerOptions, out Exception? failure))
			return;

		_logger.LogWarning(
			failure,
			"Could not write {Path}. The room problems were still reported; the cost is only that the next start "
			+ "reports the same ones again.",
			FilePath);
	}

	/// <summary>Takes the file away once every room resolves, and its backup with it.</summary>
	private void TryRemove()
	{
		if (_file.TryRemove(out Exception? failure))
			return;

		_logger.LogWarning(
			failure,
			"Could not remove {Path} now that every room is set up. It is stale rather than wrong: the rooms named in "
			+ "it are running, and the next start with a problem rewrites it.",
			FilePath);
	}
}
