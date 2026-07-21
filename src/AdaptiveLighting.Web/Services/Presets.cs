namespace AdaptiveLighting.Web.Services;

/// <summary>One choice in a preset dropdown: the number the config stores, and the words a person picks it by.</summary>
/// <param name="Value">The stored value.</param>
/// <param name="Label">What the dropdown shows.</param>
public sealed record PresetChoice(double Value, string Label);

/// <summary>
///     The preset lists the period editor offers, shared so every brightness dropdown and every colour dropdown
///     on the page agrees about what the sensible choices are.
/// </summary>
/// <remarks>
///     These are choices, not limits — every dropdown built on them keeps an "enter a value" escape, and a
///     stored value that is not on the list is shown as its own entry rather than snapped to a neighbour.
///     Brightness steps follow perception rather than arithmetic: the difference between 1% and 5% is a
///     different room, the difference between 85% and 90% is nothing, so the list is dense at the dim end and
///     coarse at the top. The colour stops are the familiar lamp temperatures, named the way bulb boxes name
///     them — the same 2200K→4500K family the dashboard's Kelvin palette is built on.
/// </remarks>
public static class Presets
{
	/// <summary>Brightness stops, in percent. Dense where dim, coarse where bright.</summary>
	public static readonly IReadOnlyList<PresetChoice> BrightnessPct =
	[
		new(1, "1 % — the faintest glow"),
		new(2, "2 %"),
		new(5, "5 % — night-light"),
		new(10, "10 %"),
		new(15, "15 % — dim"),
		new(20, "20 %"),
		new(25, "25 %"),
		new(30, "30 % — cosy"),
		new(40, "40 %"),
		new(50, "50 % — half"),
		new(60, "60 %"),
		new(70, "70 % — bright"),
		new(80, "80 %"),
		new(90, "90 %"),
		new(100, "100 % — full")
	];

	/// <summary>Colour-temperature stops, in kelvin, named as bulb boxes name them.</summary>
	public static readonly IReadOnlyList<PresetChoice> ColorTempKelvin =
	[
		new(2200, "2200 K — candlelight"),
		new(2500, "2500 K"),
		new(2700, "2700 K — warm white"),
		new(3000, "3000 K — soft warm"),
		new(3500, "3500 K — neutral"),
		new(4000, "4000 K — cool white"),
		new(4500, "4500 K — daylight"),
		new(5000, "5000 K"),
		new(6500, "6500 K — overcast sky")
	];
}
