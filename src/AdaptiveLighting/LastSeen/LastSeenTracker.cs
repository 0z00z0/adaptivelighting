using System.Reactive.Concurrency;
using System.Reactive.Disposables;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.LastSeen;

/// <summary>
///     Keeps an honest record of when each Home Assistant entity was last genuinely heard from, and survives both
///     a Home Assistant restart and an engine restart.
/// </summary>
/// <remarks>
///     <para>
///         <b>The trap this exists to avoid, written down because it is not visible in the code and somebody will
///         otherwise simplify it away.</b> Home Assistant resets <c>last_updated</c> and <c>last_changed</c> on
///         every restart: each entity is restored and re-announced, and every timestamp in the house collapses to
///         the same instant. Measured on the live house on 2026-07-28, 2.3 hours after a restart: of 51 motion
///         sensors, the oldest timestamp of any of them was 2.30 hours. A sensor dead for a week read exactly the
///         same as one that reported five minutes before the restart. So the naive implementation — "remember the
///         newest <c>last_updated</c> we have seen" — is worse than useless: it agrees with Home Assistant that
///         everything is fresh, on precisely the occasions when it is not.
///     </para>
///     <para>
///         <b>How a restart is told apart from a report.</b> By the shape of the whole population, which is the one
///         signal that needs no cooperation from Home Assistant and survives being disconnected while it happened.
///         A running house has timestamps spread over hours or days — a door nobody opened since Tuesday sits well
///         behind a power meter reporting every ten seconds. Immediately after a restart that spread is gone,
///         because nothing can carry a timestamp older than the restart. So when the entire population's timestamps
///         fit inside <see cref="LastSeenOptions.CollapseWindow"/>, and the population is large enough for that to
///         mean anything, this reads it as a restart and takes the oldest timestamp in it as the moment Home
///         Assistant started. Home Assistant's own <c>homeassistant_start</c> event does the same job when it
///         arrives — it usually does not, because the socket is down while Home Assistant is restarting — so it is
///         a second opinion rather than the mechanism.
///     </para>
///     <para>
///         <b>What happens in the window immediately after a restart.</b> Nothing advances. A timestamp is treated
///         as evidence only if it is more than <see cref="LastSeenOptions.StartupGrace"/> newer than the restart,
///         and every restored timestamp is inside that window by construction. Every entity therefore keeps the
///         record it had before the restart: the sensor that was dead for a week still reads a week, and the healthy
///         one still reads five minutes before the restart until it reports again — which, being healthy, it does.
///         The cost is that a genuine report in the first few minutes after a restart is not counted, which is a few
///         minutes of apparent staleness on an entity that will report again shortly. That trade is deliberate and
///         one-sided: refusing a real report is recoverable, believing a restore is not.
///     </para>
///     <para>
///         <b>Why this samples rather than subscribes.</b> Home Assistant keeps its own <c>last_updated</c> until it
///         restarts, so a census a minute misses nothing a subscription would have caught. A subscription would
///         actually be worse: the restore burst arrives before anything could have worked out that a restart
///         happened, so an event-driven design advances every record first and discovers its mistake afterwards,
///         which then needs a rollback to undo. A census decides whether Home Assistant restarted and only then
///         decides what to believe, so the mistake never happens.
///     </para>
///     <para>
///         <b>"The value did not change" is not "it did not report", and this never confuses the two.</b> A
///         light-level sensor sitting at a constant 3 lx all night is healthy and quiet. Evidence here is any
///         forward movement of Home Assistant's <c>last_updated</c> — which covers a changed attribute and a forced
///         update as well as a changed value — and never a comparison of one state string against the previous one.
///         One residual limit is worth stating because it is not this module's to fix: since Home Assistant 2024.8 a
///         report of a byte-identical value with byte-identical attributes moves only <c>last_reported</c>, and
///         NetDaemon 26.21's <see cref="EntityState"/> does not expose that field, so such a report is invisible to
///         this process. When NetDaemon surfaces <c>last_reported</c>, reading it here in preference to
///         <c>last_updated</c> is the whole fix.
///     </para>
/// </remarks>
public sealed class LastSeenTracker : IEntityLastSeen, IDisposable
{
	private const string DeviceClassAttribute = "device_class";

	private readonly IHaContext _ha;
	private readonly IScheduler _scheduler;
	private readonly LastSeenStore _store;
	private readonly LastSeenOptions _options;
	private readonly ILogger<LastSeenTracker> _logger;

	// Readers are the lighting decisions, on Home Assistant's threads; writers are the census and the flush, on the
	// scheduler's. Everything touching the dictionary or the restart estimate holds this.
	private readonly Lock _gate = new();

	private readonly Dictionary<string, TrackedEntity> _entities = new(StringComparer.Ordinal);
	private readonly HashSet<LastSeenKind> _dirty = [];
	private readonly CompositeDisposable _subscriptions = [];

	private DateTimeOffset? _haStartedAt;
	private bool _previousCensusCollapsed;
	private bool _started;

	/// <summary>Creates a tracker. Nothing is loaded, sampled or written until <see cref="Start"/>.</summary>
	/// <param name="ha">Where the population and its timestamps are read from. Never commanded.</param>
	/// <param name="scheduler">The module's only clock: every "now" and every timer comes from here.</param>
	/// <param name="store">The cache files beside the configuration document.</param>
	/// <param name="options">The tuning. The defaults are the documented ones.</param>
	/// <param name="logger">Where restarts, evictions and write failures are reported.</param>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public LastSeenTracker(
		IHaContext ha,
		IScheduler scheduler,
		LastSeenStore store,
		LastSeenOptions options,
		ILogger<LastSeenTracker> logger)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <inheritdoc/>
	public DateTimeOffset? HomeAssistantStartedUtc
	{
		get
		{
			lock (_gate)
				return _haStartedAt;
		}
	}

	/// <inheritdoc/>
	public int TrackedCount
	{
		get
		{
			lock (_gate)
				return _entities.Count;
		}
	}

	/// <inheritdoc/>
	public DateTimeOffset? LastSeenUtc(string entityId)
	{
		if (entityId is not { Length: > 0 })
			return null;

		lock (_gate)
			return _entities.TryGetValue(entityId, out TrackedEntity? tracked) ? tracked.LastSeen : null;
	}

	/// <inheritdoc/>
	public TimeSpan? SilenceOf(string entityId) =>
		LastSeenUtc(entityId) is { } seen ? _scheduler.Now.ToUniversalTime() - seen : null;

	/// <inheritdoc/>
	public bool HasBeenSilentFor(string entityId, TimeSpan threshold) =>
		threshold > TimeSpan.Zero && SilenceOf(entityId) is { } silence && silence > threshold;

	/// <summary>
	///     Loads the cache, takes the first census, and arms the census and flush timers.
	/// </summary>
	/// <remarks>
	///     The first census happens inline rather than on the first timer tick, so that a caller asking a question
	///     immediately after start-up gets the loaded history and not an empty one — and so that a Home Assistant
	///     restart which happened while this process was down is noticed at once rather than a minute later.
	/// </remarks>
	/// <exception cref="InvalidOperationException">Already started.</exception>
	public void Start()
	{
		lock (_gate)
		{
			if (_started)
				throw new InvalidOperationException("The last-seen tracker has already been started.");

			_started = true;
			LoadCore();
		}

		// A second opinion on restarts, not the mechanism: Home Assistant fires this while the socket is down for
		// the restart, so it usually never arrives. When it does, it is the earliest and most certain signal there
		// is, and it covers the one case the population test abstains from — an instance too small to reason about.
		try
		{
			_subscriptions.Add(_ha.Events
				.Where(@event => @event.EventType is "homeassistant_start" or "homeassistant_started")
				.SubscribeSafe(OnHomeAssistantStarted, _logger));
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
		{
			// No event stream is a degraded tracker, not a dead one: the population test carries the load.
			_logger.LogWarning(exception, "Could not watch for Home Assistant start events; restarts will be detected from the entity population alone.");
		}

		TakeCensus();

		_subscriptions.Add(_scheduler.SchedulePeriodic(_options.CensusInterval, TakeCensus));
		_subscriptions.Add(_scheduler.SchedulePeriodic(_options.FlushInterval, Flush));
	}

	/// <summary>Stops sampling and writes whatever has changed, so a graceful shutdown loses nothing.</summary>
	public void Dispose()
	{
		_subscriptions.Dispose();
		Flush();
	}

	// ---- the census -------------------------------------------------------------------------

	/// <summary>
	///     Samples every entity's Home Assistant timestamp, decides whether Home Assistant has restarted, and only
	///     then decides what to believe.
	/// </summary>
	/// <remarks>
	///     The order is the whole design: the restart verdict is reached from the same sample it is then applied to,
	///     so there is never a moment at which restore timestamps have been believed and must be taken back.
	/// </remarks>
	private void TakeCensus()
	{
		try
		{
			TakeCensusCore();
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			// This runs on a timer with no caller to catch anything, and an unobserved exception on a thread-pool
			// scheduler ends the process — the whole Home Assistant host, not just this cache.
			_logger.LogWarning(exception, "A last-seen census failed and was abandoned. The record is unchanged and the next census will try again.");
		}
	}

	private void TakeCensusCore()
	{
		DateTimeOffset now = _scheduler.Now.ToUniversalTime();

		IReadOnlyList<Entity> population;

		try
		{
			population = _ha.GetAllEntities();
		}
		catch (InvalidOperationException exception)
		{
			// NetDaemon's state cache throws until its first connection to Home Assistant completes.
			_logger.LogDebug(exception, "Home Assistant is not readable yet; skipping this last-seen census.");
			return;
		}

		List<EntitySample> samples = [];
		HashSet<string> present = new(StringComparer.Ordinal);

		foreach (Entity entity in population)
		{
			EntityState? state = _ha.GetState(entity.EntityId);

			if (state is null)
				continue;

			present.Add(entity.EntityId);

			if (StampOf(state, now) is { } stamp)
				samples.Add(new EntitySample(entity.EntityId, stamp, state));
		}

		if (samples.Count == 0)
		{
			// A house with nothing in it is a connection problem, not a house where everything died. Concluding
			// anything here — a restart, an eviction — would be concluding it from no evidence.
			_logger.LogDebug("No Home Assistant entity carried a timestamp this census; nothing to record.");
			return;
		}

		lock (_gate)
		{
			DetectRestart(samples, now);

			foreach (EntitySample sample in samples)
				Record(sample, now);

			Evict(present, now);
		}
	}

	/// <summary>
	///     Decides whether this sample is a house that has just restarted, and if so when.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The collapse must be a <i>transition</i>, not a state. A handful of chatty sensors report inside the
	///         window every minute of their lives, and a rule that read that as a restart would declare one every
	///         census and never believe anything again. So a population that was already collapsed last time is
	///         simply a tight population; only a spread one becoming tight is a restart.
	///     </para>
	///     <para>
	///         The estimate only ever moves forwards, and it is persisted, so an engine restarted five minutes after
	///         Home Assistant restarted knows it is inside the window and refuses the same timestamps its previous
	///         run would have refused.
	///     </para>
	///     <para>
	///         <b>The residual, stated rather than hidden.</b> A small installation in which every entity happens to
	///         report inside one window, having previously been spread, looks exactly like a restart and is called
	///         one. That costs a few minutes in which nothing advances, and then it corrects itself; it can never
	///         make an entity look fresher than it is, which is the only error worth defending against here. A house
	///         of any size has quiet entities and never reaches that state.
	///     </para>
	/// </remarks>
	private void DetectRestart(List<EntitySample> samples, DateTimeOffset now)
	{
		DateTimeOffset oldest = samples[0].Stamp;
		DateTimeOffset newest = samples[0].Stamp;

		foreach (EntitySample sample in samples)
		{
			if (sample.Stamp < oldest)
				oldest = sample.Stamp;

			if (sample.Stamp > newest)
				newest = sample.Stamp;
		}

		bool collapsed = samples.Count >= _options.MinimumPopulation && newest - oldest <= _options.CollapseWindow;
		bool wasCollapsed = _previousCensusCollapsed;
		_previousCensusCollapsed = collapsed;

		if (!collapsed || wasCollapsed)
			return;

		if (_haStartedAt is { } known && oldest <= known)
			// The same restart we already know about — an engine restarted moments after Home Assistant was.
			return;

		DeclareRestart(oldest, now,
			$"all {samples.Count} entity timestamps collapsed into {(newest - oldest).TotalSeconds:0}s of each other");
	}

	private void DeclareRestart(DateTimeOffset startedAt, DateTimeOffset now, string evidence)
	{
		_haStartedAt = startedAt;

		// Every file carries the estimate, so every file is now out of date. This is rare and cheap.
		foreach (LastSeenKind kind in LastSeenKinds.All)
			_dirty.Add(kind);

		_logger.LogInformation(
			"Home Assistant appears to have restarted at {StartedAt:u} ({Evidence}). Its own last_updated timestamps have all been "
			+ "reset, so nothing is believed until {TrustedFrom:u}: every entity keeps the last-seen time it already had, which is "
			+ "the whole point of this cache.",
			startedAt, evidence, startedAt + _options.StartupGrace);

		if (now - startedAt > _options.CollapseWindow)
			_logger.LogDebug("The restart was noticed {Age:0} minutes after it happened.", (now - startedAt).TotalMinutes);
	}

	private void OnHomeAssistantStarted(Event @event)
	{
		lock (_gate)
		{
			DateTimeOffset now = _scheduler.Now.ToUniversalTime();

			if (_haStartedAt is { } known && known >= now)
				return;

			// The population is about to collapse, so record that as the state rather than waiting to observe it.
			_previousCensusCollapsed = true;
			DeclareRestart(now, now, $"Home Assistant announced it with a {@event.EventType} event");
		}
	}

	/// <summary>
	///     Whether a Home Assistant timestamp is evidence that the entity is alive, or an artefact of a restart.
	/// </summary>
	/// <remarks>
	///     With no restart on record everything is believed, and that is the right default rather than a gap: it is
	///     what a first run against a Home Assistant that has been up for weeks needs, and such an instance's
	///     timestamps are honest precisely because nothing has reset them.
	/// </remarks>
	private bool IsEvidence(DateTimeOffset stamp) =>
		_haStartedAt is not { } started || stamp > started + _options.StartupGrace;

	/// <summary>
	///     Files one sampled entity, creating its record if this is the first time it has been met.
	/// </summary>
	/// <remarks>
	///     <b>Seeding is not a special case, and deliberately so.</b> The first timestamp an entity is met with is
	///     accepted exactly when any later one would be: when it postdates the last known restart. A timestamp that
	///     survived a restart is real evidence, whether or not this process was running when it was set, so a fresh
	///     installation against a long-running Home Assistant starts with a useful record rather than a blank one.
	///     A timestamp inside the restart window is not evidence for a new entity either, so the entity starts as
	///     unknown — which is why a fresh installation cannot declare every sensor dead: the worst it can say is
	///     that it does not know, and <see cref="IEntityLastSeen.HasBeenSilentFor"/> answers <c>false</c> to that.
	/// </remarks>
	private void Record(EntitySample sample, DateTimeOffset now)
	{
		LastSeenKind kind = KindOf(sample);
		bool evidence = IsEvidence(sample.Stamp);

		if (!_entities.TryGetValue(sample.EntityId, out TrackedEntity? tracked))
		{
			_entities[sample.EntityId] = new TrackedEntity(sample.EntityId, kind, evidence ? sample.Stamp : null, now);
			_dirty.Add(kind);
			return;
		}

		if (tracked.Kind != kind)
		{
			// Moved, never copied: the old file is rewritten without it in the same flush that adds it to the new
			// one. A device class can change when an integration is updated, and a record in two files would then
			// be two divergent histories of one entity.
			_dirty.Add(tracked.Kind);
			tracked.Kind = kind;
			_dirty.Add(kind);
		}

		if (!evidence || (tracked.LastSeen is { } seen && sample.Stamp <= seen))
			return;

		tracked.LastSeen = sample.Stamp;
		_dirty.Add(kind);
	}

	private LastSeenKind KindOf(EntitySample sample) =>
		LastSeenKinds.Classify(sample.EntityId, sample.State.AttrString(DeviceClassAttribute), LabelsOf(sample.EntityId), _options);

	private IEnumerable<string>? LabelsOf(string entityId)
	{
		try
		{
			return _ha.GetEntityRegistration(entityId)?.Labels?.Select(label => label.Name).OfType<string>();
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
		{
			// NetDaemon's registry throws until its first connection completes. A missing label means a sensor is
			// filed under Other, which costs legibility and nothing else.
			_logger.LogDebug(exception, "Could not read the labels of {EntityId} for filing.", entityId);
			return null;
		}
	}

	/// <summary>
	///     Drops records that have outlived their entity.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>Only entities Home Assistant no longer reports are ever dropped.</b> A device that is merely quiet
	///         is never forgotten however long the silence lasts — the silence <i>is</i> the measurement, and
	///         evicting the record would erase exactly the finding this cache exists to make. That also makes the
	///         set self-bounding: what is present is bounded by the size of the house, so only the absent set can
	///         grow, and it is the only set either rule here touches.
	///     </para>
	///     <para>
	///         An entity removed from Home Assistant stops advancing, so its own age is its eviction timer and no
	///         "missing since" field is needed. The ceiling then catches the pathological case retention is too slow
	///         for — an instance being rebuilt, minting new entity ids faster than the old ones age out.
	///     </para>
	/// </remarks>
	private void Evict(HashSet<string> present, DateTimeOffset now)
	{
		List<string> absent = [.. _entities
			.Where(pair => !present.Contains(pair.Key))
			.OrderBy(pair => pair.Value.AgeAnchor)
			.Select(pair => pair.Key)];

		if (absent.Count == 0)
			return;

		HashSet<string> dropped = new(StringComparer.Ordinal);

		foreach (string entityId in absent)
			if (now - _entities[entityId].AgeAnchor > _options.Retention)
				dropped.Add(entityId);

		// Oldest first, which is the order retention would have taken them in anyway.
		foreach (string entityId in absent)
		{
			if (_entities.Count - dropped.Count <= _options.MaxTracked)
				break;

			dropped.Add(entityId);
		}

		if (dropped.Count == 0)
			return;

		foreach (string entityId in dropped)
			if (_entities.Remove(entityId, out TrackedEntity? tracked))
				_dirty.Add(tracked.Kind);

		_logger.LogInformation(
			"Dropped {Count} last-seen records for entities Home Assistant no longer reports ({Entities}). Entities it still "
			+ "reports are kept however quiet they are, because that quiet is the measurement.",
			dropped.Count, string.Join(", ", dropped.Take(10)));
	}

	// ---- persistence ------------------------------------------------------------------------

	/// <summary>Reads the cache. Called under the lock, from <see cref="Start"/>, exactly once.</summary>
	private void LoadCore()
	{
		LastSeenCacheLoad load = _store.Load();

		foreach (KeyValuePair<string, LoadedEntity> pair in load.Entities)
			_entities[pair.Key] = new TrackedEntity(
				pair.Key, pair.Value.Kind, pair.Value.Entry.LastSeen, pair.Value.Entry.TrackedSince);

		_haStartedAt = load.HomeAssistantStarted;

		// A file that was found in the wrong bucket, or a bucket that failed to load, leaves the set inconsistent
		// with what the next census will decide. Writing everything once settles it.
		if (load.DuplicatesResolved > 0 || load.FilesUnreadable > 0)
			foreach (LastSeenKind kind in LastSeenKinds.All)
				_dirty.Add(kind);

		if (load.FilesRead == 0)
		{
			_logger.LogInformation(
				"No last-seen cache under {Directory} yet, so every entity starts as unknown and earns a last-seen time as it "
				+ "reports. Nothing is treated as dead in the meantime.",
				_store.DirectoryPath);

			return;
		}

		_logger.LogInformation(
			"Loaded {Count} last-seen records from {Files} cache files under {Directory}; Home Assistant last started {Started}.",
			_entities.Count, load.FilesRead, _store.DirectoryPath,
			_haStartedAt is { } started ? started.ToString("u") : "at an unknown time");
	}

	/// <summary>
	///     Writes whichever buckets have changed since the last flush.
	/// </summary>
	/// <remarks>
	///     <para>
	///         One timer for the whole cache rather than one per file, so a busy motion bucket never drags an idle
	///         illuminance one into a write it did not need. Batching in time is what makes the write cost bounded:
	///         state changes arrive constantly, and a write per change would punish the card Home Assistant boots
	///         from for no gain whatsoever.
	///     </para>
	///     <para>
	///         The documents are built under the lock and written outside it, so a flush never blocks a lighting
	///         decision on a file system.
	///     </para>
	/// </remarks>
	private void Flush()
	{
		List<KeyValuePair<LastSeenKind, LastSeenDocument>> pending;

		lock (_gate)
		{
			if (_dirty.Count == 0)
				return;

			pending = [.. _dirty.Select(kind => new KeyValuePair<LastSeenKind, LastSeenDocument>(kind, BuildDocument(kind)))];
			_dirty.Clear();
		}

		foreach (KeyValuePair<LastSeenKind, LastSeenDocument> pair in pending)
			if (!_store.TrySave(pair.Key, pair.Value))
				lock (_gate)
					// Still unwritten, so still dirty: the next flush retries rather than losing the change.
					_dirty.Add(pair.Key);
	}

	private LastSeenDocument BuildDocument(LastSeenKind kind)
	{
		LastSeenDocument document = new()
		{
			Kind = kind.Token(),
			SavedAt = _scheduler.Now.ToUniversalTime(),
			HomeAssistantStarted = _haStartedAt
		};

		foreach (TrackedEntity tracked in _entities.Values)
			if (tracked.Kind == kind)
				document.Entities[tracked.EntityId] = new LastSeenEntry(tracked.LastSeen, tracked.TrackedSince);

		return document;
	}

	// ---- reading Home Assistant's clock ------------------------------------------------------

	/// <summary>
	///     The entity's Home Assistant timestamp, in UTC, never in the future.
	/// </summary>
	/// <remarks>
	///     <c>last_updated</c> rather than <c>last_changed</c>, because the two differ exactly on the case that
	///     matters: an entity re-reporting the same value with a changed attribute moves <c>last_updated</c> alone,
	///     and that is a report. The clamp is against a Home Assistant host whose clock runs ahead of this one:
	///     clamping reads such a stamp as "just now", which is what it is claiming, whereas rejecting it would make
	///     a clock-skewed installation permanently unknowable.
	/// </remarks>
	private static DateTimeOffset? StampOf(EntityState state, DateTimeOffset now)
	{
		DateTime? raw = state.LastUpdated ?? state.LastChanged;

		if (raw is not { } value)
			return null;

		DateTimeOffset stamp = value.Kind switch
		{
			DateTimeKind.Utc => new DateTimeOffset(value, TimeSpan.Zero),
			DateTimeKind.Local => new DateTimeOffset(value).ToUniversalTime(),
			// Home Assistant publishes UTC. A value that arrived without a kind is UTC that lost its label on the
			// way through the JSON reader, not a local time.
			_ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero)
		};

		return stamp > now ? now : stamp;
	}

	/// <summary>One entity as this census found it.</summary>
	private sealed record EntitySample(string EntityId, DateTimeOffset Stamp, EntityState State);

	/// <summary>One entity's record while the process is running. The disk shape is <see cref="LastSeenEntry"/>.</summary>
	private sealed class TrackedEntity(string entityId, LastSeenKind kind, DateTimeOffset? lastSeen, DateTimeOffset trackedSince)
	{
		public string EntityId { get; } = entityId;

		public LastSeenKind Kind { get; set; } = kind;

		public DateTimeOffset? LastSeen { get; set; } = lastSeen;

		public DateTimeOffset TrackedSince { get; } = trackedSince;

		/// <summary>What ageing and eviction measure from: real evidence when there is any, else when we met it.</summary>
		public DateTimeOffset AgeAnchor => LastSeen ?? TrackedSince;
	}
}
