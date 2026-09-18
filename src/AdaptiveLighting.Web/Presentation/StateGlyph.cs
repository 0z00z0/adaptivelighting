using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>How one room state is drawn: a shape, a colour family, a word, and whether it blinks.</summary>
/// <remarks>One record, so a caller cannot draw the shape of one state beside the word of another.</remarks>
public sealed record StateMark(string? Icon, string Family, string Word, bool Blinks);

/// <summary>The mapping from a live room state to the shape that stands for it.</summary>
/// <remarks>
///     Every state that means something carries a distinct outline, so the page still reads in greyscale and to a
///     colourblind reader. The states that mean "nothing is happening" carry no shape at all.
/// </remarks>
public static class StateGlyph
{
	/// <summary>The mark for a live state. <paramref name="isLeadIn"/> only changes <see cref="AreaState.PreOff"/>'s
	/// word: a lead-in sensor lighting a dark room ahead of anyone coming in reads differently from the ordinary
	/// dim light before switching off, though both share the shape.</summary>
	public static StateMark For(AreaState state, bool isLeadIn = false, bool isOffByLevel = false) => state switch
	{
		AreaState.AutoActive when isOffByLevel => new StateMark(null, "state-idle", "off · auto", false),
		AreaState.AutoActive =>new StateMark(Glyph.StateAuto, "state-machine", "lit · auto", false),
		AreaState.PreOff => new StateMark(Glyph.StateDimming, "state-warn", isLeadIn ? "lead-in · auto" : "warning dim", true),

		// The three human states share one shape: a person decided, and the word says which.
		AreaState.OverriddenOn => new StateMark(Glyph.StateManual, "state-human", "set manually", false),
		AreaState.SuppressedOff => new StateMark(Glyph.StateManual, "state-human", "off manually", false),
		AreaState.SceneHold => new StateMark(Glyph.StateManual, "state-human", "held by a scene", false),

		AreaState.Disabled => new StateMark(Glyph.StateOff, "state-idle", "switched off", false),

		// No shape: the engine is watching and commanding nothing.
		AreaState.AutoVacant => new StateMark(null, "state-idle", "watching", false),
		AreaState.Away => new StateMark(null, "state-idle", "house away", false),
		_ => new StateMark(null, "state-idle", "unknown", false)
	};

	/// <summary>The mark for a snapshot, which alone can tell a room held off by a 0 % level from one lit.</summary>
	public static StateMark For(AreaSnapshot snapshot) =>
		For(snapshot.State, snapshot.IsLeadIn ?? false, IsOffByLevel(snapshot));

	/// <summary>Whether the engine holds this room active with its lights off, because its level came to 0 %.</summary>
	// A scene nulls the levels too, so a scened room is not off.
	public static bool IsOffByLevel(AreaSnapshot snapshot) =>
		snapshot is { State: AreaState.AutoActive, BrightnessPct: null, LastCommandAt: not null }
		&& snapshot.SceneApplied is not { Length: > 0 };

	/// <summary>Whether this state's colour follows the light's actual warmth; only a room the engine holds lit has a commanded Kelvin.</summary>
	public static bool TakesKelvinTint(AreaState state) => state is AreaState.AutoActive;
}
