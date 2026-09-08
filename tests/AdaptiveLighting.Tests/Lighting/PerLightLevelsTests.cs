using System.Globalization;
using System.Reactive.Subjects;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

using NetDaemon.HassModel;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>What a room commands once single lights inside it state levels of their own.</summary>
/// <remarks>
///     The first test here is the safety gate: a room stating nothing per light must produce the identical
///     sequence of actuator calls to the build before the feature existed, entry for entry.
/// </remarks>
[TestClass]
public sealed class PerLightLevelsTests
{
	private const string Motion = "binary_sensor.stue_bevegelse";
	private const string Lux = "sensor.stue_lux";
	private const string Group = "light.stue_taklys";
	private const string First = "light.stue_tak_1";
	private const string Second = "light.stue_tak_2";
	private const string Lamp = "light.stue_leselampe";

	/// <summary>Comfortably past the eight-second echo window plus the fifteen-second night fade.</summary>
	private static readonly TimeSpan PastTheEcho = TimeSpan.FromSeconds(40);

	private sealed record Fixture(
		TestScheduler Scheduler,
		FakeHaContext Ha,
		FakeLightActuator Actuator,
		AreaController Area);

	/// <summary>The house's schedule, shared by the room and by every light that states levels of its own.</summary>
	private static List<TimePeriodConfig> Schedule() =>
	[
		new() { Name = "day", Start = "07:00", BrightnessPct = 90, ColorTempKelvin = 4500 },
		new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
		new() { Name = "night", Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200 }
	];

	private static LightLevelOverride Pins(string entityId, string periodId, int? brightness = null, int? kelvin = null, bool curve = false) =>
		new()
		{
			EntityId = entityId,
			Levels = [new RoomLevelOverride
			{
				PeriodId = periodId,
				Brightness = brightness,
				ColorTempKelvin = kelvin,
				FollowDaylightCurve = curve ? true : null
			}]
		};

	/// <summary>A room of one group of two bulbs and one stand-alone lamp, started at 20:00 inside "evening".</summary>
	private static Fixture Build(
		IReadOnlyList<RoomLevelOverride>? roomLevels = null,
		IReadOnlyList<LightLevelOverride>? lightLevels = null,
		IReadOnlyList<string>? lights = null)
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
			// Long enough for the room to cross the 22:30 boundary while it is still lit.
			VacancyTimeoutSeconds = 10800,
			PreOffSeconds = 30,
			Darkness = DarknessSource.Lux,
			OverrideUntilVacant = false,
			OverrideDurationMinutes = 120,
			VacancyResetMinutes = 10
		};

		GlobalConfig global = new() { SmoothTransitions = false, CircadianTickSeconds = 60 };
		List<TimePeriodConfig> table = Schedule();

		IReadOnlyList<string> entries = lights ?? [Group, Lamp];

		Dictionary<string, IReadOnlySet<string>> leaves = new(StringComparer.Ordinal);
		foreach (string entry in entries)
			leaves[entry] = string.Equals(entry, Group, StringComparison.Ordinal)
				? new HashSet<string>(StringComparer.Ordinal) { First, Second }
				: new HashSet<string>(StringComparer.Ordinal) { entry };

		Dictionary<string, IReadOnlyList<RoomLevelOverride>> stated = new(StringComparer.OrdinalIgnoreCase);
		foreach (LightLevelOverride light in lightLevels ?? [])
			stated[light.EntityId] = light.Levels;

		ResolvedArea area = new("Stue", settings, entries, [Motion], [Lux], [])
		{
			LeavesOfEntry = leaves,
			LightLevels = stated
		};

		Dictionary<string, CircadianCalculator> perLight = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string leaf, IReadOnlyList<RoomLevelOverride> rows) in stated)
			perLight[leaf] = new CircadianCalculator(
				table, global, () => SunTimes.Unknown, LightLevelMerge.MergeOnto(roomLevels, rows), zone: TimeZoneInfo.Utc);

		FakeLightActuator actuator = new();
		FakeStatePublisher publisher = new();
		BehaviorSubject<HouseState> house = new(HouseState.Initial);

		AreaController controller = new(
			ha,
			scheduler,
			area,
			global,
			table,
			new CircadianCalculator(table, global, () => SunTimes.Unknown, roomLevels, zone: TimeZoneInfo.Utc),
			actuator,
			publisher,
			house,
			NullLoggerFactory.Instance,
			areaId: "stue",
			lightCalculators: perLight.Count > 0 ? perLight : null);

		controller.Start();
		house.OnNext(new HouseState(true, ModeKind.Normal, false));

		return new Fixture(scheduler, ha, actuator, controller);
	}

	private static void Advance(Fixture fixture, TimeSpan by) => fixture.Scheduler.AdvanceBy(by.Ticks);

	/// <summary>A change with no user and no parent: a wall switch or dimmer acting on the light itself.</summary>
	private static Context PhysicalDevice() => new() { Id = "physical" };

	/// <summary>Every actuator call, in order, as one line each: the entity and what it was told to be.</summary>
	private static IReadOnlyList<string> Recorded(FakeLightActuator actuator) =>
	[
		.. actuator.Applied.Select(call => call.Command.On
			? string.Create(
				CultureInfo.InvariantCulture,
				$"{call.EntityId} on {call.Command.BrightnessPct:0.##}% {call.Command.ColorTempKelvin}K")
			: $"{call.EntityId} off")
	];

	/// <summary>Drives one room through movement, a boundary, the warning dim and the off.</summary>
	private static IReadOnlyList<string> WholeCycle(Fixture fixture)
	{
		fixture.Ha.Trigger(Motion, "on");

		// Past 22:30, so the tick re-aims the room at "night" while it is still lit.
		Advance(fixture, TimeSpan.FromHours(2) + TimeSpan.FromMinutes(31));

		// 20:00 plus the three-hour vacancy timeout: the warning dim.
		Advance(fixture, TimeSpan.FromMinutes(29));

		// And the off behind it.
		Advance(fixture, TimeSpan.FromSeconds(30));

		return Recorded(fixture.Actuator);
	}

	// ===================== the safety gate =====================

	/// <summary>The sequence the build before per-light levels produced, recorded from it and pinned here.</summary>
	// Two entries, four sends: on at the evening level, the night level at the boundary, the warning dim, the off.
	// A group is one call, exactly as it was before membership was resolved at all.
	private static readonly string[] UntouchedCycle =
	[
		"light.stue_taklys on 70% 2700K",
		"light.stue_leselampe on 70% 2700K",
		"light.stue_taklys on 15% 2200K",
		"light.stue_leselampe on 15% 2200K",
		"light.stue_taklys on 7.5% 2200K",
		"light.stue_leselampe on 7.5% 2200K",
		"light.stue_taklys off",
		"light.stue_leselampe off"
	];

	[TestMethod]
	public void A_Room_With_No_Light_Rows_Commands_Exactly_What_It_Commanded_Before()
	{
		Fixture room = Build();

		CollectionAssert.AreEqual(
			UntouchedCycle,
			WholeCycle(room).ToArray(),
			"a document naming no light levels must reach Home Assistant with the identical calls, in the identical "
			+ "order, through movement, the boundary, the warning dim and the off. Nothing may move in any room "
			+ "until somebody ticks the box in one.");
	}

	[TestMethod]
	public void A_Room_Whose_Own_Levels_Moved_But_Names_No_Light_Still_Commands_Entry_By_Entry()
	{
		Fixture room = Build(roomLevels: [new RoomLevelOverride { PeriodId = "evening", Brightness = 128 }]);

		room.Ha.Trigger(Motion, "on");

		CollectionAssert.AreEqual(
			new[] { "light.stue_taklys on 50.2% 2700K", "light.stue_leselampe on 50.2% 2700K" },
			Recorded(room.Actuator).ToArray(),
			"the room's own levels are not the per-light path; the group stays one call");
	}

	// ===================== the fan-out =====================

	[TestMethod]
	public void One_Light_Under_A_Group_Given_Its_Own_Level_Explodes_Only_That_Group()
	{
		Fixture room = Build(lightLevels: [Pins(First, "evening", brightness: 26)]);

		room.Ha.Trigger(Motion, "on");

		CollectionAssert.AreEqual(
			new[]
			{
				// The group itself is not commanded, and its two bulbs are, the sibling at the room's level.
				"light.stue_tak_1 on 10.2% 2700K",
				"light.stue_tak_2 on 70% 2700K",

				// The stand-alone lamp has no light under it with a level of its own, so it takes today's path.
				"light.stue_leselampe on 70% 2700K"
			},
			Recorded(room.Actuator).ToArray());

		Assert.IsFalse(
			room.Actuator.Applied.Any(call => string.Equals(call.EntityId, Group, StringComparison.Ordinal)),
			"the group entity must receive no command of its own, or a bulb takes two commands in one send");
	}

	[TestMethod]
	public void The_Group_Entity_Still_Gets_An_Expectation_So_Its_Echo_Is_Not_Read_As_A_Person()
	{
		Fixture room = Build(lightLevels: [Pins(First, "evening", brightness: 26)]);

		room.Ha.Trigger(Motion, "on");
		Assert.AreEqual(AreaState.AutoActive, room.Area.State);

		// Home Assistant re-publishes a member's change under the group's own id, carrying neither a user nor a
		// parent. Without the expectation declared on the entry, that reads as a hand at a switch.
		room.Ha.Trigger(Group, "on", context: PhysicalDevice());

		Assert.AreEqual(
			AreaState.AutoActive,
			room.Area.State,
			"the group's re-publication of its own member's change is the engine's own work echoing back. Removing "
			+ "the expectation declared on the entry is what must turn this red.");
	}

	[TestMethod]
	public void After_The_Echo_Window_A_Hand_On_A_Sibling_Is_Still_Read_As_Manual()
	{
		Fixture room = Build(lightLevels: [Pins(First, "evening", brightness: 26)]);

		room.Ha.Trigger(Motion, "on");
		Advance(room, PastTheEcho);

		// The room subscribes to its entries, so somebody switching a bulb off at the wall arrives as the group
		// changing. The expectation must have expired by now, or the room would be blind to it for ever.
		room.Ha.Trigger(Group, "off", context: PhysicalDevice());

		Assert.AreEqual(AreaState.SuppressedOff, room.Area.State);
	}

	[TestMethod]
	public void A_Light_Pinned_To_Nothing_Is_Switched_Off_Inside_An_Otherwise_On_Send()
	{
		Fixture room = Build(lightLevels: [Pins(First, "evening", brightness: 0)]);

		room.Ha.Trigger(Motion, "on");

		CollectionAssert.AreEqual(
			new[] { "light.stue_tak_1 off", "light.stue_tak_2 on 70% 2700K", "light.stue_leselampe on 70% 2700K" },
			Recorded(room.Actuator).ToArray(),
			"a turn-on at nothing is carried out as a turn-off, which an on-expectation would not match");

		Assert.IsFalse(
			room.Actuator.Applied.Single(call => call.EntityId == First).Command.On,
			"and the expectation declared for it is an off, because the command is one");
	}

	[TestMethod]
	public void A_Bulb_Reached_Through_Both_Its_Group_And_Its_Own_Entry_Is_Commanded_Once()
	{
		Fixture room = Build(
			lightLevels: [Pins(First, "evening", brightness: 26)],
			lights: [Group, First, Lamp]);

		room.Ha.Trigger(Motion, "on");

		CollectionAssert.AreEqual(
			new[] { "light.stue_tak_1 on 10.2% 2700K", "light.stue_tak_2 on 70% 2700K", "light.stue_leselampe on 70% 2700K" },
			Recorded(room.Actuator).ToArray(),
			"the group claims both bulbs, so the entry naming one of them again commands nothing");
	}

	[TestMethod]
	public void A_Light_Keeps_Its_Own_Warmth_And_Takes_The_Rooms_Brightness()
	{
		Fixture room = Build(lightLevels: [Pins(Lamp, "evening", kelvin: 2000)]);

		room.Ha.Trigger(Motion, "on");

		CollectionAssert.AreEqual(
			new[] { "light.stue_taklys on 70% 2700K", "light.stue_leselampe on 70% 2000K" },
			Recorded(room.Actuator).ToArray(),
			"warmth travels alone; the lamp is a stand-alone entry, so it is still one call");
	}

	[TestMethod]
	public void The_Tick_Reapplies_When_Only_A_Lights_Own_Level_Has_Moved()
	{
		// The room states one level for both periods, so its own target does not move across 22:30 at all. Only
		// the pinned light's does.
		Fixture room = Build(
			roomLevels:
			[
				new RoomLevelOverride { PeriodId = "evening", Brightness = 179 },
				new RoomLevelOverride { PeriodId = "night", Brightness = 179 }
			],
			lightLevels:
			[
				new LightLevelOverride
				{
					EntityId = First,
					Levels =
					[
						new RoomLevelOverride { PeriodId = "evening", Brightness = 128 },
						new RoomLevelOverride { PeriodId = "night", Brightness = 26 }
					]
				}
			]);

		room.Ha.Trigger(Motion, "on");
		room.Actuator.Clear();

		Advance(room, TimeSpan.FromHours(2) + TimeSpan.FromMinutes(31));

		CollectionAssert.AreEqual(
			new[] { "light.stue_tak_1 on 10.2% 2200K", "light.stue_tak_2 on 70.2% 2200K", "light.stue_leselampe on 70.2% 2200K" },
			Recorded(room.Actuator).ToArray(),
			"the room's brightness is pinned across the boundary, so nothing but the light's own level moved — and "
			+ "the tick must still re-apply");
	}

	[TestMethod]
	public void The_Warning_Dim_Reaches_A_Light_On_Its_Own_Level_Too()
	{
		Fixture room = Build(lightLevels: [Pins(First, "evening", brightness: 128)]);

		room.Ha.Trigger(Motion, "on");
		Advance(room, TimeSpan.FromHours(3));

		Assert.AreEqual(AreaState.PreOff, room.Area.State);

		CollectionAssert.AreEqual(
			new[] { "light.stue_tak_1 on 7.5% 2200K", "light.stue_tak_2 on 7.5% 2200K", "light.stue_leselampe on 7.5% 2200K" },
			Recorded(room.Actuator).TakeLast(3).ToArray(),
			"the dim factor is the room's and applies to every light, its own level included");
	}

	[TestMethod]
	public void Going_Off_Is_One_Off_To_Every_Entry_However_Many_Lights_State_Levels()
	{
		Fixture room = Build(lightLevels: [Pins(First, "evening", brightness: 128)]);

		room.Ha.Trigger(Motion, "on");
		Advance(room, TimeSpan.FromHours(3) + TimeSpan.FromSeconds(30));

		Assert.AreEqual(AreaState.AutoVacant, room.Area.State);

		CollectionAssert.AreEqual(
			new[] { "light.stue_taklys off", "light.stue_leselampe off" },
			Recorded(room.Actuator).TakeLast(2).ToArray(),
			"off is unchanged: one off to every entry, and a group switches off what is under it");
	}

	[TestMethod]
	public void A_Level_Test_Puts_Each_Lights_Own_Level_On_The_Real_Lights()
	{
		Fixture room = Build(lightLevels: [Pins(First, "night", brightness: 26)]);

		Assert.IsNull(room.Area.TestPeriod("night"));

		CollectionAssert.AreEqual(
			new[] { "light.stue_tak_1 on 10.2% 2200K", "light.stue_tak_2 on 15% 2200K", "light.stue_leselampe on 15% 2200K" },
			Recorded(room.Actuator).ToArray(),
			"the ten-second test shows the room as it will really be lit, not every lamp at the room's level");
	}

	// ===================== the merge rule =====================

	[TestMethod]
	public void A_Light_Stating_A_Brightness_Owns_Its_Own_Curve_Flag()
	{
		IReadOnlyList<RoomLevelOverride> merged = LightLevelMerge.MergeOnto(
			[new RoomLevelOverride { PeriodId = "evening", BrightnessPct = 70, FollowDaylightCurve = true }],
			[new RoomLevelOverride { PeriodId = "evening", BrightnessPct = 30 }]);

		RoomLevelOverride row = merged.Single();

		Assert.AreEqual(30d, row.BrightnessPct);
		Assert.IsNull(row.FollowDaylightCurve,
			"a lamp pinned to 30 % under a room that follows the curve must not be silently overruled by the curve");
	}

	[TestMethod]
	public void A_Light_Stating_Only_The_Curve_Takes_It_Up_Under_A_Pinned_Room()
	{
		IReadOnlyList<RoomLevelOverride> merged = LightLevelMerge.MergeOnto(
			[new RoomLevelOverride { PeriodId = "evening", BrightnessPct = 30 }],
			[new RoomLevelOverride { PeriodId = "evening", FollowDaylightCurve = true }]);

		RoomLevelOverride row = merged.Single();

		Assert.IsTrue(row.FollowDaylightCurve);
		Assert.AreEqual(30d, row.BrightnessPct, "the curve owns the number, and the room's is what it starts from");
	}

	[TestMethod]
	public void A_Light_Stating_Only_Warmth_Inherits_The_Rooms_Brightness_And_Curve()
	{
		IReadOnlyList<RoomLevelOverride> merged = LightLevelMerge.MergeOnto(
			[new RoomLevelOverride { PeriodId = "evening", BrightnessPct = 70, FollowDaylightCurve = true }],
			[new RoomLevelOverride { PeriodId = "evening", ColorTempKelvin = 2000 }]);

		RoomLevelOverride row = merged.Single();

		Assert.AreEqual(70d, row.BrightnessPct);
		Assert.IsTrue(row.FollowDaylightCurve, "brightness and the curve flag travel together");
		Assert.AreEqual(2000, row.ColorTempKelvin, "warmth travels alone");
	}

	[TestMethod]
	public void A_Light_Stating_Nothing_For_A_Period_Runs_The_Rooms_Row_Unchanged()
	{
		IReadOnlyList<RoomLevelOverride> merged = LightLevelMerge.MergeOnto(
			[new RoomLevelOverride { PeriodId = "night", BrightnessPct = 15, ColorTempKelvin = 2200 }],
			[new RoomLevelOverride { PeriodId = "evening", BrightnessPct = 30 }]);

		RoomLevelOverride night = merged.Single(row => row.PeriodId == "night");

		Assert.AreEqual(15d, night.BrightnessPct);
		Assert.AreEqual(2200, night.ColorTempKelvin);
	}

	[TestMethod]
	public void With_No_Room_Row_A_Lights_Own_Values_Stand_Alone()
	{
		IReadOnlyList<RoomLevelOverride> merged = LightLevelMerge.MergeOnto(
			null,
			[new RoomLevelOverride { PeriodId = "evening", BrightnessPct = 30, ColorTempKelvin = 2000 }]);

		RoomLevelOverride row = merged.Single();

		Assert.AreEqual(30d, row.BrightnessPct);
		Assert.AreEqual(2000, row.ColorTempKelvin);
		Assert.IsNull(row.FollowDaylightCurve);
	}

	[TestMethod]
	public void A_Light_Stating_Nothing_At_All_Leaves_The_Room_Exactly_As_It_Was()
	{
		IReadOnlyList<RoomLevelOverride> rooms =
			[new RoomLevelOverride { PeriodId = "evening", BrightnessPct = 70, ColorTempKelvin = 2700, FollowDaylightCurve = true }];

		IReadOnlyList<RoomLevelOverride> merged = LightLevelMerge.MergeOnto(rooms, []);

		RoomLevelOverride row = merged.Single();

		Assert.AreEqual(70d, row.BrightnessPct);
		Assert.AreEqual(2700, row.ColorTempKelvin);
		Assert.IsTrue(row.FollowDaylightCurve);
	}

	[TestMethod]
	public void An_Empty_Row_Never_Shadows_A_Later_Row_That_Says_Something()
	{
		IReadOnlyList<RoomLevelOverride> merged = LightLevelMerge.MergeOnto(
			null,
			[
				new RoomLevelOverride { PeriodId = "evening" },
				new RoomLevelOverride { PeriodId = "evening", BrightnessPct = 30 }
			]);

		Assert.AreEqual(30d, merged.Single().BrightnessPct, "the calculator skips empty rows and so must the merge");
	}

	[TestMethod]
	public void The_First_Row_Wins_When_One_Light_Names_A_Period_Twice()
	{
		IReadOnlyList<RoomLevelOverride> merged = LightLevelMerge.MergeOnto(
			null,
			[
				new RoomLevelOverride { PeriodId = "evening", BrightnessPct = 30 },
				new RoomLevelOverride { PeriodId = "evening", BrightnessPct = 90 }
			]);

		Assert.AreEqual(30d, merged.Single().BrightnessPct, "matching the calculator and what the validator reports");
	}
}
