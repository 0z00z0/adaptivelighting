using AdaptiveLighting.Configuration;
using AdaptiveLighting.Hosting;

namespace AdaptiveLighting.Web.Services;

/// <summary>The settings editor's write: the whole document, refused when the file has moved on since it loaded.</summary>
/// <remarks>
///     That page edits a whole draft, with no smaller part to scope a check to, so it takes a whole-document
///     token. The room page's, which is per area, is <see cref="RoomWrite"/>.
/// </remarks>
public static class DocumentWrite
{
	/// <summary>Whether the file holds something other than what the page was shown.</summary>
	public static bool ChangedUnderneath(LightingConfigStore store, string stamp)
	{
		ArgumentNullException.ThrowIfNull(store);

		if (!store.Exists)
			return false;

		try
		{
			return !string.Equals(ConfigStamp.OfDocument(store.Load()), stamp, StringComparison.Ordinal);
		}
		catch (LightingConfigException)
		{
			// A file that will not parse has nothing worth protecting, and this page's save is how it gets repaired.
			return false;
		}
	}

	/// <summary>The refusal, in the shape the save bar already renders.</summary>
	public static SaveResult Conflict(ValidationResult? validation) =>
		new(SaveStatus.Conflicted,
			validation ?? new ValidationResult(),
			"The settings file was changed somewhere else while this page was open, so nothing was written — "
			+ "saving now would have reverted that change. Discard changes reloads the file as it now stands.");
}
