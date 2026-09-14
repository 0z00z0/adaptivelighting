using System.Reactive.Disposables;
using System.Text.Json;

using AdaptiveLighting.Extensions;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>Names whoever caused a light change, as closely as Home Assistant's own logbook does.</summary>
// A label and nothing more: whether a change is manual is OverrideDetector's question, never this one's.
// Automations are named off the automation_triggered event carrying the change's context id, then its parent's
// (one level, as the logbook looks). People are named off the person entity whose user_id attribute matches,
// which any token reads; the auth user list needs an administrator.
public sealed class ChangeOriginNames : IDisposable
{
	public const string AutomationTriggeredEvent = "automation_triggered";

	public const string ByTheEngine = "By adaptive lighting";

	public const string AtTheDevice = "At the device or wall switch";

	// The change arrives right behind its event, so this only has to outlast a burst.
	internal const int RememberedRuns = 256;

	private const string PersonDomain = "person";
	private const string UserIdAttribute = "user_id";
	private const string FriendlyNameAttribute = "friendly_name";

	private readonly IHaContext _ha;
	private readonly ILogger _logger;
	private readonly Lock _gate = new();
	private readonly Dictionary<string, string> _automationByContext = new(StringComparer.Ordinal);
	private readonly Queue<string> _remembered = new();
	private readonly IDisposable _subscription;

	public ChangeOriginNames(IHaContext ha, ILogger logger)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

		try
		{
			_subscription = ha.Events
				.Where(@event => string.Equals(@event.EventType, AutomationTriggeredEvent, StringComparison.Ordinal))
				.SubscribeSafe(Remember, logger);
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
		{
			logger.LogWarning(exception, "Could not watch automation runs, so a change an automation makes is not named.");
			_subscription = Disposable.Empty;
		}
	}

	/// <summary>Who caused a change, in the activity log's words, or <c>null</c> when no name can be put to it.</summary>
	public string? Describe(ChangeOrigin origin, Context? context) => origin switch
	{
		ChangeOrigin.Self => ByTheEngine,
		ChangeOrigin.PhysicalDevice => AtTheDevice,
		ChangeOrigin.Automation => AutomationName(context) is { } automation ? $"By automation: {automation}" : null,
		ChangeOrigin.HaUser => PersonName(context?.UserId) is { } person ? $"By {person}" : null,
		_ => null
	};

	private void Remember(Event @event)
	{
		if (@event.Context?.Id is not { Length: > 0 } contextId || NameOf(@event) is not { } name)
			return;

		lock (_gate)
		{
			if (_automationByContext.TryAdd(contextId, name))
				_remembered.Enqueue(contextId);
			else
				_automationByContext[contextId] = name;

			while (_remembered.Count > RememberedRuns)
				_automationByContext.Remove(_remembered.Dequeue());
		}
	}

	// The event's own name first; an automation with no alias has its entity's friendly name there anyway.
	private string? NameOf(Event @event)
	{
		if (@event.DataElement is not { ValueKind: JsonValueKind.Object } data)
			return null;

		if (data.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String
			&& name.GetString() is { Length: > 0 } named)
			return named;

		return data.TryGetProperty("entity_id", out JsonElement entity) && entity.ValueKind == JsonValueKind.String
			&& entity.GetString() is { Length: > 0 } entityId
			? _ha.AttrString(entityId, FriendlyNameAttribute) ?? entityId
			: null;
	}

	private string? AutomationName(Context? context)
	{
		if (context is null)
			return null;

		lock (_gate)
		{
			if (context.Id is { Length: > 0 } id && _automationByContext.TryGetValue(id, out string? own))
				return own;

			return context.ParentId is { Length: > 0 } parent && _automationByContext.TryGetValue(parent, out string? caller)
				? caller
				: null;
		}
	}

	private string? PersonName(string? userId)
	{
		if (userId is not { Length: > 0 })
			return null;

		try
		{
			foreach (Entity entity in _ha.GetAllEntities())
			{
				if (entity.EntityId.HasDomain(PersonDomain)
					&& string.Equals(_ha.AttrString(entity.EntityId, UserIdAttribute), userId, StringComparison.Ordinal))
					return _ha.AttrString(entity.EntityId, FriendlyNameAttribute) ?? entity.EntityId;
			}
		}
		catch (InvalidOperationException exception)
		{
			// NetDaemon's state cache throws until its first connection completes.
			_logger.LogDebug(exception, "Could not read the person entities to name a user.");
		}

		return null;
	}

	public void Dispose() => _subscription.Dispose();
}
