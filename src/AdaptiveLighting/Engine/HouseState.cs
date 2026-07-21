using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>
///     The house-wide mode a zone reacts to. Derived from the flags on <see cref="HouseState"/> rather than set
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

	/// <summary>Somebody is home and guests are present. A guest option carrying a scene drives the zones into SceneHold; without a scene it is a dashboard flag only.</summary>
	Guest
}

/// <summary>
///     An immutable snapshot of everything house-wide a zone needs. The orchestrator owns the stream of these;
///     zones only read them.
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

	/// <summary>The active option's <c>scene.*</c>, when its kind is Away/Guest and it names one; else <c>null</c>. Zones read whether a scene holds; the orchestrator uses the id.</summary>
	public string? ActiveScene { get; init; }

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
