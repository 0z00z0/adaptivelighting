using System.Reactive.Subjects;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The daylight brightness adjustment as the area actually uses it: which sensor it reads, when it is
///     re-evaluated, and what still outranks it.
/// </summary>
/// <remarks>
///     The area gates on <see cref="DarknessSource.Always"/> throughout, which is not a dodge but the owner's
///     case: a hallway with no daylight of its own, lit on motion whatever the hour, whose level should follow
///     the sun outside. It also keeps the two questions apart — "may the engine light this room" is the darkness
///     gate's, and a bright reading answering it "no" would hide everything this file is about.
/// </remarks>
[TestClass]
public sealed class LuxBrightnessControllerTests
{
	private const string Motion = "binary_sensor.hallway_motion";
	private const string Light = "light.hallway";
	private const string Lux = "sensor.hallway_lux";
	private const string OutdoorLux = "sensor.outdoor_lux";

	private sealed record Fixture(
		TestScheduler Scheduler,
		FakeHaContext Ha,
		FakeLightActuator Actuator,
		BehaviorSubject<HouseState> House,
		AreaController Area);

	/// <summary>The cabin's helper shape, for the one test that needs sleep to outrank the sun.</summary>
	private static HouseModeConfig SoverMode() => new()
	{
		Entity = "input_select.husmodus",
		Options =
		[
			new() { Value = "Normal", Kind = ModeKind.Normal },
			new() { Value = "Sover", Kind = ModeKind.Sleep }
		]
	};

	/// <summary>
	///     A started area at 20:00 — inside "evening", whose 70 % is the number every assertion here moves from.
	/// </summary>
	/// <param name="tweak">The area's settings, curve included.</param>
	/// <param name="lux">What the area's own lux sensor reads.</param>
	/// <param name="luxSensor">The area's own sensor, or <c>null</c> for a room that resolved none.</param>
	/// <param name="outdoorLux">The house-wide outdoor sensor's reading, or <c>null</c> to leave it unconfigured.</param>
	/// <param name="followOutdoorLux">Whether the room asked to read the house's outdoor sensor when it has none of its own.</param>
	/// <param name="houseMode">The house-mode helper, for the sleep-clamp test.</param>
	private static Fixture Build(
		Action<AreaSettings>? tweak = null,
		string lux = "5",
		string? luxSensor = Lux,
		string? outdoorLux = null,
		bool followOutdoorLux = false,
		HouseModeConfig? houseMode = null)
	{
		TestScheduler scheduler = new();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		FakeHaContext ha = new();
		ha.SetState(Motion, "off");
		ha.SetState(Light, "off");
		ha.SetState(Lux, lux);

		if (outdoorLux is not null)
			ha.SetState(OutdoorLux, outdoorLux);

		AreaSettings settings = new()
		{
			VacancyTimeoutSeconds = 600,
			PreOffSeconds = 30,
			Darkness = DarknessSource.Always,
			LuxBrightnessStartLux = 100,
			LuxBrightnessFullLux = 10000,
			LuxBrightnessMaxPct = 100
		};
		tweak?.Invoke(settings);

		GlobalConfig global = new()
		{
			SmoothTransitions = false,
			CircadianTickSeconds = 60,
			OutdoorLuxSensor = outdoorLux is null ? null : OutdoorLux,
			HouseMode = houseMode
		};

		List<TimePeriodConfig> table =
		[
			new() { Name = "day", Start = "07:00", BrightnessPct = 90, ColorTempKelvin = 4500 },
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new() { Name = "night", Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200, MaxBrightnessPct = 30 }
		];

		ResolvedArea area = new(
			"Hallway", settings, [Light], [Motion], luxSensor is null ? [] : [luxSensor], [], followOutdoorLux);
		FakeLightActuator actuator = new();
		BehaviorSubject<HouseState> house = new(HouseState.Initial);

		AreaController controller = new(
			ha, scheduler, area, global, table,
			new CircadianCalculator(table, global, () => SunTimes.Unknown),
			actuator, new FakeStatePublisher(), house, NullLoggerFactory.Instance, areaId: "hallway");

		controller.Start();
		return new Fixture(scheduler, ha, actuator, house, controller);
	}

	/// <summary>Turns the area on and hands back the brightness it was commanded to.</summary>
	private static double CommandedOnMotion(Fixture fixture)
	{
		fixture.Ha.Trigger(Motion, "on");
		Assert.AreEqual(AreaState.AutoActive, fixture.Area.State);
		Assert.IsNotNull(fixture.Actuator.Last);
		return fixture.Actuator.Last!.BrightnessPct!.Value;
	}

	// ===================== off is off =====================

	/// <summary>
	///     The one that protects the houses already running this. Broad daylight on the sensor, and the room is
	///     commanded the period's own 70 % — the same number it would have been commanded before the setting
	///     existed.
	/// </summary>
	[TestMethod]
	public void With_The_Feature_Off_A_Blazing_Sensor_Changes_Nothing()
	{
		Assert.AreEqual(70d, CommandedOnMotion(Build(lux: "20000")));
	}

	[TestMethod]
	public void With_The_Feature_Off_A_Changing_Reading_Commands_Nothing_On_A_Tick()
	{
		Fixture area = Build(lux: "5");
		CommandedOnMotion(area);
		area.Actuator.Clear();

		area.Ha.SetState(Lux, "20000");
		area.Scheduler.AdvanceBy(TimeSpan.FromSeconds(90).Ticks);

		Assert.AreEqual(0, area.Actuator.Applied.Count,
			"the tick re-reads the world, but with the feature off the world it reads has not moved");
	}

	// ===================== on =====================

	[TestMethod]
	public void A_Dark_Reading_Still_Gives_The_Schedules_Level()
	{
		Assert.AreEqual(70d, CommandedOnMotion(Build(settings => settings.LuxBrightnessEnabled = true, lux: "5")));
	}

	[TestMethod]
	public void A_Bright_Reading_Raises_The_Room_To_The_Ceiling()
	{
		Assert.AreEqual(100d, CommandedOnMotion(Build(settings => settings.LuxBrightnessEnabled = true, lux: "20000")));
	}

	[TestMethod]
	public void The_Log_Midpoint_Lands_Halfway_Between_The_Schedule_And_The_Ceiling()
	{
		double commanded = CommandedOnMotion(Build(settings => settings.LuxBrightnessEnabled = true, lux: "1000"));

		Assert.AreEqual(85, commanded, 1e-9, "70 % plus half of the 30 points of headroom");
	}

	/// <summary>A reading that is not a number is a room with no reading, not a room that fails.</summary>
	[TestMethod]
	public void An_Unavailable_Sensor_Falls_Back_To_The_Schedule()
	{
		Assert.AreEqual(70d, CommandedOnMotion(Build(settings => settings.LuxBrightnessEnabled = true, lux: "unavailable")));
	}

	// ===================== which sensor =====================

	/// <summary>
	///     The feature as it was asked for: one outdoor sensor brightening a hallway that has none of its own —
	///     now that the hallway has said it wants that.
	/// </summary>
	/// <remarks>
	///     <b>This test's contract changed: the opt-in is the new half of it.</b> The outdoor sensor used to reach
	///     every sensorless room automatically, which is the fallback the owner removed. The daylight curve reads
	///     whatever the darkness gate reads — one sensor per room, one answer — so a room that does not follow the
	///     outdoor sensor gets no reading here either, which the test below pins.
	/// </remarks>
	[TestMethod]
	public void A_Room_That_Follows_The_Outdoor_Sensor_Brightens_With_It()
	{
		Fixture area = Build(
			settings => settings.LuxBrightnessEnabled = true,
			luxSensor: null,
			outdoorLux: "20000",
			followOutdoorLux: true);

		Assert.AreEqual(100d, CommandedOnMotion(area));
	}

	/// <summary>And a room that did not ask keeps the schedule's brightness, because it has no reading to follow.</summary>
	[TestMethod]
	public void A_Room_That_Did_Not_Ask_Keeps_The_Schedules_Brightness()
	{
		Fixture area = Build(
			settings => settings.LuxBrightnessEnabled = true,
			luxSensor: null,
			outdoorLux: "20000");

		Assert.AreEqual(70d, CommandedOnMotion(area),
			"blazing outside, but this room never asked to look — so the schedule stands, as it did before the feature existed");
	}

	/// <summary>The room's own sensor wins, exactly as it does for the darkness verdict — one resolution, one answer.</summary>
	[TestMethod]
	public void A_Room_With_Its_Own_Sensor_Ignores_The_Outdoor_One()
	{
		Fixture area = Build(
			settings => settings.LuxBrightnessEnabled = true,
			lux: "5",
			outdoorLux: "20000");

		Assert.AreEqual(70d, CommandedOnMotion(area));
	}

	[TestMethod]
	public void A_Room_With_No_Sensor_Anywhere_Keeps_The_Schedule()
	{
		Fixture area = Build(settings => settings.LuxBrightnessEnabled = true, luxSensor: null);

		Assert.AreEqual(70d, CommandedOnMotion(area));
	}

	// ===================== the tick is what notices =====================

	/// <summary>
	///     Nothing subscribes to the lux sensor, so the periodic tick is the only thing that can see the sun come
	///     out. If the adjustment were applied when a command is built rather than when the target is resolved,
	///     a hallway would brighten on the next motion event and never before — which in a hallway is never.
	/// </summary>
	[TestMethod]
	public void The_Tick_Retargets_When_It_Gets_Brighter_Outside()
	{
		Fixture area = Build(settings => settings.LuxBrightnessEnabled = true, lux: "5");
		Assert.AreEqual(70d, CommandedOnMotion(area));
		area.Actuator.Clear();

		area.Ha.SetState(Lux, "20000");
		area.Scheduler.AdvanceBy(TimeSpan.FromSeconds(90).Ticks);

		Assert.AreEqual(100d, area.Actuator.Last?.BrightnessPct);
	}

	// ===================== what still outranks it =====================

	/// <summary>
	///     The period's cap is the rule that stops 100 % at 03:00, and a bright sensor must not be able to defeat
	///     it. Night runs from 22:30 at 15 % capped to 30 %.
	/// </summary>
	[TestMethod]
	public void The_Periods_Cap_Beats_A_Bright_Reading()
	{
		Fixture area = Build(
			settings =>
			{
				settings.LuxBrightnessEnabled = true;

				// Long enough that the area is still occupied when the clock reaches the night period: this test is
				// about the cap, not about the vacancy timer beating it there.
				settings.VacancyTimeoutSeconds = (int)TimeSpan.FromHours(4).TotalSeconds;
			},
			lux: "20000");

		CommandedOnMotion(area);
		area.Actuator.Clear();

		// Into the night period, which caps at 30.
		area.Scheduler.AdvanceBy(TimeSpan.FromHours(3).Ticks);

		Assert.AreEqual(30d, area.Actuator.Last?.BrightnessPct,
			"the daylight adjustment proposes; the period disposes");
	}

	/// <summary>
	///     Sleep is the stronger of the two statements. An afternoon nap under a bright sky must land on the night
	///     rules, which is only guaranteed if the clamp runs after the adjustment rather than before it.
	/// </summary>
	[TestMethod]
	public void The_Sleep_Clamp_Beats_A_Bright_Reading()
	{
		Fixture area = Build(
			settings =>
			{
				settings.LuxBrightnessEnabled = true;
				settings.RespectSleepMode = true;
			},
			lux: "20000",
			houseMode: SoverMode());

		Assert.AreEqual(100d, CommandedOnMotion(area), "awake, the bright sky takes the hallway to the ceiling");

		area.House.OnNext(new HouseState(true, ModeKind.Sleep, false) { ModeValue = "Sover" });

		Assert.AreEqual(30d, area.Actuator.Last?.BrightnessPct, "and asleep, the night period's cap takes it back");
	}

	/// <summary>
	///     The pre-off warning is a fraction of whatever the room is actually holding, so in a bright hallway it
	///     dims from the raised level rather than from the schedule's — otherwise the "speak now" dim would be
	///     invisible on exactly the days the room is brightest.
	/// </summary>
	[TestMethod]
	public void The_Pre_Off_Dim_Is_Half_Of_The_Raised_Level()
	{
		Fixture area = Build(
			settings =>
			{
				settings.LuxBrightnessEnabled = true;
				settings.PreOffBrightnessFactor = 0.5;
			},
			lux: "20000");

		Assert.AreEqual(100d, CommandedOnMotion(area));

		area.Scheduler.AdvanceBy(TimeSpan.FromSeconds(601).Ticks);

		Assert.AreEqual(50d, area.Actuator.Last?.BrightnessPct);
	}
}
