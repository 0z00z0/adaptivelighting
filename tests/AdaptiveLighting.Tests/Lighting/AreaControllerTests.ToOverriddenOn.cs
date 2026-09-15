using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void Manual_On_Overrides_And_The_Engine_Backs_Off_Until_Expiry()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();
		Advance(t, TimeSpan.FromSeconds(30));

		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);

		Advance(t, TimeSpan.FromMinutes(30));
		Assert.AreEqual(0, t.Actuator.Applied.Count, "the human's levels are sacred until the override expires");
	}

	[TestMethod]
	public void Override_Expiring_While_Vacant_Turns_The_Area_Off()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Motion, "off");
		Advance(t, TimeSpan.FromSeconds(30));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(121));

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: false });
	}

	[TestMethod]
	public void Override_Expiring_While_Occupied_Resumes_Control_Instead()
	{
		var t = Build(s => s.OverrideDurationMinutes = 5);
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(2));
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromMinutes(4));

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70 });
	}

	[TestMethod]
	public void Motion_Under_A_Fixed_Hold_Extends_Nothing()
	{
		var t = Build(s => s.OverrideDurationMinutes = 5);
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(4));
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "motion must not push the manual levels around");

		Advance(t, TimeSpan.FromMinutes(1));
		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "motion did not extend the override past its five minutes");
	}

	[TestMethod]
	public void Motion_Under_A_Movement_Led_Hold_Restarts_It()
	{
		var t = Build(s =>
		{
			s.OverrideUntilVacant = true;
			s.VacancyTimeoutSeconds = 300;
		});

		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(4));
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Motion, "off");
		Assert.AreEqual(0, t.Actuator.Applied.Count, "motion must not push the manual levels around");

		Advance(t, TimeSpan.FromMinutes(4));
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State,
			"the five minutes restarted at the movement, so the manual level still stands");

		Advance(t, TimeSpan.FromMinutes(1));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State, "the room has now been motion-free for its whole timeout");
		Assert.IsTrue(t.Actuator.Last is { On: false }, "an empty room settles off, the same as any other vacancy");
	}

	// The number is left in the document while the hold follows movement, so a stale one must not be able to end
	// the hold early or hold it open.
	[TestMethod]
	public void A_Movement_Led_Hold_Ignores_The_Fixed_Duration()
	{
		var t = Build(s =>
		{
			s.OverrideUntilVacant = true;
			s.VacancyTimeoutSeconds = 600;
			s.OverrideDurationMinutes = 1;
		});

		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Motion, "off");
		Advance(t, TimeSpan.FromSeconds(30));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());

		Advance(t, TimeSpan.FromMinutes(5));
		Assert.AreEqual(AreaState.OverriddenOn, t.Area.State, "one minute has passed five times over and nothing ended");

		Advance(t, TimeSpan.FromMinutes(5));
		Assert.AreEqual(AreaState.AutoVacant, t.Area.State, "the ten-minute vacancy timeout is what ended it");
	}

	[TestMethod]
	public void A_Movement_Led_Hold_Publishes_The_Vacancy_Timeout_As_Its_Expiry()
	{
		var start = new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);

		var t = Build(s =>
		{
			s.OverrideUntilVacant = true;
			s.VacancyTimeoutSeconds = 300;
		});

		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());

		Assert.AreEqual(
			start + TimeSpan.FromSeconds(30) + TimeSpan.FromMinutes(5),
			t.Publisher.Snapshots[^1].NextChangeAt,
			"the snapshot names the moment the quiet would run out, not a fixed duration");

		Advance(t, TimeSpan.FromMinutes(2));
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(
			start + TimeSpan.FromSeconds(30) + TimeSpan.FromMinutes(2) + TimeSpan.FromMinutes(5),
			t.Publisher.Snapshots[^1].NextChangeAt,
			"a deadline that moved has to be republished, or the page counts down to a moment nothing happens at");
	}
}
