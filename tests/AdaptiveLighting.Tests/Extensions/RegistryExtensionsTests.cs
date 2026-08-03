using AdaptiveLighting.Tests.Lighting;

namespace AdaptiveLighting.Tests.Extensions;

/// <summary>
///     The <see cref="IHaRegistry"/> extensions (the engine's former <c>HaAreaRegistry</c> body).
/// </summary>
/// <remarks>
///     Only the empty-registry path is reachable here; HassModel's Area, EntityRegistration and Label have no
///     public constructors. The populated path is covered by AreaEntityResolver against the IAreaRegistry seam.
/// </remarks>
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
		Assert.AreEqual(0, Registry.EntityIdsInAreaByDomain("stue", "light").Count);
		Assert.IsNull(Registry.FloorOf("stue"));
	}
}
