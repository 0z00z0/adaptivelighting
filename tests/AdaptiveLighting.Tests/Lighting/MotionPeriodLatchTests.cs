using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The one object <see cref="ModeMonitor"/> writes and every area's <see cref="CircadianCalculator"/> reads.</summary>
[TestClass]
public sealed class MotionPeriodLatchTests
{
	private static readonly DateOnly Today = new(2026, 1, 15);
	private static readonly DateOnly Yesterday = new(2026, 1, 14);

	private static List<TimePeriodConfig> Periods() =>
	[
		new() { Name = "morning", Start = "06:30", StartsOnMotion = true },
		new() { Name = "day", Start = "09:00" }
	];

	[TestMethod]
	public void For_HoldsEveryPeriodThatAsksToStartOnMotion()
	{
		MotionPeriodLatch latch = MotionPeriodLatch.For(Periods(), new GlobalConfig());

		Assert.IsTrue(latch.Holds("morning"));
		Assert.IsTrue(latch.Holds("MORNING"), "period names are matched case-insensitively everywhere else too");
		Assert.IsFalse(latch.Holds("day"));
	}

	// The single branch on period authority. Nothing is held, so nothing waits and nothing can be started.
	[TestMethod]
	public void For_UnderHomeAssistantPeriodAuthority_HoldsNothing()
	{
		GlobalConfig global = new()
		{
			PeriodSelect = new PeriodSelectConfig
			{
				Entity = "input_select.tid_pa_dagen",
				Authority = PeriodAuthority.HomeAssistant
			}
		};

		MotionPeriodLatch latch = MotionPeriodLatch.For(Periods(), global);

		Assert.AreEqual(0, latch.HeldPeriods.Count);
		Assert.IsFalse(latch.IsHeldBack("morning", Today));
	}

	// A select this application owns is a mirror of its own schedule, so the rule stands.
	[TestMethod]
	public void For_UnderThisApplicationsPeriodAuthority_StillHolds()
	{
		GlobalConfig global = new()
		{
			PeriodSelect = new PeriodSelectConfig
			{
				Entity = "input_select.tid_pa_dagen",
				Authority = PeriodAuthority.AdaptiveLighting
			}
		};

		Assert.IsTrue(MotionPeriodLatch.For(Periods(), global).Holds("morning"));
	}

	[TestMethod]
	public void AHeldPeriodIsHeldBackUntilTheDayItBegan()
	{
		MotionPeriodLatch latch = new(["morning"]);

		Assert.IsTrue(latch.IsHeldBack("morning", Today));

		latch.MarkBegun("morning", Today);

		Assert.IsFalse(latch.IsHeldBack("morning", Today));
		Assert.IsTrue(latch.IsHeldBack("morning", Yesterday), "yesterday's instance is a different one");
	}

	[TestMethod]
	public void TryBegin_ClaimsTheDayOnce()
	{
		MotionPeriodLatch latch = new(["morning"]);

		Assert.IsTrue(latch.TryBegin("morning", Today));
		Assert.IsFalse(latch.TryBegin("morning", Today), "the day's one start is spent");
		Assert.IsTrue(latch.TryBegin("morning", Today.AddDays(1)), "and comes round again tomorrow");
	}

	[TestMethod]
	public void APeriodTheLatchDoesNotHold_IsNeverHeldBack()
	{
		MotionPeriodLatch latch = new(["morning"]);

		Assert.IsFalse(latch.IsHeldBack("day", Today));
		Assert.IsFalse(latch.IsHeldBack(null, Today));
	}
}
