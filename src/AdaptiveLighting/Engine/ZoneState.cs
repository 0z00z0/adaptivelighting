namespace AdaptiveLighting.Engine;

/// <summary>
///     The states of the per-zone machine. See 02-architecture.md §5 for the transition diagram; the states
///     here and the arrows there are meant to stay in step.
/// </summary>
public enum ZoneState
{
	/// <summary>Kill switch on, or the zone is configured off. The engine observes and publishes but commands nothing.</summary>
	Disabled,

	/// <summary>Nobody home. Motion does nothing.</summary>
	Away,

	/// <summary>Under automatic control, no recent motion. The engine's resting state.</summary>
	AutoVacant,

	/// <summary>Under automatic control, occupied, lights held at the circadian target.</summary>
	AutoActive,

	/// <summary>Vacancy timed out; dimmed as a warning. Motion still rescues the zone.</summary>
	PreOff,

	/// <summary>A human set the lights and they stay set. Circadian retargeting is suspended.</summary>
	OverriddenOn,

	/// <summary>A human turned the lights off. Motion is deliberately ignored until the zone goes vacant.</summary>
	SuppressedOff,

	/// <summary>
	///     A Guest-kind mode with a scene holds the zone (09 §3.4): the scene is the look, so the engine commands
	///     nothing and ignores motion for commanding until the mode resets to Normal.
	/// </summary>
	SceneHold
}

/// <summary>
///     Why a zone changed state. Carried in <see cref="AdaptiveLighting.Abstractions.ZoneSnapshot"/>,
///     because a state machine you cannot see the reasoning of is a state machine you cannot debug.
/// </summary>
public enum TransitionReason
{
	/// <summary>The zone was just created, and its lights were off.</summary>
	Startup,

	/// <summary>
	///     The zone was just created and found its lights already on, so it took charge of them without
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

	/// <summary>The zone stayed vacant long enough to lift a manual turn-off.</summary>
	SuppressionLifted,

	/// <summary>Presence reported the house empty.</summary>
	EveryoneLeft,

	/// <summary>Presence reported the first arrival.</summary>
	FirstPersonArrived,

	/// <summary>The kill switch, or the zone's Enabled flag, changed.</summary>
	EnablementChanged,

	/// <summary>The circadian target moved.</summary>
	CircadianTick,

	/// <summary>The house mode kind or value changed.</summary>
	HouseModeChanged,

	/// <summary>A Guest scene took the zone into, or released it from, an indefinite hold.</summary>
	SceneHold
}
