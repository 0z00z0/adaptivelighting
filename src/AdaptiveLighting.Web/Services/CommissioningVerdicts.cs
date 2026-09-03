using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>Which colour a roll-call note carries.</summary>
public enum VerdictTone
{
	/// <summary>A neutral fact about the room.</summary>
	Info,

	/// <summary>Something needs a person: the amber family, used sparingly.</summary>
	Warn
}

/// <summary>One note on a roll-call row.</summary>
public sealed record Verdict(string Text, VerdictTone Tone);

/// <summary>One proposed room as the roll-call draws it: the counts, the notes, and the sentences it unfolds.</summary>
/// <remarks>
///     Assembled once per document, never on the dashboard's one-second tick, which would run
///     <c>AreaEntityResolver</c> per room per second. What moves is read off <see cref="Resolved"/> at render time.
///     The <see cref="AreaConfig"/> is not carried, so a component cannot edit around the commit button.
/// </remarks>
/// <param name="Key">The room's identity across the board, the draft and the document.</param>
/// <param name="AreaId">The registry area id, or <c>null</c> for a room configured with explicit entities.</param>
/// <param name="Notes">The row's verdict chips, worst first. Empty means the row says <see cref="CommissioningVerdicts.ReadyWord"/>.</param>
/// <param name="Sentences">The room's behaviour, through <see cref="AreaSentences.ForArea"/>, rendered read-only.</param>
/// <param name="Resolved">What discovery makes of the room, or <c>null</c> when it cannot resolve at all.</param>
public sealed record CommissioningRow(
	string Key,
	string? AreaId,
	string Name,
	int LightCount,
	int MotionCount,
	IReadOnlyList<Verdict> Notes,
	IReadOnlyList<Sentence> Sentences,
	ResolvedArea? Resolved);

/// <summary>
///     What each proposed room has to say for itself before it is switched on, and the lines under the table
///     that say once what a whole group of them has in common.
/// </summary>
/// <remarks>
///     Every note is read off the document, never re-derived. Nothing here decides whether a room would light;
///     that is the engine's answer.
/// </remarks>
public static class CommissioningVerdicts
{
	/// <summary>The word a row with nothing to say carries.</summary>
	public const string ReadyWord = "Ready";

	/// <summary>The note a room with no motion sensor carries, which is how the table tells the two kinds apart.</summary>
	public const string SwitchDrivenNote = "no motion sensor — it lights at the wall, never by itself";

	/// <summary>The notes one proposed room earns, worst first.</summary>
	/// <param name="area">The proposed room, whose <c>null</c> properties mean "inherit".</param>
	/// <param name="suspectCount">How many of the room's commanded lights <c>LightAudit</c> flags.</param>
	/// <param name="motionCount">How many motion sensors resolve; none makes the room switch-driven.</param>
	/// <returns>The notes, worst first. Empty means the row says <see cref="ReadyWord"/>.</returns>
	public static IReadOnlyList<Verdict> For(
		AreaConfig area,
		AreaSettings defaults,
		int luxSensorCount,
		int suspectCount,
		int lightCount,
		int motionCount)
	{
		ArgumentNullException.ThrowIfNull(area);
		ArgumentNullException.ThrowIfNull(defaults);

		AreaSettings effective = area.Effective(defaults);
		List<Verdict> notes = [];

		// Must come first and return: no lights means no suspects, and the remaining notes are about settings, so
		// the list would come back empty. An empty list is how this table says the room is good to go.
		if (lightCount == 0)
		{
			notes.Add(new Verdict("no lights found — switching this on will do nothing", VerdictTone.Warn));

			return notes;
		}

		// Ahead of the suspects: this one is about the whole room rather than some of its lights, and a row that
		// buried it among the info chips would read as an ordinary room.
		if (motionCount == 0)
			notes.Add(new Verdict(SwitchDrivenNote, VerdictTone.Warn));

		if (suspectCount > 0 && lightCount > 0)
		{
			notes.Add(new Verdict(
				suspectCount == 1
					? $"1 of {lightCount} lights looks like something else"
					: $"{suspectCount} of {lightCount} lights look like something else",
				VerdictTone.Warn));
		}

		// A pinned LuxSensor reads one sensor whatever discovery found, so there is no average to warn about.
		if (luxSensorCount > 1 && area.LuxSensor is not { Length: > 0 })
			notes.Add(new Verdict($"reads the average of {luxSensorCount} sensors", VerdictTone.Info));

		if (SleepSteps.Of(effective) is not SleepStep.Normal)
			notes.Add(new Verdict("quiet while the house sleeps", VerdictTone.Info));

		if (effective.WelcomeHome)
			notes.Add(new Verdict("welcomes you home", VerdictTone.Info));

		if (effective.SkipAwaySweep)
			notes.Add(new Verdict("stays on when everyone leaves", VerdictTone.Info));

		return notes;
	}

	/// <summary>
	///     Whether a room judges darkness by a light-level sensor it does not have, which the engine reads as
	///     "dark", so movement alone lights the room whatever the hour.
	/// </summary>
	/// <param name="motionCount">
	///     How many motion sensors resolve. None means nothing ever asks the darkness gate, so the room has no
	///     consequence to warn about.
	/// </param>
	public static bool CountsAsDarkForWantOfASensor(
		AreaConfig area,
		AreaSettings defaults,
		int luxSensorCount,
		int motionCount)
	{
		ArgumentNullException.ThrowIfNull(area);
		ArgumentNullException.ThrowIfNull(defaults);

		// Either is retired but still parses, and IlluminanceGate answers it as Lux in every arm, so any predicate
		// over Darkness has to name it alongside Lux.
		return motionCount > 0
			&& luxSensorCount == 0
			&& area.LuxSensor is not { Length: > 0 }
			&& area.Effective(defaults).Darkness is DarknessSource.Lux or DarknessSource.Either;
	}

	/// <summary>The line under the table about the rooms with no light-level sensor, said once for the house.</summary>
	/// <param name="count">How many rooms <see cref="CountsAsDarkForWantOfASensor"/> is true of.</param>
	public static string? NoSensorLine(int count) => count switch
	{
		<= 0 => null,
		1 => "One room has no light-level sensor, so it counts as dark all day — movement alone lights it. "
			+ "Give it one in Home Assistant, or set how it decides it is dark on its own page.",
		_ => $"{count} rooms have no light-level sensor, so they count as dark all day — movement alone lights "
			+ "them. Give them one in Home Assistant, or set how they decide they are dark on their own pages."
	};

	/// <summary>The line under the table about the rooms with no motion sensor, said once for the house.</summary>
	/// <param name="count">How many proposed rooms have lights but nothing that senses movement.</param>
	public static string? SwitchDrivenLine(int count) => count switch
	{
		<= 0 => null,
		1 => "One room has no motion sensor, so it never lights itself. Switching it on at the wall lights it, "
			+ "and it goes off when that hold runs out.",
		_ => $"{count} rooms have no motion sensor, so they never light themselves. Switching one on at the wall "
			+ "lights it, and it goes off when that hold runs out."
	};

	/// <summary>What the commit button says, which is also the whole progress model.</summary>
	public static string CommitLabel(int picked) => picked switch
	{
		0 => "Switch on the rooms you pick",
		1 => "Switch on 1 room",
		_ => $"Switch on {picked} rooms"
	};

	/// <summary>The line under the button about the rooms left off, saying where they went.</summary>
	public static string? RestLine(int picked, int total)
	{
		int rest = total - picked;

		return rest switch
		{
			<= 0 => null,
			1 => "The other room stays listed under House, with its own switch.",
			_ => $"The other {rest} stay listed under House, each with its own switch."
		};
	}
}
