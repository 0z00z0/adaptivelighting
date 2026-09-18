namespace AdaptiveLighting.Engine;

/// <summary>What a room shows of its recent past. Read by people, never by a rule.</summary>
public sealed record AreaHistory(DateTimeOffset? LastMotionAt, DateTimeOffset? ChangedAt, string? ChangedBy);

/// <summary>A hold made at the switch, and when its countdown last started.</summary>
public sealed record AreaHold(AreaState State, DateTimeOffset StartedAt);

/// <summary>What a rebuilt room takes over from the room it replaces when settings are saved.</summary>
// In memory only. A hold must never outlive the engine: after downtime nobody knows what happened at the switch.
public sealed record AreaCarryOver(AreaHistory History, AreaHold? Hold)
{
	/// <summary>A level test still running, handed on only when the saved document keeps the room.</summary>
	internal CarriedLevelTest? Test { get; init; }
}
