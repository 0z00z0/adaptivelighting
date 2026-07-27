using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     One house mode as the House tab reads it: its name, what it is, and what it does said as a sentence.
/// </summary>
/// <remarks>
///     The name is carried beside the sentence rather than inside it so the card can set it in bold — the
///     mock-up's own rendering, and the thing an eye scans a list of four modes for. Keeping it out of the
///     sentence also keeps the sentence a sentence: it starts with a verb and reads on from whatever the name
///     happens to be, in a house whose modes are called Hjemme and Borte rather than Home and Away.
/// </remarks>
/// <param name="Name">The option value, exactly as the Home Assistant select reports it.</param>
/// <param name="Kind">The one behaviour the option carries.</param>
/// <param name="Sentences">The sentence, as the one-item list <c>SentenceView</c> takes.</param>
public sealed record ModeLine(string Name, ModeKind Kind, IReadOnlyList<Sentence> Sentences);

/// <summary>
///     The house's own behaviour — its modes, its schedule blending and its idea of an empty house — written as
///     the same readable prose a room's settings are written in.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="AreaSentences"/> covers the settings a room can override, and the House tab renders those
///         over the document's defaults with no extra code. What it does <i>not</i> cover is everything that only
///         exists house-wide: what each mode means, how periods hand over, how long an empty house stays empty.
///         Those are the sentences here.
///     </para>
///     <para>
///         Pure, for the reason the room sentences are pure: this repo has no Razor render harness, and a
///         sentence assembled inside markup is a sentence nothing can assert about. A mode sentence that renders
///         the wrong knob is a setting nobody can find — and the mode sentences are the only place several of
///         these values are ever read as English.
///     </para>
/// </remarks>
public static class HouseSentences
{
	/// <summary>The prefix every per-mode token key carries, so a page can tell one from an area setting.</summary>
	private const string ModePrefix = "mode";

	/// <summary>The shortlist offered for how long an empty house stays empty before the rooms react.</summary>
	public static IReadOnlyList<TokenChoice> AwayDebounceChoices { get; } =
		TokenChoices.DurationsInMinutes(1, 5, 10, 15, 30);

	/// <summary>The shortlist offered for how long the house must be still before a mode switches itself on.</summary>
	public static IReadOnlyList<TokenChoice> AutoAwayChoices { get; } =
		TokenChoices.DurationsInMinutes(60, 120, 240, 360, 720);

	/// <summary>The shortlist offered for the grace in which an arrival does not end a mode.</summary>
	public static IReadOnlyList<TokenChoice> GraceChoices { get; } =
		TokenChoices.DurationsInMinutes(0, 5, 15, 30);

	/// <summary>
	///     How lights cross a period boundary, as one value rather than as a switch beside a number.
	/// </summary>
	/// <remarks>
	///     <c>SmoothTransitions</c> and <c>BlendMinutes</c> are one decision in the schema's clothing: blending
	///     off and blending over zero minutes are the same instruction, and a switch that greys out a number box
	///     spends two controls saying what one word says. Zero carries "step at the boundary"; anything else
	///     carries the minutes and turns blending on.
	/// </remarks>
	/// <param name="global">The document's house-wide settings.</param>
	/// <exception cref="ArgumentNullException"><paramref name="global"/> is <c>null</c>.</exception>
	public static Sentence Blend(GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		int minutes = global.SmoothTransitions ? Math.Max(0, global.BlendMinutes) : 0;

		return SentenceBuilder.Start("Lights ")
			.Choice(
				nameof(GlobalConfig.BlendMinutes),
				"Blend between periods",
				TokenFormat.Carry(minutes),
				BlendChoices(minutes))
			.Text(" when one period hands over to the next.")
			.Build();
	}

	/// <summary>
	///     The blend options, always including the value the document actually holds.
	/// </summary>
	/// <remarks>
	///     A shortlist that omits the current value opens with nothing ticked, which reads as a control that has
	///     lost its own state. A house blending over 22 minutes gets 22 minutes offered alongside the curated set.
	/// </remarks>
	/// <param name="minutes">The blend in minutes; zero means the lights step at the boundary.</param>
	public static IReadOnlyList<TokenChoice> BlendChoices(int minutes)
	{
		List<int> offered = [0, 15, 30, 60];

		if (minutes > 0 && !offered.Contains(minutes))
			offered.Add(minutes);

		offered.Sort();

		return TokenChoices.Of([.. offered.Select(value =>
			(value == 0 ? "step at the boundary" : $"ease over {TokenFormat.DurationFromMinutes(value)}", TokenFormat.Carry(value)))]);
	}

	/// <summary>How long everyone has to be gone before the rooms treat the house as empty.</summary>
	/// <param name="global">The document's house-wide settings.</param>
	/// <exception cref="ArgumentNullException"><paramref name="global"/> is <c>null</c>.</exception>
	public static Sentence AwayDebounce(GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		return SentenceBuilder.Start("Count the house as empty ")
			.Duration(
				nameof(GlobalConfig.AwayDebounceMinutes),
				"Count the house as empty after",
				global.AwayDebounceMinutes * 60,
				AwayDebounceChoices)
			.Text(" after the last person leaves — a trip to the bin should not sweep the lights off.")
			.Build();
	}

	/// <summary>
	///     One sentence per house-mode option: what it does, what turns it on, and what ends it.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The summary layer over <c>HouseModeOptions</c>, which stays underneath with every field. Only the
	///         two values a house actually tunes are tokens here — how long the house must be still before Away
	///         arms itself, and the grace in which your own departure does not cancel it. Everything else is
	///         prose, because a scene id and a period name are picked from what Home Assistant has rather than
	///         from a shortlist.
	///     </para>
	///     <para>
	///         An option carrying no behaviour at all still gets a sentence. A mode that does nothing is exactly
	///         the thing somebody is looking for when they open this card, and silence would read as "not
	///         configured yet" rather than as "configured to do nothing".
	///     </para>
	/// </remarks>
	/// <param name="houseMode">The house-mode block, or <c>null</c> when no select is configured.</param>
	/// <param name="periods">The circadian table, for resolving a sleep option's clamp period.</param>
	/// <returns>One line per configured option, in the document's own order.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="periods"/> is <c>null</c>.</exception>
	public static IReadOnlyList<ModeLine> Modes(HouseModeConfig? houseMode, IReadOnlyList<TimePeriodConfig> periods)
	{
		ArgumentNullException.ThrowIfNull(periods);

		if (houseMode is not { Options.Count: > 0 })
			return [];

		List<ModeLine> lines = new(houseMode.Options.Count);

		for (int index = 0; index < houseMode.Options.Count; index++)
		{
			HouseModeOptionConfig option = houseMode.Options[index];

			lines.Add(new ModeLine(Name(option), option.Kind, [Mode(option, index, periods)]));
		}

		return lines;
	}

	/// <summary>The token key one mode option's setting is carried under.</summary>
	/// <param name="index">The option's position in <c>HouseModeConfig.Options</c>.</param>
	/// <param name="property">The <see cref="HouseModeOptionConfig"/> property name.</param>
	public static string ModeKey(int index, string property) =>
		$"{ModePrefix}:{index.ToString(CultureInfo.InvariantCulture)}:{property}";

	/// <summary>
	///     Reads a mode token key back into the option it names and the setting it changes.
	/// </summary>
	/// <remarks>
	///     The area sentences can key on an <c>AreaSettings</c> property name alone because there is one room per
	///     page. A house has several modes on one card, so the key has to carry which — and the page that applies
	///     the edit must not parse that string itself, or the two halves of the encoding live in two files.
	/// </remarks>
	/// <param name="key">The token's key.</param>
	/// <param name="index">The option's position.</param>
	/// <param name="property">The <see cref="HouseModeOptionConfig"/> property name.</param>
	/// <returns>Whether the key named a mode option's setting.</returns>
	public static bool TryReadModeKey(string? key, out int index, out string property)
	{
		index = -1;
		property = string.Empty;

		if (key is null)
			return false;

		string[] parts = key.Split(':');

		if (parts.Length != 3 || !string.Equals(parts[0], ModePrefix, StringComparison.Ordinal))
			return false;

		if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
			return false;

		property = parts[2];

		return property.Length > 0;
	}

	private static Sentence Mode(HouseModeOptionConfig option, int index, IReadOnlyList<TimePeriodConfig> periods)
	{
		SentenceBuilder builder = SentenceBuilder.Start();

		switch (option.Kind)
		{
			case ModeKind.Normal:
				return builder
					.Text("is everyday automatic lighting. The house returns here when another mode ends.")
					.Build();

			case ModeKind.Sleep:
				builder.Text(HouseModeConfig.SleepClampPeriodFor(option, periods) is { Length: > 0 } clamp
					? $"holds the rooms that are gentle at night to the {clamp} period's limits, and the rooms set never to come on by themselves stay off"
					: "holds the rooms that are gentle at night to the night period's limits — but no period is named for it yet, so nothing is clamped");
				break;

			case ModeKind.Away:
				Arms(builder, option, index);
				builder.Text(option.Scene is { Length: > 0 } scene
					? $"runs the {scene} scene and pauses automatic lighting"
					: "sweeps the lights off and pauses automatic lighting — rooms set to stay on when everyone leaves are left alone");
				break;

			default:
				builder.Text(option.Scene is { Length: > 0 } guestScene
					? $"runs the {guestScene} scene and holds every room"
					: "holds every room, so nothing changes by itself");
				break;
		}

		Ends(builder, option, index);
		builder.Text(".");

		return builder.Build();
	}

	/// <summary>The clause about a mode that turns itself on. Not built at all when nothing arms it.</summary>
	private static void Arms(SentenceBuilder builder, HouseModeOptionConfig option, int index) =>
		builder.When(option.ActivateAfterNoMotionMinutes is > 0, clause => clause
			.Text("switches on by itself after the house has been still ")
			.Duration(
				ModeKey(index, nameof(HouseModeOptionConfig.ActivateAfterNoMotionMinutes)),
				"Switch to this mode after no motion for",
				option.ActivateAfterNoMotionMinutes!.Value * 60,
				AutoAwayChoices)
			.Text(", then "));

	/// <summary>
	///     The clauses about what ends a mode, joined as English joins them.
	/// </summary>
	/// <remarks>
	///     Built as a list and then written, rather than appended one by one, because the joining word depends on
	///     how many there turn out to be — and a sentence reading "ends when someone comes home and, when Morning
	///     starts" is a bug in the product rather than in a helper.
	/// </remarks>
	private static void Ends(SentenceBuilder builder, HouseModeOptionConfig option, int index)
	{
		if (option.Kind == ModeKind.Normal)
			return;

		bool presence = option.ResetOnPresence;
		List<string> others = [];

		if (option.ResetOnPeriodStart is { Length: > 0 } period)
			others.Add($"when the {period} period starts");

		if (option.ResetAtTime is { Length: > 0 } at)
			others.Add($"when {at} comes round");

		if (!presence && others.Count == 0)
		{
			builder.Text(" until you switch the house back yourself");
			return;
		}

		builder.Text(". It ends ");

		if (presence)
		{
			builder.Text("when someone comes home — ignoring the first ")
				.Duration(
					ModeKey(index, nameof(HouseModeOptionConfig.ResetPresenceGraceMinutes)),
					"Ignore arrivals for",
					Math.Max(0, option.ResetPresenceGraceMinutes) * 60,
					GraceChoices)
				.Text(" so your own leaving does not cancel it");

			foreach (string other in others)
				builder.Text($", or {other}");

			return;
		}

		builder.Text(AreaSentences.JoinClauses(others));
	}

	/// <summary>An option's own value, or a stand-in when the document left it blank.</summary>
	private static string Name(HouseModeOptionConfig option) =>
		option.Value is { Length: > 0 } value ? value : "This option";
}
