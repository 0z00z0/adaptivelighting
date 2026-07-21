using AdaptiveLighting.Tests.Lighting;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Extensions;

/// <summary>Verbs and one-hop questions on <see cref="IHaContext"/>.</summary>
[TestClass]
public sealed class HaContextExtensionsTests
{
	[TestMethod]
	public void IsOn_And_IsOff_Are_Both_False_When_Unavailable()
	{
		var ha = new FakeHaContext();
		ha.SetState("light.a", "on");
		ha.SetState("light.b", "off");
		ha.SetState("light.c", "unavailable");

		Assert.IsTrue(ha.IsOn("light.a"));
		Assert.IsFalse(ha.IsOff("light.a"));

		Assert.IsTrue(ha.IsOff("light.b"));
		Assert.IsFalse(ha.IsOn("light.b"));

		Assert.IsFalse(ha.IsOn("light.c"), "IsOff is not !IsOn — both are false when unavailable");
		Assert.IsFalse(ha.IsOff("light.c"));

		Assert.IsFalse(ha.IsOn("light.unknown"));
		Assert.IsFalse(ha.IsOff("light.unknown"));
	}

	[TestMethod]
	public void StateIs_And_AttrDouble_One_Hop()
	{
		var ha = new FakeHaContext();
		ha.SetState("person.a", "home");
		ha.SetState("sun.sun", "above_horizon", new() { ["elevation"] = -4.2 });

		Assert.IsTrue(ha.StateIs("person.a", "Home"));
		Assert.AreEqual(-4.2, ha.AttrDouble("sun.sun", "elevation"));
		Assert.IsNull(ha.AttrDouble("sun.sun", "missing"));
	}

	[TestMethod]
	public void EntityIdsInDomain_Is_Ordered_And_Domain_Scoped()
	{
		var ha = new FakeHaContext();
		ha.SetState("person.zoe", "home");
		ha.SetState("person.amy", "home");
		ha.SetState("light.k", "on");

		CollectionAssert.AreEqual(new[] { "person.amy", "person.zoe" }, ha.EntityIdsInDomain("person").ToArray());
	}

	[TestMethod]
	public void TurnOn_Infers_The_Domain_From_A_Same_Domain_Set()
	{
		var ha = new FakeHaContext();
		ha.TurnOn("light.a", "light.b");

		var call = ha.Calls.Single();
		Assert.AreEqual("light", call.Domain);
		Assert.AreEqual("turn_on", call.Service);
		CollectionAssert.AreEqual(new[] { "light.a", "light.b" }, call.Target!.EntityIds!.ToArray());
	}

	[TestMethod]
	public void TurnOff_Falls_Back_To_Homeassistant_For_A_Mixed_Set()
	{
		var ha = new FakeHaContext();
		ha.TurnOff("light.a", "switch.b");

		Assert.AreEqual("homeassistant", ha.Calls.Single().Domain);
	}

	[TestMethod]
	public void TurnOn_On_An_Empty_Set_Throws()
	{
		var ha = new FakeHaContext();
		Assert.ThrowsException<ArgumentNullException>(() => ha.TurnOn());
	}

	[TestMethod]
	public void CallServiceById_Splits_A_Full_Service_Id()
	{
		var ha = new FakeHaContext();
		ha.CallServiceById("notify.mobile_app_phone", data: new { message = "hi" });

		var call = ha.Calls.Single();
		Assert.AreEqual("notify", call.Domain);
		Assert.AreEqual("mobile_app_phone", call.Service);
	}

	[TestMethod]
	public void CallServiceById_Throws_On_A_Malformed_Id()
	{
		var ha = new FakeHaContext();
		Assert.ThrowsException<ArgumentException>(() => ha.CallServiceById("notonly"));
		Assert.ThrowsException<ArgumentException>(() => ha.CallServiceById("notify."));
		Assert.AreEqual(0, ha.Calls.Count, "a malformed id sends nothing");
	}

	[TestMethod]
	public void NotifyPersistent_Uses_Create_With_An_Optional_Id()
	{
		var ha = new FakeHaContext();
		ha.NotifyPersistent("Title", "Body", "laget_lighting_x");

		var call = ha.Calls.Single();
		Assert.AreEqual("persistent_notification", call.Domain);
		Assert.AreEqual("create", call.Service);

		var data = (Dictionary<string, object>)call.Data!;
		Assert.AreEqual("Title", data["title"]);
		Assert.AreEqual("Body", data["message"]);
		Assert.AreEqual("laget_lighting_x", data["notification_id"]);
	}

	[TestMethod]
	public void NotifyPersistent_Omits_The_Id_When_None_Is_Given()
	{
		var ha = new FakeHaContext();
		ha.NotifyPersistent("Title", "Body");

		var data = (Dictionary<string, object>)ha.Calls.Single().Data!;
		Assert.IsFalse(data.ContainsKey("notification_id"));
	}

	[TestMethod]
	public void SetInputText_And_SetInputNumber_Target_The_Set_Value_Service()
	{
		var ha = new FakeHaContext();
		ha.SetInputText("input_text.status", "awaiting");
		ha.SetInputNumber("input_number.tokens", 1234);

		var text = ha.Calls[0];
		Assert.AreEqual("input_text", text.Domain);
		Assert.AreEqual("set_value", text.Service);
		Assert.AreEqual("input_text.status", text.Target!.EntityIds!.Single());

		var number = ha.Calls[1];
		Assert.AreEqual("input_number", number.Domain);
		Assert.AreEqual("set_value", number.Service);
	}
}
