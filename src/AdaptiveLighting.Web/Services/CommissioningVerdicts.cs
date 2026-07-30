using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>Which colour a roll-call note carries — the state families the rest of the app already speaks.</summary>
public enum VerdictTone
{
	/// <summary>A fact about the room. Neutral; the everyday case.</summary>
	Info,

	/// <summary>Something needs a human. The amber family, used sparingly.</summary>
	Warn
}

/// <summary>One note on a roll-call row.</summary>
/// <param name="Text">What it says, in the words the row has room for.</param>
/// <param name="Tone">Which colour it carries.</param>
public sealed record Verdict(string Text, VerdictTone Tone);

/// <summary>
///     One proposed room as the roll-call draws it: the counts, the notes, and the sentences its unfold shows.
/// </summary>
/// <remarks>
///     <para>
///         Assembled once per document, not once per second. The board re-renders on the dashboard's one-second
///         tick, and rebuilding this would put a full <c>AreaEntityResolver</c> run per room per tick behind a
///         page nobody is reading that fast. What genuinely moves — the room's live light level — is read off
///         <see cref="Resolved"/> at render time, which costs one state read per sensor.
///     </para>
///     <para>
///         The <see cref="AreaConfig"/> itself is deliberately not carried. The row is what the table draws; the
///         document is the board's, and handing a component a mutable room would invite an edit outside the one
///         write path the commit button is.
///     </para>
/// </remarks>
/// <param name="Key">The room's identity across the board, the draft and the document — <see cref="CommissioningDraft.RoomKey"/>.</param>
/// <param name="AreaId">The registry area id, or <c>null</c> for a room configured with explicit entities.</param>
/// <param name="Name">The room's display name, as every other surface says it.</param>
/// <param name="LightCount">How many lights the room would command.</param>
/// <param name="MotionCount">How many motion sensors it resolves.</param>
/// <param name="LuxCount">How many illuminance sensors it resolves.</param>
/// <param name="Notes">The row's verdict chips, worst first. Empty means the row says <see cref="CommissioningVerdicts.ReadyWord"/>.</param>
/// <param name="Sentences">
///     The room's behaviour, through <see cref="AreaSentences.ForArea"/> — the real token machinery, rendered
///     read-only, so the deferred editable unfold is a switch rather than a rewrite (§2.5).
/// </param>
/// <param name="Resolved">What discovery makes of the room, or <c>null</c> when it cannot resolve at all.</param>
public sealed record CommissioningRow(
	string Key,
	string? AreaId,
	string Name,
	int LightCount,
	int MotionCount,
	int LuxCount,
	IReadOnlyList<Verdict> Notes,
	IReadOnlyList<Sentence> Sentences,
	ResolvedArea? Resolved);

/// <summary>
///     What each proposed room has to say for itself before anybody switches it on, and what the table says
///     underneath about the rooms that are not in it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every note here is read off the document, never re-derived.</b> The role guesses are the flags
///         <c>AreaAutoDiscovery</c> already wrote and <see cref="AreaSentences"/> already renders as a room's
///         fourth sentence, so "bedroom manners" on a row and the sentence in that row's unfold cannot disagree.
///         Nothing in this file decides whether a room would light: that is the engine's, and a page that
///         re-answered it would be a second opinion nobody reconciles.
///     </para>
///     <para>
///         <b>"Ready" is quiet.</b> A row with nothing to say says one muted word, not a green tick — seventeen
///         celebrations is the reassurance dashboard this design refuses (§8). The phone drops the word entirely,
///         which is a CSS decision rather than one made here: the projection stays the same at both widths so
///         nothing has to be asserted twice.
///     </para>
///     <para>
///         Pure, and asserted rather than screenshotted, like every other projection here.
///     </para>
/// </remarks>
public static class CommissioningVerdicts
{
	/// <summary>The word a row with nothing to say carries.</summary>
	public const string ReadyWord = "Ready";

	/// <summary>
	///     The notes one proposed room earns.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Ordered worst-first: a light that looks like a router LED is the one thing on the row somebody has to
	///         act on, and a reader scanning seventeen rows reads the first chip on each. The role guesses follow,
	///         because they explain rather than ask.
	///     </para>
	///     <para>
	///         The light-level note is about the room's <i>darkness source</i>, not about its sensor count alone: a
	///         room set to <see cref="DarknessSource.Sun"/> or <see cref="DarknessSource.Always"/> is not missing
	///         anything by having no sensor, and telling it so would be manufacturing a problem out of a setting.
	///     </para>
	/// </remarks>
	/// <param name="area">The proposed room, whose <c>null</c> properties mean "inherit".</param>
	/// <param name="defaults">The document's all-rooms settings.</param>
	/// <param name="luxSensorCount">How many illuminance sensors discovery finds in the room.</param>
	/// <param name="suspectCount">How many of the room's commanded lights <c>LightAudit</c> flags. 0 until the impostor sheet ships.</param>
	/// <param name="lightCount">How many lights the room would command.</param>
	/// <returns>The notes, worst first. Empty means the row says <see cref="ReadyWord"/>.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static IReadOnlyList<Verdict> For(
		AreaConfig area,
		AreaSettings defaults,
		int luxSensorCount,
		int suspectCount,
		int lightCount)
	{
		ArgumentNullException.ThrowIfNull(area);
		ArgumentNullException.ThrowIfNull(defaults);

		AreaSettings effective = area.Effective(defaults);
		List<Verdict> notes = [];

		if (suspectCount > 0 && lightCount > 0)
		{
			notes.Add(new Verdict(
				suspectCount == 1
					? $"1 of {lightCount} lights looks like something else"
					: $"{suspectCount} of {lightCount} lights look like something else",
				VerdictTone.Warn));
		}

		// A room whose own LuxSensor is pinned reads one sensor whatever discovery found, so the ambiguity the
		// average note describes is already settled and saying it would be wrong.
		if (luxSensorCount > 1 && area.LuxSensor is not { Length: > 0 })
			notes.Add(new Verdict($"reads the average of {luxSensorCount} sensors", VerdictTone.Info));

		if (luxSensorCount == 0 && area.LuxSensor is not { Length: > 0 } && effective.Darkness == DarknessSource.Lux)
			notes.Add(new Verdict("no light-level sensor — counts as dark all day", VerdictTone.Info));

		if (effective.RespectSleepMode || effective.SleepBlocksAutoOn)
			notes.Add(new Verdict("bedroom manners", VerdictTone.Info));

		if (effective.WelcomeHome)
			notes.Add(new Verdict("welcomes you home", VerdictTone.Info));

		if (effective.SkipAwaySweep)
			notes.Add(new Verdict("stays on when everyone leaves", VerdictTone.Info));

		return notes;
	}

	/// <summary>
	///     The line under the table about rooms discovery looked at and left out, or <c>null</c> when there are
	///     none.
	/// </summary>
	/// <remarks>
	///     Discovery's rule is strict — a room needs a light <b>and</b> something that senses movement — and the
	///     rooms it refuses are simply absent, which reads as the app having missed them. One muted sentence turns
	///     an invisible decision into an inspectable one, and names the fix rather than the rule.
	/// </remarks>
	/// <param name="names">The rooms that have lights but nothing that senses movement, in display order.</param>
	/// <exception cref="ArgumentNullException"><paramref name="names"/> is <c>null</c>.</exception>
	public static string? NearMiss(IReadOnlyList<string> names)
	{
		ArgumentNullException.ThrowIfNull(names);

		if (names.Count == 0)
			return null;

		string subject = Join(names);
		string verb = names.Count == 1 ? "has lights but nothing that senses movement, so it sits" : "have lights but nothing that senses movement, so they sit";
		string them = names.Count == 1 ? "it" : "them";

		return $"{subject} {verb} this out — give {them} a motion sensor in Home Assistant and press Set up rooms again.";
	}

	/// <summary>
	///     What the commit button says, which is also the whole progress model: the button counts, and there is no
	///     progress bar anywhere.
	/// </summary>
	/// <param name="picked">How many rooms are switched on in the draft.</param>
	public static string CommitLabel(int picked) => picked switch
	{
		0 => "Switch on the rooms you pick",
		1 => "Switch on 1 room",
		_ => $"Switch on {picked} rooms"
	};

	/// <summary>
	///     The line under the button about the rooms left off, or <c>null</c> when every room was picked.
	/// </summary>
	/// <remarks>
	///     Says where they went, not merely how many there are. A room left off is not lost — it keeps its own
	///     switch under House — and somebody who does not know that reads the button as a one-way door.
	/// </remarks>
	/// <param name="picked">How many rooms are switched on.</param>
	/// <param name="total">How many rooms the table lists.</param>
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

	/// <summary>"a", "a and b", "a, b and c" — a list somebody reads rather than parses.</summary>
	private static string Join(IReadOnlyList<string> names) => names.Count switch
	{
		1 => names[0],
		2 => $"{names[0]} and {names[1]}",
		_ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}"
	};
}
