using AdaptiveLighting.Abstractions;

namespace AdaptiveLighting.Ha;

/// <summary>
///     Raises persistent notifications in Home Assistant.
/// </summary>
/// <remarks>
///     Calls <c>persistent_notification.create</c>, not <c>notify.persistent_notification</c>, because only the
///     former takes a notification id. Re-raising the same problem then replaces its card instead of stacking one.
/// </remarks>
public sealed class HaNotifier : INotifier
{
	private const string NotificationIdPrefix = "laget_lighting_";

	private readonly IHaContext _ha;
	private readonly ILogger _logger;

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
