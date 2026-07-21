using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>
///     Proposes a starting set of zones by asking the Home Assistant area registry which rooms could plausibly be
///     lit automatically.
/// </summary>
/// <remarks>
///     <para>
///         This exists because the alternative was worse. A shipped example full of placeholder ids reads as
///         helpful and behaves as sabotage: every id is one Home Assistant does not know, so a new installation
///         starts refusing to run and the owner's first experience of the system is a list of errors about rooms
///         that were never theirs. Starting empty is honest but useless. Starting empty and then <i>looking</i> is
///         both.
///     </para>
///     <para>
///         The test for "could this room be lit automatically" is deliberately strict: it needs <b>at least one
///         light and at least one motion sensor</b>. A room with lights but nothing to sense presence cannot
///         participate in motion-driven lighting, and a room with motion but no lights has nothing to offer. Every
///         other area — a cupboard with a temperature probe, a "system" area holding a router — is left alone.
///         Being conservative matters more than being complete: a missed room is one the owner adds in a moment
///         from the UI, whereas an unwanted room is lights coming on in a bedroom at 03:00.
///     </para>
///     <para>
///         Only <see cref="ZoneConfig.AreaId"/> is set. Everything else — which lights, which sensors, the display
///         name — resolves from the area at run time, so a proposal stays true across a rename and the document
///         stays small enough to read.
///     </para>
/// </remarks>
public static class ZoneAutoDiscovery
{
	/// <summary>
	///     The zones worth proposing for this instance, in registry order.
	/// </summary>
	/// <param name="registry">Source of the area list.</param>
	/// <param name="resolver">Classifies each area's entities, applying the same exclusions real discovery uses.</param>
	/// <returns>A zone per qualifying area, naming only its area id. Empty when nothing qualifies.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static IReadOnlyList<ZoneConfig> Propose(IAreaRegistry registry, ZoneEntityResolver resolver)
	{
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(resolver);

		List<ZoneConfig> proposed = [];

		foreach (string areaId in registry.AreaIds)
		{
			if (string.IsNullOrWhiteSpace(areaId))
				continue;

			// The same resolver the engine uses at run time, so a proposed zone is one that will actually resolve —
			// group members and excluded entities are already filtered out of these counts.
			AreaDiscovery found = resolver.DiscoverArea(areaId);

			if (found.Lights.Count > 0 && found.MotionSensors.Count > 0)
			{
				ZoneConfig zone = new() { AreaId = areaId };
				ApplyRole(zone);
				proposed.Add(zone);
			}
		}

		return proposed;
	}

	// Room roles guessed from the area id. Area ids are slugs of the names people actually use, so "soverom",
	// "gang" and "terrasse" carry real information about how a room should behave — and getting one wrong is
	// cheap: a bedroom that does not dim, or a porch swept off when the house empties, is one checkbox on the
	// Configuration page. That asymmetry is what makes guessing worthwhile here and not for the lux sensor.
	private static readonly string[] Bedroom = ["sov", "soverom", "bedroom", "seng", "sengerom"];
	private static readonly string[] Bathroom = ["bad", "wc", "toalett", "bathroom", "dusj"];
	private static readonly string[] Entrance = ["gang", "inngang", "entre", "hall", "hallway", "entrance", "korridor", "trapp"];
	private static readonly string[] Outdoor = ["ute", "uteplass", "terrasse", "veranda", "hage", "garasje", "garage", "outdoor", "garden", "porch", "carport"];

	/// <summary>
	///     Gives a proposed zone the behaviour its name implies, and returns what it decided (for logging).
	/// </summary>
	/// <remarks>
	///     Only ever sets a flag <i>on</i>. Everything left alone keeps following <c>Defaults</c>, so a guess adds
	///     behaviour rather than overriding a choice the household has made elsewhere.
	/// </remarks>
	internal static string? ApplyRole(ZoneConfig zone)
	{
		if (zone.AreaId is not { Length: > 0 } areaId)
			return null;

		List<string> roles = [];

		if (Matches(areaId, Bedroom))
		{
			// Somewhere people sleep: hold it to night levels, and never light itself.
			zone.RespectSleepMode = true;
			zone.SleepBlocksAutoOn = true;
			roles.Add("bedroom");
		}
		else if (Matches(areaId, Bathroom))
		{
			// A 03:00 trip should get a dim light, not a bright one - but it must still light.
			zone.RespectSleepMode = true;
			roles.Add("bathroom");
		}

		if (Matches(areaId, Entrance))
		{
			zone.WelcomeHome = true;
			zone.RespectSleepMode = true;
			roles.Add("entrance");
		}

		if (Matches(areaId, Outdoor))
		{
			// Porch, terrace and garage lights are wanted precisely when nobody is home.
			zone.SkipAwaySweep = true;
			roles.Add("outdoor");
		}

		return roles.Count > 0 ? string.Join("+", roles) : null;
	}

	/// <summary>
	///     Whether an area id names one of <paramref name="tokens"/>.
	/// </summary>
	/// <remarks>
	///     Matched per underscore-separated segment rather than as a substring, so <c>inngang_ute</c> matches
	///     "ute" while <c>utleieleilighet</c> does not accidentally. Longer tokens may also match the end of a
	///     compound segment, which is what catches <c>kjellergang</c>.
	/// </remarks>
	private static bool Matches(string areaId, string[] tokens)
	{
		string[] segments = areaId.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries);

		return segments.Any(segment => tokens.Any(token =>
			string.Equals(segment, token, StringComparison.Ordinal)
			|| (token.Length >= 4 && segment.EndsWith(token, StringComparison.Ordinal))));
	}
}
