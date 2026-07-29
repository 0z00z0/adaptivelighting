using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     One line of the room page's evidence table.
/// </summary>
/// <param name="Label">What the fact is called, down the left.</param>
/// <param name="Value">The reading itself — short enough to be read at a glance.</param>
/// <param name="Title">A fuller explanation for the hover, or <c>null</c> when the value speaks for itself.</param>
/// <param name="Detail">
///     The measurement behind the value, rendered as a quieter second line, or <c>null</c> when there is none.
/// </param>
/// <remarks>
///     <b>Why <paramref name="Detail"/> is a field and not just a longer <paramref name="Value"/>.</b> The
///     darkness row used to read <i>"dark enough — lux 4 (mean of 2 of 2 sensors), dark below 1000"</i> as one
///     string: a two-word answer and its whole working, in one monospaced run that wrapped across three lines on
///     a phone and buried the answer in the middle of it. The verdict is what somebody scanning the table needs;
///     the reading is what they need next, and only if the verdict surprised them. Splitting them lets the eye
///     take the first column of answers straight down and stop where it wants to.
/// </remarks>
public sealed record RoomFact(string Label, string Value, string? Title = null, string? Detail = null);

/// <summary>
///     What the room page says about a room right now: the present-tense line, what happens next, and the
///     table of what the engine actually saw.
/// </summary>
/// <remarks>
///     <para>
///         This is the detective's half of the page. The owner's question is "why didn't that light come on",
///         and the answer is a measured reading beside the threshold it was compared against — so the darkness
///         row carries the engine's own words rather than a second opinion assembled here, which would
///         eventually disagree with the one the engine acted on.
///     </para>
///     <para>
///         Pure, and every string is asserted rather than screenshotted: there is no Razor render harness in
///         this repo. Dates are passed a <c>now</c> instead of reading the clock, so "2 min ago" is a function
///         of two instants and a test can pin it.
///     </para>
/// </remarks>
public static class RoomFacts
{
	/// <summary>
	///     How long past a deadline a room has to be before the page stops counting down and says it has lost
	///     touch instead. Matches the dashboard card's tolerance, so the two never disagree about one room.
	/// </summary>
	public static readonly TimeSpan OverdueAfter = TimeSpan.FromSeconds(90);

	/// <summary>
	///     What the engine saw, as the table the design calls <i>Right now — what the engine saw</i>.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>Ordered by the question that brought somebody here</b>, which is almost always "why is this room
	///         dark". So darkness leads, then whether walking in would change that, and only then the readings that
	///         are merely context. It used to open with State and Lights — true, but an answer to a question
	///         nobody had, sitting above the one they did.
	///     </para>
	///     <para>
	///         <b>There is no State row.</b> The page header carries the state chip and the headline sentence an
	///         inch above this table, so a row reading "watching — no lights commanded, waiting for movement" was
	///         the third telling of one fact on one screen. Dropping it is the single biggest thing that made the
	///         table scannable.
	///     </para>
	///     <para>
	///         The master-switch row appears only when the switch is off, and outranks even darkness: while it is
	///         off the engine commands nothing anywhere, and a table that answered "is it dark enough" without
	///         saying so would send somebody hunting a room-level fault that is not there.
	///     </para>
	/// </remarks>
	/// <param name="snapshot">The room's most recent report.</param>
	/// <param name="now">The reader's present, for the relative ages.</param>
	/// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <c>null</c>.</exception>
	public static IReadOnlyList<RoomFact> For(AreaSnapshot snapshot, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		List<RoomFact> facts = [];

		if (snapshot.KillSwitchActive)
		{
			facts.Add(new RoomFact(
				"Master switch",
				"off — nothing will change",
				"Adaptive lighting is switched off for the whole house."));
		}

		facts.Add(new RoomFact(
			"Dark enough?",
			DarknessVerdict(snapshot),
			DarknessTitle(snapshot),
			snapshot.DarknessDetail is { Length: > 0 } detail ? detail : null));

		if (AutoOnNote(snapshot) is { Length: > 0 } blocked)
		{
			facts.Add(new RoomFact(
				"If someone walks in",
				blocked,
				"Whether walking in would switch these lights on, as the engine judged it when it reported."));
		}

		facts.Add(new RoomFact("Lights", Reading(snapshot), LightsTitle(snapshot)));

		facts.Add(snapshot.LastMotionAt is { } motion
			? new RoomFact("Last movement", Stamp(motion, now), $"Movement was last seen at {Clock(motion)}.")
			: new RoomFact("Last movement", "none seen", "No movement has been reported since the engine started."));

		facts.Add(snapshot.LastCommandAt is { } command
			? new RoomFact("Last changed", Stamp(command, now), $"The engine last changed these lights at {Clock(command)}.")
			: new RoomFact("Last changed", "not yet", "The engine has not changed these lights since it started."));

		facts.Add(new RoomFact(
			"Time of day",
			snapshot.PeriodName is { Length: > 0 } period ? period : "nothing scheduled",
			"The schedule period this room is in. It sets brightness and warmth."));

		return facts;
	}

	/// <summary>
	///     The room's present tense: what the lights are doing and why, in the dashboard card's own words.
	/// </summary>
	/// <remarks>
	///     Present tense, deliberately, where the activity log is past tense. The log answers "what happened";
	///     this answers "what is true now", and somebody standing in the room comparing the two needs them to be
	///     different sentences rather than one sentence twice.
	/// </remarks>
	/// <param name="snapshot">The room's most recent report.</param>
	/// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <c>null</c>.</exception>
	public static string Headline(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return snapshot switch
		{
			{ KillSwitchActive: true } => "Paused by the master switch — no lights will change until it is turned back on.",
			{ State: AreaState.AutoActive, LastCommandAt: null } =>
				"These lights were already on when the engine started. They're managed now — their levels weren't touched.",
			{ State: AreaState.AutoActive } => Levels("Lit at", snapshot),
			{ State: AreaState.PreOff } => Levels("Dimmed to", snapshot, " as a warning"),
			{ State: AreaState.OverriddenOn } => "Someone set these lights manually — they're being left alone.",
			{ State: AreaState.SuppressedOff } => "Someone switched these lights off. Movement is ignored for now.",
			{ State: AreaState.SceneHold } => "A scene is holding this room. The engine stands back until the scene lets go.",
			{ State: AreaState.Away, BrightnessPct: not null } => "Nobody home. This room keeps its lights on.",
			{ State: AreaState.Away } => "Nobody home.",
			{ State: AreaState.Disabled } => "This room never changes by itself.",
			{ State: AreaState.AutoVacant, IsDark: false } => "Off, watching. Too bright to switch on right now.",
			{ State: AreaState.AutoVacant, LastCommandAt: null } => "Watching for movement. The lights haven't been touched yet.",
			_ => "Off, watching for movement."
		};
	}

	/// <summary>
	///     What happens next, from the deadline the engine armed, or <c>null</c> when nothing is pending.
	/// </summary>
	/// <remarks>
	///     A deadline long past that no new report replaced means the page has lost touch, and it says that
	///     instead of counting down into the negative. A missing deadline is a missing line, never an invented one.
	/// </remarks>
	/// <param name="snapshot">The room's most recent report.</param>
	/// <param name="now">The reader's present.</param>
	/// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <c>null</c>.</exception>
	public static string? NextLine(AreaSnapshot snapshot, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		if (IsOverdue(snapshot, now))
		{
			return $"An update was due {Ago(snapshot.NextChangeAt!.Value, now)} and hasn't arrived. " +
				"The Home Assistant connection may be down.";
		}

		string? countdown = snapshot.NextChangeAt is { } due ? In(due, now) : null;

		// A gated room is answered before anything else its state would say. Every AutoVacant line below promises
		// that movement lights the room, and in a sleeping bedroom or a room whose television is on that promise
		// is false — which is the exact claim this field was added to stop the activity page making.
		if (snapshot.State == AreaState.AutoVacant && AutoOnNote(snapshot) is { Length: > 0 } blocked)
			return blocked;

		return snapshot.State switch
		{
			AreaState.AutoActive when countdown is not null => $"Starts dimming {countdown} unless someone moves.",
			AreaState.PreOff when countdown is not null => $"Lights out {countdown} — any movement keeps them on.",
			AreaState.OverriddenOn when countdown is not null => $"Back under automatic control {countdown}.",
			AreaState.SuppressedOff when countdown is not null =>
				$"Starts answering movement again {countdown}, sooner if the room stays quiet.",
			AreaState.AutoVacant when snapshot is { IsDark: false, DarknessDetail: { Length: > 0 } detail } =>
				$"Movement will light it once it's dark — {detail}.",
			AreaState.AutoVacant when snapshot.IsDark is false => "Movement will light it once it's dark.",
			AreaState.AutoVacant => "Movement in the dark turns the lights on.",
			AreaState.Away => "Wakes when the first person comes home.",
			AreaState.Disabled => "Nothing will be commanded until it is switched back on.",
			_ => null
		};
	}

	/// <summary>
	///     How much of the armed countdown is left, 1 down to 0, or <c>null</c> when there is no honest ring to
	///     draw: no deadline, no armed instant, a degenerate span, or a deadline the overdue line already covers.
	/// </summary>
	/// <param name="snapshot">The room's most recent report.</param>
	/// <param name="now">The reader's present.</param>
	/// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <c>null</c>.</exception>
	public static double? Countdown(AreaSnapshot snapshot, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		if (IsOverdue(snapshot, now)
			|| snapshot.NextChangeAt is not { } due
			|| snapshot.NextChangeFrom is not { } from
			|| due <= from)
		{
			return null;
		}

		return Math.Clamp((due - now).TotalSeconds / (due - from).TotalSeconds, 0, 1);
	}

	/// <summary>
	///     Why movement would not switch these lights on right now, or <c>null</c> when there is nothing to say.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Read off the verdict the engine published, never worked out again here from
	///         <c>RespectSleepMode</c>, <c>IgnoreWhenOn</c> and the house mode. The engine is the only thing that
	///         knows which gates it consulted, and a second copy of those rules in a page would drift from the one
	///         it acts on — which is precisely how the activity page came to promise lights that were never going
	///         to come on.
	///     </para>
	///     <para>
	///         <b>Only the two gates that are otherwise invisible.</b> A sleeping house and a blocking entity both
	///         leave the room in <see cref="AreaState.AutoVacant"/>, indistinguishable from a room simply waiting
	///         for somebody to walk in. The other refusals already have their own place on this page — the room's
	///         own switch, the master-switch row, the state chip, the darkness row — and repeating them here would
	///         be the same fact three times.
	///     </para>
	///     <para>
	///         <c>null</c> from a report that predates the field means <b>say nothing</b>. An older payload cannot
	///         support "nothing is blocking this room" any better than it supports the opposite.
	///     </para>
	///     <para>
	///         The wording is <c>ActivityView</c>'s, on purpose: somebody moving between the timeline and this page
	///         should meet one vocabulary rather than two descriptions of one fact.
	///     </para>
	/// </remarks>
	/// <param name="snapshot">The room's most recent report.</param>
	/// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <c>null</c>.</exception>
	public static string? AutoOnNote(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return snapshot.AutoOnBlockedBy switch
		{
			AutoOnBlock.Sleep => "The house is asleep — movement won't light the room.",
			AutoOnBlock.EntityOn => snapshot.AutoOnBlockingEntity is { Length: > 0 } blocker
				? $"{blocker} is on — movement won't light the room."
				: "Something here is on — movement won't light the room.",
			_ => null
		};
	}

	/// <summary>Whether the engine's armed deadline has passed with no report replacing it.</summary>
	/// <param name="snapshot">The room's most recent report.</param>
	/// <param name="now">The reader's present.</param>
	/// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <c>null</c>.</exception>
	public static bool IsOverdue(AreaSnapshot snapshot, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return snapshot.NextChangeAt is { } due && now - due > OverdueAfter;
	}

	/// <summary>When the room last reported, as the header's stamp.</summary>
	/// <param name="snapshot">The room's most recent report.</param>
	/// <param name="now">The reader's present.</param>
	/// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <c>null</c>.</exception>
	public static string Since(AreaSnapshot snapshot, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return $"since {Clock(snapshot.Timestamp)} · {Ago(snapshot.Timestamp, now)}";
	}

	/// <summary>Relative past time, kept truthful by the page's one-second tick.</summary>
	/// <param name="at">The moment.</param>
	/// <param name="now">The reader's present.</param>
	public static string Ago(DateTimeOffset at, DateTimeOffset now)
	{
		TimeSpan span = now - at;

		if (span < TimeSpan.FromSeconds(10))
			return "just now";
		if (span < TimeSpan.FromMinutes(1))
			return $"{(int)span.TotalSeconds} s ago";
		if (span < TimeSpan.FromHours(1))
			return $"{(int)span.TotalMinutes} min ago";
		if (span < TimeSpan.FromDays(1))
			return span.Minutes == 0 ? $"{(int)span.TotalHours} h ago" : $"{(int)span.TotalHours} h {span.Minutes} min ago";

		return $"{(int)span.TotalDays} d ago";
	}

	/// <summary>Relative future time. Never negative — the overdue line takes over before it could be.</summary>
	/// <param name="due">The deadline.</param>
	/// <param name="now">The reader's present.</param>
	public static string In(DateTimeOffset due, DateTimeOffset now)
	{
		TimeSpan span = due - now;

		if (span <= TimeSpan.Zero)
			return "any moment now";
		if (span < TimeSpan.FromMinutes(2))
			return $"in {(int)span.TotalSeconds} s";
		if (span < TimeSpan.FromHours(1))
			return $"in {(int)span.TotalMinutes} min";

		return span.Minutes == 0 ? $"in {(int)span.TotalHours} h" : $"in {(int)span.TotalHours} h {span.Minutes} min";
	}

	/// <summary>A clock time in the reader's own zone.</summary>
	/// <param name="at">The moment.</param>
	public static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

	/// <summary>
	///     Colour temperature to a CSS colour, by the usual blackbody approximation.
	/// </summary>
	/// <remarks>
	///     Decoration derived from data: the engine's Kelvin is the source and no palette is hard-coded, which is
	///     the rule the state chip and the dashboard lamp already follow.
	/// </remarks>
	/// <param name="kelvin">The commanded colour temperature.</param>
	public static string KelvinCss(int kelvin)
	{
		double k = Math.Clamp(kelvin, 1500, 6600) / 100.0;

		int g = (int)Math.Clamp(99.4708025861 * Math.Log(k) - 161.1195681661, 0, 255);
		int b = (int)Math.Clamp(138.5177312231 * Math.Log(k - 10) - 305.0447927307, 0, 255);

		return $"rgb(255, {g}, {b})";
	}

	/// <summary>
	///     A past moment as the table writes it: how long ago first, the clock time second.
	/// </summary>
	/// <remarks>
	///     Ago-first because "2 min ago" is the fact and "17:42" is the corroboration — reversed, the eye had to
	///     cross a timestamp it did not want to reach the number it did. Seconds are dropped: this table is read
	///     to the nearest minute, and <c>17:42:10</c> asks to be compared digit by digit with the row below it.
	///     <see cref="Clock"/> keeps its seconds for the log rows, where two entries can share a minute.
	/// </remarks>
	private static string Stamp(DateTimeOffset at, DateTimeOffset now) =>
		$"{Ago(at, now)} · {at.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)}";

	/// <summary>
	///     The levels the table reports, terse: the header's own line writes them out in prose instead.
	/// </summary>
	/// <remarks>
	///     The warmth is named, not numbered. "2700 K" is a unit somebody has to already know to read, and this
	///     table is the one surface on the page written for the person asking why a light did not come on rather
	///     than for the person tuning it. The number is kept, in the hover — see <see cref="LightsTitle"/>.
	/// </remarks>
	private static string Reading(AreaSnapshot snapshot)
	{
		if (snapshot.BrightnessPct is not { } brightness)
			return snapshot.LastCommandAt is null ? "not commanded yet" : "off";

		return snapshot.ColorTempKelvin is { } kelvin
			? $"{brightness:0} % · {Warmth(kelvin)}"
			: $"{brightness:0} %";
	}

	private static string LightsTitle(AreaSnapshot snapshot)
	{
		if (snapshot.BrightnessPct is null && snapshot.LastCommandAt is null)
			return "The engine has commanded nothing here yet, so it cannot say what the lights are doing.";

		return snapshot.ColorTempKelvin is { } kelvin
			? $"The levels the engine is holding these lights at — {kelvin} K."
			: "The levels the engine is holding these lights at.";
	}

	private static string Levels(string prefix, AreaSnapshot snapshot, string suffix = "")
	{
		if (snapshot.BrightnessPct is not { } brightness)
			return $"{prefix.TrimEnd()} — level unknown{suffix}.";

		return snapshot.ColorTempKelvin is { } kelvin
			? $"{prefix} {brightness:0} %{suffix} — {Warmth(kelvin)}, {kelvin} K."
			: $"{prefix} {brightness:0} %{suffix}.";
	}

	private static string Warmth(int kelvin) => kelvin switch
	{
		< 2600 => "candlelight warm",
		< 3300 => "warm white",
		< 4100 => "neutral white",
		_ => "cool daylight"
	};

	/// <summary>
	///     The darkness verdict alone, as the answer to the row's question.
	/// </summary>
	/// <remarks>
	///     The engine's reading no longer joins it here — it travels as <see cref="RoomFact.Detail"/> and is drawn
	///     as a quieter second line. Still passed through and never rebuilt: the gate is the only thing that knows
	///     which source it consulted, and a reading assembled here would eventually disagree with the one the
	///     engine acted on.
	/// </remarks>
	/// <remarks>
	///     Lower case, like every other value in this column. Capitalising the answers to the one row phrased as a
	///     question made them the only capitals in the table, which read as emphasis nobody had asked for.
	/// </remarks>
	private static string DarknessVerdict(AreaSnapshot snapshot) => snapshot.IsDark switch
	{
		true => "yes",
		false => "no — too bright",
		null => "not checked yet"
	};

	private static string DarknessTitle(AreaSnapshot snapshot) => snapshot.IsDark is null
		? "Darkness hasn't been checked here yet."
		: "Whether the room was dark enough to light, at the moment of this report.";
}
