using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Fixture = AdaptiveLighting.Tests.Common.AreaFixture;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void AwayKind_SweepsImmediately_UnlessTheAreaOptsOut()
	{
		Fixture swept = Build(tweakGlobal: g => g.HouseMode = SoverMode());
		swept.Ha.Trigger(Motion, "on");
		swept.Actuator.Clear();

		swept.House.OnNext(House(kind: ModeKind.Away, modeValue: "Borte"));

		Assert.AreEqual(AreaState.Away, swept.Area.State);
		Assert.IsTrue(swept.Actuator.Last is { On: false }, "an away-kind Borte sweeps a full house at once");

		Fixture optedOut = Build(s => s.SkipAwaySweep = true, g => g.HouseMode = SoverMode());
		optedOut.Ha.Trigger(Motion, "on");
		optedOut.Actuator.Clear();

		optedOut.House.OnNext(House(kind: ModeKind.Away, modeValue: "Borte"));

		Assert.AreEqual(AreaState.Away, optedOut.Area.State);
		Assert.AreEqual(0, optedOut.Actuator.Applied.Count, "a SkipAwaySweep area is left alone");
	}

	[TestMethod]
	public void AwayKind_MotionIgnored()
	{
		Fixture t = Build(tweakGlobal: g => g.HouseMode = SoverMode());
		t.House.OnNext(House(kind: ModeKind.Away, modeValue: "Borte"));
		t.Actuator.Clear();

		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.Away, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "motion is ignored while the house is away");
	}

	[TestMethod]
	public void Away_WithAScene_SkipsTheSweep()
	{
		HouseModeConfig mode = SoverMode();
		mode.OptionFor("Borte")!.Scene = "scene.borte";
		Fixture t = Build(tweakGlobal: g => g.HouseMode = mode);
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		t.House.OnNext(House(kind: ModeKind.Away, modeValue: "Borte", scene: "scene.borte"));

		Assert.AreEqual(AreaState.Away, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "an away scene is the look; the area publishes but sweeps nothing");
	}

	// ---- a forced mode is never a presence departure -------------------------------------------

	private static AreaSnapshot LastReport(Fixture fixture) => fixture.Publisher.Snapshots[^1];

	[TestMethod]
	public void ForcedAwayMode_IsReportedAsAHouseModeChange()
	{
		Fixture t = Build(tweakGlobal: g => g.HouseMode = SoverMode());
		t.Ha.Trigger(Motion, "on");

		t.House.OnNext(House(kind: ModeKind.Away, modeValue: "Borte", forced: ForcedAway()));

		AreaSnapshot report = LastReport(t);

		Assert.AreEqual(AreaState.Away, report.State, "the mode still sweeps the room — that part was never wrong");
		Assert.AreEqual(TransitionReason.HouseModeChanged, report.Reason);
	}

	[TestMethod]
	public void ForcedAwayMode_ReportsWhoIsHomeAndWhatIsForcingIt()
	{
		Fixture t = Build(tweakGlobal: g => g.HouseMode = SoverMode());
		t.House.OnNext(House(kind: ModeKind.Away, modeValue: "Borte", forced: ForcedAway()));

		AreaSnapshot report = LastReport(t);

		Assert.AreEqual(true, report.IsAnyoneHome,
			"presence said the house was full throughout — the room must not be able to claim otherwise");
		Assert.AreEqual(ModeForceSource.WhileEntityOn, report.Forced!.Source);
		Assert.AreEqual("input_boolean.occupancy", report.Forced.EntityId);
		Assert.AreEqual("Away mode is forced while input_boolean.occupancy is on.", report.Forced.Describe());
	}

	[TestMethod]
	public void AwayModeReleasing_IsAModeChange()
	{
		Fixture t = Build(tweakGlobal: g => g.HouseMode = SoverMode());
		t.House.OnNext(House(kind: ModeKind.Away, modeValue: "Borte", forced: ForcedAway()));
		Assert.AreEqual(AreaState.Away, t.Area.State);

		// The boolean goes off. Nobody arrived; the mode let go.
		t.House.OnNext(House(modeValue: "Normal"));

		AreaSnapshot report = LastReport(t);

		Assert.AreEqual(TransitionReason.HouseModeChanged, report.Reason);
	}

	// ---- the mode an area found when it started is not a mode change ---------------------------

	/// <summary>A lit room that comes up with <paramref name="opening"/> as the house it finds.</summary>
	private static Fixture BuildLitInto(HouseState opening) =>
		Build(
			tweakGlobal: g => g.HouseMode = SoverMode(),
			seed: ha => ha.SetState(Light, "on", new() { ["brightness"] = 178.5 }),
			openingHouse: opening);

	[TestMethod]
	public void TheModeFoundAtStartUp_IsNotReportedAsAModeChange()
	{
		Fixture t = BuildLitInto(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		Assert.AreEqual(TransitionReason.AdoptedAtStartup, LastReport(t).Reason,
			"the select never moved; the engine started and read it");
	}

	[TestMethod]
	public void AModeChangeAfterStartUp_IsStillAModeChange()
	{
		Fixture t = BuildLitInto(House(modeValue: "Normal"));

		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		Assert.AreEqual(TransitionReason.HouseModeChanged, LastReport(t).Reason);
	}

	[TestMethod]
	public void AnAwayModeFoundAtStartUp_StillSweepsTheRoom_AndStillNamesWhatIsForcingIt()
	{
		Fixture t = Build(
			tweakGlobal: g => g.HouseMode = SoverMode(),
			openingHouse: House(kind: ModeKind.Away, modeValue: "Borte", forced: ForcedAway()));

		AreaSnapshot report = LastReport(t);

		Assert.AreEqual(AreaState.Away, report.State, "the sweep was never the part that was wrong");
		Assert.AreEqual(TransitionReason.Startup, report.Reason);
		Assert.AreEqual("input_boolean.occupancy", report.Forced?.EntityId,
			"a mode forced at start-up must still be able to say what is holding it");
	}

	// Asserted on the tick, not on the house change: an area already Away short-circuits out of OnHouseChanged
	// without publishing. The correction only lands because IsAnyoneHome counts in HasSameMeaningAs.
	[TestMethod]
	public void ComingHomeToAForcedAwayMode_IsCorrectedOnTheNextTick()
	{
		Fixture t = Build(tweakGlobal: g => g.HouseMode = SoverMode());
		t.House.OnNext(House(home: false, kind: ModeKind.Away, modeValue: "Borte", forced: ForcedAway()));

		Assert.AreEqual(false, LastReport(t).IsAnyoneHome);

		// The boolean is still on, so the mode does not move, but the house fills up again.
		t.House.OnNext(House(home: true, kind: ModeKind.Away, modeValue: "Borte", forced: ForcedAway()));
		Advance(t, TimeSpan.FromSeconds(60));

		Assert.AreEqual(true, LastReport(t).IsAnyoneHome,
			"the report that said the house was empty while somebody stood in it is the one that had to move");
	}

	[TestMethod]
	public void Migration_LiveCabin_NoHouseMode_UsesBaseline()
	{
		// No HouseMode, nobody asleep, no mode selected: the baseline evening period drives.
		Fixture t = Build(s => s.RespectSleepMode = true);
		t.House.OnNext(House(modeValue: null));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70 },
			"with no mode selected and nobody asleep, the baseline evening period drives, exactly as today");
	}
}
