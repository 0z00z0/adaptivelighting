using System.Globalization;
using System.Reactive.Subjects;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

using NetDaemon.HassModel;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>A level that works out to nothing goes out as an off the room recognises as its own.</summary>
/// <remarks>
///     Home Assistant carries out a turn-on at 0 % as a turn-off. Declared as an on, the echo reads as a person at
///     the switch and the room drops into its manual-off state, where movement no longer lights it.
/// </remarks>
[TestClass]
public sealed class ZeroBrightnessTests
{
	private const string Motion = "binary_sensor.stue_bevegelse";
	private const string Lux = "sensor.stue_lux";
	private const string Group = "light.stue_taklys";
	private const string First = "light.stue_tak_1";
	private const string Second = "light.stue_tak_2";
	private const string Lamp = "light.stue_leselampe";

	private sealed record Fixture(TestScheduler Scheduler, FakeHaContext Ha, FakeLightActuator Actuator, AreaController Area);

	private static List<TimePeriodConfig> Schedule() =>
	[
		new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
		new() { Name = "late", Start = "21:00", BrightnessPct = 40, ColorTempKelvin = 2400 },
		new() { Name = "night", Start = "22:00", BrightnessPct = 15, ColorTempKelvin = 2200 }
	];

	/// <summary>A group of two bulbs and one lamp, started at 20:00 inside "evening".</summary>
	private static Fixture Build(
		IReadOnlyList<RoomLevelOverride>? roomLevels = null,
		IReadOnlyList<LightLevelOverride>? lightLevels = null,
		double preOffFactor = 0.5,
		int vacancySeconds = 10800)
	{
		TestScheduler scheduler = new();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		FakeHaContext ha = new();
		ha.SetState(Motion, "off");
		ha.SetState(Lux, "5");
		ha.SetState(Group, "off", new Dictionary<string, object> { ["entity_id"] = new[] { First, Second } });
		ha.SetState(First, "off");
		ha.SetState(Second, "off");
		ha.SetState(Lamp, "off");

		AreaSettings settings = new()
		{
			VacancyTimeoutSeconds = vacancySeconds,
			PreOffSeconds = 30,
			PreOffBrightnessFactor = preOffFactor,
			Darkness = DarknessSource.Lux,
			OverrideUntilVacant = false,
			OverrideDurationMinutes = 120,
			VacancyResetMinutes = 10
		};

		GlobalConfig global = new() { SmoothTransitions = false, CircadianTickSeconds = 60 };
		List<TimePeriodConfig> table = Schedule();

		Dictionary<string, IReadOnlySet<string>> leaves = new(StringComparer.Ordinal)
		{
			[Group] = new HashSet<string>(StringComparer.Ordinal) { First, Second },
			[Lamp] = new HashSet<string>(StringComparer.Ordinal) { Lamp }
		};

		Dictionary<string, IReadOnlyList<RoomLevelOverride>> stated = new(StringComparer.OrdinalIgnoreCase);
		foreach (LightLevelOverride light in lightLevels ?? [])
			stated[light.EntityId] = light.Levels;

		ResolvedArea area = new("Stue", settings, [Group, Lamp], [Motion], [Lux], [])
		{
			LeavesOfEntry = leaves,
			LightLevels = stated
		};

		Dictionary<string, CircadianCalculator> perLight = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string leaf, IReadOnlyList<RoomLevelOverride> rows) in stated)
			perLight[leaf] = new CircadianCalculator(
				table, global, () => SunTimes.Unknown, LightLevelMerge.MergeOnto(roomLevels, rows), zone: TimeZoneInfo.Utc);

		FakeLightActuator actuator = new();
		BehaviorSubject<HouseState> house = new(HouseState.Initial);

		AreaController controller = new(
			ha,
			scheduler,
			area,
			global,
			table,
			new CircadianCalculator(table, global, () => SunTimes.Unknown, roomLevels, zone: TimeZoneInfo.Utc),
			actuator,
			new FakeStatePublisher(),
			house,
			NullLoggerFactory.Instance,
			areaId: "stue",
			lightCalculators: perLight.Count > 0 ? perLight : null);

		controller.Start();
		house.OnNext(new HouseState(true, ModeKind.Normal, false));

		return new Fixture(scheduler, ha, actuator, controller);
	}

	/// <summary>What Home Assistant attaches to a change the engine's own service call caused: a user, no parent.</summary>
	private static Context EngineCall() => new() { Id = "engine-call", UserId = "engine-token-user" };

	private static IReadOnlyList<string> Recorded(FakeLightActuator actuator) =>
	[
		.. actuator.Applied.Select(call => call.Command.On
			? string.Create(
				CultureInfo.InvariantCulture,
				$"{call.EntityId} on {call.Command.BrightnessPct:0.##}% {call.Command.ColorTempKelvin}K")
			: $"{call.EntityId} off")
	];

	/// <summary>Movement lights the room at the evening level and Home Assistant reports both entries lit.</summary>
	private static void LightTheRoom(Fixture room)
	{
		room.Ha.Trigger(Motion, "on");
		room.Ha.Trigger(Motion, "off");
		room.Ha.Trigger(Group, "on", context: EngineCall());
		room.Ha.Trigger(Lamp, "on", context: EngineCall());
		room.Actuator.Clear();
	}

	private static void AdvanceTo(Fixture room, int hour, int minute, int second = 0) =>
		room.Scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, hour, minute, second, TimeSpan.Zero).Ticks);

	[TestMethod]
	public void A_Lit_Room_Reaching_A_Zero_Row_Is_Switched_Off_And_Comes_Back_By_Itself()
	{
		Fixture room = Build(roomLevels: [new RoomLevelOverride { PeriodId = "late", Brightness = 0 }]);
		LightTheRoom(room);

		AdvanceTo(room, 21, 0, 5);
		room.Ha.Trigger(Group, "off", context: EngineCall());
		room.Ha.Trigger(Lamp, "off", context: EngineCall());

		Assert.AreEqual(
			AreaState.AutoActive,
			room.Area.State,
			"the lights going out is the engine's own work; read as a person, the room sits in manual-off and ignores "
			+ "the next period");

		CollectionAssert.AreEqual(
			new[] { "light.stue_taklys off", "light.stue_leselampe off" },
			Recorded(room.Actuator).ToArray(),
			"a level of nothing is an off, not a turn-on at 0 %");

		room.Ha.Trigger(Motion, "on");
		room.Ha.Trigger(Motion, "off");
		room.Actuator.Clear();

		AdvanceTo(room, 22, 0, 30);

		CollectionAssert.AreEqual(
			new[] { "light.stue_taklys on 15% 2200K", "light.stue_leselampe on 15% 2200K" },
			Recorded(room.Actuator).ToArray(),
			"the next period lights the room again without anyone waiting for it to go quiet");
	}

	[TestMethod]
	public void A_Warning_Dim_Of_Nothing_Switches_Off_And_Movement_Still_Brings_The_Lights_Back()
	{
		Fixture room = Build(preOffFactor: 0, vacancySeconds: 600);
		LightTheRoom(room);

		room.Scheduler.AdvanceBy(TimeSpan.FromSeconds(600).Ticks);
		room.Ha.Trigger(Group, "off", context: EngineCall());
		room.Ha.Trigger(Lamp, "off", context: EngineCall());

		Assert.AreEqual(
			AreaState.PreOff,
			room.Area.State,
			"the warning dim going dark is the engine's own; read as a person, movement can no longer rescue the room");

		CollectionAssert.AreEqual(new[] { "light.stue_taklys off", "light.stue_leselampe off" }, Recorded(room.Actuator).ToArray());

		room.Actuator.Clear();
		room.Ha.Trigger(Motion, "on");

		Assert.AreEqual(AreaState.AutoActive, room.Area.State);
		CollectionAssert.AreEqual(
			new[] { "light.stue_taklys on 70% 2700K", "light.stue_leselampe on 70% 2700K" },
			Recorded(room.Actuator).ToArray());
	}

	[TestMethod]
	public void A_Dim_Deep_Enough_To_Land_On_Raw_Zero_Is_An_Off_Too()
	{
		// 70 % dimmed by 0.002 is 0.14 %, which Home Assistant stores as raw 0 and carries out as a turn-off.
		Fixture room = Build(preOffFactor: 0.002, vacancySeconds: 600);
		LightTheRoom(room);

		room.Scheduler.AdvanceBy(TimeSpan.FromSeconds(600).Ticks);

		CollectionAssert.AreEqual(
			new[] { "light.stue_taklys off", "light.stue_leselampe off" },
			Recorded(room.Actuator).ToArray(),
			"the rule is what the byte comes out as, not whether the percentage is above zero");
	}

	[TestMethod]
	public void A_Group_Whose_Every_Bulb_Goes_Off_Is_Expected_Off_While_The_Room_Stays_Lit()
	{
		Fixture room = Build(lightLevels:
		[
			new LightLevelOverride { EntityId = First, Levels = [new RoomLevelOverride { PeriodId = "late", Brightness = 0 }] },
			new LightLevelOverride { EntityId = Second, Levels = [new RoomLevelOverride { PeriodId = "late", Brightness = 0 }] }
		]);
		LightTheRoom(room);

		AdvanceTo(room, 21, 0, 5);

		CollectionAssert.AreEqual(
			new[] { "light.stue_tak_1 off", "light.stue_tak_2 off", "light.stue_leselampe on 40% 2400K" },
			Recorded(room.Actuator).ToArray());

		// Home Assistant reports the group going dark under the group's own id once both bulbs have.
		room.Ha.Trigger(Group, "off", context: EngineCall());

		Assert.AreEqual(
			AreaState.AutoActive,
			room.Area.State,
			"the group is sent nothing but off beneath it, so its expectation must be an off too");
	}

	[TestMethod]
	public void Testing_A_Zero_Row_Switches_The_Room_Off_And_Still_Gives_It_Back()
	{
		Fixture room = Build(roomLevels: [new RoomLevelOverride { PeriodId = "late", Brightness = 0 }]);
		LightTheRoom(room);

		Assert.IsNull(room.Area.TestPeriod("late"));
		room.Ha.Trigger(Group, "off", context: EngineCall());
		room.Ha.Trigger(Lamp, "off", context: EngineCall());

		Assert.IsTrue(
			room.Area.IsTestingLevels,
			"the test's own off must not read as a person, or the return is cancelled and the room stays dark");
		Assert.AreEqual(AreaState.AutoActive, room.Area.State);

		room.Scheduler.AdvanceBy(TimeSpan.FromSeconds(AreaController.LevelTestSeconds).Ticks);

		CollectionAssert.AreEqual(
			new[]
			{
				"light.stue_taklys off",
				"light.stue_leselampe off",
				"light.stue_taklys on 70% 2700K",
				"light.stue_leselampe on 70% 2700K"
			},
			Recorded(room.Actuator).ToArray());
	}
}
