using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>What a room's lamps are doing right now: the facts a design paints, never a colour itself.</summary>
/// <param name="IsLit">Whether the room counts as lit.</param>
/// <param name="BrightnessPct">The lamps' brightness, rounded for display, or <c>null</c> off or unread.</param>
/// <param name="Kelvin">The lamps' colour temperature, or <c>null</c> off, unread, or set by a scene.</param>
/// <param name="IsHandHeld">Whether a hand is holding the room on rather than the engine.</param>
public readonly record struct RoomLamp(bool IsLit, int? BrightnessPct, int? Kelvin, bool IsHandHeld)
{
	/// <summary>A room with nothing lit, which is also what no snapshot reads as.</summary>
	public static readonly RoomLamp Off = new(false, null, null, false);

	/// <summary>Reads a room's lamp state off its snapshot. No snapshot means off, never unpainted.</summary>
	public static RoomLamp Of(AreaSnapshot? snapshot)
	{
		if (snapshot is not { IsLit: true } lit)
			return Off;

		return new RoomLamp(
			true,
			lit.BrightnessPct is { } pct ? (int)DisplayRounding.Whole(pct) : null,
			lit.ColorTempKelvin,
			lit.State is AreaState.OverriddenOn);
	}
}

/// <summary>One room as the dashboard's grid draws it: its name, its route, and what its lamps are doing.</summary>
/// <param name="AreaId">The room's Home Assistant area, or <c>null</c> for a room named by entities alone —
/// <see cref="HouseView.RoomHref"/> already turns that into no link.</param>
public sealed record RoomTile(string Name, string? AreaId, RoomLamp Lamp);
