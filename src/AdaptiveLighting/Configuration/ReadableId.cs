using System.Globalization;

namespace Adaptive.Settings;

/// <summary>The highest number ever issued for each kind of id. Only ever raised, so a deleted id is never issued again.</summary>
public sealed class IdCounters
{
	public int Periods { get; set; }

	/// <summary>Rooms without a Home Assistant area only; a room with one is known by its area id.</summary>
	public int Rooms { get; set; }
}

/// <summary>The ids this document's own references use: a prefix and a running number, <c>period_001</c>.</summary>
public static class ReadableId
{
	public const string PeriodPrefix = "period_";

	public const string RoomPrefix = "room_";

	/// <summary>Three digits, widening past 999 without renaming an earlier id.</summary>
	public static string Format(string prefix, int number) =>
		prefix + number.ToString("D3", CultureInfo.InvariantCulture);

	/// <summary>The number in <paramref name="id"/> when it is written exactly as <see cref="Format"/> writes it, else <c>null</c>.</summary>
	public static int? NumberOf(string prefix, string? id)
	{
		ArgumentNullException.ThrowIfNull(prefix);

		if (id is null || !id.StartsWith(prefix, StringComparison.Ordinal))
			return null;

		string digits = id[prefix.Length..];

		if (digits.Length < 3 || !digits.All(char.IsAsciiDigit))
			return null;

		return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
			&& number > 0
			&& string.Equals(Format(prefix, number), id, StringComparison.Ordinal)
				? number
				: null;
	}

	/// <summary>The highest number among <paramref name="ids"/> of this kind, or 0.</summary>
	public static int HighestInUse(string prefix, IEnumerable<string?> ids)
	{
		ArgumentNullException.ThrowIfNull(ids);

		return ids.Select(id => NumberOf(prefix, id?.Trim()) ?? 0).DefaultIfEmpty(0).Max();
	}

	/// <summary>A new period id; <see cref="IdCounters.Periods"/> then holds its number.</summary>
	public static string NextPeriod(IdCounters counters, IEnumerable<TimePeriodConfig> periods)
	{
		ArgumentNullException.ThrowIfNull(counters);
		ArgumentNullException.ThrowIfNull(periods);

		counters.Periods = Math.Max(counters.Periods, HighestInUse(PeriodPrefix, periods.Select(period => period.Id))) + 1;

		return Format(PeriodPrefix, counters.Periods);
	}

	/// <summary>A new id for a room without an area; <see cref="IdCounters.Rooms"/> then holds its number.</summary>
	public static string NextRoom(IdCounters counters, IEnumerable<AreaConfig> areas)
	{
		ArgumentNullException.ThrowIfNull(counters);
		ArgumentNullException.ThrowIfNull(areas);

		counters.Rooms = Math.Max(counters.Rooms, HighestInUse(RoomPrefix, areas.Select(area => area.Id))) + 1;

		return Format(RoomPrefix, counters.Rooms);
	}
}
