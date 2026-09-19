using System.Reactive.Concurrency;

using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Persistence;

/// <summary><see cref="IActivityJournalStore"/> over one declared state file in the state folder.</summary>
/// <remarks>
///     Written coalesced: a burst of rows arriving between two flushes costs one write, not one per row. Registered
///     in the engine's shared <see cref="StateStoreRegistry"/> where the container has one.
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

	// Null where the journal is declared in a registry it does not own.
	private readonly StateStoreRegistry? _ownedRegistry;
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

		_ownedRegistry = new StateStoreRegistry(configFilePath, loggerFactory.CreateLogger<StateStoreRegistry>(), scheduler);
		_store = _ownedRegistry.Open(Declaration, ActivityJournalDocument.SerializerOptions, loggerFactory.CreateLogger<ActivityJournalStore>());
	}

	/// <summary>Declares the journal in <paramref name="registry"/>, whose flusher and start-up report then cover it.</summary>
	/// <exception cref="ArgumentException">The registry's configuration path has no directory.</exception>
	internal ActivityJournalStore(StateStoreRegistry registry, ILoggerFactory loggerFactory, Func<DateTimeOffset>? now = null)
	{
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(loggerFactory);

		_now = now ?? (() => DateTimeOffset.UtcNow);

		_store = registry.Open(Declaration, ActivityJournalDocument.SerializerOptions, loggerFactory.CreateLogger<ActivityJournalStore>());
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

	/// <summary>Writes whatever is still waiting, and stops the flusher where the journal owns its registry.</summary>
	public void Dispose()
	{
		if (_ownedRegistry is not null)
			_ownedRegistry.Dispose();
		else
			_store.Flush();
	}
}
