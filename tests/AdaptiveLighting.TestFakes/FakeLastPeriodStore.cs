using AdaptiveLighting.Engine;

namespace AdaptiveLighting.TestFakes;

/// <summary>The note recording which period the last run ended in, where <c>null</c> is a first run or a lost note.</summary>
public sealed class FakeLastPeriodStore(string? recalled = null) : ILastPeriodStore
{
	/// <summary>Every period written, in order.</summary>
	public List<string> Saved { get; } = [];

	public string? Load() => recalled;

	public bool TrySave(string periodName)
	{
		Saved.Add(periodName);
		return true;
	}
}
