using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Persistence;

/// <summary><see cref="ILastPeriodStore"/> over a single small JSON file beside the configuration document.</summary>
// Written on every change and never batched: the write a flush timer would delay is the one a restart is about
// to need. Nothing here throws; every failure is a warning and an answer of "unknown".
internal sealed class LastPeriodStore : ILastPeriodStore
{
	private const string NameSuffix = ".last-period.json";

	private readonly ILogger<LastPeriodStore> _logger;
	private readonly JsonNoteFile _file;
	private readonly Lock _gate = new();

	/// <summary>Creates a store whose file sits beside <paramref name="configFilePath"/>.</summary>
	/// <exception cref="ArgumentException"><paramref name="configFilePath"/> is blank or has no directory.</exception>
	public LastPeriodStore(string configFilePath, ILogger<LastPeriodStore> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);

		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_file = new JsonNoteFile(configFilePath, NameSuffix, _logger);
	}

	/// <summary>The directory the file lives in, which is the configuration document's own.</summary>
	public string DirectoryPath => _file.DirectoryPath;

	public string FilePath => _file.FilePath;

	/// <inheritdoc/>
	public string? Load()
	{
		lock (_gate)
		{
			if (!_file.TryRead(LastPeriodDocument.SerializerOptions, out LastPeriodDocument? document, out Exception? failure))
			{
				_logger.LogWarning(
					failure,
					"Could not read {Path}, so the engine does not know which period it was last running in. Nothing is "
					+ "assumed from that: a period's house mode is left alone until the next boundary comes round with the "
					+ "engine running, and the file is rewritten as soon as the period changes.",
					FilePath);

				return null;
			}

			// No file is a first run, and not a boundary crossing.
			return document?.Period is { Length: > 0 } period ? period.Trim() : null;
		}
	}

	/// <inheritdoc/>
	public bool TrySave(string periodKey)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);

		LastPeriodDocument document = new() { SavedAt = DateTimeOffset.UtcNow, Period = periodKey.Trim() };

		lock (_gate)
		{
			if (_file.TryWrite(document, LastPeriodDocument.SerializerOptions, out Exception? failure))
				return true;

			_logger.LogWarning(
				failure,
				"Could not write {Path}. The engine still knows which period it is in; the cost is only that the next "
				+ "restart will not be able to tell whether a boundary went by while it was down.",
				FilePath);

			return false;
		}
	}
}
