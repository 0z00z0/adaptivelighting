using System.Text.Json;
using System.Text.Json.Serialization;

using AdaptiveLighting.Abstractions;

namespace AdaptiveLighting.Engine;

/// <summary>Keeps the activity record's rows across a restart, so the page is not empty after one.</summary>
// A null store reads the same as an empty one: unknown, never "nothing happened".
public interface IActivityJournalStore
{
	/// <summary>The rows the previous run left behind, oldest first, or empty when there are none to be had.</summary>
	IReadOnlyList<ActivityJournalRow> Load();

	/// <summary>Records the rows to keep, oldest first, reporting failure and never throwing.</summary>
	bool TrySave(IReadOnlyList<ActivityJournalRow> rows);
}

/// <summary>One journalled row: an area's report, or a house-wide notice, in the order it was recorded.</summary>
/// <remarks>Mirrors <c>ActivityEntry</c> in the web layer, which this store never references.</remarks>
public sealed record ActivityJournalRow(long Sequence, AreaSnapshot? Snapshot, EngineNotice? Notice);

/// <summary>The file's contents: the rows kept, and enough context for whoever opens it.</summary>
public sealed class ActivityJournalDocument
{
	public const string Explanation =
		"Machine-written note: the newest activity-record rows, kept so the page is not empty after a restart. "
		+ "Not configuration - nothing here is edited by hand, and deleting this file is safe: the only cost is "
		+ "an empty record until new rows arrive.";

	// Bumped only when an older file could be misread.
	public const int CurrentVersion = 1;

	[JsonPropertyName(JsonNoteFile.CommentProperty)]
	[JsonPropertyOrder(JsonNoteFile.CommentOrder)]
	public string Comment { get; set; } = Explanation;

	[JsonPropertyName(JsonNoteFile.VersionProperty)]
	[JsonPropertyOrder(JsonNoteFile.VersionOrder)]
	public int Version { get; set; } = CurrentVersion;

	// Context for a human reading the file; nothing reasons from it.
	[JsonPropertyName(JsonNoteFile.SavedAtProperty)]
	public DateTimeOffset SavedAt { get; set; }

	[JsonPropertyName("rows")]
	public IReadOnlyList<ActivityJournalRow>? Rows { get; set; }

	public static readonly JsonSerializerOptions SerializerOptions = JsonNoteFile.CreateSerializerOptions();
}
