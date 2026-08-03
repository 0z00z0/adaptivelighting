namespace AdaptiveLighting.Engine;

/// <summary>The states of the per-area machine.</summary>
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

	/// <summary>A human turned the lights off. Motion is ignored until the area goes vacant.</summary>
	SuppressedOff,

	/// <summary>
	///     A Guest-kind mode with a scene holds the area: the scene is the look, so the engine commands nothing
	///     and ignores motion for commanding until the mode resets to Normal.
	/// </summary>
	SceneHold
}

/// <summary>Why an area changed state. Carried in <see cref="AdaptiveLighting.Abstractions.AreaSnapshot"/>.</summary>
public enum TransitionReason
{
	/// <summary>The area was just created, and its lights were off.</summary>
	Startup,

	/// <summary>
	///     The area was just created and found its lights already on, so it took charge without touching them.
	///     Most often it is inheriting from itself across a restart.
	/// </summary>
	AdoptedAtStartup,

	Motion,
	VacancyTimeout,
	PreOffElapsed,

	/// <summary>A human turned lights on, or changed their level or colour.</summary>
	ManualOn,

	ManualOff,
	OverrideExpired,

	/// <summary>The area stayed vacant long enough to lift a manual turn-off.</summary>
	SuppressionLifted,

	/// <summary>
	///     Presence reported the house empty. Presence only: an area going Away because the mode says so reports
	///     <see cref="HouseModeChanged"/>.
	/// </summary>
	EveryoneLeft,

	FirstPersonArrived,

	/// <summary>The kill switch, or the area's Enabled flag, changed.</summary>
	EnablementChanged,

	/// <summary>The circadian target moved.</summary>
	CircadianTick,

	/// <summary>
	///     The house mode kind or value changed, including into and out of an away-kind mode.
	///     <see cref="AreaState.Away"/> under this reason is the mode holding the room shut, not an empty house.
	/// </summary>
	HouseModeChanged,

	/// <summary>A Guest scene took the area into, or released it from, an indefinite hold.</summary>
	SceneHold
}

/// <summary>
///     Which gate is refusing to switch an area's lights on for movement. Carried in
///     <see cref="AdaptiveLighting.Abstractions.AreaSnapshot"/>: a sleeping house and a blocking entity both leave
///     the area in <see cref="AreaState.AutoVacant"/>, the same state as one simply waiting for someone to walk in.
/// </summary>
public enum AutoOnBlock
{
	/// <summary>Nothing is refusing: movement would light the area.</summary>
	None,

	/// <summary>Automatic lighting is switched off for this area.</summary>
	Disabled,

	/// <summary>The master switch is on, so the engine commands nothing anywhere.</summary>
	KillSwitch,

	/// <summary>
	///     The house is away, or nobody is home. Two causes under one name:
	///     <see cref="AdaptiveLighting.Abstractions.AreaSnapshot.IsAnyoneHome"/> says which, and a wording that
	///     does not read it says "nobody is home yet" to people standing in the room.
	/// </summary>
	Away,

	/// <summary>A Guest-kind mode with a scene holds the area; movement records occupancy and commands nothing.</summary>
	SceneHold,

	/// <summary>The house is asleep and this area is set not to light itself while it is.</summary>
	Sleep,

	/// <summary>One of the area's <c>IgnoreWhenOn</c> entities is on. Which one is published beside this.</summary>
	EntityOn,

	/// <summary>The darkness gate says it is too bright.</summary>
	NotDark
}
