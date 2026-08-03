using System.Threading;

using AdaptiveLighting.Abstractions;

namespace AdaptiveLighting.Web.Services;

/// <summary>One line of the activity page: a report the engine published, with the position it arrived in.</summary>
/// <param name="Sequence">
///     Where this report fell in the run, counting from one. Two areas can publish on the same instant, so
///     <see cref="AreaSnapshot.Timestamp"/> alone does not order the timeline. Dense and never reused, which is
///     what lets the page count new arrivals by subtraction after eviction.
/// </param>
/// <param name="Snapshot">The report itself, as the engine published it.</param>
public sealed record ActivityEntry(long Sequence, AreaSnapshot Snapshot)
{
	public string AreaName => Snapshot.AreaName;

	/// <summary>When the engine made this decision.</summary>
	public DateTimeOffset At => Snapshot.Timestamp;
}

/// <summary>
///     The timeline as one consistent read: everything the log was holding, and the sequence it had reached, as of
///     the same instant.
/// </summary>
/// <remarks>
///     The two numbers only mean anything together. Read separately, a report landing between them counts as shown
///     while being absent from the list it was shown in, and stays invisible until the next report arrives.
/// </remarks>
/// <param name="Entries">Every report the log held, newest first.</param>
/// <param name="Newest">The sequence of the newest report as of that same instant, or zero when none had been.</param>
public sealed record ActivityTimeline(IReadOnlyList<ActivityEntry> Entries, long Newest);

/// <summary>
///     The engine's recent decisions, kept in order and bounded.
/// </summary>
/// <remarks>
///     <see cref="AreaSnapshotCache"/> keeps one report per room; this keeps the ones that cache overwrites. Both
///     are fed from the cache's single subscription. Only distinct reports arrive: the engine already suppresses a
///     snapshot repeating what an area last said.
/// </remarks>
public sealed class ActivityLog
{
	/// <summary>How many reports the log keeps before the oldest falls off, about a day or two for a real house.</summary>
	public const int Capacity = 500;

	private readonly Lock _gate = new();
	private readonly Queue<ActivityEntry> _entries = new(Capacity);

	private long _sequence;

	/// <summary>Every report the log still holds, newest first, in the order the page renders them.</summary>
	/// <remarks>
	///     Only for a caller that wants the entries alone. Wanting the count as well means <see cref="Read"/>; two
	///     separately-locked reads leave a gap a report can land in.
	/// </remarks>
	public IReadOnlyList<ActivityEntry> Entries
	{
		get
		{
			lock (_gate)
				return [.. _entries.Reverse()];
		}
	}

	/// <summary>The sequence of the newest report recorded, or zero when nothing has been.</summary>
	public long Newest
	{
		get
		{
			lock (_gate)
				return _sequence;
		}
	}

	public int Count
	{
		get
		{
			lock (_gate)
				return _entries.Count;
		}
	}

	/// <summary>Whether nothing has been recorded yet. Drives the activity page's empty state.</summary>
	public bool IsEmpty => Count == 0;

	/// <summary>The timeline and the count that goes with it, under one lock.</summary>
	public ActivityTimeline Read()
	{
		lock (_gate)
			return new ActivityTimeline([.. _entries.Reverse()], _sequence);
	}

	/// <summary>Records a report, evicting the oldest once the buffer is full.</summary>
	/// <remarks>
	///     Called on a Home Assistant event-loop thread while the page reads from a Blazor circuit's. Under
	///     <c>_gate</c>: the sequence and the rotation have to happen together.
	/// </remarks>
	/// <returns>The entry as filed, so a caller can see what sequence it took.</returns>
	public ActivityEntry Record(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		lock (_gate)
		{
			ActivityEntry entry = new(++_sequence, snapshot);
			_entries.Enqueue(entry);

			while (_entries.Count > Capacity)
				_entries.Dequeue();

			return entry;
		}
	}
}
