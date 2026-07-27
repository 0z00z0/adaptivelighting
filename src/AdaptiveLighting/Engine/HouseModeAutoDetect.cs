using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>
///     Finds the Home Assistant dropdown that is obviously the house mode, and says what each of its options
///     means.
/// </summary>
/// <remarks>
///     <para>
///         Almost every house already has this helper — <c>Home / Away / Sleeping / Guests</c>, or the same four
///         in the owner's own language — long before it meets this engine. Asking somebody to point at it and then
///         re-declare, one option at a time, what "Away" means is asking them to type out something both parties
///         already know.
///     </para>
///     <para>
///         <b>Adoption cannot make the engine write.</b> A detected mode carries kinds and nothing else: no
///         scene, no reset trigger, and no period is given a <c>SetsMode</c>. The engine therefore only ever
///         <i>reads</i> the select — the writing paths (a period entering, a reset firing, auto-away) all require
///         configuration this never adds, so nothing adopted here can switch the household's dropdown or apply a
///         look to a room.
///     </para>
///     <para>
///         <b>Reading the wrong dropdown is not harmless, which is what the narrowness below is for.</b>
///         Qualifying takes two different kinds, so an adopted select always carries at least one Sleep, Away or
///         Guest option — "everything classifies as Normal and nothing happens" is the one outcome adoption rules
///         out, not its worst case. While the select stands on an Away option <see cref="HouseState.Mode"/>
///         reports the whole house away whatever presence says, and every area goes to
///         <see cref="AreaState.Away"/>; a Sleep option holds sleep-respecting areas to the night caps. A drying
///         cupboard's <c>Inne / Ute</c> would qualify — <c>Ute</c> is Away vocabulary and <c>Inne</c> matches
///         nothing, so it falls to Normal.
///     </para>
///     <para>
///         Detection is therefore deliberately narrow: a select qualifies only when its options name at least two
///         different kinds <i>and</i> one of them is the everyday one, and adoption only happens when exactly one
///         select in the house qualifies. Two candidates is a house whose owner should choose. Loosening either
///         rule trades a house's lighting for a guess, and the log line below is the only notice anyone gets.
///     </para>
/// </remarks>
public static class HouseModeAutoDetect
{
	private const string SelectDomain = "input_select";
	private const string OptionsAttribute = "options";

	// Matched against the whole option text, lower-cased. English and Norwegian, because those are the two this
	// has been used in; an unrecognised word simply classifies as Normal, which is the harmless default.
	private static readonly (ModeKind Kind, string[] Words)[] Vocabulary =
	[
		(ModeKind.Sleep, ["sleep", "sleeping", "asleep", "night", "sover", "sove", "natt", "senga"]),
		(ModeKind.Away,  ["away", "gone", "out", "empty", "borte", "ute", "reist"]),
		(ModeKind.Guest, ["guest", "guests", "visitor", "visitors", "gjest", "gjester", "besok", "besøk"]),
		(ModeKind.Normal, ["home", "normal", "default", "day", "hjemme", "dag", "vanlig"]),
	];

	/// <summary>
	///     The house-mode configuration this instance obviously wants, or <c>null</c> when it is not obvious.
	/// </summary>
	/// <param name="ha">Source of the candidate selects and their options.</param>
	/// <param name="logger">Where the decision — including a refusal to choose — is recorded.</param>
	/// <returns>A configured <see cref="HouseModeConfig"/>, or <c>null</c> to leave the house mode unset.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static HouseModeConfig? Detect(IHaContext ha, ILogger logger)
	{
		ArgumentNullException.ThrowIfNull(ha);
		ArgumentNullException.ThrowIfNull(logger);

		List<HouseModeConfig> candidates = [];

		foreach (string entityId in SelectIds(ha))
		{
			IReadOnlyList<string> options = ha.GetState(entityId).AttrStringList(OptionsAttribute);
			if (options.Count == 0)
				continue;

			List<HouseModeOptionConfig> classified =
				[.. options
					.Where(option => !string.IsNullOrWhiteSpace(option))
					.Select(option => new HouseModeOptionConfig { Value = option.Trim(), Kind = KindOf(option) })];

			// Needs a real spread of meanings and somewhere to return to. A dropdown of "Eco / Comfort / Boost"
			// classifies as three Normals and is correctly ignored.
			bool hasNormal = classified.Any(option => option.Kind == ModeKind.Normal);
			int distinctKinds = classified.Select(option => option.Kind).Distinct().Count();

			if (hasNormal && distinctKinds >= 2)
				candidates.Add(new HouseModeConfig { Entity = entityId, Options = classified });
		}

		switch (candidates.Count)
		{
			case 0:
				return null;

			case 1:
				HouseModeConfig detected = candidates[0];
				logger.LogInformation(
					"Adopted {Entity} as the house mode ({Options}). Nothing switches it automatically — add a scene, "
					+ "a reset trigger or a period that sets it on the Configuration page.",
					detected.Entity,
					string.Join(", ", detected.Options.Select(option => $"{option.Value}={option.Kind}")));
				return detected;

			default:
				logger.LogInformation(
					"{Count} dropdowns could be the house mode ({Entities}); leaving it unset rather than choosing. Pick one on the Configuration page.",
					candidates.Count, string.Join(", ", candidates.Select(candidate => candidate.Entity)));
				return null;
		}
	}

	private static IEnumerable<string> SelectIds(IHaContext ha)
	{
		try
		{
			return [.. ha.GetAllEntities()
				.Select(entity => entity.EntityId)
				.Where(entityId => entityId.HasDomain(SelectDomain))
				.Distinct(StringComparer.Ordinal)];
		}
		catch (InvalidOperationException)
		{
			// NetDaemon's state cache throws until its first connection completes.
			return [];
		}
	}

	/// <summary>The kind an option's text names, defaulting to <see cref="ModeKind.Normal"/> when nothing matches.</summary>
	private static ModeKind KindOf(string option)
	{
		string text = option.Trim().ToLowerInvariant();

		foreach ((ModeKind kind, string[] words) in Vocabulary)
			if (words.Any(word => text.Contains(word, StringComparison.Ordinal)))
				return kind;

		return ModeKind.Normal;
	}
}
