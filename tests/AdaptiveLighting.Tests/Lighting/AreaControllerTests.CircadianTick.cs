using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void The_Tick_Retargets_An_Active_Area_When_The_Period_Changes()
	{
		// A vacancy timeout long enough that the area is still AutoActive when the night boundary passes.
		var t = Build(s => s.VacancyTimeoutSeconds = 60 * 60 * 5);
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(151));   // 20:00 -> 22:31, into night

		Assert.IsTrue(t.Actuator.Applied.Any(a => a.Command is { On: true, BrightnessPct: 15 }));
		Assert.IsTrue(t.Actuator.Applied.Count < 5, "a retarget is one command, not one per tick");
	}

	/// <summary>The boundary is its own wake-up, so the tick does not decide how late the levels change.</summary>
	/// <remarks>Measured at a 300 s tick, boundaries landed up to four minutes late, so this asserts on the seconds either side of 20:03.</remarks>
	[TestMethod]
	public void The_Period_Arrives_At_The_Boundary_Not_At_The_Next_Tick()
	{
		var periods = new List<TimePeriodConfig>
		{
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new() { Name = "night", Start = "20:03", BrightnessPct = 15, ColorTempKelvin = 2200 }
		};
		var t = Build(s => s.VacancyTimeoutSeconds = 60 * 60 * 5, g => g.CircadianTickSeconds = 300, periods: periods);
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromSeconds(170));   // 20:00 -> 20:02:50
		Assert.AreEqual(0, t.Actuator.Applied.Count, "night@20:03 has not come round yet");

		Advance(t, TimeSpan.FromSeconds(15));    // -> 20:03:05
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 15 },
			"the boundary itself, two minutes before the 300 s tick would have reached it");
	}

	/// <summary>A table with one fixed boundary and one anchored to sunset, lit and quiet at 20:00.</summary>
	private static Fixture SunAnchored(MovableSun sun, bool watchSun = true)
	{
		var periods = new List<TimePeriodConfig>
		{
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new() { Name = "night", Start = "sunset", BrightnessPct = 15, ColorTempKelvin = 2200 }
		};
		var t = Build(
			s => s.VacancyTimeoutSeconds = 60 * 60 * 5,
			g => g.CircadianTickSeconds = 300,
			periods: periods,
			sun: sun,
			watchSun: watchSun);

		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();
		return t;
	}

	[TestMethod]
	public void A_Sun_Time_That_Moves_Rearms_The_Boundary_Without_Waiting_For_A_Tick()
	{
		var sun = new MovableSun();
		sun.SetQuietly(sunrise: new TimeOnly(8, 0), sunset: new TimeOnly(23, 0));
		var t = SunAnchored(sun);

		sun.MoveTo(sunrise: new TimeOnly(8, 0), sunset: new TimeOnly(20, 2));

		Advance(t, TimeSpan.FromSeconds(125));   // 20:00 -> 20:02:05
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 15 },
			"night now begins at 20:02, three minutes before the 300 s tick would have looked");
	}

	/// <summary>A sun time can move backwards as easily as forwards, and a boundary it has just taken past still counts.</summary>
	/// <remarks>The next boundary is only ever the first start ahead of now, so one that moved into the past would be armed straight over.</remarks>
	[TestMethod]
	public void A_Sun_Time_That_Moves_Behind_Us_Is_Acted_On_At_Once()
	{
		var sun = new MovableSun();
		sun.SetQuietly(sunrise: new TimeOnly(8, 0), sunset: new TimeOnly(22, 0));
		var t = SunAnchored(sun);

		sun.MoveTo(sunrise: new TimeOnly(8, 0), sunset: new TimeOnly(19, 30));

		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 15 },
			"night began half an hour ago, so the room is already owed its levels");
	}

	/// <summary>Without the announcement the tick is what re-arms, which is the behaviour of a house with no sun.</summary>
	[TestMethod]
	public void An_Unwatched_Sun_Waits_For_The_Tick()
	{
		var sun = new MovableSun();
		sun.SetQuietly(sunrise: new TimeOnly(8, 0), sunset: new TimeOnly(23, 0));
		var t = SunAnchored(sun, watchSun: false);

		sun.MoveTo(sunrise: new TimeOnly(8, 0), sunset: new TimeOnly(20, 2));

		Advance(t, TimeSpan.FromSeconds(125));   // 20:00 -> 20:02:05
		Assert.AreEqual(0, t.Actuator.Applied.Count, "the boundary armed at 23:00 has not moved");

		Advance(t, TimeSpan.FromSeconds(180));   // -> 20:05:05, the first tick
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 15 }, "the tick is the safety net and still catches it");
	}

	/// <summary>A sun that stops resolving takes its own boundaries out of the table and nothing else.</summary>
	[TestMethod]
	public void A_Sun_That_Becomes_Unreadable_Leaves_The_Area_Running()
	{
		var sun = new MovableSun();
		sun.SetQuietly(sunrise: new TimeOnly(8, 0), sunset: new TimeOnly(20, 2));
		var t = SunAnchored(sun);

		sun.MoveTo(sunrise: null, sunset: null);

		Advance(t, TimeSpan.FromSeconds(600));   // past both the moved boundary and two ticks
		Assert.AreEqual(0, t.Actuator.Applied.Count, "night cannot be placed, so the area stays on evening");
		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "the area is still running the state machine");
	}

	[TestMethod]
	public void A_Tick_That_Changes_Nothing_Sends_Nothing()
	{
		var t = Build(s => s.VacancyTimeoutSeconds = 60 * 60 * 5);
		t.Ha.Trigger(Motion, "on");
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(10));

		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void The_Tick_Never_Retargets_An_Overridden_Area()
	{
		var t = Build(s =>
		{
			s.VacancyTimeoutSeconds = 60 * 60 * 5;
			s.OverrideDurationMinutes = 60 * 24;
		});
		t.Ha.Trigger(Motion, "on");
		Advance(t, TimeSpan.FromSeconds(30));
		t.Ha.Trigger(Light, "on", new() { ["brightness"] = 255 }, PhysicalDevice());
		t.Actuator.Clear();

		Advance(t, TimeSpan.FromMinutes(151));   // across the night boundary

		Assert.AreEqual(0, t.Actuator.Applied.Count, "the human's levels are sacred until the override expires");
	}
}
