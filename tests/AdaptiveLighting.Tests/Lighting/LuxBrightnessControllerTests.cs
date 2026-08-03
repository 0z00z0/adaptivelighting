using System.Reactive.Subjects;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The daylight brightness adjustment as the area uses it: which sensor it reads, when it is re-evaluated,
///     and what still outranks it.
/// </summary>
/// <remarks>
///     Every fixture gates on <see cref="DarknessSource.Always"/>. Any other source lets a bright reading answer
///     "do not light this room", which would hide what these tests are about.
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

	/// <summary>The house-mode helper, for the one test that needs sleep to outrank the sun.</summary>
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
	///     A started area at 20:00, inside "evening", whose 70 % is the number every assertion here moves from.
	/// </summary>
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
			new() { Name = "night", Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200 }
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

	[TestMethod]
	public void An_Unavailable_Sensor_Falls_Back_To_The_Schedule()
	{
		Assert.AreEqual(70d, CommandedOnMotion(Build(settings => settings.LuxBrightnessEnabled = true, lux: "unavailable")));
	}

	// ===================== which sensor =====================

	// The curve reads whatever the darkness gate reads: one sensor per room. The outdoor sensor is opt-in, so a
	// room that has not asked gets no reading at all, which the test below pins.
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

	// Nothing subscribes to the lux sensor, so the periodic tick is the only thing that sees the sun come out.
	// The adjustment has to be applied when the target is resolved, not when a command is built.
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


	// Ordering: the sleep clamp runs after the daylight adjustment, or an afternoon nap keeps the raised level.
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

		Assert.AreEqual(15d, area.Actuator.Last?.BrightnessPct, "and asleep, the night period's own level takes it back");
	}

	// The pre-off dim is a fraction of what the room is holding, not of the schedule's level.
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
