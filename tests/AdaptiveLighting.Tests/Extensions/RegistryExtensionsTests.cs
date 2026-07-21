using AdaptiveLighting.Tests.Lighting;

namespace AdaptiveLighting.Tests.Extensions;

/// <summary>
///     The <see cref="IHaRegistry"/> extensions (the engine's former <c>HaAreaRegistry</c> body).
/// </summary>
/// <remarks>
///     Verified against <see cref="FakeHaRegistry"/>, whose collections are all empty: HassModel's <c>Area</c>,
///     <c>EntityRegistration</c> and <c>Label</c> have no public constructors, so a populated <c>IHaRegistry</c>
///     cannot be built in a test — the populated path is exercised through the engine's own
///     <c>ZoneEntityResolver</c> tests, which run against the <c>IAreaRegistry</c> seam. What is tested here is the
///     null-safe empty-registry behaviour: every one of these must answer without throwing.
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
	}
}
