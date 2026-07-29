using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     How one room state is drawn: a shape, a semantic colour family, a word, and whether it blinks.
/// </summary>
/// <remarks>
///     Four fields rather than four lookups so a caller cannot render a shape from one state and a word from
///     another — the mismatch would be invisible in code review and obvious only to whoever was standing in
///     the room.
/// </remarks>
/// <param name="Icon">The <see cref="Glyph"/> id to draw, or <c>null</c> for the states that get a bare dot.</param>
/// <param name="Family">The <c>state-*</c> class carrying the semantic colour.</param>
/// <param name="Word">What the chip says, in the words the rest of the UI uses.</param>
/// <param name="Blinks">Whether the mark blinks — reserved for the one state that wants attention now.</param>
public sealed record StateMark(string? Icon, string Family, string Word, bool Blinks);

/// <summary>
///     The mapping from a live room state to the shape that stands for it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists at all:</b> the shipped UI told lit-by-the-engine from set-by-hand from switched-off
///         <i>by colour alone</i>. That is nothing to a colourblind reader, nothing in a greyscale screenshot, and
///         nothing to anyone reading the page in bright sunlight on a phone. Every state that means something now
///         has a distinct outline, and the colour agrees with the shape instead of carrying the message alone.
///     </para>
///     <para>
///         <b>Why some states get no shape:</b> the dark-cockpit rule, carried into iconography. A room merely
///         watching is the normal case in fourteen rooms out of seventeen, and fourteen repeated glyphs are
///         texture. Those get a quiet dot, and the eye is left free for the three that are doing something.
///     </para>
///     <para>
///         Pure and total, so the tests are the specification: this repo has no Razor render harness, and a
///         mapping that lives in markup is a mapping nothing can assert about.
///     </para>
/// </remarks>
public static class StateGlyph
{
	/// <summary>
	///     The shape, colour and word for a state.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <see cref="AreaState.PreOff"/> gets its own glyph rather than the auto loop tinted amber. The
	///         approved design allowed either and named the shared shape a compromise; a shared shape means the
	///         two states are one picture in greyscale, which defeats the point of drawing shapes at all. The
	///         amber and the blink still apply — the shape is an addition, not a replacement.
	///     </para>
	///     <para>
	///         Both hand states share the person glyph deliberately: they are one fact — somebody decided —
	///         and the word beside the shape says which. Splitting them would spend a second shape on a
	///         distinction the sentence next to it already makes.
	///     </para>
	/// </remarks>
	/// <param name="state">The area's last published state.</param>
	public static StateMark For(AreaState state) => state switch
	{
		AreaState.AutoActive => new StateMark(Glyph.StateAuto, "state-machine", "lit · auto", false),
		AreaState.PreOff => new StateMark(Glyph.StateDimming, "state-warn", "warning dim", true),
		AreaState.OverriddenOn => new StateMark(Glyph.StateManual, "state-human", "set manually", false),
		AreaState.SuppressedOff => new StateMark(Glyph.StateManual, "state-human", "off manually", false),

		// A scene holding the room is a person's decision too — somebody put the house in a guest mode and
		// chose the look. The engine is standing back for the same reason it stands back after a manual change,
		// so it takes the same shape and colour, and the word says which kind of decision it was.
		AreaState.SceneHold => new StateMark(Glyph.StateManual, "state-human", "held by a scene", false),

		AreaState.Disabled => new StateMark(Glyph.StateOff, "state-idle", "switched off", false),

		// No shape: the engine is watching and commanding nothing, which is what most rooms are doing most
		// of the time. An empty track is information; so is an empty chip.
		AreaState.AutoVacant => new StateMark(null, "state-idle", "watching", false),
		AreaState.Away => new StateMark(null, "state-idle", "house empty", false),
		_ => new StateMark(null, "state-idle", state.ToString(), false)
	};

	/// <summary>
	///     Whether this state's colour should follow the light's actual warmth rather than the family's own.
	/// </summary>
	/// <remarks>
	///     Only a room the engine is actively holding lit has a commanded Kelvin worth reporting, and reporting
	///     it is what makes a 2200 K night dim and a 4500 K midday white visibly different rooms. Every other
	///     state has no colour temperature of its own, and inventing one would be decoration pretending to be
	///     data. A caller with no Kelvin to hand simply passes none and gets the semantic colour.
	/// </remarks>
	/// <param name="state">The area's last published state.</param>
	public static bool TakesKelvinTint(AreaState state) => state is AreaState.AutoActive;
}
