using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>One line of the room page's evidence table.</summary>
public sealed record RoomFact(string Label, string Value, string? Title = null, string? Detail = null, bool IsProse = false);

/// <summary>What the room page says about a room now: the present-tense line, what happens next, and what the engine saw.</summary>
/// <remarks>
///     Every reading is passed through from the snapshot, never worked out again: only the engine knows which
///     gates and which sensors it consulted. Times are a function of <c>now</c> and the report.
/// </remarks>
public static class RoomFacts
{
	/// <summary>How far past a deadline a room goes before the page says it has lost touch instead of counting down.</summary>
	public static readonly TimeSpan OverdueAfter = TimeSpan.FromSeconds(90);

	/// <summary>What the engine saw, as the room page's evidence table.</summary>
	public static IReadOnlyList<RoomFact> For(AreaSnapshot snapshot, DateTimeOffset now, Func<string, string>? nameOf = null)
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

		facts.Add(new RoomFact("Lights", Reading(snapshot), LightsTitle(snapshot), LightReadout.Line(snapshot, nameOf)));

		if (snapshot.SceneApplied is { Length: > 0 } scene)
		{
			facts.Add(new RoomFact(
				"Scene",
				SceneName(scene, nameOf),
				"The engine activated this scene instead of aiming the lights itself, so it holds no level of its own here.",
				IsProse: true));
		}

		// `is true` throughout: null is a build predating the field, which cannot say either way.
		if (snapshot.IsHeldLit is true)
		{
			facts.Add(new RoomFact(
				"Held on by",
				Holder(snapshot, nameOf),
				"This room is set to stay lit while this is holding it. Dimming and switching off wait until it lets go.",
				IsProse: true));
		}

		facts.Add(snapshot.LastMotionAt is { } motion
			? new RoomFact("Last movement", Stamp(motion, now), $"Movement was last seen at {Clock(motion)}.")
			: new RoomFact("Last movement", "none seen", "No movement has been reported since the engine started."));

		facts.Add(LastChange(snapshot, now));

		facts.Add(new RoomFact(
			"Time of day",
			snapshot.PeriodName is { Length: > 0 } period ? period : "nothing scheduled",
			"The schedule period this room is in. It sets brightness and warmth."));

		return facts;
	}

	// The newer of the engine's own last command and the last change somebody else made, with who made it.
	private static RoomFact LastChange(AreaSnapshot snapshot, DateTimeOffset now)
	{
		if (snapshot.ChangedAt is { } changed && (snapshot.LastCommandAt is not { } commanded || changed > commanded))
		{
			return snapshot.ChangedBy is { Length: > 0 } by
				? new RoomFact("Last changed", Stamp(changed, now), $"These lights were last changed at {Clock(changed)}.", by)
				: new RoomFact("Last changed", Stamp(changed, now), $"These lights were last changed at {Clock(changed)}, by something the engine could not name.");
		}

		return snapshot.LastCommandAt is { } command
			? new RoomFact("Last changed", Stamp(command, now), $"The engine last changed these lights at {Clock(command)}.", ChangeOriginNames.ByTheEngine)
			: new RoomFact("Last changed", "not yet", "The engine has not changed these lights since it started.");
	}

	// An older engine reports the hold without naming it, so the copy stays true with nothing to name.
	private static string Holder(AreaSnapshot snapshot, Func<string, string>? nameOf) =>
		snapshot.HeldLitBy is { Length: > 0 } entity
			? nameOf?.Invoke(entity) is { Length: > 0 } friendly ? friendly : entity
			: "something in this room";

	private static string SceneName(string sceneId, Func<string, string>? nameOf) =>
		nameOf?.Invoke(sceneId) is { Length: > 0 } friendly ? friendly : sceneId;

	/// <summary>Whether a scene the engine itself applied is what these lights are showing.</summary>
	/// <remarks>
	///     The one test every arm of the page that credits a scene shares, or the headline and the table contradict
	///     each other. Only the two states the engine scenes: once a hand has been at the switch the room is
	///     <see cref="AreaState.OverriddenOn"/> and the standing scene no longer describes the lights.
	/// </remarks>
	private static bool IsEngineScened(AreaSnapshot snapshot) =>
		snapshot is { State: AreaState.AutoActive or AreaState.AutoVacant, SceneApplied: { Length: > 0 } };

	/// <summary>The room's present tense: what the lights are doing and why.</summary>
	public static string Headline(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return snapshot switch
		{
			{ KillSwitchActive: true } => "Paused by the master switch",

			// A scene nulls both levels, so every arm below quoting them would read "level unknown".
			_ when IsEngineScened(snapshot) =>
				"On this room's own scene",

			{ State: AreaState.AutoActive, LastCommandAt: null } =>
				"On at start-up — levels untouched",
			{ State: AreaState.AutoActive } => Levels("Lit at", snapshot),
			{ State: AreaState.PreOff, Reason: TransitionReason.LeadIn } => Levels("Lead-in at", snapshot),
			{ State: AreaState.PreOff } => Levels("Warning dim at", snapshot),
			{ State: AreaState.OverriddenOn } => "Set by hand — left alone",
			{ State: AreaState.SuppressedOff } => "Switched off by hand",
			{ State: AreaState.SceneHold } => "Held by a scene",

			{ State: AreaState.Away, BrightnessPct: not null } =>
				"Away — kept on",
			{ State: AreaState.Away } => "Away",
			{ State: AreaState.Disabled } => "Never changes by itself",
			{ State: AreaState.AutoVacant, IsDark: false } => "Off — too bright to switch on",
			_ => "Off, watching for movement"
		};
	}

	/// <summary>What happens next, from the deadline the engine armed, or <c>null</c> when nothing is pending.</summary>
	public static string? NextLine(AreaSnapshot snapshot, DateTimeOffset now, Func<string, string>? nameOf = null)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		if (IsOverdue(snapshot, now))
		{
			return $"Update overdue, due {Ago(snapshot.NextChangeAt!.Value, now)} — connection down?";
		}

		// A hold nulls NextChangeAt while the room stays lit, so without this a room refusing to switch off would
		// say nothing at all.
		if (snapshot is { IsHeldLit: true, State: AreaState.AutoActive or AreaState.PreOff })
			return $"Held on by {Holder(snapshot, nameOf)}";

		string? countdown = snapshot.NextChangeAt is { } due ? In(due, now) : null;

		// Answered ahead of the switch below, whose AutoVacant arms promise that movement lights the room.
		if (snapshot.State == AreaState.AutoVacant && AutoOnNote(snapshot) is { Length: > 0 } blocked)
			return blocked;

		return snapshot.State switch
		{
			AreaState.AutoActive when countdown is not null && StateGlyph.IsOffByLevel(snapshot) =>
				$"Empty {countdown} unless someone moves",
			AreaState.AutoActive when countdown is not null => $"Dims {countdown} unless someone moves",
			AreaState.PreOff when countdown is not null && snapshot.Reason is TransitionReason.LeadIn =>
				$"Off {countdown} unless someone comes in",
			AreaState.PreOff when countdown is not null => $"Off {countdown} unless someone moves",
			AreaState.OverriddenOn when countdown is not null => $"Automatic again {countdown}",
			AreaState.SuppressedOff when countdown is not null =>
				$"Answers movement {countdown}, or sooner",
			AreaState.AutoVacant when snapshot is { IsDark: false, DarknessDetail: { Length: > 0 } detail } =>
				$"Lights once dark — {detail}",
			AreaState.AutoVacant when snapshot.IsDark is false => "Lights once dark",
			AreaState.AutoVacant => "Awaiting movement",

			AreaState.Away => "Wakes when away mode ends",
			AreaState.Disabled => "Off until switched back on",
			_ => null
		};
	}

	/// <summary>How much of the armed countdown is left, 1 down to 0, or <c>null</c> when there is no ring to draw.</summary>
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

	/// <summary>The period a running level test is showing, or <c>null</c> once its deadline has passed.</summary>
	/// <remarks>
	///     Read off the report alone, so a page that just loaded or navigated back (carrying no local memory of
	///     which button was pressed) redraws the same countdown a page that has been open throughout would show.
	/// </remarks>
	public static string? TestingPeriod(AreaSnapshot snapshot, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return snapshot.TestEndsAt is { } ends && ends > now ? snapshot.TestingPeriodId : null;
	}

	/// <summary>How many seconds a running level test has left, zero once its deadline has passed.</summary>
	public static int TestSecondsLeft(AreaSnapshot snapshot, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return snapshot.TestEndsAt is { } ends ? Math.Max(0, (int)Math.Ceiling((ends - now).TotalSeconds)) : 0;
	}

	/// <summary>Why movement would not switch these lights on right now, or <c>null</c> when there is nothing to say.</summary>
	/// <remarks>
	///     Read off the verdict the engine published, never re-derived here: only the engine knows which gates it
	///     consulted.
	/// </remarks>
	public static string? AutoOnNote(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return snapshot.AutoOnBlockedBy switch
		{
			AutoOnBlock.Sleep => "Asleep — movement won't light it",
			AutoOnBlock.EntityOn => snapshot.AutoOnBlockingEntity is { Length: > 0 } blocker
				? $"{blocker} is on — won't light"
				: "Something here is on — won't light",
			AutoOnBlock.Away => ActivityView.AwayHold(snapshot),
			_ => null
		};
	}

	/// <summary>How many of the room's lights have stopped answering, or <c>null</c> while every one answers.</summary>
	/// <remarks>Read off the count the engine published, never off Home Assistant.</remarks>
	public static string? NotResponding(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return snapshot switch
		{
			{ LightsNotResponding: not > 0 } => null,
			{ LightCount: 1 } => "This room's light is not responding.",
			{ LightsNotResponding: 1, LightCount: { } total } => $"1 of {total} lights is not responding.",
			{ LightsNotResponding: { } missing, LightCount: { } total } => $"{missing} of {total} lights are not responding.",
			{ LightsNotResponding: 1 } => "1 light is not responding.",
			{ LightsNotResponding: { } missing } => $"{missing} lights are not responding."
		};
	}

	/// <summary>One sentence per motion sensor whose battery the engine reported low, empty while none is.</summary>
	public static IReadOnlyList<string> LowBatteries(AreaSnapshot snapshot, Func<string, string> nameOf)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(nameOf);

		return snapshot.LowBatteries is { Count: > 0 } low
			? [.. low.Select(battery => battery.LevelPct is { } level
				? $"{nameOf(battery.SensorId)} battery at {level:0} %"
				: $"{nameOf(battery.SensorId)} battery low")]
			: [];
	}

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

	/// <summary>Relative future time, never negative, since the overdue line takes over before it could be.</summary>
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

	/// <summary>A clock time in the reader's own zone, seconds included, which the log rows need.</summary>
	public static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

	/// <summary>A past moment as the table writes it: how long ago first, the clock time second.</summary>
	private static string Stamp(DateTimeOffset at, DateTimeOffset now) =>
		$"{Ago(at, now)} · {at.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)}";

	/// <summary>The levels the table reports; the warmth is named here and numbered in the hover.</summary>
	private static string Reading(AreaSnapshot snapshot)
	{
		if (snapshot.BrightnessPct is not { } brightness)
		{
			// A scene nulls both levels, so without this arm the row reads "off" beside the scene naming it.
			if (IsEngineScened(snapshot))
				return "set by a scene";

			return snapshot.LastCommandAt is null ? "not commanded yet" : "off";
		}

		return snapshot.ColorTempKelvin is { } kelvin
			? $"{brightness:0} % · {Warmth(kelvin)}"
			: $"{brightness:0} %";
	}

	private static string LightsTitle(AreaSnapshot snapshot)
	{
		if (snapshot.BrightnessPct is null && IsEngineScened(snapshot))
			return "The scene decides the levels here. The engine commanded none of its own, so it cannot report them.";

		if (snapshot.BrightnessPct is null && snapshot.LastCommandAt is null)
			return "The engine has commanded nothing here yet, so it cannot say what the lights are doing.";

		return snapshot.ColorTempKelvin is { } kelvin
			? $"The levels the engine is holding these lights at — {kelvin} K."
			: "The levels the engine is holding these lights at.";
	}

	private static string Levels(string prefix, AreaSnapshot snapshot, string suffix = "")
	{
		// Null with no scene standing is TargetCommand's off for a level that came to 0 %.
		if (snapshot.BrightnessPct is not { } brightness)
		{
			if (snapshot.SceneApplied is { Length: > 0 })
				return $"{prefix.TrimEnd()} — level unknown{suffix}";

			return snapshot.PeriodName is { Length: > 0 } period
				? $"Off — {period} level is 0 %"
				: "Off — this room's level is 0 %";
		}

		return snapshot.ColorTempKelvin is { } kelvin
			? $"{prefix} {brightness:0} %{suffix} · {kelvin} K"
			: $"{prefix} {brightness:0} %{suffix}";
	}

	private static string Warmth(int kelvin) => kelvin switch
	{
		< 2600 => "candlelight warm",
		< 3300 => "warm white",
		< 4100 => "neutral white",
		_ => "cool daylight"
	};

	/// <summary>The darkness verdict alone; the reading behind it travels as <see cref="RoomFact.Detail"/>.</summary>
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
