using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void Sleep_NonRespectingArea_FollowsThePlainTable()
	{
		// A sleeping house, but this area does not respect sleep: it follows the one shared table, unclamped.
		var t = Build(tweakGlobal: g => g.HouseMode = SoverMode());
		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70 },
			"an area that does not respect sleep follows the shared evening period, unclamped");
	}

	[TestMethod]
	public void Sleep_RespectingArea_ClampsViaAnExplicitClampPeriod()
	{
		// The Sover option names its own clamp period explicitly, which beats the 'night' fallback.
		var mode = SoverMode();
		mode.OptionFor("Sover")!.ClampPeriodId = "dim";
		var periods = new List<TimePeriodConfig>
		{
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new() { Name = "dim", Start = "22:00", BrightnessPct = 5, ColorTempKelvin = 2000 },
			new() { Name = "night", Start = "23:00", BrightnessPct = 15, ColorTempKelvin = 2200 }
		};
		var t = Build(s => s.RespectSleepMode = true, g => g.HouseMode = mode, periods: periods);
		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 5 },
			"the explicit ClampPeriodId 'dim' (its own 5 %) drives the clamp, not the 'night' fallback");
	}

	[TestMethod]
	public void Sleep_RespectingArea_WithNoResolvableClamp_LeavesTheTargetAlone()
	{
		// Sover has no ClampPeriodId, and there is no 'night' period nor one that SetsModeId Sover, so nothing resolves.
		var periods = new List<TimePeriodConfig>
		{
			new() { Name = "day", Start = "07:00", BrightnessPct = 90, ColorTempKelvin = 4500 },
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 }
		};
		var t = Build(s => s.RespectSleepMode = true, g => g.HouseMode = SoverMode(), periods: periods);
		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70 },
			"with no clamp period resolving, the respecting area is left on the plain evening target");
	}

	/// <summary>Sover clamps to "dim" (5 %) while the "night" fallback any other option lands on says 15 %.</summary>
	// The two chains have to give different answers, or a clamp resolved from the wrong option passes by luck.
	private static (HouseModeConfig Mode, List<TimePeriodConfig> Periods) SleepChainsThatDiffer()
	{
		HouseModeConfig mode = SoverMode();
		mode.OptionFor("Sover")!.ClampPeriodId = "dim";

		return (mode,
		[
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new() { Name = "dim", Start = "22:00", BrightnessPct = 5, ColorTempKelvin = 2000 },
			new() { Name = "night", Start = "23:00", BrightnessPct = 15, ColorTempKelvin = 2200 }
		]);
	}

	private static ForcedMode ForcedSleep() =>
		new(ModeKind.Sleep, "Sover", ModeForceSource.WhileEntityOn, "input_boolean.sover", "on");

	[TestMethod]
	public void Sleep_ForcedByAnEntity_ClampsThroughTheOptionInForce_NotTheSelectsValue()
	{
		(HouseModeConfig mode, List<TimePeriodConfig> periods) = SleepChainsThatDiffer();
		var t = Build(s => s.RespectSleepMode = true, g => g.HouseMode = mode, periods: periods);

		// The overlay entity holds sleep. Nothing wrote the select, so it still reads Normal.
		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Normal", forced: ForcedSleep()));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 5 },
			"Sover is the mode in force, so its own 'dim' ceiling applies — not the 'night' fallback Normal lands on");
	}

	[TestMethod]
	public void Sleep_ForcedWhileTheSelectIsUnreadable_StillClamps()
	{
		(HouseModeConfig mode, List<TimePeriodConfig> periods) = SleepChainsThatDiffer();
		var t = Build(s => s.RespectSleepMode = true, g => g.HouseMode = mode, periods: periods);

		// The select is unavailable, so it names no option at all; the overlay is the whole answer.
		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: null, forced: ForcedSleep()));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 5 },
			"a bedroom must not run at the evening's 70 % at three in the morning because a helper went unavailable");
	}

	[TestMethod]
	public void Sleep_WithNothingForcing_StillClampsThroughTheSelectsOwnValue()
	{
		(HouseModeConfig mode, List<TimePeriodConfig> periods) = SleepChainsThatDiffer();
		var t = Build(s => s.RespectSleepMode = true, g => g.HouseMode = mode, periods: periods);

		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 5 },
			"the control: with no overlay the select's own value is still what resolves the ceiling");
	}

	// The sleep clamp reads this room's night level, not the house's. It is the one place a room's level is a
	// ceiling instead of a target.
	[TestMethod]
	public void Sleep_RespectingArea_ClampsToItsOwnNightLevelRatherThanTheHouses()
	{
		var periods = new List<TimePeriodConfig>
		{
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new() { Name = "night", Start = "23:00", BrightnessPct = 15, ColorTempKelvin = 2200 }
		};
		var t = Build(
			s => s.RespectSleepMode = true,
			g => g.HouseMode = SoverMode(),
			periods: periods,
			levels: [new RoomLevelOverride { PeriodId = "night", BrightnessPct = 4 }]);

		t.House.OnNext(House(kind: ModeKind.Sleep, modeValue: "Sover"));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 4 },
			"the clamp period's level is this room's 4, so 4 is the ceiling — the house's 15 never applies here");
	}
}
