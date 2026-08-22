using AdaptiveLighting.Configuration;
using AdaptiveLighting.Hosting;

namespace AdaptiveLighting.Web.Services;

/// <summary>The slot of the document the room page owns, and that slot as it stood on disk when the page read it.</summary>
public sealed record RoomWriteToken(string? AreaId, string Stamp);

/// <summary>What one scoped write did, and the token the page carries on with, unchanged unless the write reached the disk.</summary>
public sealed record RoomWriteResult(SaveResult Result, RoomWriteToken Token);

/// <summary>The room page's write: one area slot, applied to the document as it is on disk at the moment of writing.</summary>
/// <remarks>
///     The room page reads the whole document once, when it opens, and stays open, so writing that copy back would
///     revert anything written in between, the engine's own writes included. This reads the file again, checks that
///     this one room is still what the page was shown, and applies the page's room onto the fresh document. The save
///     is still <see cref="LightingEngineHost.Save"/>, so normalisation, validation and the engine rebuild are unchanged.
/// </remarks>
public static class RoomWrite
{
	public static RoomWriteToken Open(AdaptiveLightingConfig document, string? areaId) =>
		new(areaId, ConfigStamp.OfArea(document, areaId));

	/// <summary>
	///     Writes <paramref name="room"/> into its slot of the current document, or removes the slot when
	///     <paramref name="room"/> is <c>null</c>.
	/// </summary>
	/// <exception cref="LightingConfigException">The document could not be read or written.</exception>
	public static RoomWriteResult Save(
		LightingEngineHost engine,
		RoomWriteToken token,
		AreaConfig? room,
		string roomName)
	{
		ArgumentNullException.ThrowIfNull(engine);
		ArgumentNullException.ThrowIfNull(token);

		AdaptiveLightingConfig working = engine.Store.Load();

		if (!string.Equals(ConfigStamp.OfArea(working, token.AreaId), token.Stamp, StringComparison.Ordinal))
			return new RoomWriteResult(
				new SaveResult(SaveStatus.Conflicted, new ValidationResult(), Conflict(roomName)),
				token);

		ApplyArea(working, token.AreaId, room);

		SaveResult saved = engine.Save(working);

		if (!saved.Written)
			return new RoomWriteResult(saved, token);

		// Re-read, never stamped off the object just sent: the store normalises on the way out, so the next save
		// has to match the bytes on disk.
		return new RoomWriteResult(saved, Open(engine.Store.Load(), room?.AreaId ?? token.AreaId));
	}

	private static string Conflict(string roomName) =>
		$"{roomName} was changed somewhere else while this page was open, so nothing was written — saving now "
		+ "would have reverted that change. Reload the room to see it as it now stands, then make your change again.";

	/// <summary>Puts <paramref name="area"/> in the slot <paramref name="areaId"/> names, leaving every other area as it was read.</summary>
	private static void ApplyArea(AdaptiveLightingConfig document, string? areaId, AreaConfig? area)
	{
		int index = areaId is { Length: > 0 }
			? document.Areas.FindIndex(
				candidate => string.Equals(candidate.AreaId, areaId, StringComparison.Ordinal))
			: -1;

		if (index < 0)
		{
			// Only reachable on a slot that was already absent when the page read it. Appending keeps this total
			// instead of silently dropping a write.
			if (area is not null)
				document.Areas.Add(area);

			return;
		}

		if (area is null)
			document.Areas.RemoveAt(index);
		else
			document.Areas[index] = area;
	}
}
