using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     A room's behaviour, written as the handful of sentences the design's Layer 1 shows.
/// </summary>
/// <remarks>
///     <para>
///         The premise, from <c>ui-design-c.md</c> §1: the rare visitor has forgotten the vocabulary. A form of
///         seventeen numeric fields assumes somebody who remembers what <c>VacancyTimeoutSeconds</c> meant;
///         a sentence does not. So the overview layer is prose, the values inside it are the controls, and this
///         class is the projection that turns a document into that prose.
///     </para>
///     <para>
///         Pure, and the tests are §3's own table. That matters more here than anywhere else in the UI: these
///         sentences are the only place several settings are ever read by a person, so a sentence that renders
///         the wrong knob is a setting nobody can find. There is no Razor render harness in this repo, which is
///         exactly why the sentences are built here and merely drawn in the component.
///     </para>
///     <para>
///         Every token's key is the <c>AreaSettings</c> property it changes, so the page that applies an edit
///         writes <c>case nameof(AreaSettings.VacancyTimeoutSeconds)</c> and the compiler keeps the two in step.
///     </para>
/// </remarks>
public static class AreaSentences
{
	/// <summary>The shortlist offered for how long a room stays lit after the last movement.</summary>
	public static IReadOnlyList<TokenChoice> StayOnChoices { get; } =
		TokenChoices.DurationsInMinutes(1, 3, 5, 10, 20, 30);

	/// <summary>The shortlist offered for the warning dim's level.</summary>
	public static IReadOnlyList<TokenChoice> DimLevelChoices { get; } =
		TokenChoices.Percentages(20, 30, 50, 60, 75);

	/// <summary>The shortlist offered for how long the warning dim lasts.</summary>
	public static IReadOnlyList<TokenChoice> DimForChoices { get; } =
		TokenChoices.Durations(10, 20, 30, 60, 120);

	/// <summary>The shortlist offered for how long a hand change holds the room.</summary>
	public static IReadOnlyList<TokenChoice> HandHoldChoices { get; } =
		TokenChoices.DurationsInMinutes(30, 60, 120, 240, 480);

	/// <summary>The shortlist offered for the quiet a room needs before movement counts again.</summary>
	public static IReadOnlyList<TokenChoice> HandOffWaitChoices { get; } =
		TokenChoices.DurationsInMinutes(2, 5, 10, 15, 30);

	/// <summary>The shortlist offered for the lux a room counts as dark below.</summary>
	/// <remarks>Half-decades, spanning an indoor probe and the outdoor sensor most rooms actually gate on.</remarks>
	public static IReadOnlyList<TokenChoice> LuxChoices { get; } =
		TokenChoices.Numbers("lx", 10, 30, 100, 300, 1000, 3000);

	/// <summary>The shortlist offered for the sun elevation a room counts as dark below.</summary>
	public static IReadOnlyList<TokenChoice> SunChoices { get; } =
		TokenChoices.Numbers("°", -6, -3, 0, 3, 6);

	/// <summary>
	///     How a room decides it is dark, worded as it reads inside the sentence rather than as the enum.
	/// </summary>
	/// <remarks>
	///     "Whatever the daylight" is the phrase for <see cref="DarknessSource.Always"/> because that is what
	///     the setting means to somebody standing in a windowless bathroom: not "always dark" as a claim about
	///     the world, but "don't check" as an instruction to the engine.
	/// </remarks>
	public static IReadOnlyList<TokenChoice> DarknessChoices { get; } = TokenChoices.Of(
		("the light sensor", nameof(DarknessSource.Lux)),
		("the sun", nameof(DarknessSource.Sun)),
		("either the sensor or the sun", nameof(DarknessSource.Either)),
		("whatever the daylight", nameof(DarknessSource.Always)));

	/// <summary>
	///     One room's behaviour, with every value marked as the room's own or the house's.
	/// </summary>
	/// <param name="area">The room, whose <c>null</c> properties mean "inherit".</param>
	/// <param name="defaults">The document's all-rooms settings.</param>
	/// <returns>Two or three sentences: the movement rule, the hand rule, and the flags when a flag is on.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static IReadOnlyList<Sentence> ForArea(AreaConfig area, AreaSettings defaults)
	{
		ArgumentNullException.ThrowIfNull(area);
		ArgumentNullException.ThrowIfNull(defaults);

		return Build(area, defaults);
	}

	/// <summary>
	///     What every room starts with, as the same sentences over the house's own defaults.
	/// </summary>
	/// <remarks>
	///     Deliberately the same projection rather than a second one worded slightly differently: the House tab
	///     teaches the model once, and a visitor who learns to read a room's sentences should recognise the
	///     house's on sight. Every token comes back as <see cref="TokenOrigin.None"/> — a default has nothing to
	///     inherit from, and marking it as inherited would claim a house above the house.
	/// </remarks>
	/// <param name="defaults">The document's all-rooms settings.</param>
	/// <exception cref="ArgumentNullException"><paramref name="defaults"/> is <c>null</c>.</exception>
	public static IReadOnlyList<Sentence> ForDefaults(AreaSettings defaults)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		return Build(null, defaults);
	}

	/// <summary>
	///     Joins flag phrases the way English does, keeping the design's own two-flag wording.
	/// </summary>
	/// <remarks>
	///     Exposed because it is the one piece of prose assembly worth asserting on: an off-by-one in a list
	///     joiner produces "gentle while the house sleeps and, welcomes the first person home", which reads as
	///     a bug in the product rather than in a helper.
	/// </remarks>
	/// <param name="phrases">The phrases, already worded as verb clauses.</param>
	/// <exception cref="ArgumentNullException"><paramref name="phrases"/> is <c>null</c>.</exception>
	public static string JoinClauses(IReadOnlyList<string> phrases)
	{
		ArgumentNullException.ThrowIfNull(phrases);

		return phrases.Count switch
		{
			0 => string.Empty,
			1 => phrases[0],
			2 => $"{phrases[0]}, and {phrases[1]}",
			_ => $"{string.Join(", ", phrases.Take(phrases.Count - 1))}, and {phrases[^1]}"
		};
	}

	private static IReadOnlyList<Sentence> Build(AreaConfig? area, AreaSettings defaults)
	{
		AreaSettings effective = area?.Effective(defaults) ?? defaults;

		List<Sentence> sentences = [Movement(area, defaults, effective), Hands(area, defaults, effective)];

		if (Flags(area, effective) is { } flags)
			sentences.Add(flags);

		return sentences;
	}

	/// <summary>
	///     The first sentence: what lights the room, and what puts it out again.
	/// </summary>
	/// <remarks>
	///     Four openings rather than one, because the darkness rule changes the shape of the clause and not just
	///     a value in it. The <see cref="DarknessSource.Always"/> case is the only one that renders the rule
	///     itself as a token: with no threshold to show, the phrase <i>is</i> the setting, and leaving it as flat
	///     prose would make a windowless room the one room whose defining choice cannot be reached from its
	///     sentence. The other three carry their thresholds instead, and the rule stays one row below.
	/// </remarks>
	private static Sentence Movement(AreaConfig? area, AreaSettings defaults, AreaSettings effective)
	{
		SentenceBuilder builder = SentenceBuilder.Start("Lights when someone moves");

		switch (effective.Darkness)
		{
			case DarknessSource.Always:
				builder
					.Text(" — ")
					.Choice(
						nameof(AreaSettings.Darkness),
						"How the room decides it's dark",
						nameof(DarknessSource.Always),
						DarknessChoices,
						OriginOf(area, area?.Darkness),
						defaults.Darkness.ToString());
				break;

			case DarknessSource.Lux:
				builder.Text(" and it's darker than ");
				Lux(builder, area, defaults, effective);
				break;

			case DarknessSource.Sun:
				builder.Text(" and the sun is below ");
				Sun(builder, area, defaults, effective);
				break;

			default:
				builder.Text(" and it's darker than ");
				Lux(builder, area, defaults, effective);
				builder.Text(" — or the sun is below ");
				Sun(builder, area, defaults, effective);
				break;
		}

		builder.Text(". After ")
			.Duration(
				nameof(AreaSettings.VacancyTimeoutSeconds),
				"Lights stay on for",
				effective.VacancyTimeoutSeconds,
				StayOnChoices,
				OriginOf(area, area?.VacancyTimeoutSeconds),
				defaults.VacancyTimeoutSeconds)
			.Text(" without movement, dim to ")
			.Percent(
				nameof(AreaSettings.PreOffBrightnessFactor),
				"Warning dim level",
				effective.PreOffBrightnessFactor * 100,
				DimLevelChoices,
				OriginOf(area, area?.PreOffBrightnessFactor),
				defaults.PreOffBrightnessFactor * 100)
			.Text(" for ")
			.Duration(
				nameof(AreaSettings.PreOffSeconds),
				"Warning dim lasts",
				effective.PreOffSeconds,
				DimForChoices,
				OriginOf(area, area?.PreOffSeconds),
				defaults.PreOffSeconds)
			.Text(", then off.");

		return builder.Build();
	}

	/// <summary>The second sentence: what happens when a person overrules the engine at the wall.</summary>
	private static Sentence Hands(AreaConfig? area, AreaSettings defaults, AreaSettings effective) =>
		SentenceBuilder.Start("Hand changes hold for ")
			.Duration(
				nameof(AreaSettings.OverrideDurationMinutes),
				"Hand changes hold for",
				effective.OverrideDurationMinutes * 60,
				HandHoldChoices,
				OriginOf(area, area?.OverrideDurationMinutes),
				defaults.OverrideDurationMinutes * 60)
			.Text("; after a manual off, movement is ignored until the room is empty ")
			.Duration(
				nameof(AreaSettings.VacancyResetMinutes),
				"After a manual off, wait",
				effective.VacancyResetMinutes * 60,
				HandOffWaitChoices,
				OriginOf(area, area?.VacancyResetMinutes),
				defaults.VacancyResetMinutes * 60)
			.Text(".")
			.Build();

	/// <summary>
	///     The third sentence, which exists only when the room has a flag on.
	/// </summary>
	/// <remarks>
	///     <para>
	///         A room with no flags gets no sentence at all, rather than "This room does none of the following":
	///         the ordinary room is the one with nothing to say, and a paragraph reporting an absence is exactly
	///         the noise the whole design is spending its budget to avoid.
	///     </para>
	///     <para>
	///         Flags are prose, not tokens. A boolean has no value to pick — its control is a switch, and its
	///         switch lives in the All-settings row one tap below. Rendering four dashed boxes that each open a
	///         two-item popover would be a control where a sentence was asked for.
	///     </para>
	/// </remarks>
	private static Sentence? Flags(AreaConfig? area, AreaSettings effective)
	{
		List<string> clauses = [];

		// Blocking auto-on entirely and merely capping the levels are not two facts to list side by side —
		// the stronger one already implies the gentler, and saying both would read as a contradiction.
		if (effective.SleepBlocksAutoOn)
			clauses.Add("never comes on by itself while the house sleeps");
		else if (effective.RespectSleepMode)
			clauses.Add("is gentle while the house sleeps");

		if (effective.SkipAwaySweep)
			clauses.Add("stays on when everyone leaves");

		if (effective.WelcomeHome)
			clauses.Add("welcomes the first person home");

		if (area?.IgnoreWhenOn is { Count: > 0 })
			clauses.Add("is left alone while its blocker is on");

		return clauses.Count == 0
			? null
			: SentenceBuilder.Start($"This room {JoinClauses(clauses)}.").Build();
	}

	private static void Lux(SentenceBuilder builder, AreaConfig? area, AreaSettings defaults, AreaSettings effective) =>
		builder.Number(
			nameof(AreaSettings.LuxThreshold),
			"Dark below",
			effective.LuxThreshold,
			"lx",
			LuxChoices,
			OriginOf(area, area?.LuxThreshold),
			defaults.LuxThreshold);

	private static void Sun(SentenceBuilder builder, AreaConfig? area, AreaSettings defaults, AreaSettings effective) =>
		builder.Number(
			nameof(AreaSettings.SunElevationThreshold),
			"Dark when the sun is below",
			effective.SunElevationThreshold,
			"°",
			SunChoices,
			OriginOf(area, area?.SunElevationThreshold),
			defaults.SunElevationThreshold);

	/// <summary>
	///     Whether a value is the room's own, read straight off the schema's own way of saying "inherit".
	/// </summary>
	/// <remarks>
	///     <para>
	///         The document encodes inheritance as <c>null</c> on a nullable twin of the default, so provenance is
	///         not a guess or a comparison against the default value — a room that explicitly sets 10 min while
	///         the house also says 10 min has still made a decision, and the dot should say so. Comparing values
	///         instead would erase exactly the overrides somebody set deliberately to pin a room against future
	///         house edits.
	///     </para>
	///     <para>
	///         With no room at all these are the house's own defaults, which inherit from nothing:
	///         <see cref="TokenOrigin.None"/> rather than <see cref="TokenOrigin.Inherited"/>, so the House tab
	///         does not paint every value as though it were following some house above itself.
	///     </para>
	/// </remarks>
	private static TokenOrigin OriginOf<T>(AreaConfig? area, T? own) where T : struct =>
		area is null ? TokenOrigin.None
		: own is null ? TokenOrigin.Inherited
		: TokenOrigin.Own;
}
