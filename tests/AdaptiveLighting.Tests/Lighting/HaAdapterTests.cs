using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Ha;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The classes that actually talk to Home Assistant: the exact wire format, and the calls not made.
/// </summary>
/// <remarks>
///     Worth testing precisely because nothing else can catch a wrong key. A misspelled <c>brightness_pct</c>
///     is not a compile error and not a runtime exception — it is a light that quietly ignores the engine.
/// </remarks>
[TestClass]
public sealed class HaAdapterTests
{
	private const string Light = "light.a";

	private static HaLightActuator Actuator(FakeHaContext ha) => new(ha, new GlobalConfig(), NullLogger.Instance);

	private static Dictionary<string, object> DataOf(ServiceCall call) => (Dictionary<string, object>)call.Data!;

	[TestMethod]
	public void Turn_On_Carries_HAs_Own_Key_Names_And_Nothing_Else()
	{
		var ha = new FakeHaContext();
		ha.SetState(Light, "off");

		Actuator(ha).Apply(Light, new LightCommand(true, 70, 2700, 15));

		var call = ha.Calls.Single();
		Assert.AreEqual("light", call.Domain);
		Assert.AreEqual("turn_on", call.Service);
		Assert.AreEqual(Light, call.Target!.EntityIds!.Single());

		var data = DataOf(call);
		Assert.AreEqual(70d, data["brightness_pct"]);
		Assert.AreEqual(2700, data["color_temp_kelvin"]);
		Assert.AreEqual(15d, data["transition"]);
		Assert.AreEqual(3, data.Count, "a key the engine did not set is a key a human still owns");
	}

	[TestMethod]
	public void A_Command_That_Sets_Nothing_Sends_An_Empty_Payload_Rather_Than_Nulls()
	{
		var ha = new FakeHaContext();
		ha.SetState(Light, "off");

		Actuator(ha).Apply(Light, new LightCommand(true));

		Assert.AreEqual(0, DataOf(ha.Calls.Single()).Count, "HA rejects a null where it expected a number");
	}

	[TestMethod]
	public void A_Light_Already_At_The_Target_Is_Left_Alone()
	{
		var ha = new FakeHaContext();
		ha.SetState(Light, "on", new() { ["brightness"] = 178.5, ["color_temp_kelvin"] = 2700 });

		Actuator(ha).Apply(Light, new LightCommand(true, 70, 2700, 15));

		Assert.AreEqual(0, ha.Calls.Count, "a light told to fade to where it already is visibly restarts the fade");
	}

	[TestMethod]
	public void A_Light_Outside_The_Tolerance_Is_Commanded()
	{
		var ha = new FakeHaContext();
		ha.SetState(Light, "on", new() { ["brightness"] = 178.5, ["color_temp_kelvin"] = 2700 });

		Actuator(ha).Apply(Light, new LightCommand(true, 20, 2700, 15));

		Assert.AreEqual(1, ha.Calls.Count);
	}

	[TestMethod]
	public void A_Light_That_Is_Off_Is_Always_Commanded_On()
	{
		var ha = new FakeHaContext();
		ha.SetState(Light, "off", new() { ["brightness"] = 178.5 });

		Actuator(ha).Apply(Light, new LightCommand(true, 70, 2700, 15));

		Assert.AreEqual(1, ha.Calls.Count, "stale brightness on an off light must not be read as already matching");
	}

	[TestMethod]
	public void A_Light_Already_Off_Is_Not_Turned_Off_Again()
	{
		var ha = new FakeHaContext();
		ha.SetState(Light, "off");

		Actuator(ha).Apply(Light, LightCommand.TurnOff(2));

		Assert.AreEqual(0, ha.Calls.Count);
	}

	[TestMethod]
	public void Turn_Off_Carries_Only_The_Transition()
	{
		var ha = new FakeHaContext();
		ha.SetState(Light, "on");

		Actuator(ha).Apply(Light, LightCommand.TurnOff(2));

		var call = ha.Calls.Single();
		Assert.AreEqual("turn_off", call.Service);
		Assert.AreEqual(2d, DataOf(call)["transition"]);
		Assert.AreEqual(1, DataOf(call).Count);
	}

	[TestMethod]
	public void A_Light_With_No_Colour_Temperature_Cannot_Drift_From_One()
	{
		var ha = new FakeHaContext();
		ha.SetState(Light, "on", new() { ["brightness"] = 178.5 });

		Actuator(ha).Apply(Light, new LightCommand(true, 70, 2700, 15));

		Assert.AreEqual(0, ha.Calls.Count, "a white-only bulb must not be re-commanded on every single tick");
	}

	// ===================== notifier =====================

	[TestMethod]
	public void A_Notification_Is_A_Persistent_Notification_With_A_Stable_Id()
	{
		var ha = new FakeHaContext();

		new HaNotifier(ha, NullLogger.Instance).Notify("Adaptive lighting: areas disabled", "<ul><li>x</li></ul>");

		var call = ha.Calls.Single();
		Assert.AreEqual("persistent_notification", call.Domain);
		Assert.AreEqual("create", call.Service);

		var data = DataOf(call);
		StringAssert.StartsWith((string)data["notification_id"], "laget_lighting_",
			"a stable id replaces the previous notification instead of stacking a new one every restart");
		Assert.IsTrue(data.ContainsKey("title"));
		Assert.IsTrue(data.ContainsKey("message"));
	}

	// ===================== publisher =====================

	[TestMethod]
	public void A_Snapshot_Is_Published_As_An_Event_The_UI_Can_Listen_For()
	{
		var ha = new FakeHaContext();
		var when = new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);
		var snapshot = new AreaSnapshot(
			"Stue", AreaState.AutoActive, TransitionReason.Motion, HouseMode.Home,
			false, true, "evening", 70, 2700, when,
			when, when, when + TimeSpan.FromMinutes(10), when);

		new HaStatePublisher(ha, NullLogger.Instance).Publish(snapshot);

		Assert.AreEqual("adaptive_lighting_area", ha.SentEvents.Single().Type);
	}
}
