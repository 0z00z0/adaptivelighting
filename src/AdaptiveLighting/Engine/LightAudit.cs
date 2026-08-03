namespace AdaptiveLighting.Engine;

/// <summary>
///     One light as the audit reads it: the id the engine would command, and the name a person would recognise.
///     <c>Name</c> is the <c>friendly_name</c>, or the entity id when it has none.
/// </summary>
public sealed record LightUnderReview(string EntityId, string Name);

/// <summary>
///     One light the audit is not convinced is a light, and why, in a person's words. <c>Reason</c> is never a
///     rule id: the reader has to be able to judge the guess.
/// </summary>
public sealed record SuspectLight(string EntityId, string Name, string Reason);

/// <summary>
///     One room as the cross-room half of the audit reads it. <c>Lights</c> holds bulbs with groups already
///     followed down to their members, because two rooms reaching one bulb through two different groups settle on
///     two different ids and have nothing in common to compare.
/// </summary>
public sealed record RoomUnderReview(string Room, IReadOnlyList<LightUnderReview> Lights);

/// <summary>
///     Looks over the lights a room would command and points at the ones that are probably not room lighting.
/// </summary>
/// <remarks>
///     Advice, never a filter. Home Assistant's <c>entity_category</c>, the field that would settle it, is not
///     exposed by HassModel 26.21.0, so everything here is a heuristic on an id and a name. The rules are
///     asymmetric: a word that accuses must match a whole word, while a word that excuses need only appear inside
///     one, because Norwegian writes its lamps as <c>taklys</c> and <c>benkbelysning</c>.
/// </remarks>
public static class LightAudit
{
	// Trusted against the friendly name as well as the id, and run first: light.lab_taklys_status_led carries
	// "taklys" and the lamp guard below would otherwise excuse it.
	private static readonly string[] StatusWords = ["status", "indicator", "indikator"];

	// These stand the _led rule down. An LED strip, spot or panel is a real light.
	private static readonly string[] LampWords =
	[
		"lys", "lampe", "lamp", "light", "strip", "stripe", "spot", "bulb", "pære", "paere", "downlight", "pendel"
	];

	// Things that plug in and happen to expose a light. Short and specific. Bare "oven" is not here: it is
	// Norwegian for "above", so light.oven_gang is the upstairs hallway.
	private static readonly string[] ApplianceWords =
	[
		"kjoleskap", "kjoeleskap", "kjøleskap", "fridge", "refrigerator", "freezer", "fryser",
		"stekeovn", "microwave", "mikrobolgeovn", "mikrobølgeovn",
		"dishwasher", "oppvaskmaskin", "vaskemaskin", "torketrommel", "tørketrommel",
		"printer"
	];

	// WiZ publishes a bulb as itself plus one entity per channel, and commanding the channels fights the lamp.
	// Flagged only when the parent is in the same room: a one-letter suffix alone is too thin to accuse on.
	private static readonly string[] ChannelSuffixes = ["_r", "_g", "_b", "_w", "_on_off"];

	/// <summary>
	///     The lights in <paramref name="lights"/> that look like something other than room lighting, in the order
	///     given. Empty is the usual answer.
	/// </summary>
	public static IReadOnlyList<SuspectLight> Review(IReadOnlyList<LightUnderReview> lights)
	{
		ArgumentNullException.ThrowIfNull(lights);

		HashSet<string> present = new(lights.Select(light => light.EntityId), StringComparer.OrdinalIgnoreCase);
		List<SuspectLight> suspects = [];

		foreach (LightUnderReview light in lights)
			if (ReasonFor(light, present) is { Length: > 0 } reason)
				suspects.Add(new SuspectLight(light.EntityId, light.Name, reason));

		return suspects;
	}

	/// <summary>
	///     Why <paramref name="light"/> looks wrong, or <c>null</c> when it looks like a light.
	///     <paramref name="present"/> is every entity id in the same room, for the colour-channel sibling check.
	/// </summary>
	/// <remarks>
	///     The order is load-bearing: the status rule runs before the lamp guard, so a ceiling light's status LED
	///     is caught by the word describing the entity and not excused by the word describing its device.
	/// </remarks>
	public static string? ReasonFor(LightUnderReview light, IReadOnlySet<string> present)
	{
		ArgumentNullException.ThrowIfNull(light);
		ArgumentNullException.ThrowIfNull(present);

		string id = ObjectIdOf(light.EntityId);

		// A light with no friendly name arrives named by its entity id, whose domain is the word "light", and the
		// lamp guard would read that as a description and excuse every hardware LED in the house.
		string name = string.Equals(light.Name, light.EntityId, StringComparison.Ordinal) ? id : Normalise(light.Name);

		if (HasWord(id, StatusWords) || HasWord(name, StatusWords))
			return "named as a status light, which reports on a device rather than lighting a room";

		if (ChannelParentOf(light.EntityId, present) is { Length: > 0 } parent)
			return $"one colour channel of {ObjectIdOf(parent)}, which this room already commands as a whole lamp";

		// Off the id, not the name: a trailing "_led" in a generated slug is a convention, not a description.
		if (id.EndsWith("_led", StringComparison.Ordinal) && !SuggestsLamp(id) && !SuggestsLamp(name))
			return "the name ends in LED, the usual mark of a hardware status light";

		if (HasWord(id, ApplianceWords) || HasWord(name, ApplianceWords))
			return "named after an appliance, so this is likely a light inside a machine";

		return null;
	}

	/// <summary>
	///     The bulbs more than one room will command, because Home Assistant has not put them in a room at all.
	/// </summary>
	/// <remarks>
	///     The gap the per-area rules cannot see. <see cref="AreaEntityResolver"/> drops a group reaching into
	///     another area and settles overlapping groups within one area; neither catches a bulb belonging to no
	///     area, so both rooms resolve it and both command it. One entry per bulb, never one per room.
	///     <paramref name="hasOwnArea"/> is asked only about bulbs that already failed the cheap test, because
	///     answering it sweeps the registry.
	/// </remarks>
	public static IReadOnlyList<SuspectLight> SharedBetweenRooms(
		IReadOnlyList<RoomUnderReview> rooms,
		Func<string, bool> hasOwnArea)
	{
		ArgumentNullException.ThrowIfNull(rooms);
		ArgumentNullException.ThrowIfNull(hasOwnArea);

		Dictionary<string, (LightUnderReview Light, List<string> Rooms)> commanded = new(StringComparer.Ordinal);
		List<string> order = [];

		foreach (RoomUnderReview room in rooms)
			foreach (LightUnderReview light in room.Lights)
			{
				if (!commanded.TryGetValue(light.EntityId, out (LightUnderReview Light, List<string> Rooms) seen))
				{
					commanded[light.EntityId] = seen = (light, []);
					order.Add(light.EntityId);
				}

				// One room holding the same bulb twice, through a group and again on its own, is one room.
				if (!seen.Rooms.Contains(room.Room, StringComparer.Ordinal))
					seen.Rooms.Add(room.Room);
			}

		List<SuspectLight> shared = [];

		foreach (string entityId in order)
		{
			(LightUnderReview light, List<string> claimants) = commanded[entityId];

			if (claimants.Count < 2 || hasOwnArea(entityId))
				continue;

			shared.Add(new SuspectLight(entityId, light.Name, SharedReason(claimants)));
		}

		return shared;
	}

	// Lower case and no full stop: every reason here is read as the second half of a line starting with the
	// light's own name.
	private static string SharedReason(IReadOnlyList<string> rooms) =>
		$"commanded by {Join(rooms)} at once, because Home Assistant has not put it in a room of its own; "
		+ "give it an area there and only that room will switch it on and off";

	private static string Join(IReadOnlyList<string> names) => names.Count switch
	{
		1 => names[0],
		2 => $"{names[0]} and {names[1]}",
		_ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}"
	};

	/// <summary>The entity this one is a colour channel of, when that entity is in the room too.</summary>
	private static string? ChannelParentOf(string entityId, IReadOnlySet<string> present)
	{
		foreach (string suffix in ChannelSuffixes)
		{
			if (!entityId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				continue;

			string parent = entityId[..^suffix.Length];

			// A bare "light._r" has no parent to be a channel of, and a parent nobody commands is not a duplicate.
			if (ObjectIdOf(parent).Length > 0 && present.Contains(parent))
				return parent;
		}

		return null;
	}

	// The normalised part after the domain: light.stue_taklys becomes stue_taklys.
	private static string ObjectIdOf(string entityId)
	{
		int dot = entityId.IndexOf('.', StringComparison.Ordinal);

		return Normalise(dot >= 0 ? entityId[(dot + 1)..] : entityId);
	}

	// Lower-cased, non-alphanumerics to underscores, so "Status LED" and status_led are the same two words.
	private static string Normalise(string text)
	{
		char[] letters = new char[text.Length];

		for (int index = 0; index < text.Length; index++)
			letters[index] = char.IsLetterOrDigit(text[index]) ? char.ToLowerInvariant(text[index]) : '_';

		return new string(letters);
	}

	// The accusing half: whole underscore-separated words, never substrings. "oppvask" (dishwasher) lives inside
	// oppvaskbenk_lys, the light over the sink.
	private static bool HasWord(string normalised, string[] words) =>
		Words(normalised).Any(segment => words.Contains(segment, StringComparer.Ordinal));

	// The excusing half, generous where HasWord is strict: Norwegian buries the word inside the compound, as in
	// taklys and benkbelysning.
	private static bool SuggestsLamp(string normalised) =>
		LampWords.Any(word => normalised.Contains(word, StringComparison.Ordinal));

	private static string[] Words(string normalised) => normalised.Split('_', StringSplitOptions.RemoveEmptyEntries);
}
