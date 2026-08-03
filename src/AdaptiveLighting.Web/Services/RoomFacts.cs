using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>One line of the room page's evidence table.</summary>
/// <param name="Detail">The measurement behind the value, drawn as a quieter second line.</param>
/// <param name="IsProse">Whether the value is a sentence, so the row does not set it in the monospaced face.</param>
public sealed record RoomFact(string Label, string Value, string? Title = null, string? Detail = null, bool IsProse = false);

/// <summary>
///     What the room page says about a room right now: the present-tense line, what happens next, and the
///     table of what the engine actually saw.
/// </summary>
/// <remarks>
///     Every reading here is passed through from the snapshot, never worked out again: the engine is the only
///     thing that knows which gates and which sensors it consulted. Times are a function of <c>now</c> and the
///     report, so a test can pin "2 min ago".
/// </remarks>
public static class RoomFacts
{
	/// <summary>How far past a deadline a room goes before the page says it has lost touch instead of counting down.</summary>
	public static readonly TimeSpan OverdueAfter = TimeSpan.FromSeconds(90);

	/// <summary>What the engine saw, as the room page's evidence table.</summary>
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
				"Whether walking in would switch these lights on, as the engine judged it when it reported.",
				IsProse: true));
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

	/// <summary>The room's present tense: what the lights are doing and why.</summary>
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

			// Must stay ahead of the two below: those say "Nobody home", which IsAnyoneHome has just measured false.
			{ State: AreaState.Away, IsAnyoneHome: true, BrightnessPct: not null } =>
				"The house is in away mode. This room keeps its lights on.",
			{ State: AreaState.Away, IsAnyoneHome: true } => "The house is in away mode, though somebody is home.",
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
	public static string? NextLine(AreaSnapshot snapshot, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		if (IsOverdue(snapshot, now))
		{
			return $"An update was due {Ago(snapshot.NextChangeAt!.Value, now)} and hasn't arrived. " +
				"The Home Assistant connection may be down.";
		}

		string? countdown = snapshot.NextChangeAt is { } due ? In(due, now) : null;

		// Answered ahead of the switch below: every AutoVacant arm there promises that movement lights the room,
		// which a gated room makes false.
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

			AreaState.Away when snapshot.IsAnyoneHome is true => "Wakes when the house leaves away mode.",
			AreaState.Away => "Wakes when the first person comes home.",
			AreaState.Disabled => "Nothing will be commanded until it is switched back on.",
			_ => null
		};
	}

	/// <summary>
	///     How much of the armed countdown is left, 1 down to 0, or <c>null</c> when there is no ring to draw:
	///     no deadline, no armed instant, a degenerate span, or a deadline the overdue line already covers.
	/// </summary>
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
	///     Read off the verdict the engine published. The page must not re-derive it from RespectSleepMode, the
	///     blocking entities and the house mode; the engine is the only thing that knows which gates it consulted.
	///     Both nullable fields are tested for <c>true</c>, not for "not false": a report that predates them
	///     carries <c>null</c>, which supports no claim in either direction.
	/// </remarks>
	public static string? AutoOnNote(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return snapshot.AutoOnBlockedBy switch
		{
			AutoOnBlock.Sleep => "The house is asleep — movement won't light the room.",
			AutoOnBlock.EntityOn => snapshot.AutoOnBlockingEntity is { Length: > 0 } blocker
				? $"{blocker} is on — movement won't light the room."
				: "Something here is on — movement won't light the room.",
			AutoOnBlock.Away when snapshot.IsAnyoneHome is true => ActivityView.AwayHold(snapshot),
			_ => null
		};
	}

	/// <summary>Whether the engine's armed deadline has passed with no report replacing it.</summary>
	public static bool IsOverdue(AreaSnapshot snapshot, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return snapshot.NextChangeAt is { } due && now - due > OverdueAfter;
	}

	/// <summary>When the room last reported, as the header's stamp.</summary>
	public static string Since(AreaSnapshot snapshot, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return $"since {Clock(snapshot.Timestamp)} · {Ago(snapshot.Timestamp, now)}";
	}

	/// <summary>Relative past time, kept truthful by the page's one-second tick.</summary>
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

	/// <summary>Relative future time. Never negative; the overdue line takes over before it could be.</summary>
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

	/// <summary>A clock time in the reader's own zone. Seconds included; the log rows need them.</summary>
	public static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

	/// <summary>Colour temperature to a CSS colour, by the usual blackbody approximation.</summary>
	public static string KelvinCss(int kelvin)
	{
		double k = Math.Clamp(kelvin, 1500, 6600) / 100.0;

		int g = (int)Math.Clamp(99.4708025861 * Math.Log(k) - 161.1195681661, 0, 255);
		int b = (int)Math.Clamp(138.5177312231 * Math.Log(k - 10) - 305.0447927307, 0, 255);

		return $"rgb(255, {g}, {b})";
	}

	/// <summary>A past moment as the table writes it: how long ago first, the clock time second.</summary>
	private static string Stamp(DateTimeOffset at, DateTimeOffset now) =>
		$"{Ago(at, now)} · {at.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)}";

	/// <summary>The levels the table reports, terse. The warmth is named here and numbered in the hover.</summary>
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

	/// <summary>The darkness verdict alone. The reading behind it travels as <see cref="RoomFact.Detail"/>.</summary>
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
