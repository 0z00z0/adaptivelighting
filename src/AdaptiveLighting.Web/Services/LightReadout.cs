using AdaptiveLighting.Abstractions;

namespace AdaptiveLighting.Web.Services;

/// <summary>Single lights on levels of their own, in words, for the room page, the board and the activity log.</summary>
public static class LightReadout
{
	/// <summary>The lights whose last command differs from the room's, named, or <c>null</c> when none does.</summary>
	// A light stating only a night level runs the room's level in the evening, and naming it then would read as a
	// lamp set apart.
	public static string? Line(AreaSnapshot snapshot, Func<string, string>? nameOf = null)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		if (snapshot.LightLevels is not { Count: > 0 } lights)
			return null;

		string[] apart = [.. lights.Where(light => !SameAsRoom(light, snapshot)).Select(light => Describe(light, nameOf))];

		return apart.Length > 0 ? string.Join(" · ", apart) : null;
	}

	/// <summary>One light and its level: <c>Leselampe 30 %, 2200 K</c>, or <c>Leselampe off</c>.</summary>
	public static string Describe(LightStanding light, Func<string, string>? nameOf = null)
	{
		ArgumentNullException.ThrowIfNull(light);

		string name = nameOf?.Invoke(light.EntityId) is { Length: > 0 } friendly ? friendly : light.EntityId;

		if (light.BrightnessPct is not { } brightness)
			return $"{name} off";

		return light.ColorTempKelvin is { } kelvin
			? $"{name} {brightness:0} %, {kelvin} K"
			: $"{name} {brightness:0} %";
	}

	// Compared as the page prints them, whole percentages, so a light is never named beside a room showing the same number.
	private static bool SameAsRoom(LightStanding light, AreaSnapshot snapshot) =>
		light.BrightnessPct is { } brightness
		&& snapshot.BrightnessPct is { } room
		&& Math.Round(brightness) == Math.Round(room)
		&& light.ColorTempKelvin == snapshot.ColorTempKelvin;
}
