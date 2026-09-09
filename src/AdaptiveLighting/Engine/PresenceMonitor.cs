using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Subjects;

using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>The presence transitions the house reacts to; steady state is read from <see cref="PresenceMonitor.IsAnyoneHome"/>.</summary>
public enum PresenceEvent
{
	/// <summary>The last person left, and stayed gone for the debounce.</summary>
	EveryoneLeft,

	/// <summary>Somebody came home to an empty house.</summary>
	FirstPersonArrived
}

/// <summary>Watches the configured people and the managed rooms' movement, and reports the two transitions that matter.</summary>
// Leaving is debounced, arriving is not, and that holds for movement as much as for a tracker.
public sealed class PresenceMonitor : IDisposable
{
	private const string PersonDomain = "person";
	private const string HomeState = "home";

	private readonly IHaContext _ha;
	private readonly IScheduler _scheduler;
	private readonly GlobalConfig _global;
	private readonly ILogger _logger;
	private readonly Subject<PresenceEvent> _events = new();
	private readonly CompositeDisposable _subscriptions = [];
	private readonly SerialDisposable _awayTimer = new();

	// Replaced by every movement, never stacked: the window is refreshed, not extended by one per sensor.
	private readonly SerialDisposable _motionExpiry = new();
	private readonly object _gate = new();
	private readonly IReadOnlyList<string> _personEntityIds;
	private readonly IReadOnlyCollection<string> _motionSensors;
	private readonly TimeSpan _motionWindow;

	private bool _isAnyoneHome = true;
	private bool _awayAnnounced;
	private DateTimeOffset? _lastMotionAt;

	// motionSensors is the union across every managed area. Movement on any of them counts as somebody being home
	// for GlobalConfig.MotionPresenceMinutes; an empty collection or a window of zero watches trackers only.
	public PresenceMonitor(
		IHaContext ha,
		IScheduler scheduler,
		GlobalConfig global,
		ILogger logger,
		IReadOnlyCollection<string>? motionSensors = null)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
		_global = global ?? throw new ArgumentNullException(nameof(global));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

		_personEntityIds = ResolvePersons();
		_motionSensors = motionSensors ?? [];
		_motionWindow = TimeSpan.FromMinutes(Math.Max(0, _global.MotionPresenceMinutes));
	}

	/// <summary>The transitions, hot: subscribe before calling <see cref="Start"/>.</summary>
	public IObservable<PresenceEvent> Events => _events;

	/// <summary>Whether anybody is home right now, flipping the instant a tracker or a movement says so and before any debounce.</summary>
	public bool IsAnyoneHome
	{
		get { lock (_gate) return _isAnyoneHome; }
	}

	/// <summary>The people being watched, after discovery; empty means presence cannot be determined.</summary>
	public IReadOnlyList<string> WatchedEntityIds => _personEntityIds;

	/// <summary>The motion sensors whose movement counts as somebody being home; empty when the rule is off.</summary>
	public IReadOnlyCollection<string> WatchedMotionSensors =>
		_motionWindow > TimeSpan.Zero ? _motionSensors : [];

	public void Start()
	{
		if (_personEntityIds.Count == 0)
		{
			// No one to watch means permanently occupied. Never sweeping is safer than sweeping wrongly, and
			// movement cannot help: with nothing to compose against, a quiet house would start sweeping itself.
			_logger.LogWarning("No person entities configured or discovered; presence is assumed permanently home.");
			return;
		}

		lock (_gate)
		{
			_isAnyoneHome = _personEntityIds.Any(IsHome);

			// The opening publication tells every area the house is away, so an empty start counts as announced.
			// Left false, the first arrival is swallowed as a return inside a debounce that never ran, and an
			// engine started in an empty house never comes home at all.
			_awayAnnounced = !_isAnyoneHome;
		}

		_logger.LogInformation("Watching presence for {Count} entities: {Entities}. Anyone home: {IsAnyoneHome}.",
			_personEntityIds.Count, string.Join(", ", _personEntityIds), _isAnyoneHome);

		foreach (string entityId in _personEntityIds)
			_subscriptions.Add(_ha.Entity(entityId)
				.StateChanges()
				.SubscribeSafe(_ => Reevaluate(), _logger));

		SubscribeMotion();
	}

	// Subscribed before every area's own motion handler, because the orchestrator starts the house monitors first.
	// That order is what lets one movement flip presence, republish the house and reach the room while it is
	// already out of Away, so a person waves once rather than twice.
	private void SubscribeMotion()
	{
		if (_motionWindow <= TimeSpan.Zero)
		{
			_logger.LogInformation("Movement does not count as presence; only the watched trackers do.");
			return;
		}

		if (_motionSensors.Count == 0)
		{
			_logger.LogInformation(
				"Movement would count as presence, but no managed room has a motion sensor; only the watched trackers do.");
			return;
		}

		_logger.LogInformation(
			"Movement in {Count} managed rooms counts as somebody being home for {Minutes} minutes after the last movement.",
			_motionSensors.Count, _global.MotionPresenceMinutes);

		foreach (string sensor in _motionSensors)
			_subscriptions.Add(_ha.Entity(sensor).WhenTurnsOn(_ => OnMotion(), _logger));
	}

	private void OnMotion()
	{
		lock (_gate)
		{
			_lastMotionAt = _scheduler.Now;
			_motionExpiry.Disposable = _scheduler.Schedule(_motionWindow, Reevaluate);
		}

		Reevaluate();
	}

	// Under _gate. A window of zero is the rule switched off, and an unseen movement is not fresh.
	private bool MotionIsFresh(DateTimeOffset now) =>
		_motionWindow > TimeSpan.Zero
		&& _lastMotionAt is { } seen
		&& now - seen < _motionWindow;

	private IReadOnlyList<string> ResolvePersons()
	{
		if (_global.Persons.Count > 0)
			return [.. _global.Persons];

		return _ha.EntityIdsInDomain(PersonDomain);
	}

	private bool IsHome(string entityId) => _ha.StateIs(entityId, HomeState);

	// Both of these decide under _gate and publish outside it. Subscribers run arbitrary code.
	private void Reevaluate()
	{
		// Before the lock: this consults Home Assistant.
		bool trackerHome = _personEntityIds.Any(IsHome);
		DateTimeOffset now = _scheduler.Now;
		bool announceArrival = false;

		lock (_gate)
		{
			// A tracker and a recent movement are the same claim, so either one alone holds the house occupied.
			bool anyoneHome = trackerHome || MotionIsFresh(now);

			if (anyoneHome == _isAnyoneHome)
				return;

			_isAnyoneHome = anyoneHome;

			if (anyoneHome)
			{
				_awayTimer.Disposable = Disposable.Empty;

				// Returning before the debounce elapsed means nobody ever left.
				announceArrival = _awayAnnounced;
				_awayAnnounced = false;
			}
			else
			{
				_logger.LogInformation("Everyone appears to have left; confirming in {Minutes} minutes.", _global.AwayDebounceMinutes);
				_awayTimer.Disposable = _scheduler.Schedule(TimeSpan.FromMinutes(_global.AwayDebounceMinutes), ConfirmAway);
			}
		}

		if (!announceArrival)
			return;

		_logger.LogInformation("First person arrived.");
		_events.OnNext(PresenceEvent.FirstPersonArrived);
	}

	// _isAnyoneHome is the composed verdict, so a movement inside the debounce has already set it true and this
	// finds an occupied house. Nothing here needs to know which of the two claims put it there.
	private void ConfirmAway()
	{
		lock (_gate)
		{
			if (_isAnyoneHome || _awayAnnounced)
				return;

			_awayAnnounced = true;
		}

		_logger.LogInformation("Everyone left.");
		_events.OnNext(PresenceEvent.EveryoneLeft);
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		_subscriptions.Dispose();
		_awayTimer.Dispose();
		_motionExpiry.Dispose();
		_events.Dispose();
	}
}
