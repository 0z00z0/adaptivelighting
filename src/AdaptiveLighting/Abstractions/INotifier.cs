namespace AdaptiveLighting.Abstractions;

/// <summary>
///     How the engine tells a human something it cannot fix itself. Reserved for exactly that: a notification
///     per zone transition would train the household to ignore all of them.
/// </summary>
public interface INotifier
{
	/// <summary>Raises a notification. <paramref name="message"/> may contain HTML.</summary>
	void Notify(string title, string message);
}
