using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>One house mode as the House tab reads it: its name, what it is, and what it does said as a sentence.</summary>
/// <param name="Name">The option value, as the Home Assistant select reports it. Carried beside the sentence, not
///     inside it, so the sentence can open on a verb whatever the option happens to be called.</param>
/// <param name="Sentences">The sentence, as the one-item list <c>SentenceView</c> takes.</param>
public sealed record ModeLine(string Name, ModeKind Kind, IReadOnlyList<Sentence> Sentences);

/// <summary>
///     The house's own behaviour written as prose: its modes, its schedule blending, its idea of an empty house.
/// </summary>
/// <remarks>
///     <see cref="AreaSentences"/> covers the settings a room can override, which the House tab renders over the
///     document's defaults. Only what exists house-wide is here.
/// </remarks>
public static class HouseSentences
{
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

	/// <summary>How lights cross a period boundary, as one value instead of a switch beside a number.</summary>
	public static Sentence Blend(GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		// SmoothTransitions and BlendMinutes are folded into one token keyed on BlendMinutes. Zero carries "step at
		// the boundary", so whoever applies the edit has to set the switch from the number too.
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

	/// <summary>The blend options, always including the value the document holds so the popover opens on it.</summary>
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

	/// <summary>One sentence per house-mode option: what it does, what turns it on, and what ends it.</summary>
	/// <param name="houseMode">The house-mode block, or <c>null</c> when no select is configured.</param>
	/// <param name="periods">The circadian table, for resolving a sleep option's clamp period.</param>
	/// <returns>One line per configured option, in the document's own order.</returns>
	public static IReadOnlyList<ModeLine> Modes(HouseModeConfig? houseMode, IReadOnlyList<TimePeriodConfig> periods)
	{
		ArgumentNullException.ThrowIfNull(periods);

		if (houseMode is not { Options.Count: > 0 })
			return [];

		List<ModeLine> lines = new(houseMode.Options.Count);

		for (int index = 0; index < houseMode.Options.Count; index++)
		{
			HouseModeOptionConfig option = houseMode.Options[index];

			lines.Add(new ModeLine(Name(option), option.Kind, [Mode(option, index, periods, ReferenceEquals(option, houseMode.NormalOption))]));
		}

		return lines;
	}

	/// <summary>The token key one mode option's setting is carried under.</summary>
	/// <param name="index">The option's position in <c>HouseModeConfig.Options</c>.</param>
	public static string ModeKey(int index, string property) =>
		$"{ModePrefix}:{index.ToString(CultureInfo.InvariantCulture)}:{property}";

	/// <summary>Reads a mode token key back into the option it names and the setting it changes.</summary>
	/// <returns>Whether the key named a mode option's setting.</returns>
	public static bool TryReadModeKey(string? key, out int index, out string property)
	{
		// Both halves of the encoding stay here. A page that split the key itself would be the second copy.
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

	/// <param name="isResetTarget">
	///     Whether this is the option <see cref="HouseModeConfig.NormalOption"/> returns. Not the same as having
	///     kind Normal: <see cref="HouseModeOptionConfig.Kind"/> defaults to Normal, so a list can hold several and
	///     only the first is the reset target.
	/// </param>
	private static Sentence Mode(HouseModeOptionConfig option, int index, IReadOnlyList<TimePeriodConfig> periods, bool isResetTarget)
	{
		SentenceBuilder builder = SentenceBuilder.Start();

		switch (option.Kind)
		{
			case ModeKind.Normal:
				return builder
					.Text(isResetTarget
						? "is everyday automatic lighting. The house returns here when another mode ends."
						: "is everyday automatic lighting. It does nothing of its own — and the house returns to the first Normal mode above, not to this one.")
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

	/// <summary>The clauses about what ends a mode, joined as English joins them.</summary>
	private static void Ends(SentenceBuilder builder, HouseModeOptionConfig option, int index)
	{
		if (option.Kind == ModeKind.Normal)
			return;

		bool presence = option.ResetOnPresence;
		List<string> others = [];

		if (option.ResetOnPeriodStart is { Length: > 0 } period)
			others.Add($"when the {period} period starts");

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
