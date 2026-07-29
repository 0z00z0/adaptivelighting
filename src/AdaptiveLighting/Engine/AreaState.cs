namespace AdaptiveLighting.Engine;

/// <summary>
///     The states of the per-area machine. See 02-architecture.md §5 for the transition diagram; the states
///     here and the arrows there are meant to stay in step.
/// </summary>
public enum AreaState
{
	/// <summary>Kill switch on, or the area is configured off. The engine observes and publishes but commands nothing.</summary>
	Disabled,

	/// <summary>Nobody home. Motion does nothing.</summary>
	Away,

	/// <summary>Under automatic control, no recent motion. The engine's resting state.</summary>
	AutoVacant,

	/// <summary>Under automatic control, occupied, lights held at the circadian target.</summary>
	AutoActive,

	/// <summary>Vacancy timed out; dimmed as a warning. Motion still rescues the area.</summary>
	PreOff,

	/// <summary>A human set the lights and they stay set. Circadian retargeting is suspended.</summary>
	OverriddenOn,

	/// <summary>A human turned the lights off. Motion is deliberately ignored until the area goes vacant.</summary>
	SuppressedOff,

	/// <summary>
	///     A Guest-kind mode with a scene holds the area (09 §3.4): the scene is the look, so the engine commands
	///     nothing and ignores motion for commanding until the mode resets to Normal.
	/// </summary>
	SceneHold
}

/// <summary>
///     Why an area changed state. Carried in <see cref="AdaptiveLighting.Abstractions.AreaSnapshot"/>,
///     because a state machine you cannot see the reasoning of is a state machine you cannot debug.
/// </summary>
public enum TransitionReason
{
	/// <summary>The area was just created, and its lights were off.</summary>
	Startup,

	/// <summary>
	///     The area was just created and found its lights already on, so it took charge of them without
	///     touching them. The engine did not light this room — it inherited it, most often from itself
	///     across a restart.
	/// </summary>
	AdoptedAtStartup,

	/// <summary>A motion sensor reported occupancy.</summary>
	Motion,

	/// <summary>No motion for the vacancy timeout.</summary>
	VacancyTimeout,

	/// <summary>The pre-off warning elapsed without motion.</summary>
	PreOffElapsed,

	/// <summary>A human turned lights on, or changed their level or colour.</summary>
	ManualOn,

	/// <summary>A human turned lights off.</summary>
	ManualOff,

	/// <summary>The manual override ran its course.</summary>
	OverrideExpired,

	/// <summary>The area stayed vacant long enough to lift a manual turn-off.</summary>
	SuppressionLifted,

	/// <summary>Presence reported the house empty.</summary>
	EveryoneLeft,

	/// <summary>Presence reported the first arrival.</summary>
	FirstPersonArrived,

	/// <summary>The kill switch, or the area's Enabled flag, changed.</summary>
	EnablementChanged,

	/// <summary>The circadian target moved.</summary>
	CircadianTick,

	/// <summary>The house mode kind or value changed.</summary>
	HouseModeChanged,

	/// <summary>A Guest scene took the area into, or released it from, an indefinite hold.</summary>
	SceneHold
}

/// <summary>
///     Which gate is refusing to switch an area's lights on for movement. Carried in
///     <see cref="AdaptiveLighting.Abstractions.AreaSnapshot"/> because two of these refusals are otherwise
///     invisible from outside: a sleeping house and a blocking entity both leave the area in
///     <see cref="AreaState.AutoVacant"/>, which is the same state as an area simply waiting for someone to
///     walk in. A reader holding only the state and the darkness verdict would promise a light that is never
///     going to come on.
/// </summary>
public enum AutoOnBlock
{
	/// <summary>Nothing is refusing: movement would light the area.</summary>
	None,

	/// <summary>Automatic lighting is switched off for this area.</summary>
	Disabled,

	/// <summary>The master switch is on, so the engine commands nothing anywhere.</summary>
	KillSwitch,

	/// <summary>The house is away, or nobody is home.</summary>
	Away,

	/// <summary>
	///     A Guest-kind mode with a scene holds the area: the scene is the look, so movement records occupancy and
	///     commands nothing.
	/// </summary>
	/// <remarks>
	///     Added when declined movement became reportable. The area refuses movement in exactly four places, and
	///     this was the one with no name — so a report of movement into a scene-held room either named a different
	///     gate or named none, and both are worse than the silence they replaced.
	/// </remarks>
	SceneHold,

	/// <summary>The house is asleep and this area is set not to light itself while it is.</summary>
	Sleep,

	/// <summary>
	///     One of the area's <c>IgnoreWhenOn</c> entities is on. Which one is published beside this, because
	///     "something is on" sends somebody hunting through the room.
	/// </summary>
	EntityOn,

	/// <summary>The darkness gate says it is too bright.</summary>
	NotDark
}
