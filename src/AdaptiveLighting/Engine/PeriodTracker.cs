using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>The period half of the mode brain: entry, the note that survives a restart, the period select mirror and motion starts.</summary>
// The gate is ModeMonitor's own, shared with HouseModeRules: reading which period is in force and claiming the
// transition must be one step, or a period-select flip arriving on Home Assistant's thread acts on it twice.
// Nothing that calls Home Assistant runs under it.
internal sealed class PeriodTracker
{
	private readonly ILogger _logger;
	private readonly IReadOnlyList<TimePeriodConfig> _periods;
	private readonly Func<SunTimes> _sunTimes;
	private readonly TimeZoneInfo _zone;
	private readonly CircadianCalculator _circadian;
	private readonly HouseModeRules _modes;
	private readonly Func<bool> _isDisposed;
	private readonly Action _raiseChanged;
	private readonly object _gate;

	// Shared with every area's calculator: which periods wait for movement, and which have begun today. This is the
	// only writer, and the reason the rooms and the monitor cannot disagree about whether a period has started.
	private readonly MotionPeriodLatch _motionPeriods;

	// Where the period the engine is running in is written down, so the next start can tell whether a boundary
	// went by while it was stopped. Null reads as unknown.
	private readonly ILastPeriodStore? _lastPeriod;

	// Null when no period select is configured. Which direction it grants is its own to say.
	private readonly PeriodSelectReader? _periodSelect;

	// The period select as an output. OptionForPeriod is non-null on the one authority that permits a write.
	private readonly SelectMirror _periodMirror;

	// Which sensors may start each held period. A null value means any watched sensor; an empty set is a period
	// naming rooms none of whose sensors resolved, which can never fire.
	private readonly Dictionary<string, IReadOnlySet<string>?> _motionStartPeriods = new(StringComparer.OrdinalIgnoreCase);

	// This engine replaced a running one on a save. The note on disk cannot tell that from a cold start, and a save
	// is not a boundary that went by.
	private readonly bool _afterSave;

	private string? _previousPeriodId;

	// Armed by the start, spent on the first tick that can read the select. Held open until the select answers,
	// because an input_select can be unavailable for a while after a Home Assistant restart.
	private bool _startPeriodModePending;

	// What the previous run left on disk, read once at the start. Null is unknown, which differs from knowing a
	// boundary was crossed.
	private string? _periodAtLastRun;

	// What this run believes is on disk now, so the file is written on a period change and not on every tick.
	private string? _persistedPeriodId;

	// Bounds the mirror log line only. The call itself compares against what the select actually reads, so a
	// select that never echoes is asked again.
	private string? _mirroredPeriodOption;

	internal PeriodTracker(
		IHaContext ha,
		ILogger logger,
		IReadOnlyList<TimePeriodConfig> periods,
		Func<SunTimes> sunTimes,
		TimeZoneInfo zone,
		CircadianCalculator circadian,
		MotionPeriodLatch motionPeriods,
		ILastPeriodStore? lastPeriod,
		PeriodSelectReader? periodSelect,
		IReadOnlyDictionary<string, IReadOnlyList<string>>? motionSensorsByArea,
		HouseModeRules modes,
		object gate,
		Func<bool> isDisposed,
		Action raiseChanged,
		bool afterSave)
	{
		_logger = logger;
		_periods = periods;
		_sunTimes = sunTimes;
		_zone = zone;
		_circadian = circadian;
		_motionPeriods = motionPeriods;
		_lastPeriod = lastPeriod;
		_periodSelect = periodSelect;
		_modes = modes;
		_gate = gate;
		_isDisposed = isDisposed;
		_raiseChanged = raiseChanged;
		_afterSave = afterSave;

		// OptionForPeriod is non-null on the one authority that permits a write; PeriodSelectReader is where that
		// branch is decided, and it is decided once.
		_periodMirror = new SelectMirror(ha, periodSelect?.Entity, periodSelect?.OptionForPeriod is not null);

		BuildMotionStartPeriods(motionSensorsByArea);
	}

	// Only periods the latch holds. That is where the authority branch lives, so a dropdown-owned house builds no
	// rows here and motion starts nothing.
	private void BuildMotionStartPeriods(IReadOnlyDictionary<string, IReadOnlyList<string>>? motionSensorsByArea)
	{
		foreach (TimePeriodConfig period in _periods)
		{
			if (!_motionPeriods.Holds(period.Key))
				continue;

			if (period.StartsOnMotionAreas is not { Count: > 0 })
			{
				_motionStartPeriods[period.Key] = null;
				continue;
			}

			HashSet<string> sensors = new(StringComparer.OrdinalIgnoreCase);

			foreach (string areaId in period.StartsOnMotionAreas)
				if (motionSensorsByArea?.TryGetValue(areaId.Trim(), out IReadOnlyList<string>? found) == true)
					sensors.UnionWith(found);

			// Kept even when empty: a named room that resolved nothing must never read as "any room".
			_motionStartPeriods[period.Key] = sensors;
		}
	}

	/// <summary>Whether any period can be started by movement.</summary>
	internal bool StartsPeriodsOnMotion => _motionStartPeriods.Count > 0;

	/// <summary>Which sensors may start each held period, for the start-up report.</summary>
	internal IReadOnlyDictionary<string, IReadOnlySet<string>?> MotionStartPeriods => _motionStartPeriods;

	/// <summary>Reads the note, seeds the latch and records the period this run comes up inside. Called under the gate.</summary>
	// A save rebuilds the monitor, and an edit to the schedule can put a different period in force than the note
	// names. That reads as a boundary the engine slept through, which it is not: the engine was running the whole
	// time. Only a start from nothing may spend the note.
	internal string? BeginRun(DateTimeOffset now)
	{
		_startPeriodModePending = !_afterSave;

		// Read once: the answer is about the run that ended, and a later read finds what this run wrote over it.
		// A file read under the gate is safe only here, before the subscriptions and the tick exist.
		_periodAtLastRun = ReadPeriodAtLastRun();
		_persistedPeriodId = _periodAtLastRun;

		// Before the active period is read: seeding may put a held period back in the table.
		SeedPeriodLatch(now);
		_previousPeriodId = _circadian.ActivePeriodId(now);

		return _previousPeriodId;
	}

	/// <summary>One evaluation of the clock's news: period entry, the across-restart catch-up, the note and the mirror.</summary>
	// Under the gate, because reading the period and claiming the transition must be one step: a period-select flip
	// runs this from Home Assistant's thread, and a transition read but not claimed is acted on twice. The read is
	// inside the gate too; with only the claim under the lock, a thread holding a stale name fires a backwards
	// entry and regresses _previousPeriodId. The read is a state-cache lookup plus a sort and cannot re-enter.
	internal void Tick(DateTimeOffset now, HouseModeOptionConfig? activeOption, bool modeIsReadable)
	{
		// Before the lock: this consults Home Assistant. A period select that cannot be read is no period change.
		// Under Home Assistant authority the period falls through to the clock while the helper is unavailable, and
		// that answer is indistinguishable from a real one: a household holding "day" at 23:30 would have night's
		// SetsMode latch the house asleep, and nothing puts it back when the helper returns.
		bool overrideIsBlind = _periodSelect is { ReadPeriod: not null } reader && reader.CurrentValue() is null;

		string? currentPeriodId;
		bool entered;
		bool applyOnStart;

		lock (_gate)
		{
			currentPeriodId = _circadian.ActivePeriodId(now);

			entered = !overrideIsBlind
				&& currentPeriodId is { Length: > 0 }
				&& _previousPeriodId is { Length: > 0 }
				&& !string.Equals(currentPeriodId, _previousPeriodId, StringComparison.OrdinalIgnoreCase);

			applyOnStart = !entered
				&& !overrideIsBlind
				&& _startPeriodModePending
				&& currentPeriodId is { Length: > 0 }
				&& modeIsReadable;

			// A blind read is no evidence about which period is running, and recording the clock's guess would
			// make the helper's recovery look like a fresh entry.
			if (!overrideIsBlind)
				_previousPeriodId = currentPeriodId;

			// The one restart chance is spent by an entry or by using it. A tick that could read neither the
			// period nor the select leaves it armed.
			if (entered || applyOnStart)
				_startPeriodModePending = false;

			// Nothing latches a period here: the clock can only enter one the latch already lets through, and
			// the start seeded whichever period this run came up inside.
		}

		if (entered)
			OnPeriodEntered(currentPeriodId!, activeOption);
		else if (applyOnStart)
			ApplyPeriodModeOnStart(currentPeriodId!);

		// After the decision above, never before it. The note is the only evidence that a boundary went by while
		// the engine was down, and writing first erases it if the process dies between the two.
		RememberPeriod(currentPeriodId);

		// The period select as an output, reading the same answer the two rules above did. No-op unless this
		// application owns the select.
		MirrorPeriodSelect(currentPeriodId);
	}

	/// <summary>Offers the movement to the period it is allowed to start, and enters that period if it may.</summary>
	// Under the gate for the reason Tick gives: this runs on Home Assistant's thread, and a transition read but not
	// claimed is entered twice. Asked of the schedule and never of what is in force, because the period movement
	// may start is the one the calculators hold out of the table. Two bounds are load-bearing: a period is never
	// placed before its own Start, so night still running at 02:00 cannot let the 06:30 period fire on a trip to
	// the kitchen; and once per local day, so walking back in at lunch does not re-fire SetsModeId over a mode
	// somebody chose since.
	internal void StartPeriodOnMotion(string sensor, DateTimeOffset now)
	{
		if (_motionStartPeriods.Count == 0)
			return;

		// Before the lock: this consults Home Assistant.
		HouseModeOptionConfig? activeOption = _modes.CurrentOption;

		// The household's day, as the circadian table reads it. The two must agree or a period's mode switch is
		// filed against a different day from the one the table placed it on.
		DateOnly today = now.DayIn(_zone);

		string? periodKey;
		bool start;

		lock (_gate)
		{
			periodKey = _circadian.ScheduledPeriodId(now);

			// TryBegin last: it claims the day, so nothing after it may refuse the start.
			start = periodKey is { Length: > 0 }
				&& MotionMayStart(periodKey, sensor)
				&& StartHasPassed(periodKey, now)
				&& _motionPeriods.TryBegin(periodKey, today, now);

			if (start)
			{
				_previousPeriodId = periodKey;
				_startPeriodModePending = false;
			}
		}

		if (!start)
			return;

		_logger.LogInformation("Motion on {Sensor} started period '{Period}'.", sensor, DisplayName(periodKey));

		// Written now, because the latch is in memory and a config save rebuilds the engine: the note is the only
		// thing that tells the rebuilt monitor this period had already begun.
		RememberPeriod(periodKey);

		OnPeriodEntered(periodKey!, activeOption);
		_raiseChanged();
	}

	// Under the gate; reads configuration settled in the constructor and nothing else.
	private bool MotionMayStart(string periodKey, string sensor) =>
		_motionStartPeriods.TryGetValue(periodKey, out IReadOnlySet<string>? rooms)
		&& (rooms is null || rooms.Contains(sensor));

	// A period whose Start has not come round today cannot be started by motion. This is what keeps the wrapped
	// period (night, still running at 02:00) from re-entering on the far side of midnight.
	private bool StartHasPassed(string periodKey, DateTimeOffset now)
	{
		if (PeriodWithKey(periodKey) is not { } period || !PeriodStart.TryParse(period.Start, out PeriodStart? start))
			return false;

		return start!.Resolve(_sunTimes()) is { } resolved && resolved <= now.TimeIn(_zone);
	}

	/// <summary>Seeds the latch, under the gate, so the period the clock places now counts as begun for this run.</summary>
	// Restarting inside a period is no entry, so a later movement must not re-fire its SetsModeId or its
	// period-start reset over a mode a person chose. A period that waits for movement is seeded only when the note
	// on disk says the last run was already inside it; without the note there is no evidence it ever began.
	private void SeedPeriodLatch(DateTimeOffset now)
	{
		if (_circadian.ScheduledPeriodId(now) is not { Length: > 0 } scheduled)
			return;

		if (_motionPeriods.Holds(scheduled)
			&& !string.Equals(_periodAtLastRun, scheduled, StringComparison.OrdinalIgnoreCase))
			return;

		_motionPeriods.MarkBegun(scheduled, InstanceDay(scheduled, now));
	}

	// The local day the running instance of this period began on. A Start still ahead of now belongs to yesterday's
	// instance, which is the one the table's wrap puts in force.
	private DateOnly InstanceDay(string periodKey, DateTimeOffset now)
	{
		DateOnly today = now.DayIn(_zone);
		return StartHasPassed(periodKey, now) ? today : today.AddDays(-1);
	}

	private static bool BoundaryWentByWhileDown(string? previousRun, string periodKey) =>
		previousRun is { Length: > 0 } && !string.Equals(previousRun, periodKey, StringComparison.OrdinalIgnoreCase);

	private TimePeriodConfig? PeriodWithKey(string periodKey) => _periods.ByKey(periodKey);

	private string DisplayName(string? periodKey) => DisplayName(_periods, periodKey);

	// Every log line names the period a person would recognise, never the id the engine resolved by.
	internal static string DisplayName(IReadOnlyList<TimePeriodConfig> periods, string? periodKey) =>
		periodKey is { Length: > 0 } && periods.ByKey(periodKey)?.Name is { Length: > 0 } name ? name : periodKey ?? "";

	private void OnPeriodEntered(string periodKey, HouseModeOptionConfig? activeOption)
	{
		TimePeriodConfig? period = PeriodWithKey(periodKey);

		bool resets = activeOption is { Kind: not ModeKind.Normal, ResetOnPeriodStartId: { Length: > 0 } resetPeriod }
			&& resetPeriod.SameName(periodKey);

		// Once, at entry, so a human override mid-period stands. A boundary the engine was not running for never
		// reaches here; ApplyPeriodModeOnStart handles that from the note on disk.
		if (period?.SetsModeId is { Length: > 0 } setsMode
			&& _modes.OptionValueFor(setsMode) is { Length: > 0 } wanted)
		{
			_modes.WriteMode(wanted, entity => _logger.LogInformation(
				"Period '{Period}' started; setting {Select} to '{Mode}'.", DisplayName(periodKey), entity, wanted));

			// One boundary is one instruction. A reset landing after this writes the same select a second time,
			// the later write wins, and both log lines claim to have set the mode.
			if (resets)
				_logger.LogInformation(
					"'{Option}' also resets when period '{Period}' starts, but that period sets the mode itself, so the reset is skipped.",
					activeOption!.Value, DisplayName(periodKey));

			return;
		}

		if (resets)
			_modes.Reset($"period '{DisplayName(periodKey)}' started");
	}

	/// <summary>Applies the current period's <see cref="TimePeriodConfig.SetsModeId"/> once, on the first tick after the start.</summary>
	// The question is whether a boundary went by, never whether the engine restarted: restarting inside the same
	// period is no event, while restarting across a boundary is the event the schedule exists to act on. A null
	// _periodAtLastRun is unknown and does nothing, because guessing the other way overwrites a mode on no
	// evidence, on a path a corrupt file could trigger every start. Kept separate from OnPeriodEntered because
	// that path also fires the period-start reset, which would cancel a retained Away or Guest mode.
	private void ApplyPeriodModeOnStart(string periodKey)
	{
		string? previousRun;
		lock (_gate)
			previousRun = _periodAtLastRun;

		if (previousRun is not { Length: > 0 })
		{
			_logger.LogDebug(
				"Started inside period '{Period}', but there is no note of which period the last run ended in; "
				+ "assuming nothing and leaving the house mode alone.",
				DisplayName(periodKey));
			return;
		}

		if (!BoundaryWentByWhileDown(previousRun, periodKey))
			return;   // the same period the last run ended in: no boundary went by, so nothing to apply

		if (_modes.HouseModeIsHomeAssistants)
			return;

		TimePeriodConfig? period = PeriodWithKey(periodKey);

		if (period?.SetsModeId is not { Length: > 0 } setsMode
			|| _modes.OptionValueFor(setsMode) is not { Length: > 0 } wanted)
			return;

		_modes.WriteMode(wanted, entity => _logger.LogInformation(
			"Period '{Period}' began while the engine was stopped (it was last running in '{Previous}'); setting {Select} to '{Mode}'.",
			DisplayName(periodKey), DisplayName(previousRun), entity, wanted));
	}

	/// <summary>Points the period select at the period the engine's own schedule resolved.</summary>
	// A no-op under PeriodAuthority.HomeAssistant, where the reader hands out no OptionForPeriod, so the two
	// directions are exclusive by construction and never by a flag this method could get wrong. Triggered by
	// comparison, never by memory: a select moved by hand, or one back from a Home Assistant restart on the wrong
	// option, is put right, where an already-asked latch would make both permanent. The log line is bounded
	// separately, because an option the select does not offer is rejected and correctly retried on every tick.
	internal void MirrorPeriodSelect(string? periodKey)
	{
		if (_isDisposed() || _periodSelect?.OptionForPeriod is not { } optionFor || periodKey is not { Length: > 0 })
			return;

		if (optionFor(periodKey) is not { Length: > 0 } wanted)
			return;   // no row maps this period; the validator has already said so if it matters

		_periodMirror.Ensure(wanted, entity =>
		{
			// Info the first time a value is asked for, Debug on every retry: an option the select does not offer
			// is rejected and correctly re-asked on every tick, and that must not fill the log.
			bool firstTime;
			lock (_gate)
			{
				firstTime = !string.Equals(_mirroredPeriodOption, wanted, StringComparison.OrdinalIgnoreCase);
				_mirroredPeriodOption = wanted;
			}

			if (firstTime)
				_logger.LogInformation("Period '{Period}' is in force; setting {Select} to '{Option}'.",
					DisplayName(periodKey), entity, wanted);
			else
				_logger.LogDebug("Period select {Select} still does not read '{Option}'; asking again.", entity, wanted);
		});
	}

	/// <summary>Reads the previous run's period, treating any failure as unknown.</summary>
	// LastPeriodStore promises never to throw, and this catches anyway: the store is an interface a host
	// supplies, and this runs inside the start, where a throw takes the engine down. A note written before periods
	// had ids holds a name, and without translating it a start compares a name against a key and reads a
	// boundary crossing that never happened.
	private string? ReadPeriodAtLastRun()
	{
		if (_lastPeriod is null)
			return null;

		try
		{
			if (_lastPeriod.Load() is not { Length: > 0 } stored)
				return null;

			if (PeriodWithKey(stored) is not null)
				return stored;

			return _periods.ByName(stored)?.Key ?? stored;
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			_logger.LogWarning(exception, "Could not read which period the last run ended in; assuming nothing.");
			return null;
		}
	}

	/// <summary>Writes the period now current, when it has changed since the last write.</summary>
	// Only on a change: a handful of writes a day, and the file is read once per process. A null period, from a
	// table with no placeable boundary, is left unwritten, or a good note is replaced by a worse one.
	private void RememberPeriod(string? periodKey)
	{
		if (_lastPeriod is null || periodKey is not { Length: > 0 })
			return;

		lock (_gate)
		{
			if (string.Equals(_persistedPeriodId, periodKey, StringComparison.OrdinalIgnoreCase))
				return;

			// Recorded as attempted even when the write fails, or a persistent fault warns once a minute.
			_persistedPeriodId = periodKey;
		}

		try
		{
			_lastPeriod.TrySave(periodKey);
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			// Losing the note costs one re-application after the next restart, never the tick it is written from.
			_logger.LogWarning(exception, "Could not record that the engine is now in period '{Period}'.", DisplayName(periodKey));
		}
	}
}
