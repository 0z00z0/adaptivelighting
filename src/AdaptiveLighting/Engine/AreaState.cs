namespace AdaptiveLighting.Engine;

/// <summary>The states of the per-area machine.</summary>
public enum AreaState
{
	/// <summary>Kill switch on, or the area configured off: the engine observes and publishes but commands nothing.</summary>
	Disabled,

	/// <summary>Nobody home, so motion does nothing.</summary>
	Away,

	/// <summary>Under automatic control with no recent motion; the engine's resting state.</summary>
	AutoVacant,

	/// <summary>Under automatic control, occupied, lights held at the circadian target.</summary>
	AutoActive,

	/// <summary>Vacancy timed out and the area is dimmed as a warning, but motion still rescues it.</summary>
	PreOff,

	/// <summary>A human set the lights and they stay set, with circadian retargeting suspended.</summary>
	OverriddenOn,

	/// <summary>A human turned the lights off, and motion is ignored until the area goes vacant.</summary>
	SuppressedOff,

	/// <summary>A Guest-kind mode with a scene holds the area, so the engine commands nothing until it resets to Normal.</summary>
	SceneHold
}

/// <summary>Why an area changed state.</summary>
public enum TransitionReason
{
	/// <summary>The area was just created, and its lights were off.</summary>
	Startup,

	/// <summary>The area was just created, found its lights already on, and took charge without touching them.</summary>
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

	/// <summary>Presence reported the house empty; an area going Away because the mode says so reports <see cref="HouseModeChanged"/>.</summary>
	EveryoneLeft,

	FirstPersonArrived,

	/// <summary>The kill switch, or the area's Enabled flag, changed.</summary>
	EnablementChanged,

	/// <summary>The circadian target moved.</summary>
	CircadianTick,

	/// <summary><see cref="AreaState.Away"/> under this reason is the mode holding the room shut, never an empty house.</summary>
	HouseModeChanged,

	/// <summary>A Guest scene took the area into, or released it from, an indefinite hold.</summary>
	SceneHold
}

/// <summary>Which gate is refusing to switch an area's lights on for movement.</summary>
// A sleeping house and a blocking entity both leave the area in AutoVacant, the same state as one waiting for
// someone to walk in, so the state alone cannot say which gate applies.
public enum AutoOnBlock
{
	/// <summary>Nothing is refusing: movement would light the area.</summary>
	None,

	/// <summary>Automatic lighting is switched off for this area.</summary>
	Disabled,

	/// <summary>The master switch is on, so the engine commands nothing anywhere.</summary>
	KillSwitch,

	/// <summary>The house is away, or nobody is home; <see cref="AdaptiveLighting.Abstractions.AreaSnapshot.IsAnyoneHome"/> says which.</summary>
	Away,

	/// <summary>A Guest-kind mode with a scene holds the area; movement records occupancy and commands nothing.</summary>
	SceneHold,

	/// <summary>The house is asleep and this area is set not to light itself while it is.</summary>
	Sleep,

	/// <summary>One of the area's <c>IgnoreWhenOn</c> entities applies: on, or off under <c>IgnoreWhenOnInverted</c>.</summary>
	EntityOn,

	/// <summary>The darkness gate says it is too bright.</summary>
	NotDark
}
