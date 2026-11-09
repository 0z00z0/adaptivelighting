using AdaptiveLighting.Engine;

namespace Adaptive.Engine;

/// <summary>What a room shows of its recent past. Read by people, never by a rule.</summary>
public sealed record AreaHistory(DateTimeOffset? LastMotionAt, DateTimeOffset? ChangedAt, string? ChangedBy);

/// <summary>A hold made at the switch, and when its countdown last started.</summary>
public sealed record AreaHold(AreaState State, DateTimeOffset StartedAt);

/// <summary>A lit room's no-movement countdown or dim warning, and when that countdown last started.</summary>
public sealed record AreaCountdown(AreaState State, DateTimeOffset StartedAt, bool LeadIn);

/// <summary>What a rebuilt room takes over from the room it replaces when settings are saved.</summary>
// In memory only. A hold must never outlive the engine: after downtime nobody knows what happened at the switch.
public sealed record AreaCarryOver(AreaHistory History, AreaHold? Hold)
{
	/// <summary>A level test still running, handed on only when the saved document keeps the room.</summary>
	internal CarriedLevelTest? Test { get; init; }

	// Only a save fills this. A real start has no countdown to continue, so it starts one at the take-over.
	internal AreaCountdown? Countdown { get; init; }
}
