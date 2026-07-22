using System.Globalization;
using System.Text.Json;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Extensions;

/// <summary>
///     Reads values out of an <see cref="EntityState"/> and its attribute bag, tolerantly.
/// </summary>
/// <remarks>
///     The attribute bag is <c>Dictionary&lt;string, object&gt;</c> deserialised from JSON, so a value may arrive
///     as a <see cref="JsonElement"/>, a boxed primitive, or a string, depending on how it was produced. Every
///     read here handles all three rather than casting and hoping. Nothing throws: a state that is <c>null</c>, an
///     attribute that is missing or the wrong shape is indistinguishable from one that was never set, and both
///     mean "don't know" — <c>null</c>, <c>false</c> or an empty list. These bodies are the engine's former
///     <c>AttributeReader</c>, lifted here verbatim so every app can read the same way.
/// </remarks>
public static class EntityStateExtensions
{
	/// <summary>Reads a numeric attribute, or <c>null</c> when the state or attribute is absent or not a number.</summary>
	public static double? AttrDouble(this EntityState? state, string attribute)
	{
		if (!TryGetValue(state, attribute, out object? value))
			return null;

		return value switch
		{
			JsonElement { ValueKind: JsonValueKind.Number } element => element.GetDouble(),
			JsonElement { ValueKind: JsonValueKind.String } element when TryParse(element.GetString(), out double parsed) => parsed,
			double number => number,
			float number => number,
			long number => number,
			int number => number,
			string text when TryParse(text, out double parsed) => parsed,
			_ => null
		};

		static bool TryParse(string? text, out double parsed) =>
			double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
	}

	/// <summary>Reads a string attribute, or <c>null</c> when the state or attribute is absent.</summary>
	public static string? AttrString(this EntityState? state, string attribute)
	{
		if (!TryGetValue(state, attribute, out object? value))
			return null;

		return value switch
		{
			JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
			string text => text,
			_ => null
		};
	}

	/// <summary>Reads an attribute holding a list of strings. Returns an empty list when absent or not a list.</summary>
	public static IReadOnlyList<string> AttrStringList(this EntityState? state, string attribute)
	{
		if (!TryGetValue(state, attribute, out object? value))
			return [];

		switch (value)
		{
			case JsonElement { ValueKind: JsonValueKind.Array } element:
				return [.. element.EnumerateArray()
					.Where(item => item.ValueKind == JsonValueKind.String)
					.Select(item => item.GetString()!)];

			case JsonElement { ValueKind: JsonValueKind.String } element:
				return element.GetString() is { } single ? [single] : [];

			case string text:
				return [text];

			case IEnumerable<string> strings:
				return [.. strings];

			case System.Collections.IEnumerable items:
				return [.. items.OfType<object>()
					.Select(item => Convert.ToString(item, CultureInfo.InvariantCulture) ?? "")
					.Where(item => item.Length > 0)];

			default:
				return [];
		}
	}

	/// <summary>
	///     Reads an attribute holding an ISO-8601 timestamp, or <c>null</c> when absent or unparseable.
	/// </summary>
	/// <remarks>
	///     Parsed with the invariant culture and <see cref="DateTimeStyles.AssumeUniversal"/> |
	///     <see cref="DateTimeStyles.AdjustToUniversal"/>, matching how Home Assistant publishes <c>sun.sun</c>'s
	///     <c>next_rising</c>/<c>next_setting</c>. The returned offset is therefore in UTC; call
	///     <see cref="DateTimeOffset.ToLocalTime"/> for wall-clock terms.
	/// </remarks>
	public static DateTimeOffset? AttrDateTimeOffset(this EntityState? state, string attribute) =>
		state.AttrString(attribute) is { } text
		&& DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
			? parsed
			: null;

	/// <summary>The state parsed as a number (invariant culture), or <c>null</c> when absent or not a number.</summary>
	public static double? StateAsDouble(this EntityState? state) =>
		state?.State is { } text
		&& double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
			? value
			: null;

	/// <summary>
	///     Whether the entity is something Home Assistant can act on right now: it has a state, and that state is
	///     not <c>unavailable</c>.
	/// </summary>
	/// <remarks>
	///     Matches the engine's <c>AreaEntityResolver.IsLive</c> exactly — a <c>null</c> state (a registry row with
	///     no device) and <c>unavailable</c> are both dropped; <c>unknown</c> is deliberately <b>not</b> checked,
	///     so a sensor reporting <c>unknown</c> still counts as available.
	/// </remarks>
	public static bool IsAvailable(this EntityState? state) =>
		state is not null && !string.Equals(state.State, "unavailable", StringComparison.OrdinalIgnoreCase);

	/// <summary>Whether the state equals <paramref name="value"/>, ordinal-ignore-case. <c>false</c> when the state is <c>null</c>.</summary>
	public static bool StateIs(this EntityState? state, string value) =>
		string.Equals(state?.State, value, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	///     The state as a value worth acting on: the trimmed state string, or <c>null</c> when the state is
	///     absent, <c>unknown</c> or <c>unavailable</c>.
	/// </summary>
	/// <remarks>
	///     The single guard the house-mode readers share (the mode select, the reset <c>input_datetime</c>, the
	///     UI's current-value read). <c>unknown</c> and <c>unavailable</c> both mean "don't act on this" — unlike
	///     <see cref="IsAvailable"/>, which lets <c>unknown</c> through — so a select sitting on <c>unknown</c>
	///     classifies as no mode rather than as its literal text.
	/// </remarks>
	public static string? AsUsableState(this EntityState? state)
	{
		string? raw = state?.State;
		return raw is null or "unknown" or "unavailable" ? null : raw.Trim();
	}

	private static bool TryGetValue(EntityState? state, string attribute, out object? value)
	{
		value = null;
		return state?.Attributes?.TryGetValue(attribute, out value) == true && value is not null;
	}
}
