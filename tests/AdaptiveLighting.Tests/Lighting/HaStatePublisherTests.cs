using System.Text.Json;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Ha;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The wire contract between <see cref="HaStatePublisher"/> and <see cref="AreaSnapshotEvent"/>: every field has to survive the round trip.</summary>
[TestClass]
public sealed class HaStatePublisherTests
{
	[TestMethod]
	public void HouseModeValue_Survives_The_Serialize_Event_ToSnapshot_Round_Trip()
	{
		var ha = new FakeHaContext();
		var publisher = new HaStatePublisher(ha, NullLogger.Instance);

		var snapshot = new AreaSnapshot(
			"Stue", AreaState.AutoActive, TransitionReason.Motion, HouseMode.Sleep,
			KillSwitchActive: false, IsDark: true, PeriodName: "evening", BrightnessPct: 70, ColorTempKelvin: 2700,
			Timestamp: DateTimeOffset.UnixEpoch, LastCommandAt: null, LastMotionAt: null, NextChangeAt: null,
			NextChangeFrom: null, HouseModeValue: "Sover");

		publisher.Publish(snapshot);

		var (type, data) = ha.SentEvents.Single();
		Assert.AreEqual(HaStatePublisher.EventType, type);

		// Round-trip through JSON the way NetDaemon's Event<T>.Data does: serialize the published payload,
		// then bind it into the web-side event record and rebuild the snapshot.
		var json = JsonSerializer.Serialize(data);
		var wire = JsonSerializer.Deserialize<AreaSnapshotEvent>(json);
		Assert.IsNotNull(wire);
		Assert.AreEqual("Sover", wire!.HouseModeValue, "house_mode_value survives serialisation into the event");

		var rebuilt = wire.ToSnapshot();
		Assert.IsNotNull(rebuilt);
		Assert.AreEqual("Sover", rebuilt!.HouseModeValue, "…and back out into the snapshot the card reads");
	}

	[TestMethod]
	public void A_Null_HouseModeValue_Round_Trips_As_Null()
	{
		var ha = new FakeHaContext();
		var publisher = new HaStatePublisher(ha, NullLogger.Instance);

		var snapshot = new AreaSnapshot(
			"Stue", AreaState.AutoVacant, TransitionReason.Startup, HouseMode.Home,
			KillSwitchActive: false, IsDark: false, PeriodName: "day", BrightnessPct: null, ColorTempKelvin: null,
			Timestamp: DateTimeOffset.UnixEpoch, LastCommandAt: null, LastMotionAt: null, NextChangeAt: null,
			NextChangeFrom: null, HouseModeValue: null);

		publisher.Publish(snapshot);

		var json = JsonSerializer.Serialize(ha.SentEvents.Single().Data);
		var rebuilt = JsonSerializer.Deserialize<AreaSnapshotEvent>(json)!.ToSnapshot();

		Assert.IsNotNull(rebuilt);
		Assert.IsNull(rebuilt!.HouseModeValue, "no select configured → the raw value stays null through the round trip");
	}

	// The area id is the stable join between live state and the document. Names are editable mid-session.
	[TestMethod]
	public void AreaId_Survives_The_Serialize_Event_ToSnapshot_Round_Trip()
	{
		FakeHaContext ha = new();
		HaStatePublisher publisher = new(ha, NullLogger.Instance);

		AreaSnapshot snapshot = new(
			"Living room", AreaState.AutoActive, TransitionReason.Motion, HouseMode.Home,
			KillSwitchActive: false, IsDark: true, PeriodName: "evening", BrightnessPct: 70, ColorTempKelvin: 2700,
			Timestamp: DateTimeOffset.UnixEpoch, LastCommandAt: null, LastMotionAt: null, NextChangeAt: null,
			NextChangeFrom: null, HouseModeValue: null, DarknessDetail: null, AreaId: "stue");

		publisher.Publish(snapshot);

		string json = JsonSerializer.Serialize(ha.SentEvents.Single().Data);

		StringAssert.Contains(json, "\"area_id\":\"stue\"", "the id goes on the wire beside the display name");

		AreaSnapshot? rebuilt = JsonSerializer.Deserialize<AreaSnapshotEvent>(json)!.ToSnapshot();

		Assert.IsNotNull(rebuilt);
		Assert.AreEqual("stue", rebuilt!.AreaId);
		Assert.AreEqual("Living room", rebuilt.AreaName, "and the name it is joined to is unchanged");
	}

	// The field is additive: an event from a build that predates it still has to rebuild.
	[TestMethod]
	public void An_Event_From_A_Build_Without_An_Area_Id_Still_Yields_A_Snapshot()
	{
		const string OldEvent =
			"""
			{
			  "area": "Living room",
			  "state": "AutoActive",
			  "reason": "Motion",
			  "mode": "Home",
			  "kill_switch_active": false,
			  "is_dark": true,
			  "period": "evening",
			  "brightness_pct": 70,
			  "color_temp_kelvin": 2700,
			  "timestamp": "1970-01-01T00:00:00+00:00"
			}
			""";

		AreaSnapshot? rebuilt = JsonSerializer.Deserialize<AreaSnapshotEvent>(OldEvent)!.ToSnapshot();

		Assert.IsNotNull(rebuilt);
		Assert.AreEqual("Living room", rebuilt!.AreaName);
		Assert.AreEqual(AreaState.AutoActive, rebuilt.State);
		Assert.IsNull(rebuilt.AreaId, "absent means null, not a broken payload");
	}

	[TestMethod]
	public void The_Auto_On_Gate_Survives_The_Serialize_Event_ToSnapshot_Round_Trip()
	{
		FakeHaContext ha = new();
		HaStatePublisher publisher = new(ha, NullLogger.Instance);

		AreaSnapshot snapshot = new(
			"Stue", AreaState.AutoVacant, TransitionReason.CircadianTick, HouseMode.Home,
			KillSwitchActive: false, IsDark: true, PeriodName: "evening", BrightnessPct: null, ColorTempKelvin: null,
			Timestamp: DateTimeOffset.UnixEpoch, LastCommandAt: null, LastMotionAt: null, NextChangeAt: null,
			NextChangeFrom: null, HouseModeValue: null, DarknessDetail: "lux 12, dark below 40", AreaId: "stue",
			AutoOnBlockedBy: AutoOnBlock.EntityOn, AutoOnBlockingEntity: "media_player.stue_tv");

		publisher.Publish(snapshot);

		string json = JsonSerializer.Serialize(ha.SentEvents.Single().Data);
		AreaSnapshot? rebuilt = JsonSerializer.Deserialize<AreaSnapshotEvent>(json)!.ToSnapshot();

		Assert.IsNotNull(rebuilt);
		Assert.AreEqual(AutoOnBlock.EntityOn, rebuilt!.AutoOnBlockedBy);
		Assert.AreEqual("media_player.stue_tv", rebuilt.AutoOnBlockingEntity);
	}

	// Null verdict means the report cannot say. The zero value would claim nothing was blocking.
	[TestMethod]
	public void An_Event_From_A_Build_Without_The_Auto_On_Gate_Rebuilds_With_No_Verdict()
	{
		const string OldEvent =
			"""
			{
			  "area": "Stue",
			  "state": "AutoVacant",
			  "reason": "CircadianTick",
			  "mode": "Home",
			  "kill_switch_active": false,
			  "is_dark": true,
			  "timestamp": "1970-01-01T00:00:00+00:00"
			}
			""";

		AreaSnapshot? rebuilt = JsonSerializer.Deserialize<AreaSnapshotEvent>(OldEvent)!.ToSnapshot();

		Assert.IsNotNull(rebuilt);
		Assert.IsNull(rebuilt!.AutoOnBlockedBy, "absent means the report cannot say, not that nothing blocked");
		Assert.IsNull(rebuilt.AutoOnBlockingEntity);
	}

	[TestMethod]
	public void The_Room_Scene_Survives_The_Serialize_Event_ToSnapshot_Round_Trip()
	{
		FakeHaContext ha = new();
		HaStatePublisher publisher = new(ha, NullLogger.Instance);

		AreaSnapshot snapshot = new(
			"Stue", AreaState.AutoVacant, TransitionReason.VacancyTimeout, HouseMode.Home,
			KillSwitchActive: false, IsDark: true, PeriodName: "evening", BrightnessPct: null, ColorTempKelvin: null,
			Timestamp: DateTimeOffset.UnixEpoch, LastCommandAt: null, LastMotionAt: null, NextChangeAt: null,
			NextChangeFrom: null, AreaId: "stue", SceneApplied: "scene.stue_natt");

		publisher.Publish(snapshot);

		string json = JsonSerializer.Serialize(ha.SentEvents.Single().Data);
		AreaSnapshot? rebuilt = JsonSerializer.Deserialize<AreaSnapshotEvent>(json)!.ToSnapshot();

		Assert.IsNotNull(rebuilt);
		Assert.AreEqual("scene.stue_natt", rebuilt!.SceneApplied);
	}

	// The pair a page reads to redraw the countdown after a reload or a navigate-back — see Room.razor's
	// TestingPeriod. Without this round trip, only the page that clicked Test ever draws it.
	[TestMethod]
	public void The_Running_Test_Survives_The_Serialize_Event_ToSnapshot_Round_Trip()
	{
		FakeHaContext ha = new();
		HaStatePublisher publisher = new(ha, NullLogger.Instance);
		DateTimeOffset ends = DateTimeOffset.UnixEpoch.AddSeconds(10);

		AreaSnapshot snapshot = new(
			"Stue", AreaState.AutoVacant, TransitionReason.LevelTestStarted, HouseMode.Home,
			KillSwitchActive: false, IsDark: true, PeriodName: "evening", BrightnessPct: null, ColorTempKelvin: null,
			Timestamp: DateTimeOffset.UnixEpoch, LastCommandAt: null, LastMotionAt: null, NextChangeAt: null,
			NextChangeFrom: null, AreaId: "stue", TestingPeriodId: "day", TestEndsAt: ends);

		publisher.Publish(snapshot);

		string json = JsonSerializer.Serialize(ha.SentEvents.Single().Data);
		AreaSnapshot? rebuilt = JsonSerializer.Deserialize<AreaSnapshotEvent>(json)!.ToSnapshot();

		Assert.IsNotNull(rebuilt);
		Assert.AreEqual("day", rebuilt!.TestingPeriodId);
		Assert.AreEqual(ends, rebuilt.TestEndsAt);
	}

	// The field is additive: an event from a build that predates it still has to rebuild, with no test reported.
	[TestMethod]
	public void An_Event_From_A_Build_Without_A_Running_Test_Rebuilds_With_None_Reported()
	{
		const string OldEvent =
			"""
			{
			  "area": "Stue",
			  "state": "AutoVacant",
			  "reason": "CircadianTick",
			  "mode": "Home",
			  "kill_switch_active": false,
			  "is_dark": true,
			  "timestamp": "1970-01-01T00:00:00+00:00"
			}
			""";

		AreaSnapshot? rebuilt = JsonSerializer.Deserialize<AreaSnapshotEvent>(OldEvent)!.ToSnapshot();

		Assert.IsNotNull(rebuilt);
		Assert.IsNull(rebuilt!.TestingPeriodId);
		Assert.IsNull(rebuilt.TestEndsAt);
	}

	[TestMethod]
	public void IsAnyoneHome_And_Forced_Survive_The_Serialize_Event_ToSnapshot_Round_Trip()
	{
		FakeHaContext ha = new();
		HaStatePublisher publisher = new(ha, NullLogger.Instance);

		AreaSnapshot snapshot = new(
			"Stue", AreaState.SceneHold, TransitionReason.HouseModeChanged, HouseMode.Away,
			KillSwitchActive: false, IsDark: true, PeriodName: "evening", BrightnessPct: null, ColorTempKelvin: null,
			Timestamp: DateTimeOffset.UnixEpoch, LastCommandAt: null, LastMotionAt: null, NextChangeAt: null,
			NextChangeFrom: null, AreaId: "stue", IsAnyoneHome: false,
			Forced: new ForcedMode(ModeKind.Away, "Away", ModeForceSource.WhileEntityOn, "input_boolean.cabin", "on"));

		publisher.Publish(snapshot);

		string json = JsonSerializer.Serialize(ha.SentEvents.Single().Data);
		AreaSnapshotEvent? wire = JsonSerializer.Deserialize<AreaSnapshotEvent>(json);
		Assert.IsNotNull(wire);
		Assert.AreEqual(false, wire!.IsAnyoneHome);
		Assert.AreEqual("Away", wire.ModeForcedKind);
		Assert.AreEqual("Away", wire.ModeForcedOption);
		Assert.AreEqual("WhileEntityOn", wire.ModeForcedSource);
		Assert.AreEqual("input_boolean.cabin", wire.ModeForcedBy);
		Assert.AreEqual("on", wire.ModeForcedByState);

		AreaSnapshot? rebuilt = wire.ToSnapshot();
		Assert.IsNotNull(rebuilt);
		Assert.AreEqual(false, rebuilt!.IsAnyoneHome);
		Assert.AreEqual(new ForcedMode(ModeKind.Away, "Away", ModeForceSource.WhileEntityOn, "input_boolean.cabin", "on"), rebuilt.Forced);
	}

	// Neither field existed in an older build, so both have to come back null rather than a misleading default.
	[TestMethod]
	public void An_Event_From_A_Build_Without_Presence_Or_Forced_Mode_Rebuilds_With_Neither()
	{
		const string OldEvent =
			"""
			{
			  "area": "Stue",
			  "state": "AutoVacant",
			  "reason": "CircadianTick",
			  "mode": "Home",
			  "kill_switch_active": false,
			  "is_dark": true,
			  "timestamp": "1970-01-01T00:00:00+00:00"
			}
			""";

		AreaSnapshot? rebuilt = JsonSerializer.Deserialize<AreaSnapshotEvent>(OldEvent)!.ToSnapshot();

		Assert.IsNotNull(rebuilt);
		Assert.IsNull(rebuilt!.IsAnyoneHome);
		Assert.IsNull(rebuilt.Forced);
	}

	[TestMethod]
	public void An_Area_With_No_Registry_Area_Publishes_A_Null_Id()
	{
		FakeHaContext ha = new();
		new HaStatePublisher(ha, NullLogger.Instance).Publish(new AreaSnapshot(
			"Hand-built", AreaState.AutoVacant, TransitionReason.Startup, HouseMode.Home,
			KillSwitchActive: false, IsDark: null, PeriodName: null, BrightnessPct: null, ColorTempKelvin: null,
			Timestamp: DateTimeOffset.UnixEpoch, LastCommandAt: null, LastMotionAt: null, NextChangeAt: null,
			NextChangeFrom: null));

		string json = JsonSerializer.Serialize(ha.SentEvents.Single().Data);

		Assert.IsNull(JsonSerializer.Deserialize<AreaSnapshotEvent>(json)!.ToSnapshot()!.AreaId);
	}
}
