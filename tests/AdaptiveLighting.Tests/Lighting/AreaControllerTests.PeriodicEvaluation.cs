using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	// Dusk moves no state and arms no deadline, so only the tick can notice it.
	[TestMethod]
	public void A_Vacant_Area_Publishes_Once_When_Darkness_Changes_Under_It()
	{
		var t = Build(s => s.Darkness = DarknessSource.Lux);
		t.Ha.SetState(Lux, "5000");

		// One tick to notice it got bright, then quiet again.
		Advance(t, TimeSpan.FromMinutes(1));
		Assert.AreEqual(false, t.Publisher.Snapshots[^1].IsDark);

		var afterBright = t.Publisher.Snapshots.Count;
		Advance(t, TimeSpan.FromMinutes(20));
		Assert.AreEqual(afterBright, t.Publisher.Snapshots.Count, "an area whose world is not moving stays quiet");

		// Dusk: the sensor falls below the threshold with nobody in the room.
		t.Ha.SetState(Lux, "5");
		Advance(t, TimeSpan.FromMinutes(1));

		Assert.AreEqual(afterBright + 1, t.Publisher.Snapshots.Count,
			"dusk in an empty room is news, and it is published exactly once");
		Assert.AreEqual(true, t.Publisher.Snapshots[^1].IsDark);
		Assert.AreEqual(AreaState.AutoVacant, t.Publisher.Snapshots[^1].State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "noticing dusk is not a reason to light an empty room");
	}

	// The identical-consecutive guard has to swallow a republish that resolves to the snapshot already published.
	[TestMethod]
	public void A_Repeated_Identical_Snapshot_Is_Published_Only_Once()
	{
		var t = Build(s => s.OverrideDurationMinutes = 120);
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));   // past the echo window, so the manual touch is read as a human
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);

		t.Publisher.Snapshots.Clear();

		// Motion while overridden moves only the last-motion instant, which is not part of a snapshot's meaning.
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(0, t.Publisher.Snapshots.Count,
			"a republish saying the very same thing is suppressed, so one transition is published exactly once");
	}

	// Record value equality would compare the timestamps too, so every tick would differ and suppress nothing.
	[TestMethod]
	public void A_Quiet_Area_Publishes_Nothing_However_Long_It_Ticks()
	{
		var t = Build();
		var afterStartup = t.Publisher.Snapshots.Count;

		Advance(t, TimeSpan.FromHours(2));

		Assert.AreEqual(afterStartup, t.Publisher.Snapshots.Count,
			"two hours of ticks over an unchanging area is two hours of silence");
	}

	[TestMethod]
	public void A_Tick_Publishes_When_The_House_Mode_Changes_Under_A_Resting_Area()
	{
		var t = Build();
		t.Publisher.Snapshots.Clear();

		// Sleep mode does not transition an AutoVacant area, so only the tick's diff can carry the news.
		t.House.OnNext(House(kind: ModeKind.Sleep));
		Advance(t, TimeSpan.FromMinutes(1));

		Assert.IsTrue(t.Publisher.Snapshots.Any(s => s.Mode == ModeKind.Sleep),
			"the card must not keep saying 'somebody home' at a sleeping house");
	}

	[TestMethod]
	public void A_Disposed_Controller_Goes_Quiet()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");

		t.Area.Dispose();
		t.Actuator.Clear();
		Advance(t, TimeSpan.FromMinutes(30));

		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	/// <summary>A save replaces every controller; the one thrown away must not still aim lights at the table it was built from.</summary>
	[TestMethod]
	public void A_Boundary_Already_In_Flight_Commands_Nothing_Once_The_Controller_Is_Disposed()
	{
		BoundaryCapturingScheduler? captured = null;
		var t = Build(wrapScheduler: inner => captured = new BoundaryCapturingScheduler(inner));

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Actuator.Last is { BrightnessPct: 70 }, "the area has to be lit at the evening levels first");

		t.Area.Dispose();
		t.Actuator.Clear();
		t.Publisher.Snapshots.Clear();

		// Past 22:30, so the night period is what this boundary would retarget the area to.
		Advance(t, TimeSpan.FromHours(3));

		captured!.Boundary!();

		Assert.AreEqual(0, t.Actuator.Applied.Count, "a discarded controller commanded the lights");
		Assert.AreEqual(0, t.Publisher.Snapshots.Count, "a discarded controller reported over its replacement");
	}
}
