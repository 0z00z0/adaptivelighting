using System.Text.Json;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Ha;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The wire contract between <see cref="HaStatePublisher"/> and <see cref="ZoneSnapshotEvent"/>: the raw
///     house-mode string must survive serialise → HA event → <see cref="ZoneSnapshotEvent.ToSnapshot"/> so the
///     dashboard can show "Sover" rather than only the coarse <see cref="HouseMode"/> enum label.
/// </summary>
[TestClass]
public sealed class HaStatePublisherTests
{
	[TestMethod]
	public void HouseModeValue_Survives_The_Serialize_Event_ToSnapshot_Round_Trip()
	{
		var ha = new FakeHaContext();
		var publisher = new HaStatePublisher(ha, NullLogger.Instance);

		var snapshot = new ZoneSnapshot(
			"Stue", ZoneState.AutoActive, TransitionReason.Motion, HouseMode.Sleep,
			KillSwitchActive: false, IsDark: true, PeriodName: "evening", BrightnessPct: 70, ColorTempKelvin: 2700,
			Timestamp: DateTimeOffset.UnixEpoch, LastCommandAt: null, LastMotionAt: null, NextChangeAt: null,
			NextChangeFrom: null, HouseModeValue: "Sover");

		publisher.Publish(snapshot);

		var (type, data) = ha.SentEvents.Single();
		Assert.AreEqual(HaStatePublisher.EventType, type);

		// Round-trip through JSON exactly as NetDaemon's Event<T>.Data would: serialize the published payload,
		// then bind it into the web-side event record and rebuild the snapshot.
		var json = JsonSerializer.Serialize(data);
		var wire = JsonSerializer.Deserialize<ZoneSnapshotEvent>(json);
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

		var snapshot = new ZoneSnapshot(
			"Stue", ZoneState.AutoVacant, TransitionReason.Startup, HouseMode.Home,
			KillSwitchActive: false, IsDark: false, PeriodName: "day", BrightnessPct: null, ColorTempKelvin: null,
			Timestamp: DateTimeOffset.UnixEpoch, LastCommandAt: null, LastMotionAt: null, NextChangeAt: null,
			NextChangeFrom: null, HouseModeValue: null);

		publisher.Publish(snapshot);

		var json = JsonSerializer.Serialize(ha.SentEvents.Single().Data);
		var rebuilt = JsonSerializer.Deserialize<ZoneSnapshotEvent>(json)!.ToSnapshot();

		Assert.IsNotNull(rebuilt);
		Assert.IsNull(rebuilt!.HouseModeValue, "no select configured → the raw value stays null through the round trip");
	}
}
