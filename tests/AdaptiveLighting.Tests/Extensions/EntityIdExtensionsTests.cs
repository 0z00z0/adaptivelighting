using AdaptiveLighting.Tests.Lighting;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Extensions;

[TestClass]
public sealed class EntityIdExtensionsTests
{
	private static Entity Ent(string entityId) => new(new FakeHaContext(), entityId);

	[TestMethod]
	public void HasDomain_Matches_The_Domain_Segment_Only()
	{
		Assert.IsTrue("light.stue".HasDomain("light"));
		Assert.IsTrue("binary_sensor.m".HasDomain("binary_sensor"));
		Assert.IsFalse("lightbulb.x".HasDomain("light"), "a prefix that is not a whole domain segment must not match");
		Assert.IsFalse("light".HasDomain("light"), "a bare domain with no entity is not in the domain");
	}

	[TestMethod]
	public void Domain_Is_The_Part_Before_The_First_Dot_Or_Null()
	{
		Assert.AreEqual("light", "light.stue".Domain());
		Assert.IsNull("malformed".Domain());
		Assert.IsNull(".leadingdot".Domain());
	}

	[TestMethod]
	public void Entity_Domain_And_DomainEnum()
	{
		Assert.AreEqual("light", Ent("light.stue").Domain());
		Assert.AreEqual(EntityDomain.light, Ent("light.stue").DomainEnum());
		Assert.AreEqual(EntityDomain.@switch, Ent("switch.kettle").DomainEnum(), "the reserved word is mapped");
		Assert.AreEqual(EntityDomain.unknown, Ent("frobnicate.x").DomainEnum());
	}
}
