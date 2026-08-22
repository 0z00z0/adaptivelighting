using AdaptiveLighting.Tests.Lighting;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Extensions;

[TestClass]
public sealed class EntityObservableExtensionsTests
{
	private const string Sensor = "binary_sensor.m";

	[TestMethod]
	public void WhenTurnsOn_Fires_On_Off_To_On_And_Unavailable_To_On()
	{
		var ha = new FakeHaContext();
		var count = 0;
		using var sub = ha.Entity(Sensor).WhenTurnsOn(_ => count++, NullLogger.Instance);

		ha.Trigger(Sensor, "on");            // (unset) -> on : fires
		ha.Trigger(Sensor, "off");           // on -> off      : no
		ha.Trigger(Sensor, "unavailable");   // off -> unavail : no
		ha.Trigger(Sensor, "on");            // unavail -> on  : fires

		Assert.AreEqual(2, count, "an on-edge from unavailable must fire, unlike the archived helper");
	}

	[TestMethod]
	public void WhenTurnsOn_Ignores_An_Attribute_Only_Change()
	{
		var ha = new FakeHaContext();
		ha.SetState(Sensor, "on");
		var count = 0;
		using var sub = ha.Entity(Sensor).WhenTurnsOn(_ => count++, NullLogger.Instance);

		ha.Trigger(Sensor, "on", new() { ["x"] = 1 }); // on -> on: a value-only stream never emits

		Assert.AreEqual(0, count);
	}

	[TestMethod]
	public void WhenTurnsOff_Fires_On_On_To_Off()
	{
		var ha = new FakeHaContext();
		var count = 0;
		using var sub = ha.Entity(Sensor).WhenTurnsOff(_ => count++, NullLogger.Instance);

		ha.Trigger(Sensor, "on");
		ha.Trigger(Sensor, "off");

		Assert.AreEqual(1, count);
	}

	[TestMethod]
	public void WhenStateBecomes_Fires_On_The_Named_State()
	{
		var ha = new FakeHaContext();
		var count = 0;
		using var sub = ha.Entity("media_player.tv").WhenStateBecomes("playing", _ => count++, NullLogger.Instance);

		ha.Trigger("media_player.tv", "paused");
		ha.Trigger("media_player.tv", "Playing");   // ordinal-ignore-case

		Assert.AreEqual(1, count);
	}

	[TestMethod]
	public void WhenTurnsOn_Survives_A_Throwing_Handler()
	{
		var ha = new FakeHaContext();
		var count = 0;
		using var sub = ha.Entity(Sensor).WhenTurnsOn(_ =>
		{
			count++;
			if (count == 1)
				throw new InvalidOperationException("boom");
		}, NullLogger.Instance);

		ha.Trigger(Sensor, "on");    // fires #1, throws — SubscribeSafe swallows it
		ha.Trigger(Sensor, "off");
		ha.Trigger(Sensor, "on");    // fires #2 — the subscription is still alive

		Assert.AreEqual(2, count, "a thrown handler must not kill the subscription");
	}
}
