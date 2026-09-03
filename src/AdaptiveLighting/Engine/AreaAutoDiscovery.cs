using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>Proposes a starting set of areas from the rooms the area registry says could plausibly be lit.</summary>
// Only AreaId and Enabled are written; everything else resolves at run time, so a proposal survives a rename.
public static class AreaAutoDiscovery
{
	public static IReadOnlyList<AreaConfig> Propose(IAreaRegistry registry, AreaEntityResolver resolver)
	{
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(resolver);

		List<AreaConfig> proposed = [];

		foreach (string areaId in registry.AreaIds)
		{
			if (string.IsNullOrWhiteSpace(areaId))
				continue;

			// The run-time resolver, so group members and excluded entities are already out of these counts.
			AreaDiscovery found = resolver.DiscoverArea(areaId);

			// Lights alone qualify. A room with no motion sensor never lights itself, but it still runs: the wall
			// switch drives it and the manual hold ends it. A room with no lights has nothing to command.
			if (found.Lights.Count > 0)
			{
				// Explicit false on the area, never a flipped Defaults.Enabled: that would switch off every area
				// in a document that never wrote the value.
				AreaConfig area = new() { AreaId = areaId, Enabled = false };
				ApplyRole(area);
				proposed.Add(area);
			}
		}

		return proposed;
	}

	// Room roles guessed from the area id, which is a slug of the name people actually use.
	private static readonly string[] Bedroom = ["sov", "soverom", "bedroom", "seng", "sengerom"];
	private static readonly string[] Bathroom = ["bad", "wc", "toalett", "bathroom", "dusj"];
	private static readonly string[] Entrance = ["gang", "inngang", "entre", "hall", "hallway", "entrance", "korridor", "trapp"];
	private static readonly string[] Outdoor = ["ute", "uteplass", "terrasse", "veranda", "hage", "garasje", "garage", "outdoor", "garden", "porch", "carport"];

	/// <summary>Gives a proposed area the behaviour its name implies, returning what it decided.</summary>
	// Only ever sets a flag on. Everything untouched keeps following Defaults.
	internal static string? ApplyRole(AreaConfig area)
	{
		if (area.AreaId is not { Length: > 0 } areaId)
			return null;

		List<string> roles = [];

		if (Matches(areaId, Bedroom))
		{
			area.RespectSleepMode = true;
			area.SleepBlocksAutoOn = true;
			roles.Add("bedroom");
		}
		else if (Matches(areaId, Bathroom))
		{
			// Dim at night, but SleepBlocksAutoOn stays off: a 03:00 trip must still light.
			area.RespectSleepMode = true;
			roles.Add("bathroom");
		}

		if (Matches(areaId, Entrance))
		{
			area.WelcomeHome = true;
			area.RespectSleepMode = true;
			roles.Add("entrance");
		}

		if (Matches(areaId, Outdoor))
		{
			// Porch and garage lights are wanted when nobody is home.
			area.SkipAwaySweep = true;
			roles.Add("outdoor");
		}

		return roles.Count > 0 ? string.Join("+", roles) : null;
	}

	// Per underscore-separated segment, not substring: inngang_ute matches "ute", utleieleilighet does not. Tokens
	// of four or more also match the end of a compound segment, which is what catches kjellergang.
	private static bool Matches(string areaId, string[] tokens)
	{
		string[] segments = areaId.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries);

		return segments.Any(segment => tokens.Any(token =>
			string.Equals(segment, token, StringComparison.Ordinal)
			|| (token.Length >= 4 && segment.EndsWith(token, StringComparison.Ordinal))));
	}
}
