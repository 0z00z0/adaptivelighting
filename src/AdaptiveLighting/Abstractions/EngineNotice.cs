namespace AdaptiveLighting.Abstractions;

/// <summary>Something the engine did to itself, as opposed to something an area reported.</summary>
public enum EngineNoticeKind
{
	/// <summary>The engine came up, or came back up, on the settings file as it stands.</summary>
	Started,

	/// <summary>A save was accepted and every area controller was rebuilt on it.</summary>
	SettingsSaved
}

/// <summary>
///     One house-wide thing the engine did. Carries no area state: a rebuild is not something a room reported.
/// </summary>
/// <remarks><c>At</c> is wall-clock, not scheduler time: nothing schedules a rebuild.</remarks>
public sealed record EngineNotice(EngineNoticeKind Kind, DateTimeOffset At);
