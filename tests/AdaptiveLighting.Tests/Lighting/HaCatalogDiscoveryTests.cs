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
///     The window every read in the web UI has to survive: NetDaemon connects to Home Assistant after Kestrel is
///     already serving pages, and its registry throws <see cref="InvalidOperationException"/> until it has. What
///     matters is not only that the throw is caught but that it is not mistaken for an answer, and the only
///     observable difference between the two is whether Home Assistant is asked a second time.
/// </remarks>
public sealed class RecoveringHaRegistry : IHaRegistry
{
	/// <summary>How many times an area lookup was attempted.</summary>
	public int Lookups { get; private set; }

	/// <summary>Whether the registry is still refusing to answer.</summary>
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
///     The cache exists because discovery is expensive and Blazor re-renders whole pages: uncached, an eleven-area
///     house runs eleven full discoveries per keystroke. What it must never do is cache the one result that is not
///     a result — Home Assistant refusing to answer — because that turns a start-up window of a few seconds into a
///     wrong answer that stands for as long as the browser tab is open.
/// </remarks>
[TestClass]
public sealed class HaCatalogDiscoveryTests
{
	/// <summary>
	///     <b>The answer that was not an answer.</b> A discovery that threw was filed in the cache as
	///     <c>0 lights, 0 motion, 0 lux</c>. Because <see cref="HaCatalog"/> is scoped to the Blazor circuit and
	///     the dashboard asks on a one-second ticker, any page opened in the documented start-up window — Kestrel
	///     serving before NetDaemon has connected — read every area as empty for the whole session, however long
	///     Home Assistant had since been up, and only a page reload cleared it.
	/// </summary>
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

	/// <summary>
	///     An answer is still cached, which is the whole reason the cache exists: discovery costs a registry read
	///     and a state read per candidate, and the area picker needs it for every area on every re-render.
	/// </summary>
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

	/// <summary>
	///     Invalidation still drops what was cached. The page calls it whenever it re-reads the document, and
	///     anything worth re-reading the file for is worth re-reading the registry for.
	/// </summary>
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
