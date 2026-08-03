namespace AdaptiveLighting.Abstractions;

/// <summary>How the engine tells a human something it cannot fix itself. Reserved for that alone.</summary>
public interface INotifier
{
	/// <summary>Raises a notification. <paramref name="message"/> may contain HTML.</summary>
	void Notify(string title, string message);
}
