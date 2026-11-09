using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>The short figure beside a Test button: what Test sets, and whether the lights already show it.</summary>
public static class TestLevelText
{
	public static string Of(LightCommand command, bool shown)
	{
		string level = command.On && command.BrightnessPct is { } pct ? $"→ {DisplayRounding.WholeLit(pct)} %" : "→ off";

		if (command.On && command.ColorTempKelvin is { } kelvin)
			level += $" · {kelvin} K";

		return shown ? level + " = now" : level;
	}
}
