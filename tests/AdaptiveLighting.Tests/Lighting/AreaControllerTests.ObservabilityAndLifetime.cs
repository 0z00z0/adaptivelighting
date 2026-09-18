using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Tests.Common;

using Fixture = AdaptiveLighting.Tests.Common.AreaFixture;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void Every_Transition_Is_Published()
	{
		Fixture t = Build();
		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Motion, "off");
		Advance(t, TimeSpan.FromMinutes(10));
		Advance(t, TimeSpan.FromSeconds(30));

		List<AreaState> states = t.Publisher.Snapshots.Select(s => s.State).ToList();

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
		Fixture dark = Build();
		AreaSnapshot opening = dark.Publisher.Snapshots.Single();

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
		Fixture t = new AreaTestBuilder()
			.States(ha =>
			{
				ha.SetState(Motion, "off");
				ha.SetState(Light, "off");
				ha.SetState(Lux, "5000");
			})
			.ShippedSettings()
			.Settings(settings => settings.Darkness = DarknessSource.Lux)
			.Periods(new List<TimePeriodConfig>
			{
				new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 }
			})
			.Named("Test", areaId: null)
			.OpeningHouse(null)
			.Build();

		Assert.AreEqual(false, t.Publisher.Snapshots.Single().IsDark,
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
		const string Outdoor = "sensor.ute_lux";

		Fixture t = new AreaTestBuilder()
			.States(ha =>
			{
				ha.SetState(Motion, "off");
				ha.SetState(Light, "off");
				ha.SetState(Outdoor, outdoorLux);
			})
			.ShippedSettings()
			.Settings(settings =>
			{
				settings.Darkness = DarknessSource.Lux;
				settings.LuxThreshold = 1000;
			})
			.Global(global => global.OutdoorLuxSensor = Outdoor)
			.Periods(new List<TimePeriodConfig>
			{
				new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 }
			})
			.Named("Test", areaId: null)
			.LuxSensors([])
			.FollowOutdoorLux(followOutdoorLux)
			.OpeningHouse(null)
			.Build();

		return t.Publisher.Snapshots.Single();
	}
}
