using AdaptiveLighting.Tests.Lighting;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Extensions;

[TestClass]
public sealed class StateChangeExtensionsTests
{
	private const string EntityId = "light.a";

	private static EntityState St(string state)
	{
		var ha = new FakeHaContext();
		ha.SetState(EntityId, state);
		return ha.GetState(EntityId)!;
	}

	private static StateChange Change(string? oldState, string? newState) =>
		new(new Entity(new FakeHaContext(), EntityId),
			oldState is null ? null : St(oldState),
			newState is null ? null : St(newState));

	[TestMethod]
	public void TurnedOn_Is_True_Only_When_The_New_State_Is_On()
	{
		Assert.IsTrue(Change("off", "on").TurnedOn());
		Assert.IsFalse(Change("on", "off").TurnedOn());
		Assert.IsFalse(Change("off", null).TurnedOn());
	}

	[TestMethod]
	public void TurnedOff_Is_True_Only_When_The_New_State_Is_Off()
	{
		Assert.IsTrue(Change("on", "off").TurnedOff());
		Assert.IsFalse(Change("off", "on").TurnedOff());
		Assert.IsFalse(Change("on", null).TurnedOff());
	}

	[TestMethod]
	public void EntityId_Prefers_The_New_State_And_Falls_Back()
	{
		Assert.AreEqual(EntityId, Change("off", "on").EntityId());
		Assert.AreEqual(EntityId, Change("off", null).EntityId(), "falls back to the change's entity when there is no new state");
	}

	[TestMethod]
	public void StateBecame_Is_Ordinal_Ignore_Case()
	{
		Assert.IsTrue(Change("idle", "Playing").StateBecame("playing"));
		Assert.IsFalse(Change("idle", "paused").StateBecame("playing"));
		Assert.IsFalse(Change("idle", null).StateBecame("playing"));
	}
}
