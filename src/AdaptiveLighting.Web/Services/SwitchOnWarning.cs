using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     What a room's switch has to say for itself the moment it is turned on.
/// </summary>
/// <param name="Lead">The first line: the room, and how many lights it now commands.</param>
/// <param name="Suspicious">
///     The lights that look like something other than room lighting, each with its reason. Empty when none does,
///     which is what makes a note quiet rather than a warning.
/// </param>
/// <param name="Others">
///     The names of the remaining lights, in the order the resolver settled them. Named rather than counted: a
///     number sends somebody hunting through the room, which is the dead end this note exists to end.
/// </param>
/// <param name="Advice">
///     How to narrow what the room commands, or <c>null</c> when the house already has an include label and the
///     advice would be telling somebody to do what they have done.
/// </param>
public sealed record SwitchOnNote(
	string Lead,
	IReadOnlyList<SuspectLight> Suspicious,
	IReadOnlyList<string> Others,
	string? Advice)
{
	/// <summary>Whether this note is a warning rather than a quiet remark — the amber edge, or the neutral one.</summary>
	public bool IsWarning => Suspicious.Count > 0;

	/// <summary>
	///     The lights that were not flagged, as one sentence, or <c>null</c> when every light was flagged.
	/// </summary>
	/// <remarks>
	///     Every one of them, written out, however many there are. A list that stopped at three and said "and 22
	///     others" would leave somebody doing exactly the hunt this note exists to save them — and the count is
	///     already in <see cref="Lead"/>, so a truncated list would add nothing at all.
	/// </remarks>
	public string? OthersLine => Others.Count switch
	{
		0 => null,
		_ when Suspicious.Count == 0 => $"They are {Join(Others)}.",
		_ => $"The rest read as ordinary lights: {Join(Others)}."
	};

	/// <summary>"a", "a and b", "a, b and c" — a list somebody reads rather than parses.</summary>
	private static string Join(IReadOnlyList<string> names) => names.Count switch
	{
		1 => names[0],
		2 => $"{names[0]} and {names[1]}",
		_ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}"
	};
}

/// <summary>
///     The note a room shows when somebody switches it on.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why it exists.</b> A Home Assistant area holds whatever Home Assistant put in it, and on one live
///         house that is 34 <c>light.*</c> entities in the living room — access-point status LEDs, relay-board
///         indicators, five colour channels of a lamp already managed under its own name, and the fridge. Nothing
///         warned about that; the room simply started commanding all of them. The owner asked for a warning and
///         explicitly not a filter, because <see cref="LightAudit"/> can only guess.
///     </para>
///     <para>
///         <b>Advisory, and after the fact.</b> The switch works first and the note appears under it, rather than
///         a dialog standing between a thumb and a switch. Three reasons: switching a room on is reversible and
///         commands nothing until somebody walks into the room, so there is no harm to interrupt; somebody who
///         knows their house carries on in one action, which was the requirement; and a confirmation step would
///         tax every honest two-lamp room forever to catch the rare thirty-four-entity one.
///     </para>
///     <para>
///         <b>Two tiers, so it is useful rather than nagging.</b> A room where something is flagged gets the amber
///         warning and the advice. A room where nothing is flagged but more than one light will be commanded gets
///         the same list with no colour and no advice — the point there is only to show what "on" now means. A
///         single unflagged light says nothing at all: there is no doubt to raise.
///     </para>
///     <para>
///         Pure, and every string asserted rather than screenshotted — there is no Razor render harness here. The
///         page owns dismissal and remembering, which is the part that keeps it from asking twice.
///     </para>
/// </remarks>
public static class SwitchOnWarning
{
	/// <summary>
	///     The note for a room that was just switched on, or <c>null</c> when there is nothing worth saying.
	/// </summary>
	/// <param name="roomName">The room, as every other surface calls it.</param>
	/// <param name="lights">Every light the room would command, as the resolver settled it.</param>
	/// <param name="includeLabel">
	///     <see cref="GlobalConfig.IncludeLabel"/> as it stands. Set, the household has already told the app which
	///     lights it may manage, so the advice is dropped rather than repeated at them.
	/// </param>
	/// <returns>The note, or <c>null</c> for one light with nothing wrong with it.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static SwitchOnNote? For(string roomName, IReadOnlyList<LightUnderReview> lights, string? includeLabel)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(roomName);
		ArgumentNullException.ThrowIfNull(lights);

		IReadOnlyList<SuspectLight> suspicious = LightAudit.Review(lights);

		if (lights.Count <= 1 && suspicious.Count == 0)
			return null;

		HashSet<string> flagged = new(suspicious.Select(suspect => suspect.EntityId), StringComparer.Ordinal);

		return new SwitchOnNote(
			Lead(roomName, lights.Count, suspicious.Count),
			suspicious,
			[.. lights.Where(light => !flagged.Contains(light.EntityId)).Select(light => light.Name)],
			suspicious.Count > 0 ? Advice(roomName, includeLabel) : null);
	}

	/// <summary>
	///     The first line: what the switch just did, and how much of it deserves a second look.
	/// </summary>
	/// <remarks>
	///     Present tense and plain: the room is on, this is what "on" reaches. The count is said out loud because
	///     "34 lights" is itself the surprise in the case this was written for.
	/// </remarks>
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

	/// <summary>
	///     The way to narrow what every room commands, named where it actually lives.
	/// </summary>
	/// <remarks>
	///     House-wide, and it says so. <see cref="GlobalConfig.IncludeLabel"/> is one setting for the whole house,
	///     so somebody applying this advice to fix one room changes every room — including the ones that are
	///     working, which stop working the moment their lights are not labelled. That is the sentence somebody
	///     needs before they act, not after.
	/// </remarks>
	private static string? Advice(string roomName, string? includeLabel) =>
		includeLabel is { Length: > 0 }
			? null
			: "Make a label in Home Assistant — “Room light” — put it on the lights you want managed, then name it "
				+ "under Finding lights & sensors, Only manage lights with. That one setting applies to every room "
				+ $"in the house, not only {roomName}: a light anywhere without the label stops being managed.";

	private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
