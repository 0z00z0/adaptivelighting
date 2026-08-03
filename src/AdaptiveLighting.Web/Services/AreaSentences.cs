using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     A room's behaviour, projected into the handful of sentences the overview shows.
/// </summary>
/// <remarks>
///     Every token's key is the <see cref="AreaSettings"/> property it changes, so the page applying an edit
///     switches on <c>nameof</c> and the compiler keeps the two in step.
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

	/// <summary>The shortlist offered for how long a manual change holds the room.</summary>
	public static IReadOnlyList<TokenChoice> HandHoldChoices { get; } =
		TokenChoices.DurationsInMinutes(30, 60, 120, 240, 480);

	/// <summary>The shortlist offered for the quiet a room needs before movement counts again.</summary>
	public static IReadOnlyList<TokenChoice> HandOffWaitChoices { get; } =
		TokenChoices.DurationsInMinutes(2, 5, 10, 15, 30);

	/// <summary>The rungs the light-level shortlist offers, climbing by roughly a factor of three.</summary>
	public static IReadOnlyList<double> LuxLadder { get; } = [3, 10, 30, 100, 300, 1000, 3000, 10000];

	// "Off" is not a lux value, so this choice writes Darkness, not LuxThreshold; the threshold stays intact.
	public static TokenChoice LuxOff { get; } = new(
		"Off — use the sun",
		nameof(DarknessSource.Sun),
		nameof(AreaSettings.Darkness),
		TokenKind.Choice);

	/// <summary>The light-level shortlist, being the ladder plus the room's own value when that is not a rung.</summary>
	public static IReadOnlyList<TokenChoice> LuxChoicesFor(double current)
	{
		List<double> rungs = [.. LuxLadder];

		// Matched on the written form, not the number: 1000 and 1000.0 are one rung, and the popover ticks on words.
		if (!rungs.Exists(rung => string.Equals(InLux(rung), InLux(current), StringComparison.Ordinal)))
		{
			rungs.Add(current);
			rungs.Sort();
		}

		return [.. rungs.Select(rung => new TokenChoice(InLux(rung), TokenFormat.Carry(rung))), LuxOff];
	}

	/// <summary>The shortlist offered for the sun elevation a room counts as dark below.</summary>
	public static IReadOnlyList<TokenChoice> SunChoices { get; } =
		TokenChoices.Numbers("°", -6, -3, 0, 3, 6);

	/// <summary>How a room decides it is dark, worded as it reads inside the sentence.</summary>
	public static IReadOnlyList<TokenChoice> DarknessChoices { get; } = TokenChoices.Of(
		("the light-level sensor", nameof(DarknessSource.Lux)),
		("the sun", nameof(DarknessSource.Sun)),
		("whatever the daylight", nameof(DarknessSource.Always)));

	/// <summary>How the room's warmth reaches its lights, worded once for the sentence and the levels table.</summary>
	// "No colour temperature" first, and the wording is standalone: the same list fills a bare dropdown in the
	// levels table, where there is no sentence around it to lean on.
	public static IReadOnlyList<TokenChoice> WarmthChoices { get; } = TokenChoices.Of(
		("No colour temperature", nameof(ColorControl.EqualChannels)),
		("Colour temperature in kelvin", nameof(ColorControl.Kelvin)),
		("Detect it from the lights", nameof(ColorControl.Auto)));

	/// <summary>Why a room with no colour temperature is offered no kelvin, said the same way wherever it is said.</summary>
	public const string NoColourTemperature =
		"The schedule's kelvin figure does nothing for these lights, so they run at neutral white.";

	/// <summary>How the room's warmth is commanded, following the house wherever the room says nothing.</summary>
	public static ColorControl WarmthOf(AreaConfig? area, AreaSettings defaults)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		return area?.ColorControl ?? defaults.ColorControl;
	}

	/// <summary>Whether a kelvin figure reaches this room's lights at all.</summary>
	public static bool WithoutColourTemperature(AreaConfig? area, AreaSettings defaults) =>
		WarmthOf(area, defaults) is ColorControl.EqualChannels;

	/// <summary>One room's behaviour, with every value marked as the room's own or the house's.</summary>
	/// <param name="area">The room, whose <c>null</c> properties mean "inherit".</param>
	/// <returns>
	///     The movement rule and the hand rule, then the flags when a flag is on, then the warmth when there is no
	///     colour temperature to set.
	/// </returns>
	public static IReadOnlyList<Sentence> ForArea(AreaConfig area, AreaSettings defaults)
	{
		ArgumentNullException.ThrowIfNull(area);
		ArgumentNullException.ThrowIfNull(defaults);

		return Build(area, defaults);
	}

	/// <summary>What every room starts with, as the same sentences over the house's own defaults.</summary>
	public static IReadOnlyList<Sentence> ForDefaults(AreaSettings defaults)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		return Build(null, defaults);
	}

	/// <summary>Joins flag phrases the way English does, with a comma before the final "and".</summary>
	/// <param name="phrases">The phrases, already worded as verb clauses.</param>
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

		if (Warmth(area, defaults, effective) is { } warmth)
			sentences.Add(warmth);

		return sentences;
	}

	/// <summary>The first sentence: what lights the room, and what puts it out again.</summary>
	private static Sentence Movement(AreaConfig? area, AreaSettings defaults, AreaSettings effective)
	{
		SentenceBuilder builder = SentenceBuilder.Start("Lights when someone moves");

		switch (effective.Darkness)
		{
			// Always has no threshold to show, so the rule itself becomes the token; the others carry a value.
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
		SentenceBuilder.Start("Manual changes hold for ")
			.Duration(
				nameof(AreaSettings.OverrideDurationMinutes),
				"Manual changes hold for",
				effective.OverrideDurationMinutes * 60,
				HandHoldChoices,
				OriginOf(area, area?.OverrideDurationMinutes),
				defaults.OverrideDurationMinutes * 60)
			.Text("; after somebody switches them off manually, movement is ignored until the room has been empty ")
			.Duration(
				nameof(AreaSettings.VacancyResetMinutes),
				"After switching off manually, wait",
				effective.VacancyResetMinutes * 60,
				HandOffWaitChoices,
				OriginOf(area, area?.VacancyResetMinutes),
				defaults.VacancyResetMinutes * 60)
			.Text(".")
			.Build();

	/// <summary>The third sentence, which exists only when the room has a flag on.</summary>
	private static Sentence? Flags(AreaConfig? area, AreaSettings effective)
	{
		List<string> clauses = [];

		// Else-if, not two ifs: blocking auto-on already implies the gentler cap, and listing both reads as a
		// contradiction.
		if (effective.SleepBlocksAutoOn)
			clauses.Add("never comes on by itself while the house sleeps");
		else if (effective.RespectSleepMode)
			clauses.Add("is gentle while the house sleeps");

		if (effective.SkipAwaySweep)
			clauses.Add("stays on when everyone leaves");

		if (effective.WelcomeHome)
			clauses.Add("welcomes the first person home");

		// Both gates read their polarity, or the sentence says the opposite of what the room does.
		if (area?.IgnoreWhenOn is { Count: > 0 })
			clauses.Add(area.IgnoreWhenOnInverted is true
				? "does not light itself while its blocker is off"
				: "does not light itself while its blocker is on");

		if (area?.KeepLitWhenOn is { Count: > 0 })
			clauses.Add(area.KeepLitWhenOnInverted is true
				? "stays lit while its hold is off"
				: "stays lit while its hold is on");

		return clauses.Count == 0
			? null
			: SentenceBuilder.Start($"This room {JoinClauses(clauses)}.").Build();
	}

	/// <summary>The warmth sentence, which exists only when there is no colour temperature left to set.</summary>
	// Auto and Kelvin both end with the schedule's kelvin reaching the lights, which every other surface already
	// says. Only the third answer changes what the levels table can offer, so only it earns a paragraph.
	private static Sentence? Warmth(AreaConfig? area, AreaSettings defaults, AreaSettings effective) =>
		effective.ColorControl is not ColorControl.EqualChannels
			? null
			: SentenceBuilder.Start("Warmth: ")
				.Choice(
					nameof(AreaSettings.ColorControl),
					"How warmth reaches these lights",
					nameof(ColorControl.EqualChannels),
					WarmthChoices,
					OriginOf(area, area?.ColorControl),
					defaults.ColorControl.ToString())
				.Text($". {NoColourTemperature}")
				.Build();

	/// <summary>A light level as the sentence and the shortlist both write it, so the popover can tick on words.</summary>
	private static string InLux(double lux) => TokenFormat.Number(lux, "lx");

	private static void Lux(SentenceBuilder builder, AreaConfig? area, AreaSettings defaults, AreaSettings effective) =>
		builder.Number(
			nameof(AreaSettings.LuxThreshold),
			"Dark below",
			effective.LuxThreshold,
			"lx",
			LuxChoicesFor(effective.LuxThreshold),
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

	// Provenance is the null on the nullable twin, never a comparison against the default: a room that sets 10 min
	// while the house also says 10 min has still made a decision. With no room, None, since defaults inherit
	// from nothing.
	private static TokenOrigin OriginOf<T>(AreaConfig? area, T? own) where T : struct =>
		area is null ? TokenOrigin.None
		: own is null ? TokenOrigin.Inherited
		: TokenOrigin.Own;
}
