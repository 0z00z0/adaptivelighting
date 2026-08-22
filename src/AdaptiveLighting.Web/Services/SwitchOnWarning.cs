using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>What a room's switch has to say for itself the moment it is turned on.</summary>
public sealed record SwitchOnNote(
	string Lead,
	IReadOnlyList<SuspectLight> Suspicious,
	IReadOnlyList<string> Others,
	string? Advice)
{
	/// <summary>Whether this note is a warning or a quiet remark: the amber edge, or the neutral one.</summary>
	public bool IsWarning => Suspicious.Count > 0;

	/// <summary>The lights that were not flagged, as one sentence, or <c>null</c> when every light was flagged.</summary>
	public string? OthersLine => Others.Count switch
	{
		0 => null,
		_ when Suspicious.Count == 0 => $"They are {Join(Others)}.",
		_ => $"The rest read as ordinary lights: {Join(Others)}."
	};

	/// <summary>"a", "a and b", "a, b and c".</summary>
	private static string Join(IReadOnlyList<string> names) => names.Count switch
	{
		1 => names[0],
		2 => $"{names[0]} and {names[1]}",
		_ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}"
	};
}

/// <summary>The note a room shows when somebody switches it on: what "on" now reaches, and which of it looks unlike room lighting.</summary>
/// <remarks>
///     Advisory and after the fact. The switch works first and the note appears under it; nothing here filters
///     anything, because <see cref="LightAudit"/> can only guess. The page owns dismissal and remembering.
/// </remarks>
public static class SwitchOnWarning
{
	/// <summary>The note for a room that was just switched on, or <c>null</c> when there is nothing worth saying.</summary>
	public static SwitchOnNote? For(string roomName, RoomLights lights, string? includeLabel)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(roomName);
		ArgumentNullException.ThrowIfNull(lights);

		IReadOnlyList<LightUnderReview> commanded = lights.Commanded;
		List<SuspectLight> suspicious = [];

		// ReasonFor, never Review: only Commanded is judged, counted and named, while InTheRoom is the sibling
		// context the colour-channel rule needs. A lamp reached through a group is absent from Commanded, and its
		// channels then match nothing.
		foreach (LightUnderReview light in commanded)
			if (LightAudit.ReasonFor(light, lights.InTheRoom) is { Length: > 0 } reason)
				suspicious.Add(new SuspectLight(light.EntityId, light.Name, reason));

		if (commanded.Count <= 1 && suspicious.Count == 0)
			return null;

		HashSet<string> flagged = new(suspicious.Select(suspect => suspect.EntityId), StringComparer.Ordinal);

		return new SwitchOnNote(
			Lead(roomName, commanded.Count, suspicious.Count),
			suspicious,
			[.. commanded.Where(light => !flagged.Contains(light.EntityId)).Select(light => light.Name)],
			suspicious.Count > 0 ? Advice(roomName, includeLabel) : null);
	}

	/// <summary>The first line: what the switch just did, and how much of it deserves a second look.</summary>
	private static string Lead(string roomName, int lightCount, int suspectCount)
	{
		if (lightCount == 1)
		{
			return suspectCount == 0
				? $"{roomName} is on, and will command 1 light."
				: $"{roomName} is on. The one light it will command looks like something other than room lighting.";
		}

		string commands = $"{roomName} is on, and will command {Count(lightCount, "light")}";

		if (suspectCount == 0)
			return $"{commands}.";

		if (suspectCount == lightCount)
			return $"{commands} — and none of them looks like room lighting.";

		return suspectCount == 1
			? $"{commands}. One of them looks like something other than room lighting."
			: $"{commands}. {suspectCount} of them look like something other than room lighting.";
	}

	private static string? Advice(string roomName, string? includeLabel) =>
		includeLabel is { Length: > 0 }
			? null
			: "Make a label in Home Assistant — “Room light” — put it on the lights you want managed, then name it "
				+ "under Finding lights & sensors, Only manage lights with. That one setting applies to every room "
				+ $"in the house, not only {roomName}: a light anywhere without the label stops being managed.";

	private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
