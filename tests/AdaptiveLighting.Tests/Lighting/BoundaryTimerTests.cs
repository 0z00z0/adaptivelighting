using System.Reactive.Concurrency;

using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The one-shot behind every period boundary, driven from two threads at once.</summary>
/// <remarks>Recording which boundary is armed and scheduling the timer for it are one thing, which a single virtual clock cannot show.</remarks>
[TestClass]
public sealed class BoundaryTimerTests
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

	/// <summary>One scheduled callback, which the timer disposes when it re-arms for a different boundary.</summary>
	private sealed class Scheduled : IDisposable
	{
		public Scheduled(DateTimeOffset at) => At = at;

		public DateTimeOffset At { get; }

		public bool IsDisposed { get; private set; }

		public void Dispose() => IsDisposed = true;
	}

	/// <summary>Holds the first caller inside <c>Schedule</c>, which puts a second caller between recording a boundary and scheduling for it.</summary>
	private sealed class GatedScheduler : IScheduler
	{
		private readonly ManualResetEventSlim _release = new();
		private readonly List<Scheduled> _items = [];

		private int _calls;

		public ManualResetEventSlim FirstCallerIsInside { get; } = new();

		public DateTimeOffset Now => DateTimeOffset.UnixEpoch;

		public IReadOnlyList<Scheduled> Items
		{
			get { lock (_items) return [.. _items]; }
		}

		public void Release() => _release.Set();

		public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
		{
			Scheduled item = new(dueTime);

			lock (_items)
				_items.Add(item);

			if (Interlocked.Increment(ref _calls) == 1)
			{
				FirstCallerIsInside.Set();
				_release.Wait(Patience);
			}

			return item;
		}

		public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action) =>
			throw new NotSupportedException();

		public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action) =>
			throw new NotSupportedException();
	}

	/// <summary>Whatever boundary the timer has accepted is the one a timer is running for; losing it leaves that boundary never evaluated.</summary>
	[TestMethod]
	public void The_Boundary_The_Timer_Accepts_Is_The_One_A_Live_Timer_Is_Waiting_For()
	{
		DateTimeOffset[] boundaries =
		[
			new(2026, 1, 15, 22, 30, 0, TimeSpan.Zero),
			new(2026, 1, 15, 23, 15, 0, TimeSpan.Zero)
		];

		int asked = -1;
		GatedScheduler scheduler = new();

		using BoundaryTimer timer = new(
			scheduler,
			() => boundaries[Math.Min(Interlocked.Increment(ref asked), boundaries.Length - 1)],
			() => { },
			NullLogger.Instance);

		Thread first = new(timer.Arm) { IsBackground = true };
		first.Start();

		Assert.IsTrue(scheduler.FirstCallerIsInside.Wait(Patience), "the first arm never reached the scheduler");

		// The sun has moved by the time this one asks, so it is a different boundary and has to win.
		Thread second = new(timer.Arm) { IsBackground = true };
		second.Start();

		// Not a synchronisation point: it returns when the second caller is done, and times out when the first
		// caller is holding it off, which is the whole difference between the two orderings.
		second.Join(TimeSpan.FromSeconds(2));

		scheduler.Release();

		Assert.IsTrue(second.Join(Patience) && first.Join(Patience), "an arm never returned");

		IReadOnlyList<Scheduled> scheduled = scheduler.Items;

		Assert.AreEqual(2, scheduled.Count);
		Assert.IsTrue(scheduled[^1].At > scheduled[0].At, "the later boundary is the one scheduled last");
		Assert.IsFalse(scheduled[^1].IsDisposed, "the boundary the timer settled on has no timer behind it");
	}
}
