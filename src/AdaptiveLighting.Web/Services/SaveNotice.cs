namespace AdaptiveLighting.Web.Services;

/// <summary>
///     A room page's whole answer to "did that land?": a refusal, a write about to happen, a confirmation of the
///     last one, or nothing at all.
/// </summary>
/// <remarks>
///     One line rather than a line and a floating bar. Anything pinned to the viewport covers whatever body text
///     is under it, and the daylight curve's caption is what it covered.
/// </remarks>
/// <param name="Text">What the line says. Empty when there is nothing to say.</param>
/// <param name="Class">The class that paints it.</param>
public readonly record struct SaveNotice(string Text, string Class)
{
	public const string Failed = "room-save-bad";

	public const string Pending = "room-save-pending";

	public const string Done = "room-save-done";

	public const string Idle = "room-save-ok";

	/// <param name="failed">Whether the last write was refused.</param>
	/// <param name="dirty">Whether an edit is waiting to be written.</param>
	/// <param name="savedAt">When the file was last written, or <c>null</c> if it has not been.</param>
	/// <param name="now">The page's own clock, so the confirmation clears on the tick that redraws it.</param>
	/// <param name="lingers">How long a confirmation stays up.</param>
	// Order is the ranking: a refusal stands until it is resolved, and a fresh edit outranks the confirmation of
	// the one before it.
	public static SaveNotice Of(bool failed, bool dirty, DateTimeOffset? savedAt, DateTimeOffset now, TimeSpan lingers)
	{
		if (failed)
			return new SaveNotice("not saved", Failed);

		if (dirty)
			return new SaveNotice("saving in a moment…", Pending);

		if (savedAt is { } saved && now - saved < lingers)
			return new SaveNotice(
				$"Saved {saved.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)}",
				Done);

		return new SaveNotice(string.Empty, Idle);
	}
}
