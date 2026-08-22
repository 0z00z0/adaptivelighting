using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>The house-wide mode an area reacts to.</summary>
// Derived from the flags on HouseState, never set independently, so the two cannot disagree.
public enum HouseMode
{
	Home,
	Away,
	Sleep,

	/// <summary>A guest option carrying a scene drives the areas into SceneHold; without one it is a flag only.</summary>
	Guest
}

/// <summary>Which of the engine's own activation rules put the house on its mode, as opposed to a person choosing it.</summary>
public enum ModeForceSource
{
	/// <summary>An <see cref="HouseModeOptionConfig.ActivateWhileOn"/> entity is on, so this option wins over whatever the select reads.</summary>
	// Nothing writes the select, so this mode change has no visible cause.
	WhileEntityOn = 0,

	/// <summary>The house went <see cref="HouseModeOptionConfig.ActivateAfterNoMotionMinutes"/> without motion and the engine wrote the select.</summary>
	// Not known across a restart.
	NoMotionTimeout = 1
}

/// <summary>The house mode the engine put itself on, and what is holding it there.</summary>
// A null ForcedMode means the select's value is the whole story. Every kind can be forced, not only Away.
// EntityId is set for WhileEntityOn and null for a no-motion activation.
public sealed record ForcedMode(
	ModeKind Kind,
	string OptionValue,
	ModeForceSource Source,
	string? EntityId = null,
	string? EntityState = null)
{
	/// <summary>One sentence naming what is forcing the mode, shared by the log and the UI.</summary>
	public string Describe() =>
		Source is ModeForceSource.WhileEntityOn && EntityId is { Length: > 0 }
			? $"{Kind} mode is forced while {EntityId} is {EntityState ?? "on"}."
			: $"{Kind} mode was set because the whole house went quiet, not because anyone left.";
}

/// <summary>An immutable snapshot of everything house-wide an area needs.</summary>
// The orchestrator owns the stream of these; areas only read them. ActiveKind is the kind of the option the
// select stands on, Normal when unconfigured. KillSwitchActive forbids the engine from commanding anything.
public sealed record HouseState(
	bool IsAnyoneHome,
	ModeKind ActiveKind,
	bool KillSwitchActive)
{
	/// <summary>The raw house-mode option string, or <c>null</c> when unconfigured, unknown or unavailable.</summary>
	public string? ModeValue { get; init; }

	/// <summary>The active option's <c>scene.*</c> when its kind is Away or Guest and it names one.</summary>
	public string? ActiveScene { get; init; }

	/// <summary>What is forcing <see cref="ActiveKind"/>, or <c>null</c> when the select's own value is the answer.</summary>
	// Part of the record equality, so the orchestrator republishes when a forcing entity flips even though
	// ActiveKind and ModeValue have not moved.
	public ForcedMode? Forced { get; init; }

	/// <summary>The state the engine starts in, before presence and mode have reported.</summary>
	public static readonly HouseState Initial = new(true, ModeKind.Normal, false);

	/// <summary>The mode, in precedence order: Away, then Sleep, then Guest.</summary>
	// An away-kind option ORs with presence, so a house full of people can still be told to be Away.
	public HouseMode Mode =>
		!IsAnyoneHome || ActiveKind == ModeKind.Away ? HouseMode.Away
		: ActiveKind == ModeKind.Sleep ? HouseMode.Sleep
		: ActiveKind == ModeKind.Guest ? HouseMode.Guest
		: HouseMode.Home;
}
