using System.Reactive.Concurrency;

using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Persistence;

/// <summary><see cref="IActivityJournalStore"/> over one declared state file in the state folder.</summary>
/// <remarks>
///     Written coalesced: a burst of rows arriving between two flushes costs one write, not one per row. Owns its
///     own <see cref="StateStoreRegistry"/> rather than sharing one with the engine host, because the activity
///     record lives in the web layer, a separate assembly the registry's internal types cannot cross into.
/// </remarks>
internal sealed class ActivityJournalStore : IActivityJournalStore, IDisposable
{
	public const string NameSuffix = ".activity-journal.json";

	/// <summary>The most rows kept on disk. Matches the in-memory record's own bound, kept as a separate constant
	/// so a change to either is a deliberate change to both.</summary>
	internal const int Capacity = 500;

	/// <summary>The declaration: the file, the version this build writes, and when it is written.</summary>
	public static readonly StateStoreDeclaration<ActivityJournalDocument> Declaration = new(
		Name: "the activity journal",
		NameSuffix: NameSuffix,
		Version: ActivityJournalDocument.CurrentVersion,
		WritePolicy: StateWritePolicy.Coalesced,
		VersionOf: document => document.Version,
		SavedAtOf: document => document.SavedAt);

	private readonly StateStoreRegistry _registry;
	private readonly StateStore<ActivityJournalDocument> _store;
	private readonly Func<DateTimeOffset> _now;

	/// <summary>Creates a journal whose file sits in the state folder beside <paramref name="configFilePath"/>.</summary>
	/// <remarks>Without <paramref name="scheduler"/> the coalesced write only reaches disk on <see cref="Dispose"/>.</remarks>
	/// <exception cref="ArgumentException"><paramref name="configFilePath"/> is blank or has no directory.</exception>
	public ActivityJournalStore(
		string configFilePath,
		ILoggerFactory loggerFactory,
		IScheduler? scheduler = null,
		Func<DateTimeOffset>? now = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);
		ArgumentNullException.ThrowIfNull(loggerFactory);

		_now = now ?? (() => DateTimeOffset.UtcNow);

		_registry = new StateStoreRegistry(configFilePath, loggerFactory.CreateLogger<StateStoreRegistry>(), scheduler);
		_store = _registry.Open(Declaration, ActivityJournalDocument.SerializerOptions, loggerFactory.CreateLogger<ActivityJournalStore>());
	}

	/// <summary>The directory the file lives in, which is the state folder beside the configuration document.</summary>
	public string DirectoryPath => _store.DirectoryPath;

	public string FilePath => _store.FilePath;

	/// <inheritdoc/>
	public IReadOnlyList<ActivityJournalRow> Load()
	{
		ActivityJournalDocument? document = _store.Restore(_now());

		return document?.Rows ?? [];
	}

	/// <inheritdoc/>
	public bool TrySave(IReadOnlyList<ActivityJournalRow> rows)
	{
		ArgumentNullException.ThrowIfNull(rows);

		// Kept by sequence, not by arrival order: a caller that already bounds its own list still costs nothing here.
		IReadOnlyList<ActivityJournalRow> kept = rows.Count > Capacity
			? [.. rows.OrderBy(row => row.Sequence).TakeLast(Capacity)]
			: rows;

		return _store.Write(new ActivityJournalDocument { SavedAt = _now(), Rows = kept });
	}

	/// <summary>Stops the registry's flusher and writes whatever is still waiting.</summary>
	public void Dispose() => _registry.Dispose();
}
