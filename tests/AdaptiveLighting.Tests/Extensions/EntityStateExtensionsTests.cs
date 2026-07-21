using AdaptiveLighting.Tests.Lighting;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Extensions;

/// <summary>
///     The attribute and state readers lifted from the engine's former <c>AttributeReader</c>.
/// </summary>
/// <remarks>
///     States are arranged through <see cref="FakeHaContext"/>, which round-trips them through JSON — so the
///     attribute bag is a <see cref="System.Text.Json.JsonElement"/>, exactly the shape production sees off the
///     Home Assistant client.
/// </remarks>
[TestClass]
public sealed class EntityStateExtensionsTests
{
	private const string Entity = "sensor.probe";

	private static EntityState? State(string state, Dictionary<string, object>? attributes = null)
	{
		var ha = new FakeHaContext();
		ha.SetState(Entity, state, attributes);
		return ha.GetState(Entity);
	}

	[TestMethod]
	public void AttrDouble_Reads_A_Json_Number()
	{
		Assert.AreEqual(178.5, State("on", new() { ["brightness"] = 178.5 }).AttrDouble("brightness"));
	}

	[TestMethod]
	public void AttrDouble_Reads_A_Numeric_String_With_Invariant_Culture()
	{
		Assert.AreEqual(3.5, State("on", new() { ["x"] = "3.5" }).AttrDouble("x"));
	}

	[TestMethod]
	public void AttrDouble_Is_Null_For_A_Non_Numeric_String()
	{
		Assert.IsNull(State("on", new() { ["x"] = "abc" }).AttrDouble("x"));
	}

	[TestMethod]
	public void AttrDouble_Is_Null_When_Absent_Or_State_Is_Null()
	{
		Assert.IsNull(State("on").AttrDouble("missing"));
		Assert.IsNull(((EntityState?)null).AttrDouble("x"));
	}

	[TestMethod]
	public void AttrString_Reads_A_String_And_Is_Null_When_Absent()
	{
		Assert.AreEqual("motion", State("on", new() { ["device_class"] = "motion" }).AttrString("device_class"));
		Assert.IsNull(State("on").AttrString("device_class"));
		Assert.IsNull(((EntityState?)null).AttrString("device_class"));
	}

	[TestMethod]
	public void AttrStringList_Reads_A_Json_Array()
	{
		CollectionAssert.AreEqual(
			new[] { "light.a", "light.b" },
			State("on", new() { ["entity_id"] = new[] { "light.a", "light.b" } }).AttrStringList("entity_id").ToArray());
	}

	[TestMethod]
	public void AttrStringList_Wraps_A_Lone_String()
	{
		CollectionAssert.AreEqual(
			new[] { "light.a" },
			State("on", new() { ["entity_id"] = "light.a" }).AttrStringList("entity_id").ToArray());
	}

	[TestMethod]
	public void AttrStringList_Is_Empty_When_Absent_Or_Null()
	{
		Assert.AreEqual(0, State("on").AttrStringList("entity_id").Count);
		Assert.AreEqual(0, ((EntityState?)null).AttrStringList("entity_id").Count);
	}

	[TestMethod]
	public void AttrDateTimeOffset_Parses_A_Utc_Timestamp()
	{
		var parsed = State("above_horizon", new() { ["next_rising"] = "2026-01-15T06:30:00+00:00" })
			.AttrDateTimeOffset("next_rising");

		Assert.IsNotNull(parsed);
		Assert.AreEqual(new DateTimeOffset(2026, 1, 15, 6, 30, 0, TimeSpan.Zero), parsed!.Value);
	}

	[TestMethod]
	public void AttrDateTimeOffset_Is_Null_For_Junk()
	{
		Assert.IsNull(State("on", new() { ["next_rising"] = "not-a-date" }).AttrDateTimeOffset("next_rising"));
		Assert.IsNull(State("on").AttrDateTimeOffset("next_rising"));
	}

	[TestMethod]
	public void StateAsDouble_Parses_The_State()
	{
		Assert.AreEqual(12.5, State("12.5").StateAsDouble());
		Assert.IsNull(State("unavailable").StateAsDouble());
		Assert.IsNull(((EntityState?)null).StateAsDouble());
	}

	[TestMethod]
	public void IsAvailable_Matches_IsLive_Null_And_Unavailable_Only()
	{
		Assert.IsFalse(((EntityState?)null).IsAvailable(), "a null state is not a device");
		Assert.IsFalse(State("unavailable").IsAvailable());
		Assert.IsTrue(State("unknown").IsAvailable(), "unknown is deliberately not treated as unavailable (matches IsLive)");
		Assert.IsTrue(State("on").IsAvailable());
	}

	[TestMethod]
	public void StateIs_Is_Ordinal_Ignore_Case_And_Null_Tolerant()
	{
		Assert.IsTrue(State("Home").StateIs("home"));
		Assert.IsFalse(State("away").StateIs("home"));
		Assert.IsFalse(((EntityState?)null).StateIs("home"));
	}
}
