using System.Collections.Concurrent;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Hosting;

/// <summary>Forwards every publish to the real publisher, and marks the room-history note dirty on the way.</summary>
/// <remarks>
///     Reads only what a person is already shown: <see cref="AreaSnapshot.LastMotionAt"/>,
///     <see cref="AreaSnapshot.ChangedAt"/> and <see cref="AreaSnapshot.ChangedBy"/>. The live dictionary is
///     the host's own, shared across every rebuild, so a room that has not published since the last save is
///     not dropped from the file a save writes.
/// </remarks>
internal sealed class HistoryTrackingPublisher(
	IStatePublisher inner,
	IRoomHistoryStore store,
	ConcurrentDictionary<string, AreaHistory> live) : IStatePublisher
{
	public void Publish(AreaSnapshot snapshot)
	{
		inner.Publish(snapshot);

		string key = snapshot.AreaId is { Length: > 0 } id ? id : snapshot.AreaName;
		live[key] = new AreaHistory(snapshot.LastMotionAt, snapshot.ChangedAt, snapshot.ChangedBy);

		// Coalesced: this only marks the file dirty. The registry's own flusher writes it, at most once a minute.
		store.TrySave(live);
	}
}
