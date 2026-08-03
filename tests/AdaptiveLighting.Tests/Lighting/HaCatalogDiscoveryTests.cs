using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.Logging.Abstractions;

using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     A registry that throws until it is told to stop, and counts how often it was asked.
/// </summary>
/// <remarks>
///     NetDaemon connects to Home Assistant after Kestrel is already serving pages, and its registry throws
///     <see cref="InvalidOperationException"/> until it has. The only observable difference between a caught
///     throw and a real answer is whether Home Assistant gets asked a second time.
/// </remarks>
public sealed class RecoveringHaRegistry : IHaRegistry
{
	/// <summary>How many times an area lookup was attempted.</summary>
	public int Lookups { get; private set; }

	public bool IsDown { get; set; } = true;

	/// <inheritdoc/>
	public IReadOnlyCollection<EntityRegistration> Entities => [];

	/// <inheritdoc/>
	public IReadOnlyCollection<Device> Devices => [];

	/// <inheritdoc/>
	public IReadOnlyCollection<Area> Areas => [];

	/// <inheritdoc/>
	public IReadOnlyCollection<Floor> Floors => [];

	/// <inheritdoc/>
	public IReadOnlyCollection<Label> Labels => [];

	/// <inheritdoc/>
	public EntityRegistration? GetEntityRegistration(string entityId) => null;

	/// <inheritdoc/>
	public Device? GetDevice(string deviceId) => null;

	/// <inheritdoc/>
	public Floor? GetFloor(string floorId) => null;

	/// <inheritdoc/>
	public Label? GetLabel(string labelId) => null;

	/// <inheritdoc/>
	public Area? GetArea(string areaId)
	{
		Lookups++;

		return IsDown ? throw new InvalidOperationException("The registry is not connected.") : null;
	}
}

/// <summary>
///     The discovery cache behind the area picker's labels and the dashboard's light counts.
/// </summary>
/// <remarks>
///     Blazor re-renders whole pages, so uncached an eleven-area house runs eleven full discoveries per keystroke.
///     The one thing that must never be cached is a refusal to answer.
/// </remarks>
[TestClass]
public sealed class HaCatalogDiscoveryTests
{
	// A discovery that threw used to be filed as 0 lights, 0 motion, 0 lux. HaCatalog is scoped to the Blazor
	// circuit, so a page opened in the start-up window read every area as empty until it was reloaded.
	[TestMethod]
	public void A_Discovery_Home_Assistant_Refused_Is_Not_Kept_As_Its_Answer()
	{
		RecoveringHaRegistry registry = new();
		HaCatalog catalog = new(new FakeHaContext(), registry, NullLoggerFactory.Instance);
		GlobalConfig global = new();

		Assert.AreEqual(0, catalog.LightCountIn("stue", global), "nothing can be discovered while the registry throws");
		Assert.IsFalse(catalog.IsHomeAssistantReady, "and the catalogue says so rather than pretending");

		int whileDown = registry.Lookups;

		Assert.IsTrue(whileDown > 0, "the failing question did reach the registry");

		registry.IsDown = false;

		catalog.LightCountIn("stue", global);

		Assert.IsTrue(registry.Lookups > whileDown,
			"a refusal is not an answer, so the next question has to reach Home Assistant rather than the cache");
	}

	[TestMethod]
	public void A_Discovery_That_Succeeded_Is_Asked_Once()
	{
		RecoveringHaRegistry registry = new() { IsDown = false };
		HaCatalog catalog = new(new FakeHaContext(), registry, NullLoggerFactory.Instance);
		GlobalConfig global = new();

		catalog.LightCountIn("stue", global);
		int afterFirst = registry.Lookups;

		catalog.LightCountIn("stue", global);

		Assert.AreEqual(afterFirst, registry.Lookups, "the second question is answered from the cache");
		Assert.IsTrue(afterFirst > 0, "and the first one actually reached the registry");
	}

	[TestMethod]
	public void Invalidating_Sends_The_Next_Question_Back_To_Home_Assistant()
	{
		RecoveringHaRegistry registry = new() { IsDown = false };
		HaCatalog catalog = new(new FakeHaContext(), registry, NullLoggerFactory.Instance);
		GlobalConfig global = new();

		catalog.LightCountIn("stue", global);
		int afterFirst = registry.Lookups;

		catalog.Invalidate();
		catalog.LightCountIn("stue", global);

		Assert.IsTrue(registry.Lookups > afterFirst, "the cache was dropped, so the registry is asked again");
	}
}
