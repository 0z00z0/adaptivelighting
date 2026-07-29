namespace AdaptiveLighting.Web.Services;

/// <summary>
///     How a value is written in a sentence, and how it is carried back.
/// </summary>
/// <remarks>
///     <para>
///         The whole argument for sentences is that a room's behaviour should reread itself cold, six months
///         later, to somebody who has forgotten the vocabulary. "600 s" fails that and "10 min" passes it, so
///         the writing rules are the feature and not a detail of the markup — which is why they live here, as
///         pure functions with tests, rather than inline in a component nothing can assert about.
///     </para>
///     <para>
///         <b>Invariant culture throughout.</b> Every surface of this UI is written in English, and a Norwegian
///         decimal comma inside an English sentence reads as a typo rather than as localisation. The same
///         choice keeps <see cref="TokenChoice.Value"/> round-trippable on any machine: a value written on a
///         <c>nb-NO</c> host has to parse on an <c>en-US</c> one, and a culture-sensitive number would not.
///     </para>
/// </remarks>
public static class TokenFormat
{
	/// <summary>
	///     A span of seconds, in the largest unit that stays exact.
	/// </summary>
	/// <remarks>
	///     Exactness before brevity: 90 seconds is "1 min 30 s" and not "2 min", because a person reading their
	///     own configuration back should see what they set. The unit ladder stops at hours — a lighting timeout
	///     measured in days is a mistake the sentence should show rather than smooth over.
	/// </remarks>
	/// <param name="totalSeconds">The span. Negative spans are written as zero; there is no negative timeout.</param>
	public static string Duration(int totalSeconds)
	{
		if (totalSeconds <= 0)
			return "0 s";

		if (totalSeconds < 60)
			return $"{totalSeconds} s";

		int minutes = totalSeconds / 60;
		int seconds = totalSeconds % 60;

		if (minutes < 60)
			return seconds == 0 ? $"{minutes} min" : $"{minutes} min {seconds} s";

		int hours = minutes / 60;
		int restMinutes = minutes % 60;

		if (seconds != 0)
			return $"{hours} h {restMinutes} min {seconds} s";

		return restMinutes == 0 ? $"{hours} h" : $"{hours} h {restMinutes} min";
	}

	/// <summary>A span given in minutes, which is how several settings are stored.</summary>
	/// <param name="totalMinutes">The span in whole minutes.</param>
	public static string DurationFromMinutes(int totalMinutes) => Duration(totalMinutes * 60);

	/// <summary>
	///     A proportion, written the way the rest of the UI writes one: a number, a space, a percent sign.
	/// </summary>
	/// <remarks>
	///     The space is the SI convention and the shipped UI's existing habit. Fractions survive when they are
	///     real — 12.5 % stays 12.5 % — because a factor of 0.125 is a value somebody chose on purpose.
	/// </remarks>
	/// <param name="percent">The proportion, 0-100.</param>
	public static string Percent(double percent) =>
		$"{percent.ToString("0.##", CultureInfo.InvariantCulture)} %";

	/// <summary>A proportion held as the schema's 0-1 factor.</summary>
	/// <param name="fraction">The factor, 0-1.</param>
	public static string PercentFromFraction(double fraction) => Percent(fraction * 100);

	/// <summary>
	///     A quantity with its unit.
	/// </summary>
	/// <remarks>
	///     A space before the unit, except for the degree sign: "40 lx" and "3°" are both what a reader expects,
	///     and the exception is typographic convention rather than a special case worth a parameter.
	/// </remarks>
	/// <param name="value">The quantity.</param>
	/// <param name="unit">Its unit, or empty for a bare number.</param>
	public static string Number(double value, string unit = "")
	{
		string written = value.ToString("0.##", CultureInfo.InvariantCulture);

		if (unit.Length == 0)
			return written;

		return unit == "°" ? written + unit : $"{written} {unit}";
	}

	/// <summary>The canonical carrying form of a number: invariant, no thousands separator, no trailing zeros.</summary>
	/// <param name="value">The quantity.</param>
	public static string Carry(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>
///     The curated shortlists a token's popover offers.
/// </summary>
/// <remarks>
///     Curated, not complete, and the difference is the design's: the popover holds the handful of values a
///     sane house uses, and everything between them lives one layer down in the All-settings row. These
///     builders exist so a page cannot accidentally offer a value written one way and carried another.
/// </remarks>
public static class TokenChoices
{
	/// <summary>Durations, written in the largest exact unit and carried in seconds.</summary>
	/// <param name="seconds">The offered spans, in seconds.</param>
	/// <exception cref="ArgumentNullException"><paramref name="seconds"/> is <c>null</c>.</exception>
	public static IReadOnlyList<TokenChoice> Durations(params int[] seconds)
	{
		ArgumentNullException.ThrowIfNull(seconds);

		return [.. seconds.Select(value => new TokenChoice(TokenFormat.Duration(value), TokenFormat.Carry(value)))];
	}

	/// <summary>Durations offered and carried in minutes, for the settings the schema stores that way.</summary>
	/// <param name="minutes">The offered spans, in minutes.</param>
	/// <exception cref="ArgumentNullException"><paramref name="minutes"/> is <c>null</c>.</exception>
	public static IReadOnlyList<TokenChoice> DurationsInMinutes(params int[] minutes)
	{
		ArgumentNullException.ThrowIfNull(minutes);

		return
		[
			.. minutes.Select(value => new TokenChoice(TokenFormat.DurationFromMinutes(value), TokenFormat.Carry(value * 60)))
		];
	}

	/// <summary>Percentages, written with the sign and carried as 0-100.</summary>
	/// <param name="percents">The offered proportions.</param>
	/// <exception cref="ArgumentNullException"><paramref name="percents"/> is <c>null</c>.</exception>
	public static IReadOnlyList<TokenChoice> Percentages(params double[] percents)
	{
		ArgumentNullException.ThrowIfNull(percents);

		return [.. percents.Select(value => new TokenChoice(TokenFormat.Percent(value), TokenFormat.Carry(value)))];
	}

	/// <summary>Quantities sharing one unit.</summary>
	/// <param name="unit">The unit, written after every value.</param>
	/// <param name="values">The offered quantities.</param>
	/// <exception cref="ArgumentNullException"><paramref name="values"/> is <c>null</c>.</exception>
	public static IReadOnlyList<TokenChoice> Numbers(string unit, params double[] values)
	{
		ArgumentNullException.ThrowIfNull(values);

		return [.. values.Select(value => new TokenChoice(TokenFormat.Number(value, unit), TokenFormat.Carry(value)))];
	}

	/// <summary>Named options, written as given and carried as given.</summary>
	/// <param name="options">Each option's words and the value it stands for.</param>
	/// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
	public static IReadOnlyList<TokenChoice> Of(params (string Text, string Value)[] options)
	{
		ArgumentNullException.ThrowIfNull(options);

		return [.. options.Select(option => new TokenChoice(option.Text, option.Value))];
	}
}
