using System.Reactive.Subjects;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void Every_Transition_Is_Published()
	{
		var t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Motion, "off");
		Advance(t, TimeSpan.FromMinutes(10));
		Advance(t, TimeSpan.FromSeconds(30));

		var states = t.Publisher.Snapshots.Select(s => s.State).ToList();

		Assert.IsTrue(t.Publisher.Snapshots.Any(s => s.Reason == TransitionReason.Startup));
		CollectionAssert.Contains(states, AreaState.AutoActive);
		CollectionAssert.Contains(states, AreaState.PreOff);
		CollectionAssert.Contains(states, AreaState.AutoVacant);
		Assert.IsTrue(t.Publisher.Snapshots.Any(s => s.PeriodName == "evening"), "a snapshot names the period it acted under");
		Assert.AreEqual(2026, t.Publisher.Snapshots[^1].Timestamp.Year, "the snapshot clock is the scheduler, not the wall");
	}

	[TestMethod]
	public void The_Startup_Snapshot_Claims_Only_What_It_Evaluated()
	{
		var dark = Build();
		var opening = dark.Publisher.Snapshots.Single();

		Assert.AreEqual(TransitionReason.Startup, opening.Reason);
		Assert.AreEqual(true, opening.IsDark, "lux 5 is dark and the startup snapshot must have looked");
		Assert.AreEqual("evening", opening.PeriodName, "20:00 is inside the evening period whether or not anything was commanded");
		Assert.IsNull(opening.BrightnessPct);
		Assert.IsNull(opening.LastCommandAt, "no command has been sent, which is not the same as 'lights off'");
		Assert.IsNull(opening.LastMotionAt);
		Assert.IsNull(opening.NextChangeAt);
	}

	[TestMethod]
	public void The_Startup_Snapshot_Reads_The_Actual_Sensor_Not_A_Default()
	{
		var scheduler = new TestScheduler();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		var ha = new FakeHaContext();
		ha.SetState(Motion, "off");
		ha.SetState(Light, "off");
		ha.SetState(Lux, "5000");

		var settings = new AreaSettings { Darkness = DarknessSource.Lux };
		var global = new GlobalConfig { SmoothTransitions = false, CircadianTickSeconds = 60 };
		var periods = new List<TimePeriodConfig>
		{
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 }
		};

		var publisher = new FakeStatePublisher();
		var controller = new AreaController(
			ha, scheduler, new ResolvedArea("Test", settings, [Light], [Motion], [Lux], []), global, periods,
			new CircadianCalculator(periods, global, () => SunTimes.Unknown, zone: TimeZoneInfo.Utc),
			new FakeLightActuator(), publisher, new BehaviorSubject<HouseState>(HouseState.Initial),
			NullLoggerFactory.Instance);

		controller.Start();

		Assert.AreEqual(false, publisher.Snapshots.Single().IsDark,
			"5000 lux is not dark, and the opening snapshot must say so rather than echo a default");
	}

	/// <summary>Following the house's outdoor lux sensor is opt-in per room.</summary>
	/// <remarks>Asserted on a bright reading: a dark one cannot tell the two rules apart, since a room with no reading counts as dark.</remarks>
	[TestMethod]
	public void A_Room_That_Follows_The_Outdoor_Sensor_Is_Gated_By_It()
	{
		AreaSnapshot opted = SensorlessRoom(outdoorLux: "5000", followOutdoorLux: true);

		Assert.AreEqual(false, opted.IsDark,
			"the room asked to follow the outdoor sensor, and outdoors it is broad daylight");
	}

	[TestMethod]
	public void A_Room_That_Did_Not_Ask_Ignores_The_Outdoor_Sensor_And_Counts_As_Dark()
	{
		AreaSnapshot silent = SensorlessRoom(outdoorLux: "5000", followOutdoorLux: false);

		Assert.AreEqual(true, silent.IsDark,
			"no lux sensor and no opt-in means no reading, and a gate with nothing to read refuses nothing");
	}

	/// <summary>Starts a room that resolved no lux sensor of its own and hands back its opening report.</summary>
	private static AreaSnapshot SensorlessRoom(string outdoorLux, bool followOutdoorLux)
	{
		var scheduler = new TestScheduler();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		const string Outdoor = "sensor.ute_lux";
		var ha = new FakeHaContext();
		ha.SetState(Motion, "off");
		ha.SetState(Light, "off");
		ha.SetState(Outdoor, outdoorLux);

		var settings = new AreaSettings { Darkness = DarknessSource.Lux, LuxThreshold = 1000 };
		var global = new GlobalConfig { SmoothTransitions = false, CircadianTickSeconds = 60, OutdoorLuxSensor = Outdoor };
		var periods = new List<TimePeriodConfig>
		{
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 }
		};

		var publisher = new FakeStatePublisher();
		var controller = new AreaController(
			ha, scheduler,
			new ResolvedArea("Test", settings, [Light], [Motion], [], [], followOutdoorLux),
			global, periods,
			new CircadianCalculator(periods, global, () => SunTimes.Unknown, zone: TimeZoneInfo.Utc),
			new FakeLightActuator(), publisher, new BehaviorSubject<HouseState>(HouseState.Initial),
			NullLoggerFactory.Instance);

		controller.Start();
		return publisher.Snapshots.Single();
	}
}
