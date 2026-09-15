namespace AdaptiveLighting.TestFakes;

/// <summary>A scope that does nothing, for a logger call that needs one to satisfy the interface.</summary>
public sealed class NullScope : IDisposable
{
	public static readonly NullScope Instance = new();

	public void Dispose()
	{
	}
}
