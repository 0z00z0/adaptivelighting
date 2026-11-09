using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Ha;
using AdaptiveLighting.Tests.Common;
using AdaptiveLighting.Web.Presentation;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The level the room page shows beside Test: what Test sends, whether the lights already show it, and nothing when refused.</summary>
[TestClass]
public sealed class LevelBesideTestTests
{
	private const string Outdoor = "sensor.outdoor_lux";
	private const string Group = "light.ceiling";
	private const string First = "light.ceiling_1";
	private const string Second = "light.ceiling_2";
	private const string Lamp = "light.reading";

	[TestMethod]
	public void The_Level_Shown_Is_The_Command_Test_Sends()
	{
		AssertTestSendsWhatIsShown(
			new AreaTestBuilder().RoomLevels([new RoomLevelOverride { PeriodId = "day", BrightnessPct = 25 }]).Build(),
			"a set level");

		AreaFixture curve = CurveRoom();
		LightCommand dark = AssertTestSendsWhatIsShown(curve, "the curve at 5 lx");
		curve.Ha.SetState(Outdoor, "20000");
		LightCommand bright = AssertTestSendsWhatIsShown(curve, "the curve at 20000 lx");
		Assert.AreNotEqual(dark.BrightnessPct, bright.BrightnessPct, "the two readings must move the curve, or this proves nothing");

		AreaFixture perLight = PerLightRoom();
		AssertTestSendsWhatIsShown(perLight, "per-light levels");
		LevelTestLevels shown = perLight.Area.TestLevels(["day"])!["day"];
		Assert.AreEqual(40d, shown.Lights[Lamp].Command.BrightnessPct, "the lamp's own level");
		Assert.AreEqual(90d, shown.Lights[First].Command.BrightnessPct, "a light stating nothing takes the room's");

		foreach (string light in new[] { Lamp, First })
		{
			perLight.Actuator.Clear();
			Assert.IsNull(perLight.Area.TestLight(light, "day"));
			AssertSameLevel(shown.Lights[light].Command, perLight.Actuator.Applied.Single().Command, light);
			perLight.Area.EndTest();
		}

		AreaFixture noKelvin = new AreaTestBuilder().Shape(area => area with { LightsSupportColorTemp = false }).Build();
		LightCommand plain = AssertTestSendsWhatIsShown(noKelvin, "a room that cannot change colour temperature");
		Assert.IsNull(plain.ColorTempKelvin);
		Assert.AreEqual("→ 90 %", TestLevelText.Of(plain, shown: false));
	}

	[TestMethod]
	public void Now_Is_Said_Only_Within_The_Actuators_Tolerance()
	{
		// The day period is 90 % and 4500 K; raw 227 is 89.0 %, raw 217 is 85.1 %.
		(int Raw, int Kelvin, bool Shown)[] cases = [(227, 4500, true), (227, 4540, true), (217, 4500, false), (227, 4600, false)];

		foreach ((int raw, int kelvin, bool expected) in cases)
		{
			AreaFixture room = new AreaTestBuilder().Build();
			room.Ha.SetState(AreaTestBuilder.Light, "on", new Dictionary<string, object> { ["brightness"] = raw, ["color_temp_kelvin"] = kelvin });

			LevelTestLevels shown = room.Area.TestLevels(["day"])!["day"];
			string at = $"raw {raw}, {kelvin} K";

			Assert.AreEqual(expected, shown.AllShown, at);
			Assert.AreEqual(expected, TestLevelText.Of(shown.Room, shown.AllShown).EndsWith(" = now", StringComparison.Ordinal), at);

			// The control: the real actuator skips the command exactly when the page says the lights already show it.
			ActuatorOutcome outcome = new HaLightActuator(room.Ha, NullLogger.Instance).Apply(AreaTestBuilder.Light, shown.Room);
			Assert.AreEqual(expected, !outcome.Sent, at);
		}
	}

	[TestMethod]
	public void A_Refused_Room_Shows_No_Level()
	{
		AreaFixture room = new AreaTestBuilder().Build();
		Assert.IsNotNull(room.Area.TestLevels(["day"]), "the control: an ordinary house shows a level");

		room.House.OnNext(new HouseState(true, ModeKind.Normal, KillSwitchActive: true));
		Assert.IsNull(room.Area.TestLevels(["day"]), "master switch");

		room.House.OnNext(new HouseState(true, ModeKind.Away, false) { ModeValue = "Away" });
		Assert.IsNull(room.Area.TestLevels(["day"]), "away");
	}

	private static LightCommand AssertTestSendsWhatIsShown(AreaFixture room, string what)
	{
		LevelTestLevels shown = room.Area.TestLevels(["day"])!["day"];

		room.Actuator.Clear();
		Assert.IsNull(room.Area.TestPeriod("day"), what);
		Assert.IsTrue(room.Actuator.Applied.Count > 0, what);

		// A group whose lights state nothing is sent the room's command as a whole; the page shows it on each light under it.
		foreach ((string entity, LightCommand sent) in room.Actuator.Applied)
			AssertSameLevel(shown.Lights.TryGetValue(entity, out LightLevelNow? light) ? light.Command : shown.Room, sent, $"{what}: {entity}");

		HashSet<string> sentAlone = [.. room.Actuator.Applied.Select(applied => applied.EntityId)];
		foreach ((string entity, LightLevelNow light) in shown.Lights.Where(pair => !sentAlone.Contains(pair.Key)))
			AssertSameLevel(shown.Room, light.Command, $"{what}: {entity} under a group");

		room.Area.EndTest();
		return shown.Room;
	}

	// The fade length is the darkness verdict's, not part of the level a person reads.
	private static void AssertSameLevel(LightCommand expected, LightCommand actual, string what)
	{
		Assert.AreEqual(expected.On, actual.On, what);
		Assert.AreEqual(expected.BrightnessPct, actual.BrightnessPct, what);
		Assert.AreEqual(expected.ColorTempKelvin, actual.ColorTempKelvin, what);
		Assert.AreEqual(expected.EqualChannels, actual.EqualChannels, what);
	}

	private static AreaFixture CurveRoom() =>
		new AreaTestBuilder()
			.States(ha =>
			{
				ha.SetState(AreaTestBuilder.Motion, "off");
				ha.SetState(AreaTestBuilder.Light, "off");
				ha.SetState(AreaTestBuilder.Lux, "5");
				ha.SetState(Outdoor, "5");
			})
			.Settings(settings =>
			{
				settings.LuxBrightnessStartLux = 100;
				settings.LuxBrightnessFullLux = 10000;
				settings.LuxBrightnessMinPct = 20;
				settings.LuxBrightnessMaxPct = 100;
			})
			.Global(global => global.OutdoorLuxSensor = Outdoor)
			.RoomLevels([new RoomLevelOverride { PeriodId = "day", FollowDaylightCurve = true }])
			.Build();

	// A group of two bulbs and one lamp that states its own day level.
	private static AreaFixture PerLightRoom()
	{
		Dictionary<string, IReadOnlySet<string>> leaves = new(StringComparer.Ordinal)
		{
			[Group] = new HashSet<string>(StringComparer.Ordinal) { First, Second },
			[Lamp] = new HashSet<string>(StringComparer.Ordinal) { Lamp }
		};

		return new AreaTestBuilder()
			.States(ha =>
			{
				ha.SetState(AreaTestBuilder.Motion, "off");
				ha.SetState(AreaTestBuilder.Lux, "5");
				ha.SetState(Group, "off", new Dictionary<string, object> { ["entity_id"] = new[] { First, Second } });
				ha.SetState(First, "off");
				ha.SetState(Second, "off");
				ha.SetState(Lamp, "off");
			})
			.Lights([Group, Lamp])
			.Shape(area => area with { LeavesOfEntry = leaves })
			.LightLevels([new LightLevelOverride { EntityId = Lamp, Levels = [new RoomLevelOverride { PeriodId = "day", BrightnessPct = 40 }] }])
			.Build();
	}
}
