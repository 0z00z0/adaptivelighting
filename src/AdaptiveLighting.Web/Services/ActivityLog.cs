using System.Threading;

using AdaptiveLighting.Abstractions;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     One line of the activity page: a report the engine published, with the position it arrived in.
/// </summary>
/// <remarks>
///     <see cref="Sequence"/> is not decoration. Two areas can publish on the same scheduler instant, and a
///     timeline ordered on <see cref="AreaSnapshot.Timestamp"/> alone would then shuffle those two rows between
///     renders. It is also what the page counts with: sequences are dense and never reused, so "how many arrived
///     while you were reading" is one subtraction that stays correct after the oldest entries have been evicted.
/// </remarks>
/// <param name="Sequence">Where this report fell in the run, counting from one.</param>
/// <param name="Snapshot">The report itself, exactly as the engine published it.</param>
public sealed record ActivityEntry(long Sequence, AreaSnapshot Snapshot)
{
	/// <summary>The room the report came from — the timeline's left column.</summary>
	public string AreaName => Snapshot.AreaName;

	/// <summary>When the engine made this decision.</summary>
	public DateTimeOffset At => Snapshot.Timestamp;
}

/// <summary>
///     The timeline as one consistent read: everything the log was holding, and the sequence it had reached,
///     as of the same instant.
/// </summary>
/// <remarks>
///     <para>
///         <b>The bug this exists to prevent.</b> The page used to read <see cref="ActivityLog.Entries"/> and
///         then <see cref="ActivityLog.Newest"/>, each correctly locked but locked separately. A report landing
///         between the two was counted as shown while being absent from the list it was supposedly shown in: the
///         "new reports" button never appeared for it, and that row stayed invisible until some later report
///         happened to arrive — which in a quiet house is hours. The two numbers only mean anything together, so
///         they are handed over together.
///     </para>
/// </remarks>
/// <param name="Entries">Every report the log held, newest first.</param>
/// <param name="Newest">The sequence of the newest report recorded by that same instant, or zero when none had been.</param>
public sealed record ActivityTimeline(IReadOnlyList<ActivityEntry> Entries, long Newest);

/// <summary>
///     The engine's recent decisions, kept in order and bounded.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this is not the snapshot cache.</b> <see cref="AreaSnapshotCache"/> answers "what is each room
///         doing now" and so keeps exactly one report per room. The activity page asks the other question — "what
///         happened, and why" — which is the reports the cache overwrites. Both are fed from the one
///         <c>adaptive_lighting_area</c> subscription the cache already holds; nothing new is subscribed, and the
///         engine is untouched.
///     </para>
///     <para>
///         <b>Why it is bounded.</b> This process runs for months in a house. An unbounded list of every report
///         ever published is a memory leak that only shows up on the installations that have been up longest,
///         which are exactly the ones nobody is watching. The buffer therefore drops its oldest entry once it is
///         full, and <see cref="Capacity"/> is asserted by a test rather than left as a number in a field.
///     </para>
///     <para>
///         Only genuinely distinct reports arrive here: the engine already suppresses a snapshot that repeats
///         what an area last said (<c>AreaSnapshot.HasSameMeaningAs</c>), so the buffer holds transitions and real
///         condition changes, not a heartbeat.
///     </para>
/// </remarks>
public sealed class ActivityLog
{
	/// <summary>
	///     How many reports the log keeps before the oldest falls off.
	/// </summary>
	/// <remarks>
	///     Five hundred. A house of fifteen or so rooms publishes on the order of a few hundred reports a day —
	///     transitions plus the ticks where darkness or the period actually moved — so this is roughly a day or
	///     two of history: long enough to answer "why didn't the hall light come on this morning", short enough
	///     that the whole buffer is a few hundred kilobytes and can never grow past that however long the process
	///     lives. Reports are small records with no unmanaged tail, so the cap is about honesty rather than pressure.
	/// </remarks>
	public const int Capacity = 500;

	private readonly Lock _gate = new();
	private readonly Queue<ActivityEntry> _entries = new(Capacity);

	private long _sequence;

	/// <summary>Every report the log still holds, newest first — the order the page renders them in.</summary>
	/// <remarks>
	///     Only ever read on its own. A caller that needs the entries <i>and</i> how far the log has counted must
	///     take <see cref="Read"/> instead: two separately-locked reads leave a gap a report can land in, and a
	///     report that lands in that gap is counted as shown while being absent from what was shown.
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
	/// <remarks>
	///     The page holds the sequence it last drew and subtracts, which is how it can say "4 new" without
	///     redrawing the list underneath somebody's eyes.
	/// </remarks>
	public long Newest
	{
		get
		{
			lock (_gate)
				return _sequence;
		}
	}

	/// <summary>How many reports the log is holding.</summary>
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

	/// <summary>
	///     The timeline and the count that goes with it, under one lock.
	/// </summary>
	/// <remarks>
	///     The correctness lives here rather than in the page so that a page rewrite cannot quietly undo it. See
	///     <see cref="ActivityTimeline"/> for the report that used to go missing between the two reads this replaces.
	/// </remarks>
	/// <returns>Everything the log holds, newest first, and the sequence it has reached — both as of one instant.</returns>
	public ActivityTimeline Read()
	{
		lock (_gate)
			return new ActivityTimeline([.. _entries.Reverse()], _sequence);
	}

	/// <summary>
	///     Records a report, evicting the oldest once the buffer is full.
	/// </summary>
	/// <remarks>
	///     Called from the snapshot cache's subscription, which is a Home Assistant event-loop thread, while the
	///     page reads from a Blazor circuit's. The lock is what keeps a render from seeing a half-rotated buffer.
	/// </remarks>
	/// <param name="snapshot">The report to file.</param>
	/// <returns>The entry as filed, so a caller can see what sequence it took.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <c>null</c>.</exception>
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
