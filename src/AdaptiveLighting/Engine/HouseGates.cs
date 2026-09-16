namespace AdaptiveLighting.Engine;

/// <summary>The house-wide and room-wide gates, in the order they are asked.</summary>
// The order is the ladder itself: a lower member is asked before a higher one, and HouseGates.FirstClosed
// walks them in this order. Moving a member changes which gate a refusal names.
internal enum HouseGate
{
	/// <summary>Every gate the caller asked about is open.</summary>
	None = 0,

	/// <summary>The controller has been replaced by one built on settings just saved.</summary>
	Rebuilt,

	/// <summary>The master switch is on.</summary>
	KillSwitch,

	/// <summary>Automatic lighting is switched off for this room.</summary>
	Disabled,

	/// <summary>The house is set to away.</summary>
	Away,

	/// <summary>A guest scene is holding the room.</summary>
	SceneHold,

	/// <summary>Sleep mode blocks the room being lit for movement.</summary>
	Sleep,

	/// <summary>An entity the room watches is on.</summary>
	EntityOn,

	/// <summary>The room is not dark enough.</summary>
	NotDark
}

/// <summary>What the gates are judged against at one instant.</summary>
// BlockingEntity is a function because the callers that stop before that gate must not read those entities
// at all. Dark is passed in, so the caller decides which reading applies.
internal readonly record struct HouseGateState(
	bool Rebuilding,
	bool KillSwitchActive,
	bool Enabled,
	HouseMode Mode,
	string? ActiveScene,
	bool SleepBlocksAutoOn,
	Func<string?> BlockingEntity,
	bool Dark);

/// <summary>The one ordered gate evaluation the engine reads.</summary>
// Auto-on blocking, the level-test refusal and the light-now refusal all ask this and each turns the gate into
// its own sentence, so a new house-wide gate is added here alone. A caller names the last gate it honours:
// that is what keeps a level test running under sleep mode, and what keeps the entity and darkness reads off
// the paths that ignore them.
internal static class HouseGates
{
	/// <summary>The sentence every caller gives while the room is being rebuilt on the settings just saved.</summary>
	public const string BeingRebuilt = "This room is being rebuilt on the settings that were just saved. Try again in a moment.";

	/// <summary>The first closed gate between <paramref name="from"/> and <paramref name="until"/>.</summary>
	/// <returns><see cref="HouseGate.None"/> when every gate in that span is open.</returns>
	public static HouseGate FirstClosed(in HouseGateState state, HouseGate from, HouseGate until) =>
		FirstClosed(state, from, until, out _);

	/// <summary>The first closed gate between <paramref name="from"/> and <paramref name="until"/>.</summary>
	/// <returns><see cref="HouseGate.None"/> when every gate in that span is open.</returns>
	/// <remarks><paramref name="blockingEntity"/> names the entity that closed <see cref="HouseGate.EntityOn"/>,
	/// and is <c>null</c> for every other answer.</remarks>
	public static HouseGate FirstClosed(in HouseGateState state, HouseGate from, HouseGate until, out string? blockingEntity)
	{
		blockingEntity = null;

		if (Asked(HouseGate.Rebuilt) && state.Rebuilding)
			return HouseGate.Rebuilt;

		if (Asked(HouseGate.KillSwitch) && state.KillSwitchActive)
			return HouseGate.KillSwitch;

		if (Asked(HouseGate.Disabled) && !state.Enabled)
			return HouseGate.Disabled;

		if (Asked(HouseGate.Away) && state.Mode == HouseMode.Away)
			return HouseGate.Away;

		if (Asked(HouseGate.SceneHold) && state.Mode == HouseMode.Guest && state.ActiveScene is { Length: > 0 })
			return HouseGate.SceneHold;

		if (Asked(HouseGate.Sleep) && state.SleepBlocksAutoOn && state.Mode == HouseMode.Sleep)
			return HouseGate.Sleep;

		if (Asked(HouseGate.EntityOn) && state.BlockingEntity() is { } blocking)
		{
			blockingEntity = blocking;
			return HouseGate.EntityOn;
		}

		if (Asked(HouseGate.NotDark) && !state.Dark)
			return HouseGate.NotDark;

		return HouseGate.None;

		bool Asked(HouseGate gate) => gate >= from && gate <= until;
	}
}
