using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void A_Snapshot_Carries_The_Deadline_Its_State_Is_Waiting_On()
	{
		var start = new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);
		var t = Build();

		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(start + TimeSpan.FromSeconds(600), t.Publisher.Snapshots[^1].NextChangeAt,
			"an active area knows when it will start dimming");

		t.Ha.Trigger(Motion, "off");
		Advance(t, TimeSpan.FromMinutes(10));
		var preOff = t.Publisher.Snapshots[^1];
		Assert.AreEqual(AreaState.PreOff, preOff.State);
		Assert.AreEqual(start + TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(30), preOff.NextChangeAt,
			"the dim warning names the moment the lights go out");

		Advance(t, TimeSpan.FromSeconds(30));
		var vacant = t.Publisher.Snapshots[^1];
		Assert.AreEqual(AreaState.AutoVacant, vacant.State);
		Assert.IsNull(vacant.NextChangeAt, "a resting area is waiting on motion, not on a clock");
		Assert.IsNull(vacant.BrightnessPct, "the standing command is now 'off'");
		Assert.IsNotNull(vacant.LastCommandAt, "…but it is a dated command, not an absence of one");
	}

	// Motion in an active area moves the vacancy deadline without a state change.
	[TestMethod]
	public void Motion_While_Active_Republishes_With_The_Deadline_Moved()
	{
		var start = new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);
		var t = Build();
		t.Ha.Trigger(Motion, "on");

		Advance(t, TimeSpan.FromMinutes(5));
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		var republished = t.Publisher.Snapshots[^1];
		Assert.AreEqual(AreaState.AutoActive, republished.State);
		Assert.AreEqual(TransitionReason.Motion, republished.Reason);
		Assert.AreEqual(start + TimeSpan.FromMinutes(5), republished.LastMotionAt);
		Assert.AreEqual(start + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(600), republished.NextChangeAt);
		Assert.AreEqual(70, republished.BrightnessPct,
			"a republish keeps the standing command's levels — the lights did not change, only the clock did");
	}

	[TestMethod]
	public void An_Override_Publishes_Its_Expiry_And_A_Suppression_Its_Reset()
	{
		var start = new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);

		var overridden = Build();
		overridden.Ha.Trigger(Motion, "on");
		Advance(overridden, TimeSpan.FromSeconds(30));
		overridden.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());

		Assert.AreEqual(
			start + TimeSpan.FromSeconds(30) + TimeSpan.FromMinutes(120),
			overridden.Publisher.Snapshots[^1].NextChangeAt,
			"the override snapshot names the moment automatic control returns");

		var suppressed = Build();
		suppressed.Ha.Trigger(Motion, "on");
		suppressed.Ha.Trigger(Light, "off", null, PhysicalDevice());

		Assert.AreEqual(
			start + TimeSpan.FromMinutes(10),
			suppressed.Publisher.Snapshots[^1].NextChangeAt,
			"the suppression snapshot names the moment motion starts counting again");

		// Motion during the suppression restarts the reset clock, and the restarted clock is republished.
		Advance(suppressed, TimeSpan.FromMinutes(9));
		suppressed.Ha.Trigger(Motion, "off");
		suppressed.Ha.Trigger(Motion, "on");

		var moved = suppressed.Publisher.Snapshots[^1];
		Assert.AreEqual(AreaState.SuppressedOff, moved.State);
		Assert.AreEqual(start + TimeSpan.FromMinutes(9) + TimeSpan.FromMinutes(10), moved.NextChangeAt);
	}

	[TestMethod]
	public void Disabling_The_Area_Clears_The_Published_Deadline()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");

		t.House.OnNext(House(killed: true));

		var disabled = t.Publisher.Snapshots[^1];
		Assert.AreEqual(AreaState.Disabled, disabled.State);
		Assert.IsNull(disabled.NextChangeAt, "a muzzled engine has no scheduled next move to promise");
		Assert.IsNull(disabled.NextChangeFrom, "…and no countdown span either — the pair lives and dies together");
	}

	// NextChangeFrom cannot be derived client-side: Timestamp moves on republishes that re-arm nothing.
	[TestMethod]
	public void A_Snapshot_Carries_Both_Ends_Of_Its_Countdown()
	{
		var start = new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);
		var t = Build();

		t.Ha.Trigger(Motion, "on");
		var active = t.Publisher.Snapshots[^1];
		Assert.AreEqual(start, active.NextChangeFrom, "the vacancy countdown began the moment it was armed");
		Assert.AreEqual(start + TimeSpan.FromSeconds(600), active.NextChangeAt);

		// Motion re-arms the vacancy timer: both ends move together, and the re-arm republishes.
		Advance(t, TimeSpan.FromMinutes(5));
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Motion, "off");
		var rearmed = t.Publisher.Snapshots[^1];
		Assert.AreEqual(start + TimeSpan.FromMinutes(5), rearmed.NextChangeFrom);
		Assert.AreEqual(start + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(600), rearmed.NextChangeAt);

		// The pre-off warning is a new, shorter countdown, not the tail of the old one.
		Advance(t, TimeSpan.FromMinutes(10));
		var preOff = t.Publisher.Snapshots[^1];
		Assert.AreEqual(AreaState.PreOff, preOff.State);
		Assert.AreEqual(start + TimeSpan.FromMinutes(15), preOff.NextChangeFrom);
		Assert.AreEqual(start + TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(30), preOff.NextChangeAt);
	}

	[TestMethod]
	public void The_Countdown_Span_Is_Cleared_When_Nothing_Is_Scheduled()
	{
		var t = Build();

		Assert.IsNull(t.Publisher.Snapshots.Single().NextChangeFrom,
			"a dark, unlit area starts with nothing armed, so there is no span to claim");

		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Motion, "off");
		Advance(t, TimeSpan.FromSeconds(600 + 30));

		var vacant = t.Publisher.Snapshots[^1];
		Assert.AreEqual(AreaState.AutoVacant, vacant.State);
		Assert.IsNull(vacant.NextChangeAt);
		Assert.IsNull(vacant.NextChangeFrom, "an area waiting on motion has no countdown to draw");
	}

	/// <summary>The area id is what joins live state to the document; the display name is editable.</summary>
	[TestMethod]
	public void Every_Snapshot_Names_The_Registry_Area_It_Came_From()
	{
		Fixture t = Build();

		t.Ha.Trigger(Motion, "on");

		Assert.IsTrue(t.Publisher.Snapshots.All(snapshot => snapshot.AreaId == "test_area"));
	}

	// NextChangeFrom counts in HasSameMeaningAs; the as-of fields, Timestamp among them, do not.
	[TestMethod]
	public void A_Moved_Countdown_Start_Is_News_And_A_Moved_Timestamp_Is_Not()
	{
		var when = new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);
		var snapshot = new AreaSnapshot(
			"Stue", AreaState.AutoActive, TransitionReason.Motion, HouseMode.Home,
			false, true, "evening", 70, 2700, when,
			when, when, when + TimeSpan.FromMinutes(10), when);

		Assert.IsTrue(snapshot.HasSameMeaningAs(snapshot with { Timestamp = when + TimeSpan.FromMinutes(1) }));
		Assert.IsFalse(snapshot.HasSameMeaningAs(snapshot with { NextChangeFrom = when + TimeSpan.FromMinutes(1) }));
	}
}
