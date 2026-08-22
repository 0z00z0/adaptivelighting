using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>One house mode as the House tab reads it: its name, its kind, and what it does as a sentence.</summary>
public sealed record ModeLine(string Name, ModeKind Kind, IReadOnlyList<Sentence> Sentences);

/// <summary>The house's own behaviour written as prose: its modes, its schedule blending, its idea of an empty house.</summary>
public static class HouseSentences
{
	private const string ModePrefix = "mode";

	public static IReadOnlyList<TokenChoice> AwayDebounceChoices { get; } =
		TokenChoices.DurationsInMinutes(1, 5, 10, 15, 30);

	public static IReadOnlyList<TokenChoice> AutoAwayChoices { get; } =
		TokenChoices.DurationsInMinutes(60, 120, 240, 360, 720);

	/// <summary>How long an arrival is ignored before it can end a mode.</summary>
	public static IReadOnlyList<TokenChoice> GraceChoices { get; } =
		TokenChoices.DurationsInMinutes(0, 5, 15, 30);

	/// <summary>How lights cross a period boundary, as one value instead of a switch beside a number.</summary>
	public static Sentence Blend(GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		// SmoothTransitions and BlendMinutes fold into one token keyed on BlendMinutes. Zero carries "step at the
		// boundary", so whoever applies the edit sets the switch from the number too.
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
	public static IReadOnlyList<TokenChoice> BlendChoices(int minutes)
	{
		List<int> offered = [0, 15, 30, 60];

		if (minutes > 0 && !offered.Contains(minutes))
			offered.Add(minutes);

		offered.Sort();

		return TokenChoices.Of([.. offered.Select(value =>
			(value == 0 ? "step at the boundary" : $"ease over {TokenFormat.DurationFromMinutes(value)}", TokenFormat.Carry(value)))]);
	}

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
	public static string ModeKey(int index, string property) =>
		$"{ModePrefix}:{index.ToString(CultureInfo.InvariantCulture)}:{property}";

	/// <summary>Reads a mode token key back into the option it names and the setting it changes.</summary>
	public static bool TryReadModeKey(string? key, out int index, out string property)
	{
		// Both halves of the encoding stay here; a page that split the key itself would be a second copy.
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

	// Kind defaults to Normal, so a list can hold several; only the first is the reset target NormalOption returns.
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
				builder.Text(HouseModeConfig.SleepClampPeriodFor(option, periods) is { Name: { Length: > 0 } clamp }
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

		Ends(builder, option, index, periods);
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
	private static void Ends(SentenceBuilder builder, HouseModeOptionConfig option, int index, IReadOnlyList<TimePeriodConfig> periods)
	{
		if (option.Kind == ModeKind.Normal)
			return;

		bool presence = option.ResetOnPresence;
		List<string> others = [];

		if (option.ResetOnPeriodStartId is { Length: > 0 } resetPeriod)
			others.Add($"when the {NameOf(periods, resetPeriod)} period starts");

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

	private static string Name(HouseModeOptionConfig option) =>
		option.Value is { Length: > 0 } value ? value : "This option";

	private static string NameOf(IReadOnlyList<TimePeriodConfig> periods, string periodId) =>
		periods.ByKey(periodId)?.Name is { Length: > 0 } name
			? name
			: periodId.Trim();
}
