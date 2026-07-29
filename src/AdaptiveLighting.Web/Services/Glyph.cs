namespace AdaptiveLighting.Web.Services;

/// <summary>
///     The id of every glyph <c>IconSprite</c> defines, named once so nothing has to spell one out.
/// </summary>
/// <remarks>
///     <para>
///         A mistyped <c>&lt;use href="#i-atuo"&gt;</c> renders nothing at all — no exception, no console
///         message, just a blank where an icon should be, which is exactly the sort of defect that survives
///         a screenshot review. Constants turn that into a compile error.
///     </para>
///     <para>
///         The set is deliberately short. <c>ui-design-c.md</c> §6.2 refused more placements than it accepted:
///         no glyphs on log rows, quick actions, sentence tokens, person chips or the first-run table, because
///         twelve repeated marks are texture rather than information. Adding a constant here is a design
///         decision, not a plumbing one — see <c>docs/design/visual-foundation.md</c> for how to add one.
///     </para>
/// </remarks>
public static class Glyph
{
	/// <summary>The product mark: a point of light answering motion waves. The top bar's brand.</summary>
	public const string App = "i-app";

	/// <summary>A floor plan. Rooms as places, for anything addressing rooms collectively.</summary>
	public const string Areas = "i-areas";

	/// <summary>The circadian curve. The schedule card's own chart, in miniature.</summary>
	public const string Schedule = "i-schedule";

	/// <summary>A rotary selector. House modes: one at a time, chosen deliberately.</summary>
	public const string Modes = "i-modes";

	/// <summary>A house with the master switch inside it. The installation as one switchable thing.</summary>
	public const string House = "i-house";

	/// <summary>A house with a resident inside it. Who lives here — the People card.</summary>
	public const string Residents = "i-residents";

	/// <summary>Three tracks cut by a now-line. The board, and the only honest nav mark for a timeline home.</summary>
	public const string Lanes = "i-lanes";

	/// <summary>A loop around the light point: the engine acting by itself.</summary>
	public const string StateAuto = "i-auto";

	/// <summary>A person over the light: somebody set this by hand.</summary>
	public const string StateManual = "i-manual";

	/// <summary>A struck circle: disengaged, switched off, not participating.</summary>
	public const string StateOff = "i-off";

	/// <summary>A light with a quarter of its time left: the warning dim, counting down to dark.</summary>
	public const string StateDimming = "i-dimming";
}
