using AdaptiveLighting.Abstractions;

namespace AdaptiveLighting.Ha;

/// <summary>
///     Raises persistent notifications in Home Assistant.
/// </summary>
/// <remarks>
///     Uses <c>persistent_notification.create</c> rather than <c>notify.persistent_notification</c> so a
///     notification id can be supplied: the engine's news is always "here is the current state of the problem",
///     and re-raising it should replace the last one rather than stack up another card.
/// </remarks>
public sealed class HaNotifier : INotifier
{
	private const string NotificationIdPrefix = "laget_lighting_";

	private readonly IHaContext _ha;
	private readonly ILogger _logger;

	/// <summary>Creates a notifier.</summary>
	/// <param name="ha">Where the service call goes.</param>
	/// <param name="logger">Diagnostics. Anything worth notifying about is worth logging too.</param>
	public HaNotifier(IHaContext ha, ILogger logger)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <inheritdoc/>
	public void Notify(string title, string message)
	{
		_logger.LogWarning("Notifying: {Title} — {Message}", title, message);

		_ha.NotifyPersistent(title, message, NotificationIdPrefix + Slug(title));
	}

	/// <summary>Derives a stable notification id from the title, so the same problem keeps replacing itself.</summary>
	private static string Slug(string title) =>
		string.Concat(title.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '_'));
}
