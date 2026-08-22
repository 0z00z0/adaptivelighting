using AdaptiveLighting.Tests.Lighting;

namespace AdaptiveLighting.Tests.Extensions;

// Only the empty-registry path is reachable: HassModel's Area, EntityRegistration and Label have no public
// constructors. The populated path is covered by AreaEntityResolver against the IAreaRegistry seam.
[TestClass]
public sealed class RegistryExtensionsTests
{
	private static readonly FakeHaRegistry Registry = new();

	[TestMethod]
	public void An_Empty_Registry_Answers_Everything_Without_Throwing()
	{
		Assert.AreEqual(0, Registry.AreaIds().Count);
		Assert.IsFalse(Registry.AreaExists("stue"));
		Assert.AreEqual(0, Registry.EntityIdsInArea("stue").Count);
		Assert.AreEqual(0, Registry.LabelsOf("light.a").Count);
		Assert.IsFalse(Registry.HasLabel("light.a", "exclude"));
		Assert.AreEqual(0, Registry.LabelsOfArea("stue").Count);
		Assert.IsFalse(Registry.AreaHasLabel("stue", "exclude"));
		Assert.AreEqual(0, Registry.EntityIdsInAreaByDomain("stue", "light").Count);
		Assert.IsNull(Registry.FloorOf("stue"));
	}
}
