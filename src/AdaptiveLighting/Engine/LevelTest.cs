using System.Reactive.Concurrency;
using System.Reactive.Disposables;

namespace AdaptiveLighting.Engine;

/// <summary>A level test in progress: the period it shows, when it ends, the lights it covers and their levels.</summary>
internal sealed record RunningLevelTest(string PeriodId, DateTimeOffset EndsAt)
{
	/// <summary>The light the newest press named, for the snapshot. Null once the room as a whole is under test.</summary>
	public string? LightId { get; init; }

	/// <summary>The lights the return covers, or null when it covers the whole room.</summary>
	public HashSet<string>? Lights { get; init; }

	/// <summary>The levels to put back, or null when the engine resolves its own again.</summary>
	public IReadOnlyList<(string Light, LightCommand Command)>? Levels { get; set; }
}

/// <summary>The one level test a room may be running, and the return it owes.</summary>
// Called only from inside AreaController's lock and never takes one of its own, so the room's state and the
// test's cannot be read a step apart. A test is one value or none: a deadline without a period, or a period
// without a return armed, cannot be represented.
internal sealed class LevelTest : IDisposable
{
	private readonly IScheduler _scheduler;
	private readonly TimeSpan _length;
	private readonly Action _onElapsed;

	// Serial, so a second press leaves one return, the test's length after it.
	private readonly SerialDisposable _return = new();

	private RunningLevelTest? _running;

	public LevelTest(IScheduler scheduler, TimeSpan length, Action onElapsed)
	{
		_scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
		_onElapsed = onElapsed ?? throw new ArgumentNullException(nameof(onElapsed));
		_length = length;
	}

	/// <summary>Whether a test is holding the fixtures right now.</summary>
	public bool IsRunning => _running is not null;

	/// <summary>Whether the running test covers named lights instead of the room.</summary>
	public bool IsLightTest => _running is { Lights: not null };

	/// <summary>The period the running test shows, or <c>null</c> when none is running.</summary>
	public string? PeriodId => _running?.PeriodId;

	/// <summary>When the running test hands the room back, or <c>null</c> when none is running.</summary>
	public DateTimeOffset? EndsAt => _running?.EndsAt;

	/// <summary>The light the newest press named, or <c>null</c> for a room test or no test.</summary>
	public string? LightId => _running?.LightId;

	/// <summary>Starts a test, or moves the one running onto this period and a fresh deadline.</summary>
	/// <returns><c>true</c> when this began a fresh test, so its levels are still to be read.</returns>
	/// <remarks>
	///     A fresh test on one light covers that light alone; a fresh room test covers the room. A test already
	///     running keeps the lights it covers and the levels it captured, so a second press cannot read the first
	///     test's own levels back as somebody's.
	/// </remarks>
	public bool Start(string periodId, string? lightId)
	{
		bool fresh = _running is null;

		_running = _running is { } running
			? running with { PeriodId = periodId, EndsAt = Deadline(), LightId = lightId }
			: new RunningLevelTest(periodId, Deadline())
			{
				LightId = lightId,
				Lights = lightId is null ? null : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { lightId }
			};

		_return.Disposable = _scheduler.Schedule(_length, _onElapsed);
		return fresh;
	}

	/// <summary>Keeps the levels the return puts back, or <c>null</c> to have the engine resolve its own.</summary>
	public void Capture(IReadOnlyList<(string Light, LightCommand Command)>? levels)
	{
		if (_running is { } running)
			running.Levels = levels;
	}

	/// <summary>Widens a light test's return to cover one more light.</summary>
	/// <returns><c>true</c> when that light's own levels must still be read.</returns>
	// The light joins the return whether or not anything was captured: a room whose levels are the engine's
	// still owes this light the engine's answer when the test ends.
	public bool AddLight(string lightId)
	{
		if (_running is not { Lights: { } lights } running)
			return false;

		return lights.Add(lightId) && running.Levels is not null;
	}

	/// <summary>Adds levels read after the test began to what the return puts back.</summary>
	public void Append(IReadOnlyList<(string Light, LightCommand Command)> levels)
	{
		if (_running is { Levels: { } kept } running)
			running.Levels = [.. kept, .. levels];
	}

	/// <summary>Drops the return and hands back the test that owed it, leaving none running.</summary>
	/// <returns>The test that was running, or <c>null</c> when none was.</returns>
	public RunningLevelTest? Take()
	{
		_return.Disposable = Disposable.Empty;

		RunningLevelTest? running = _running;
		_running = null;
		return running;
	}

	public void Dispose() => _return.Dispose();

	private DateTimeOffset Deadline() => _scheduler.Now + _length;
}
