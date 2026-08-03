using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>
///     The house-wide mode an area reacts to. Derived from the flags on <see cref="HouseState"/>, never set
///     independently, so the two cannot disagree.
/// </summary>
public enum HouseMode
{
	Home,
	Away,
	Sleep,

	/// <summary>A guest option carrying a scene drives the areas into SceneHold; without one it is a flag only.</summary>
	Guest
}

/// <summary>
///     Which of the engine's own activation rules put the house on its mode, as opposed to a person choosing it
///     at the select.
/// </summary>
public enum ModeForceSource
{
	/// <summary>
	///     One of the option's <see cref="HouseModeOptionConfig.ActivateWhileOn"/> entities is on, so this option
	///     wins over whatever the select reads. Nothing writes the select, so this mode change has no visible cause.
	/// </summary>
	WhileEntityOn = 0,

	/// <summary>
	///     The whole house went <see cref="HouseModeOptionConfig.ActivateAfterNoMotionMinutes"/> without motion and
	///     the engine wrote the select to this option. Not known across a restart.
	/// </summary>
	NoMotionTimeout = 1
}

/// <summary>
///     The house mode the engine put itself on, and what is holding it there. <c>null</c> means nothing is
///     forcing anything and the select's value is the whole story.
/// </summary>
/// <remarks>
///     Every kind can be forced, not only <see cref="ModeKind.Away"/>. <c>EntityId</c> is set for
///     <see cref="ModeForceSource.WhileEntityOn"/> and <c>null</c> for a no-motion activation.
/// </remarks>
public sealed record ForcedMode(
	ModeKind Kind,
	string OptionValue,
	ModeForceSource Source,
	string? EntityId = null,
	string? EntityState = null)
{
	/// <summary>One sentence naming what is forcing the mode. The log and the UI share it, so there is one wording.</summary>
	public string Describe() =>
		Source is ModeForceSource.WhileEntityOn && EntityId is { Length: > 0 }
			? $"{Kind} mode is forced while {EntityId} is {EntityState ?? "on"}."
			: $"{Kind} mode was set because the whole house went quiet, not because anyone left.";
}

/// <summary>
///     An immutable snapshot of everything house-wide an area needs. The orchestrator owns the stream of these;
///     areas only read them.
/// </summary>
/// <remarks>
///     <c>ActiveKind</c> is the kind of the option the select stands on, <see cref="ModeKind.Normal"/> when
///     unconfigured. <c>KillSwitchActive</c> forbids the engine from commanding anything.
/// </remarks>
public sealed record HouseState(
	bool IsAnyoneHome,
	ModeKind ActiveKind,
	bool KillSwitchActive)
{
	/// <summary>The raw house-mode option string, or <c>null</c> when unconfigured, unknown or unavailable.</summary>
	public string? ModeValue { get; init; }

	/// <summary>The active option's <c>scene.*</c> when its kind is Away or Guest and it names one.</summary>
	public string? ActiveScene { get; init; }

	/// <summary>
	///     What is forcing <see cref="ActiveKind"/>, or <c>null</c> when the select's own value is the answer.
	/// </summary>
	/// <remarks>
	///     Part of the record equality, so the orchestrator republishes when a forcing entity flips even though
	///     <see cref="ActiveKind"/> and <see cref="ModeValue"/> have not moved.
	/// </remarks>
	public ForcedMode? Forced { get; init; }

	/// <summary>The state the engine starts in, before presence and mode have reported.</summary>
	public static readonly HouseState Initial = new(true, ModeKind.Normal, false);

	/// <summary>
	///     The mode, in precedence order: Away, then Sleep, then Guest. An away-kind option ORs with presence, so
	///     a house full of people can still be told to be Away.
	/// </summary>
	public HouseMode Mode =>
		!IsAnyoneHome || ActiveKind == ModeKind.Away ? HouseMode.Away
		: ActiveKind == ModeKind.Sleep ? HouseMode.Sleep
		: ActiveKind == ModeKind.Guest ? HouseMode.Guest
		: HouseMode.Home;
}
