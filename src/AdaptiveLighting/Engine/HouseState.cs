using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>
///     The house-wide mode an area reacts to. Derived from the flags on <see cref="HouseState"/> rather than set
///     independently, so the two can never disagree.
/// </summary>
public enum HouseMode
{
	/// <summary>Somebody is home and awake.</summary>
	Home,

	/// <summary>Nobody is home.</summary>
	Away,

	/// <summary>Somebody is home and the house is asleep.</summary>
	Sleep,

	/// <summary>Somebody is home and guests are present. A guest option carrying a scene drives the areas into SceneHold; without a scene it is a dashboard flag only.</summary>
	Guest
}

/// <summary>
///     Which of the engine's own activation rules put the house on the mode it is on, rather than a person
///     choosing it at the select.
/// </summary>
/// <remarks>
///     The distinction exists because it was missing. A cabin ran <c>ActivateWhileOn: [input_boolean.occupancy]</c>
///     on its Away option and that boolean had been on for hours; every settings save rebuilds every area
///     controller, so each edit re-asserted Away and swept the house dark while the owner stood in it. The engine
///     said "Everyone left the house" and the room said "Movement, but nobody is home yet" while both
///     <c>person.*</c> entities read <c>home</c> throughout — a cause the engine had never checked, and an hour
///     spent hunting a presence fault that did not exist.
/// </remarks>
public enum ModeForceSource
{
	/// <summary>
	///     One of the option's <see cref="HouseModeOptionConfig.ActivateWhileOn"/> entities is on, so this option
	///     wins over whatever the select reads. Nothing writes the select, so this is the mode change with no
	///     visible cause — which is exactly why it has to name itself.
	/// </summary>
	WhileEntityOn = 0,

	/// <summary>
	///     The whole house went <see cref="HouseModeOptionConfig.ActivateAfterNoMotionMinutes"/> without motion and
	///     the engine wrote the select to this option. Only known for as long as the value the engine wrote still
	///     stands, and never across a restart: after one, nobody knows why the select reads what it reads, and
	///     guessing is the failure this whole distinction exists to stop.
	/// </summary>
	NoMotionTimeout = 1
}

/// <summary>
///     The house mode the engine put itself on, and what is holding it there.
/// </summary>
/// <remarks>
///     A record rather than three loose fields on <see cref="HouseState"/> so the kind, the rule and the entity
///     cannot drift apart — and so <c>null</c> says the one thing worth saying on its own: nothing is forcing
///     anything, the select's value is the whole story.
/// </remarks>
/// <param name="Kind">The forced option's kind. Every kind can be forced, not only <see cref="ModeKind.Away"/>: Sleep has exactly the same shape.</param>
/// <param name="OptionValue">The option's own value string, as the select spells it.</param>
/// <param name="Source">Which of the engine's two activation rules is holding it.</param>
/// <param name="EntityId">The entity holding it on, for <see cref="ModeForceSource.WhileEntityOn"/>; <c>null</c> otherwise — a no-motion activation has no entity behind it.</param>
/// <param name="EntityState">That entity's state, so the sentence can say <i>while X is on</i> rather than leaving the reader to look it up.</param>
public sealed record ForcedMode(
	ModeKind Kind,
	string OptionValue,
	ModeForceSource Source,
	string? EntityId = null,
	string? EntityState = null)
{
	/// <summary>
	///     One sentence naming what is forcing the mode: <c>Away mode is forced while input_boolean.occupancy is on.</c>
	/// </summary>
	/// <remarks>
	///     Written once, here, because the engine's log and whatever renders the house both have to say it and two
	///     wordings of one fact is how a reader ends up trusting the wrong one. That exact sentence would have ended
	///     the incident in <see cref="ModeForceSource"/> in seconds.
	/// </remarks>
	public string Describe() =>
		Source is ModeForceSource.WhileEntityOn && EntityId is { Length: > 0 }
			? $"{Kind} mode is forced while {EntityId} is {EntityState ?? "on"}."
			: $"{Kind} mode was set because the whole house went quiet, not because anyone left.";
}

/// <summary>
///     An immutable snapshot of everything house-wide an area needs. The orchestrator owns the stream of these;
///     areas only read them.
/// </summary>
/// <param name="IsAnyoneHome">Whether presence says the house is occupied.</param>
/// <param name="ActiveKind">The kind of the option the select currently stands on (09 §3.1). <see cref="ModeKind.Normal"/> when unconfigured.</param>
/// <param name="KillSwitchActive">Whether the engine is forbidden from commanding anything.</param>
public sealed record HouseState(
	bool IsAnyoneHome,
	ModeKind ActiveKind,
	bool KillSwitchActive)
{
	/// <summary>The raw current house-mode option string, or <c>null</c> when unconfigured / unknown / unavailable.</summary>
	public string? ModeValue { get; init; }

	/// <summary>The active option's <c>scene.*</c>, when its kind is Away/Guest and it names one; else <c>null</c>. Areas read whether a scene holds; the orchestrator uses the id.</summary>
	public string? ActiveScene { get; init; }

	/// <summary>
	///     What is forcing <see cref="ActiveKind"/>, or <c>null</c> when the select's own value is the answer.
	/// </summary>
	/// <remarks>
	///     Compared by record equality with the rest of this state, so the orchestrator republishes the moment a
	///     forcing entity goes on or off — including the case <see cref="ActiveKind"/> cannot show, where Sleep is
	///     forced by an entity rather than chosen and the mode value never moves.
	/// </remarks>
	public ForcedMode? Forced { get; init; }

	/// <summary>The state the engine starts in, before presence and mode have reported.</summary>
	public static readonly HouseState Initial = new(true, ModeKind.Normal, false);

	/// <summary>
	///     The mode, in precedence order. Away beats sleep — an empty house is not a sleeping one — and sleep
	///     beats guest, because a sleeping household with guests still wants the night rules. An away-kind option
	///     ORs with presence, so a house full of people can still be told to be Away.
	/// </summary>
	public HouseMode Mode =>
		!IsAnyoneHome || ActiveKind == ModeKind.Away ? HouseMode.Away
		: ActiveKind == ModeKind.Sleep ? HouseMode.Sleep
		: ActiveKind == ModeKind.Guest ? HouseMode.Guest
		: HouseMode.Home;
}
