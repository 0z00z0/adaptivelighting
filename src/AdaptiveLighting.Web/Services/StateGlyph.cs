using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>How one room state is drawn: a shape, a colour family, a word, and whether it blinks.</summary>
/// <remarks>
///     One record, not four lookups, so a caller cannot draw the shape of one state beside the word of
///     another.
/// </remarks>
/// <param name="Icon">The <see cref="Glyph"/> id to draw, or <c>null</c> for the states that get a bare dot.</param>
/// <param name="Family">The <c>state-*</c> class carrying the semantic colour.</param>
/// <param name="Word">What the chip says, in the words the rest of the UI uses.</param>
/// <param name="Blinks">Whether the mark blinks.</param>
public sealed record StateMark(string? Icon, string Family, string Word, bool Blinks);

/// <summary>
///     The mapping from a live room state to the shape that stands for it.
/// </summary>
/// <remarks>
///     Every state that means something carries a distinct outline, so the page still reads in greyscale and to a
///     colourblind reader. The states that mean "nothing is happening" carry no shape at all.
/// </remarks>
public static class StateGlyph
{
	/// <summary>The shape, colour and word for a state.</summary>
	public static StateMark For(AreaState state) => state switch
	{
		AreaState.AutoActive => new StateMark(Glyph.StateAuto, "state-machine", "lit · auto", false),
		AreaState.PreOff => new StateMark(Glyph.StateDimming, "state-warn", "warning dim", true),

		// The three human states share one shape: the fact is that somebody decided, and the word says which.
		AreaState.OverriddenOn => new StateMark(Glyph.StateManual, "state-human", "set manually", false),
		AreaState.SuppressedOff => new StateMark(Glyph.StateManual, "state-human", "off manually", false),
		AreaState.SceneHold => new StateMark(Glyph.StateManual, "state-human", "held by a scene", false),

		AreaState.Disabled => new StateMark(Glyph.StateOff, "state-idle", "switched off", false),

		// No shape: the engine is watching and commanding nothing, which is most rooms most of the time.
		AreaState.AutoVacant => new StateMark(null, "state-idle", "watching", false),
		AreaState.Away => new StateMark(null, "state-idle", "house empty", false),
		_ => new StateMark(null, "state-idle", state.ToString(), false)
	};

	/// <summary>Whether this state's colour follows the light's actual warmth instead of the family's own.</summary>
	/// <remarks>Only a room the engine is holding lit has a commanded Kelvin; any other state would be inventing one.</remarks>
	public static bool TakesKelvinTint(AreaState state) => state is AreaState.AutoActive;
}
