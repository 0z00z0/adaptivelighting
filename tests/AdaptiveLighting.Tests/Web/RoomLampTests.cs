using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Presentation;

namespace AdaptiveLighting.Tests.Web;

/// <summary>A room's lamp reading, off the snapshot the engine last published — what a tile or a header paints
/// itself from.</summary>
[TestClass]
public sealed class RoomLampTests
{
	[TestMethod]
	public void LitValuesCarryThroughAndNoSnapshotReadsAsOff()
	{
		AreaSnapshot lit = new(
			"Stue", AreaState.OverriddenOn, TransitionReason.ManualOn, ModeKind.Normal,
			KillSwitchActive: false, IsDark: true, PeriodName: "Kveld", BrightnessPct: 62.5, ColorTempKelvin: 2700,
			Timestamp: DateTimeOffset.UnixEpoch, LastCommandAt: null, LastMotionAt: null, NextChangeAt: null,
			NextChangeFrom: null);

		RoomLamp glow = RoomLamp.Of(lit);

		Assert.IsTrue(glow.IsLit, "a brightness above zero in a lit state has to read as lit");
		Assert.AreEqual(63, glow.BrightnessPct, "the brightness carries through, rounded the same way every other readout rounds it");
		Assert.AreEqual(2700, glow.Kelvin, "the colour temperature carries through");
		Assert.IsTrue(glow.IsHandHeld, "a hand set this room, which a tile has to paint differently from the engine's own colour");

		RoomLamp off = RoomLamp.Of(null);

		Assert.IsFalse(off.IsLit, "no snapshot has to read as off, never as an unpainted tile");
		Assert.IsNull(off.BrightnessPct, "an off room carries no brightness");
		Assert.IsNull(off.Kelvin, "an off room carries no colour temperature");
		Assert.IsFalse(off.IsHandHeld, "an off room is held by nobody");
	}
}
