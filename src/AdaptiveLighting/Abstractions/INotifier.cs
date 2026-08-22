namespace AdaptiveLighting.Abstractions;

/// <summary>How the engine tells a human something it cannot fix itself.</summary>
public interface INotifier
{
	/// <remarks><paramref name="message"/> may contain HTML.</remarks>
	void Notify(string title, string message);
}
