namespace AdaptiveLighting.Engine;

/// <summary>
///     One light as the audit reads it: the id the engine would command, and the name a person would recognise.
/// </summary>
/// <param name="EntityId">The <c>light.*</c> entity id.</param>
/// <param name="Name">
///     Home Assistant's <c>friendly_name</c>, or the entity id when it has none. Read as well as the id because a
///     household renames the thing it can see, and an access point renamed "Stue" should stop reading as a lamp.
/// </param>
public sealed record LightUnderReview(string EntityId, string Name);

/// <summary>
///     One light the audit is not convinced is a light, and why, in a person's words.
/// </summary>
/// <param name="EntityId">The entity id.</param>
/// <param name="Name">Its friendly name.</param>
/// <param name="Reason">
///     What made it suspect, written to be read beside the name. Never a rule id: the reader has to be able to
///     judge the guess, because a guess is all this is.
/// </param>
public sealed record SuspectLight(string EntityId, string Name, string Reason);

/// <summary>
///     Looks over the lights a room would command and points at the ones that are probably not room lighting.
/// </summary>
/// <remarks>
///     <para>
///         <b>Advice, never a filter.</b> One live house has a <c>stue</c> area that resolves to 34 lights, among
///         them three Ubiquiti access-point status LEDs, four relay- and dev-board indicators, five WiZ colour
///         channels of a lamp the room already commands under its own name, and a fridge. Switching that room on
///         commands all of them. But Home Assistant's <c>entity_category</c> — the field that would settle it — is
///         not exposed by HassModel 26.21.0 (<c>EntityRegistration</c> offers <c>Id</c>, <c>Name</c>, <c>Area</c>,
///         <c>Device</c>, <c>Labels</c>, <c>Platform</c> and <c>Options</c>, and no category), so everything here
///         is a heuristic on an id and a name. A heuristic may point; it may never quietly drop a light. The
///         household knows its own house and this does not.
///     </para>
///     <para>
///         <b>The failure that matters is the false positive.</b> Somebody talked out of managing a real lamp
///         because this called it an indicator is worse off than before, and has no way to tell they were misled.
///         So the rules are asymmetric on purpose: a word that <i>accuses</i> has to match a whole word, while a
///         word that <i>excuses</i> need only appear inside one — Norwegian writes its lamps as <c>taklys</c>,
///         <c>vegglampe</c> and <c>benkbelysning</c>, and a guard that missed those would flag the ceiling light.
///     </para>
///     <para>
///         Pure, and expected to be tuned. These are one house's patterns; somebody reading them against their own
///         house should be able to see what each rule is for and change it.
///     </para>
/// </remarks>
public static class LightAudit
{
	/// <summary>
	///     Words that only ever describe a device reporting on itself.
	/// </summary>
	/// <remarks>
	///     No room lamp is called a status or an indicator, in either language this house speaks, so this is the
	///     one rule trusted against the friendly name as well as the id, and the one that runs first. It is what
	///     catches <c>light.lab_taklys_status_led</c> and <c>light.lab_taklys_indikator</c> — both of which carry
	///     <i>taklys</i> (ceiling light) and would otherwise be excused by the lamp guard below.
	/// </remarks>
	private static readonly string[] StatusWords = ["status", "indicator", "indikator"];

	/// <summary>
	///     Words that say a thing is a lamp, which stand the <c>_led</c> rule down.
	/// </summary>
	/// <remarks>
	///     The guard for the false positive that matters. An LED strip, an LED spot and an LED panel are real
	///     lights whose names carry the same three letters as an access point's indicator; a name that says what
	///     it illuminates has already answered the question the suffix was asking.
	/// </remarks>
	private static readonly string[] LampWords =
	[
		"lys", "lampe", "lamp", "light", "strip", "stripe", "spot", "bulb", "pære", "paere", "downlight", "pendel"
	];

	/// <summary>
	///     Things that plug in and happen to expose a light.
	/// </summary>
	/// <remarks>
	///     A fridge's interior bulb is the live case (<c>light.kjoleskap_colour_light</c>), and it is the same
	///     class of mistake as the illuminance sensor inside the same fridge that <c>AreaEntityResolver</c>
	///     already has a story about. Deliberately short and specific. Bare <c>oven</c> is <b>not</b> here even
	///     though the appliance is: <i>oven</i> is Norwegian for "above", so <c>light.oven_gang</c> is the upstairs
	///     hallway, and a list that accused it would be exactly the false positive this class is careful about.
	/// </remarks>
	private static readonly string[] ApplianceWords =
	[
		"kjoleskap", "kjoeleskap", "kjøleskap", "fridge", "refrigerator", "freezer", "fryser",
		"stekeovn", "microwave", "mikrobolgeovn", "mikrobølgeovn",
		"dishwasher", "oppvaskmaskin", "vaskemaskin", "torketrommel", "tørketrommel",
		"printer"
	];

	/// <summary>
	///     Suffixes a colour-capable lamp is split into by some integrations.
	/// </summary>
	/// <remarks>
	///     WiZ publishes a bulb as itself plus one entity per channel — <c>_r</c>, <c>_g</c>, <c>_b</c>, <c>_w</c>
	///     and an <c>_on_off</c> — so a room holding <c>light.stue_vegglys</c> also holds five sub-entities of it,
	///     and commanding the channels alongside the lamp fights the lamp. Flagged only when the parent is in the
	///     same room: a one-letter suffix on its own is far too thin a thing to accuse a light over.
	/// </remarks>
	private static readonly string[] ChannelSuffixes = ["_r", "_g", "_b", "_w", "_on_off"];

	/// <summary>
	///     The lights in <paramref name="lights"/> that look like something other than room lighting.
	/// </summary>
	/// <param name="lights">
	///     Every light the room would command, as the resolver settled it — groups already preferred over their
	///     members, so a flagged entity is one that really would be driven.
	/// </param>
	/// <returns>One entry per suspect, in the order given. Empty when nothing looks wrong, which is the usual answer.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="lights"/> is <c>null</c>.</exception>
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
	/// </summary>
	/// <remarks>
	///     Ordered, and the order is load-bearing: the status rule runs before the lamp guard, so a ceiling light's
	///     status LED is still caught by the word that describes the entity rather than excused by the word that
	///     describes its device.
	/// </remarks>
	/// <param name="light">The light to judge.</param>
	/// <param name="present">Every entity id in the same room, for the colour-channel rule's sibling check.</param>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static string? ReasonFor(LightUnderReview light, IReadOnlySet<string> present)
	{
		ArgumentNullException.ThrowIfNull(light);
		ArgumentNullException.ThrowIfNull(present);

		string id = ObjectIdOf(light.EntityId);

		// A light with no friendly name arrives named by its own entity id, and the domain in front of it is the
		// word "light" — which the lamp guard below would read as a description and use to excuse every hardware
		// LED in the house. Read as the object id in that case rather than as somebody's prose.
		string name = string.Equals(light.Name, light.EntityId, StringComparison.Ordinal) ? id : Normalise(light.Name);

		if (HasWord(id, StatusWords) || HasWord(name, StatusWords))
			return "named as a status light, which reports on a device rather than lighting a room";

		if (ChannelParentOf(light.EntityId, present) is { Length: > 0 } parent)
			return $"one colour channel of {ObjectIdOf(parent)}, which this room already commands as a whole lamp";

		// Read off the id, not the name: an entity id is the slug an integration generated, so a trailing "_led"
		// there is a naming convention rather than a person's description of their lamp.
		if (id.EndsWith("_led", StringComparison.Ordinal) && !SuggestsLamp(id) && !SuggestsLamp(name))
			return "the name ends in LED, the usual mark of a hardware status light";

		if (HasWord(id, ApplianceWords) || HasWord(name, ApplianceWords))
			return "named after an appliance, so this is likely a light inside a machine";

		return null;
	}

	/// <summary>
	///     The entity id this one is a colour channel of, when that entity is in the room too; <c>null</c> otherwise.
	/// </summary>
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

	/// <summary>The part after the domain — <c>light.stue_taklys</c> becomes <c>stue_taklys</c> — normalised.</summary>
	private static string ObjectIdOf(string entityId)
	{
		int dot = entityId.IndexOf('.', StringComparison.Ordinal);

		return Normalise(dot >= 0 ? entityId[(dot + 1)..] : entityId);
	}

	/// <summary>
	///     A name as the word rules read it: lower-cased, with everything that is not a letter or digit turned into
	///     an underscore, so "Status LED" and <c>status_led</c> are the same two words.
	/// </summary>
	private static string Normalise(string text)
	{
		char[] letters = new char[text.Length];

		for (int index = 0; index < text.Length; index++)
			letters[index] = char.IsLetterOrDigit(text[index]) ? char.ToLowerInvariant(text[index]) : '_';

		return new string(letters);
	}

	/// <summary>
	///     Whether a normalised name carries one of <paramref name="words"/> as a whole underscore-separated word.
	/// </summary>
	/// <remarks>
	///     Whole words, never substrings, because this is the accusing half. <c>oppvask</c> — dishwasher — lives
	///     inside <c>oppvaskbenk_lys</c>, which is the light over the sink, and a substring match would have this
	///     class calling it a machine.
	/// </remarks>
	private static bool HasWord(string normalised, string[] words) =>
		Words(normalised).Any(segment => words.Contains(segment, StringComparer.Ordinal));

	/// <summary>
	///     Whether a normalised name says "lamp" anywhere in it.
	/// </summary>
	/// <remarks>
	///     The excusing half, and deliberately generous where <see cref="HasWord"/> is strict: a substring is
	///     enough, because Norwegian buries the word inside the compound — <c>taklys</c>, <c>vegglampe</c>,
	///     <c>benkbelysning</c>, none of which a whole-word or even an ends-with rule catches. Over-matching here
	///     costs a warning nobody needed to see; under-matching calls a real ceiling light a status indicator, and
	///     only one of those is worth being wrong about.
	/// </remarks>
	private static bool SuggestsLamp(string normalised) =>
		LampWords.Any(word => normalised.Contains(word, StringComparison.Ordinal));

	private static string[] Words(string normalised) => normalised.Split('_', StringSplitOptions.RemoveEmptyEntries);
}
