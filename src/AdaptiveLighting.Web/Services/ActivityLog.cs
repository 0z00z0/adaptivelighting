using System.Threading;

using AdaptiveLighting.Abstractions;

namespace AdaptiveLighting.Web.Services;

/// <summary>One line of the activity page: an area's report, or a house-wide notice the engine raised.</summary>
/// <remarks>One of <see cref="Snapshot"/> and <see cref="Notice"/> is set, never both.</remarks>
public sealed record ActivityEntry
{
	/// <param name="sequence">
	///     Where this entry fell in the run, counting from one. Dense and never reused, and two areas can publish
	///     on the same instant, so <see cref="AreaSnapshot.Timestamp"/> alone does not order the timeline.
	/// </param>
	/// <param name="snapshot">The report itself, as the engine published it.</param>
	public ActivityEntry(long sequence, AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		Sequence = sequence;
		Snapshot = snapshot;
	}

	/// <remarks><c>sequence</c> is the same running count an area report takes; the two share one timeline.</remarks>
	public ActivityEntry(long sequence, EngineNotice notice)
	{
		ArgumentNullException.ThrowIfNull(notice);

		Sequence = sequence;
		Notice = notice;
	}

	public long Sequence { get; }

	public AreaSnapshot? Snapshot { get; }

	public EngineNotice? Notice { get; }

	public string? AreaName => Snapshot?.AreaName;

	public DateTimeOffset At => Snapshot?.Timestamp ?? Notice!.At;
}

/// <summary>The timeline as one consistent read: the entries, and the sequence reached at that same instant.</summary>
/// <remarks>
///     Read apart, a report landing between the two counts as shown while being absent from the list it was shown
///     in, and stays invisible until the next report arrives.
/// </remarks>
public sealed record ActivityTimeline(IReadOnlyList<ActivityEntry> Entries, long Newest);

/// <summary>The engine's recent decisions, kept in order and bounded.</summary>
/// <remarks>
///     <see cref="AreaSnapshotCache"/> keeps one report per room; this keeps the ones that cache overwrites, off
///     the same single subscription. Only distinct reports arrive; the engine suppresses a repeat.
/// </remarks>
public sealed class ActivityLog
{
	/// <summary>How many reports the log keeps before the oldest falls off, about a day or two for a real house.</summary>
	public const int Capacity = 500;

	private readonly Lock _gate = new();
	private readonly Queue<ActivityEntry> _entries = new(Capacity);

	private long _sequence;

	/// <summary>Every report the log still holds, newest first.</summary>
	/// <remarks>
	///     For a caller that wants the entries alone. Wanting the count as well means <see cref="Read"/>; two
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

	public bool IsEmpty => Count == 0;

	/// <summary>The timeline and the count that goes with it, under one lock.</summary>
	public ActivityTimeline Read()
	{
		lock (_gate)
			return new ActivityTimeline([.. _entries.Reverse()], _sequence);
	}

	/// <summary>Records a report, evicting the oldest once the buffer is full.</summary>
	/// <remarks>
	///     Called on a Home Assistant event-loop thread while the page reads from a Blazor circuit's. <c>_gate</c>
	///     holds the sequence and the rotation together.
	/// </remarks>
	public ActivityEntry Record(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return File(sequence => new ActivityEntry(sequence, snapshot));
	}

	/// <summary>Records one house-wide notice: the engine started, or a save rebuilt every room.</summary>
	/// <remarks>Raised once per rebuild, never once per area, so a save is one row and not one row per room.</remarks>
	public ActivityEntry Record(EngineNotice notice)
	{
		ArgumentNullException.ThrowIfNull(notice);

		return File(sequence => new ActivityEntry(sequence, notice));
	}

	private ActivityEntry File(Func<long, ActivityEntry> build)
	{
		lock (_gate)
		{
			ActivityEntry entry = build(++_sequence);
			_entries.Enqueue(entry);

			while (_entries.Count > Capacity)
				_entries.Dequeue();

			return entry;
		}
	}
}
