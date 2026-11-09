namespace AdaptiveLighting.Engine;

/// <summary>What a level test of one period sends each light now, groups followed down, and which already show it.</summary>
public sealed record LevelTestLevels(LightCommand Room, IReadOnlyDictionary<string, LightLevelNow> Lights)
{
	public bool AllShown => Lights.Count > 0 && Lights.Values.All(light => light.Shown);
}

public sealed record LightLevelNow(LightCommand Command, bool Shown);
