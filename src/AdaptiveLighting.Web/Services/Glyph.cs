namespace AdaptiveLighting.Web.Services;

/// <summary>
///     The id of every glyph <c>IconSprite</c> defines, named once so nothing has to spell one out.
/// </summary>
/// <remarks>
///     A mistyped <c>&lt;use href="#i-atuo"&gt;</c> renders nothing at all: no exception, no console message,
///     just a blank where an icon should be. Constants turn that into a compile error.
/// </remarks>
public static class Glyph
{
	/// <summary>The product mark: a point of light answering motion waves. The top bar's brand.</summary>
	public const string App = "i-app";

	/// <summary>A floor plan. Rooms as places.</summary>
	public const string Areas = "i-areas";

	/// <summary>The circadian curve, the schedule card's own chart in miniature.</summary>
	public const string Schedule = "i-schedule";

	/// <summary>A rotary selector. House modes: one at a time.</summary>
	public const string Modes = "i-modes";

	public const string House = "i-house";

	/// <summary>A house with a resident inside it. The People card.</summary>
	public const string Residents = "i-residents";

	/// <summary>Three tracks cut by a now-line. The board.</summary>
	public const string Lanes = "i-lanes";

	/// <summary>A loop around the light point: the engine acting by itself.</summary>
	public const string StateAuto = "i-auto";

	/// <summary>A person over the light: somebody set this by hand.</summary>
	public const string StateManual = "i-manual";

	public const string StateOff = "i-off";

	/// <summary>A light with a quarter of its time left: the warning dim, counting down to dark.</summary>
	public const string StateDimming = "i-dimming";
}
