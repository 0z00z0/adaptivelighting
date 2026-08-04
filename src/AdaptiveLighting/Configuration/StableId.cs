using System.Globalization;
using System.Text;

namespace AdaptiveLighting.Configuration;

/// <summary>
///     Generates the ids this document's own cross-references use: a slug of whatever the thing was called when
///     it was created, plus a random disambiguator.
/// </summary>
/// <remarks>
///     Read by a person hand-editing the YAML, so it is not a GUID. Minted once and never re-derived: the slug is
///     a snapshot of the name at creation, not a function of the current name, which is the whole point of the id.
/// </remarks>
public static class StableId
{
	private const int SuffixLength = 4;
	private const int MaxSlugLength = 24;
	private const string SuffixAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

	/// <summary>Used when the seed slugs to nothing at all: an unnamed period, an option of punctuation.</summary>
	private const string FallbackSlug = "id";

	// Norwegian, Swedish, Danish and German letters do not decompose under FormD, so they would each become a
	// separator and "kveld på hytta" would slug as "kveld-p--hytta".
	private static readonly Dictionary<char, string> Transliterations = new()
	{
		['æ'] = "ae", ['ø'] = "oe", ['å'] = "aa",
		['ä'] = "ae", ['ö'] = "oe", ['ü'] = "ue", ['ß'] = "ss",
		['ð'] = "d", ['þ'] = "th"
	};

	/// <summary>An id seeded from <paramref name="seed"/> that none of <paramref name="taken"/> already holds.</summary>
	/// <remarks>The generated id is added to <paramref name="taken"/>, so a loop over a list cannot collide with itself.</remarks>
	public static string Create(string? seed, ISet<string> taken)
	{
		ArgumentNullException.ThrowIfNull(taken);

		string slug = Slug(seed);

		while (true)
		{
			string candidate = slug + "-" + Suffix();

			if (taken.Add(candidate))
				return candidate;
		}
	}

	/// <summary>The lower-case ASCII stem of <paramref name="text"/>, or <see cref="FallbackSlug"/> when nothing survives.</summary>
	public static string Slug(string? text)
	{
		if (text is not { Length: > 0 })
			return FallbackSlug;

		StringBuilder builder = new(text.Length);
		bool lastWasSeparator = true;

		foreach (char raw in text.Trim().ToLower(CultureInfo.InvariantCulture).Normalize(NormalizationForm.FormD))
		{
			// FormD splits an accented letter into the letter plus a combining mark; the mark is dropped, so
			// "Kjøkken" and "Kjokken" slug alike apart from the transliteration above.
			if (CharUnicodeInfo.GetUnicodeCategory(raw) is UnicodeCategory.NonSpacingMark)
				continue;

			if (Transliterations.TryGetValue(raw, out string? replacement))
			{
				builder.Append(replacement);
				lastWasSeparator = false;
				continue;
			}

			if (char.IsAsciiLetterOrDigit(raw))
			{
				builder.Append(raw);
				lastWasSeparator = false;
				continue;
			}

			if (!lastWasSeparator && builder.Length > 0)
			{
				builder.Append('-');
				lastWasSeparator = true;
			}
		}

		string slug = builder.ToString().Trim('-');

		if (slug.Length > MaxSlugLength)
			slug = slug[..MaxSlugLength].TrimEnd('-');

		return slug.Length > 0 ? slug : FallbackSlug;
	}

	private static string Suffix()
	{
		Span<char> characters = stackalloc char[SuffixLength];

		for (int index = 0; index < SuffixLength; index++)
			characters[index] = SuffixAlphabet[Random.Shared.Next(SuffixAlphabet.Length)];

		return new string(characters);
	}
}
