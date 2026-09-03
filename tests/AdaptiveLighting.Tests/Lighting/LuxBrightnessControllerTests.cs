using System.Reactive.Subjects;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The daylight curve as the area uses it: which sensor it reads, when it is re-evaluated, and what outranks it.</summary>
// Every fixture gates on DarknessSource.Always; any other source lets a bright reading answer "do not light
// this room".
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

	/// <summary>A started area at 20:00, inside "evening", whose 70 % is the number every assertion moves from.</summary>
	// The curve's dark end is 70 too, so a dark reading and the period's own number agree: every assertion that
	// moves off 70 is the curve doing something, and never the mode choice alone.
	private static Fixture Build(
		Action<AreaSettings>? tweak = null,
		bool eveningOnTheCurve = false,
		bool nightOnTheCurve = false,
		string lux = "5",
		string? luxSensor = Lux,
		string? outdoorLux = null,
		string? daylightSensor = null,
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
			LuxBrightnessMinPct = 70,
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
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700, UseDaylightCurve = eveningOnTheCurve },
			new() { Name = "night", Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200, UseDaylightCurve = nightOnTheCurve }
		];

		ResolvedArea area = new(
			"Hallway", settings, [Light], [Motion], luxSensor is null ? [] : [luxSensor], [])
		{
			DaylightSensor = daylightSensor
		};

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

	// ===================== a period that states its own brightness =====================

	[TestMethod]
	public void A_Period_Off_The_Curve_Ignores_A_Blazing_Sky()
	{
		Assert.AreEqual(70d, CommandedOnMotion(Build(outdoorLux: "20000")));
	}

	[TestMethod]
	public void A_Period_Off_The_Curve_Commands_Nothing_On_A_Tick()
	{
		Fixture area = Build(outdoorLux: "5");
		CommandedOnMotion(area);
		area.Actuator.Clear();

		area.Ha.SetState(OutdoorLux, "20000");
		area.Scheduler.AdvanceBy(TimeSpan.FromSeconds(90).Ticks);

		Assert.AreEqual(0, area.Actuator.Applied.Count,
			"the tick re-reads the world, but a period that states its own brightness does not care what it reads");
	}

	// ===================== a period on the curve =====================

	[TestMethod]
	public void A_Dark_Reading_Gives_The_Curves_Dark_End()
	{
		Assert.AreEqual(70d, CommandedOnMotion(Build(eveningOnTheCurve: true, outdoorLux: "5")));
	}

	[TestMethod]
	public void A_Bright_Reading_Gives_The_Curves_Bright_End()
	{
		Assert.AreEqual(100d, CommandedOnMotion(Build(eveningOnTheCurve: true, outdoorLux: "20000")));
	}

	[TestMethod]
	public void The_Log_Midpoint_Lands_Halfway_Between_The_Curves_Two_Ends()
	{
		double commanded = CommandedOnMotion(Build(eveningOnTheCurve: true, outdoorLux: "1000"));

		Assert.AreEqual(85, commanded, 1e-9, "70 % plus half of the 30 points between the two ends");
	}

	/// <summary>The period's own number is gone, not merely hidden: the curve answers the same whatever it says.</summary>
	[TestMethod]
	public void The_Periods_Own_Percentage_No_Longer_Reaches_The_Lights()
	{
		Fixture area = Build(eveningOnTheCurve: true, outdoorLux: "20000");

		Assert.AreEqual(100d, CommandedOnMotion(area), "and the period asked for 70");
	}

	[TestMethod]
	public void An_Unavailable_Sensor_Holds_The_Curves_Dark_End()
	{
		Assert.AreEqual(70d, CommandedOnMotion(Build(eveningOnTheCurve: true, outdoorLux: "unavailable")));
	}

	// ===================== which sensor =====================

	[TestMethod]
	public void The_Curve_Reads_The_Houses_Outdoor_Sensor()
	{
		Fixture area = Build(eveningOnTheCurve: true, lux: "5", outdoorLux: "20000");

		Assert.AreEqual(100d, CommandedOnMotion(area),
			"the room's own indoor sensor reads 5 lx, and the curve is not reading it");
	}

	/// <summary>An indoor sensor measures the room's own lamps, so the darkness sensor never feeds the curve.</summary>
	[TestMethod]
	public void The_Curve_Does_Not_Read_The_Darkness_Sensor()
	{
		Fixture area = Build(eveningOnTheCurve: true, lux: "20000", outdoorLux: "5");

		Assert.AreEqual(70d, CommandedOnMotion(area),
			"the room's own sensor is blazing and the sky is dark; the curve follows the sky");
	}

	[TestMethod]
	public void A_Room_Can_Override_Which_Sensor_The_Curve_Reads()
	{
		Fixture area = Build(eveningOnTheCurve: true, lux: "20000", outdoorLux: "5", daylightSensor: Lux);

		Assert.AreEqual(100d, CommandedOnMotion(area),
			"named for this room, the sensor the curve refused above is exactly the one it now reads");
	}

	[TestMethod]
	public void With_No_Sensor_Named_Anywhere_The_Curve_Holds_Its_Dark_End()
	{
		Fixture area = Build(eveningOnTheCurve: true, lux: "20000", luxSensor: Lux);

		Assert.AreEqual(70d, CommandedOnMotion(area),
			"no outdoor sensor and no room override is a level nobody chose, which is what the validator warns about");
	}

	// ===================== the tick is what notices =====================

	// Nothing subscribes to the lux sensor, so the tick is the only thing that notices, and the curve applies
	// where the target is resolved.
	[TestMethod]
	public void The_Tick_Retargets_When_It_Gets_Brighter_Outside()
	{
		Fixture area = Build(eveningOnTheCurve: true, outdoorLux: "5");
		Assert.AreEqual(70d, CommandedOnMotion(area));
		area.Actuator.Clear();

		area.Ha.SetState(OutdoorLux, "20000");
		area.Scheduler.AdvanceBy(TimeSpan.FromSeconds(90).Ticks);

		Assert.AreEqual(100d, area.Actuator.Last?.BrightnessPct);
	}

	// ===================== what still outranks it =====================

	// Ordering: the sleep clamp runs after the curve, or an afternoon nap keeps the daylight level.
	[TestMethod]
	public void The_Sleep_Clamp_Beats_A_Bright_Reading()
	{
		Fixture area = Build(
			settings => settings.RespectSleepMode = true,
			eveningOnTheCurve: true,
			outdoorLux: "20000",
			houseMode: SoverMode());

		Assert.AreEqual(100d, CommandedOnMotion(area), "awake, the bright sky takes the hallway to the curve's top");

		area.House.OnNext(new HouseState(true, ModeKind.Sleep, false) { ModeValue = "Sover" });

		Assert.AreEqual(15d, area.Actuator.Last?.BrightnessPct, "and asleep, the night period's own level takes it back");
	}

	// ===================== a clamp period that runs the curve =====================

	// The clamp asks the calculator for the clamp period's level, and a curve period's stored number is inert.
	// Reading it unresolved made the night's ceiling a figure the room shows nowhere else.
	[TestMethod]
	public void A_Clamp_Period_On_The_Curve_Takes_Its_Ceiling_From_The_Light_Outside()
	{
		Fixture area = Build(
			settings =>
			{
				settings.RespectSleepMode = true;
				settings.LuxBrightnessMinPct = 30;
			},
			nightOnTheCurve: true,
			outdoorLux: "5",
			houseMode: SoverMode());

		Assert.AreEqual(70d, CommandedOnMotion(area), "awake, the evening states its own 70 %");

		area.House.OnNext(new HouseState(true, ModeKind.Sleep, false) { ModeValue = "Sover" });

		Assert.AreEqual(30d, area.Actuator.Last?.BrightnessPct,
			"asleep, the ceiling is the curve's dark end and never the night period's inert 15 %");
	}

	/// <summary>The ceiling follows the reading, so it is the curve answering and not one fixed number.</summary>
	[TestMethod]
	public void A_Clamp_Period_On_The_Curve_Moves_Its_Ceiling_With_The_Reading()
	{
		Fixture area = Build(
			settings =>
			{
				settings.RespectSleepMode = true;
				settings.LuxBrightnessMinPct = 30;
			},
			nightOnTheCurve: true,
			outdoorLux: "1000",
			houseMode: SoverMode());

		CommandedOnMotion(area);
		area.House.OnNext(new HouseState(true, ModeKind.Sleep, false) { ModeValue = "Sover" });

		// 1 000 lx is the log midpoint of the 100 and 10 000 anchors: halfway from 30 % to 100 % is 65 %.
		Assert.AreEqual(65d, area.Actuator.Last?.BrightnessPct);
	}

	// The control for the two above: same curve, same reading, only the clamp period's own flag moved.
	[TestMethod]
	public void A_Clamp_Period_Off_The_Curve_Keeps_Its_Stored_Ceiling()
	{
		Fixture area = Build(
			settings =>
			{
				settings.RespectSleepMode = true;
				settings.LuxBrightnessMinPct = 30;
			},
			outdoorLux: "5",
			houseMode: SoverMode());

		Assert.AreEqual(70d, CommandedOnMotion(area));

		area.House.OnNext(new HouseState(true, ModeKind.Sleep, false) { ModeValue = "Sover" });

		Assert.AreEqual(15d, area.Actuator.Last?.BrightnessPct,
			"the night period states its own level, so 15 % is the ceiling and the curve has no say");
	}

	// The pre-off dim is a fraction of what the room is holding, not of the schedule's level.
	[TestMethod]
	public void The_Pre_Off_Dim_Is_Half_Of_The_Curves_Level()
	{
		Fixture area = Build(
			settings => settings.PreOffBrightnessFactor = 0.5,
			eveningOnTheCurve: true,
			outdoorLux: "20000");

		Assert.AreEqual(100d, CommandedOnMotion(area));

		area.Scheduler.AdvanceBy(TimeSpan.FromSeconds(601).Ticks);

		Assert.AreEqual(50d, area.Actuator.Last?.BrightnessPct);
	}

	// ===================== several periods on the curve =====================

	/// <summary>The curve spans every period that claims it, and each takes the same reading.</summary>
	[TestMethod]
	public void Every_Period_On_The_Curve_Takes_The_Same_Level()
	{
		TestScheduler scheduler = new();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		FakeHaContext ha = new();
		ha.SetState(Motion, "off");
		ha.SetState(Light, "off");
		ha.SetState(OutdoorLux, "20000");

		// Long enough that nothing goes empty over the twelve hours advanced below.
		AreaSettings settings = new()
		{
			VacancyTimeoutSeconds = 100_000,
			Darkness = DarknessSource.Always,
			LuxBrightnessStartLux = 100,
			LuxBrightnessFullLux = 10000,
			LuxBrightnessMinPct = 70,
			LuxBrightnessMaxPct = 100
		};

		GlobalConfig global = new()
		{
			SmoothTransitions = false,
			CircadianTickSeconds = 60,
			OutdoorLuxSensor = OutdoorLux
		};

		List<TimePeriodConfig> table =
		[
			new() { Name = "day", Start = "07:00", BrightnessPct = 90, ColorTempKelvin = 4500, UseDaylightCurve = true },
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700, UseDaylightCurve = true }
		];

		CircadianCalculator circadian = new(table, global, () => SunTimes.Unknown, zone: TimeZoneInfo.Utc);

		ResolvedArea area = new("Hallway", settings, [Light], [Motion], [], []);
		FakeLightActuator actuator = new();

		AreaController controller = new(
			ha, scheduler, area, global, table, circadian,
			actuator, new FakeStatePublisher(), new BehaviorSubject<HouseState>(HouseState.Initial),
			NullLoggerFactory.Instance, areaId: "hallway");

		controller.Start();
		ha.Trigger(Motion, "on");

		Assert.AreEqual(100d, actuator.Last?.BrightnessPct, "evening, on the curve, at 20 000 lx");
		Assert.AreEqual(2700, actuator.Last?.ColorTempKelvin);

		// Into "day", whose own 90 % differs from evening's 70 %, so an inherited period level would show here.
		// The warmth is what proves the boundary was crossed: the two levels are the same, which is the claim.
		scheduler.AdvanceBy(TimeSpan.FromHours(12).Ticks);

		Assert.AreEqual(4500, actuator.Last?.ColorTempKelvin, "the day period is in force now");
		Assert.AreEqual(100d, actuator.Last?.BrightnessPct, "and day, on the same curve, at the same reading");
	}
}
