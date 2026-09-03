using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The one object <see cref="ModeMonitor"/> writes and every area's <see cref="CircadianCalculator"/> reads.</summary>
[TestClass]
public sealed class MotionPeriodLatchTests
{
	private static readonly DateOnly Today = new(2026, 1, 15);
	private static readonly DateOnly Yesterday = new(2026, 1, 14);

	// 06:45 on Today, a quarter of an hour after the morning's 06:30 start: the shape every arrival test needs.
	private static readonly DateTimeOffset Arrived = new(2026, 1, 15, 6, 45, 0, TimeSpan.Zero);

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

		Assert.IsTrue(latch.TryBegin("morning", Today, Arrived));
		Assert.IsFalse(latch.TryBegin("morning", Today, Arrived), "the day's one start is spent");
		Assert.IsTrue(latch.TryBegin("morning", Today.AddDays(1), Arrived.AddDays(1)), "and comes round again tomorrow");
	}

	[TestMethod]
	public void APeriodTheLatchDoesNotHold_IsNeverHeldBack()
	{
		MotionPeriodLatch latch = new(["morning"]);

		Assert.IsFalse(latch.IsHeldBack("day", Today));
		Assert.IsFalse(latch.IsHeldBack(null, Today));
	}

	/// <summary>The instant is what the blend eases from, so the start that won the day has to carry it.</summary>
	[TestMethod]
	public void TryBegin_RecordsTheInstantMovementArrived()
	{
		MotionPeriodLatch latch = new(["morning"]);

		latch.TryBegin("morning", Today, Arrived);

		Assert.AreEqual(Arrived, latch.BegunAt("morning", Today));
		Assert.IsNull(latch.BegunAt("morning", Yesterday), "yesterday's instance is a different one");
		Assert.IsNull(latch.BegunAt("morning", Today.AddDays(1)), "and so is tomorrow's");
	}

	/// <summary>A restart inherits the period but not the instant, so its blend must not restart at the restart.</summary>
	[TestMethod]
	public void MarkBegun_RecordsNoInstant_SoASeededStartFallsBackToTheBoundary()
	{
		MotionPeriodLatch latch = new(["morning"]);

		latch.MarkBegun("morning", Today);

		Assert.IsTrue(latch.HasBegun("morning", Today), "the period counts as begun for this run");
		Assert.IsNull(latch.BegunAt("morning", Today), "but nothing here says when, and guessing would move the blend");
	}

	/// <summary>One read, so the hold and the arrival cannot come from either side of a concurrent start.</summary>
	[TestMethod]
	public void StateOf_CarriesBothTheHoldAndTheArrival()
	{
		MotionPeriodLatch latch = new(["morning"]);

		Assert.AreEqual(new PeriodHold(true, null), latch.StateOf("morning", Today), "waiting, with nothing to ease from");

		latch.TryBegin("morning", Today, Arrived);

		Assert.AreEqual(new PeriodHold(false, Arrived), latch.StateOf("morning", Today));
		Assert.AreEqual(PeriodHold.OnTheClock, latch.StateOf("day", Today), "a period the latch does not hold");
		Assert.AreEqual(PeriodHold.OnTheClock, latch.StateOf(null, Today));
	}

	/// <summary>A start the day has already spent keeps the instant that claimed it, never the second arrival's.</summary>
	[TestMethod]
	public void ASpentDay_KeepsTheInstantOfTheMovementThatWonIt()
	{
		MotionPeriodLatch latch = new(["morning"]);

		latch.TryBegin("morning", Today, Arrived);
		latch.TryBegin("morning", Today, Arrived.AddMinutes(20));

		Assert.AreEqual(Arrived, latch.BegunAt("morning", Today));
	}
}
