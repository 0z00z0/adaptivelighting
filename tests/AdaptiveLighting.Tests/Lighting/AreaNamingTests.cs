using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     What a room is called, and in what order that is decided.
/// </summary>
/// <remarks>
///     <para>
///         The bug this covers was visible on every surface at once: discovery writes only an area id, nothing
///         asked Home Assistant what the area was called, and the room page's heading, the board's lanes and the
///         activity log's room column all read the slug — <c>kjeller_bad</c> for a room Home Assistant calls
///         Kjeller - Bad.
///     </para>
///     <para>
///         Three properties are worth pinning and are pinned here: a stated name still wins, the registry is only
///         consulted when there is no stated name, and a registry that cannot answer leaves the area id standing
///         rather than blanking the room.
///     </para>
/// </remarks>
[TestClass]
public sealed class AreaNamingTests
{
	private static FakeAreaRegistry Registry()
	{
		FakeAreaRegistry registry = new();
		registry.Areas["kjeller_bad"] = [];
		registry.Names["kjeller_bad"] = "Kjeller - Bad";
		registry.Areas["kjokken"] = [];
		registry.Names["kjokken"] = "Kjøkken";

		return registry;
	}

	// ===================== the order =====================

	/// <summary>The whole point: a room that states nothing is called what Home Assistant calls its area.</summary>
	[TestMethod]
	public void A_Room_With_No_Name_Takes_The_Registrys()
	{
		Assert.AreEqual("Kjeller - Bad", AreaNaming.DisplayName(new AreaConfig { AreaId = "kjeller_bad" }, Registry()));
		Assert.AreEqual("Kjøkken", AreaNaming.DisplayName(new AreaConfig { AreaId = "kjokken" }, Registry()));
	}

	/// <summary>A name in the document is the owner overruling Home Assistant, and it outranks the registry.</summary>
	[TestMethod]
	public void A_Stated_Name_Wins_Over_The_Registry()
	{
		Assert.AreEqual(
			"Kjellerbadet",
			AreaNaming.DisplayName(new AreaConfig { AreaId = "kjeller_bad", Name = "Kjellerbadet" }, Registry()));
	}

	/// <summary>An area the registry does not know keeps its id, which is still a fact about the room.</summary>
	[TestMethod]
	public void An_Unknown_Area_Falls_Back_To_Its_Id()
	{
		Assert.AreEqual("sykkelbod", AreaNaming.DisplayName(new AreaConfig { AreaId = "sykkelbod" }, Registry()));
	}

	/// <summary>A room configured with explicit entities and no area at all still has to be called something.</summary>
	[TestMethod]
	public void A_Room_With_No_Area_Reaches_The_Placeholder()
	{
		Assert.IsNull(AreaNaming.Resolve(new AreaConfig(), Registry()));
		Assert.AreEqual("(unnamed area)", AreaNaming.DisplayName(new AreaConfig(), Registry()));
	}

	// ===================== a registry that cannot be read =====================

	/// <summary>
	///     No registry is the state every page is reachable in for the first minute after a restart. It must cost
	///     the registry's name and nothing else.
	/// </summary>
	[TestMethod]
	public void Without_A_Registry_The_Area_Id_Still_Names_The_Room()
	{
		Assert.AreEqual("kjeller_bad", AreaNaming.DisplayName(new AreaConfig { AreaId = "kjeller_bad" }, registry: null));
		Assert.AreEqual("Stue", AreaNaming.DisplayName(new AreaConfig { AreaId = "stue", Name = "Stue" }, registry: null));
	}

	/// <summary>
	///     NetDaemon's registry throws until its first connection completes, and Kestrel serves pages inside that
	///     window. A room whose name blanked — or a page that failed outright — would be worse than the slug.
	/// </summary>
	[TestMethod]
	public void A_Registry_That_Throws_Does_Not_Blank_The_Name()
	{
		Assert.AreEqual(
			"kjeller_bad",
			AreaNaming.DisplayName(new AreaConfig { AreaId = "kjeller_bad" }, new ThrowingAreaRegistry()));
	}

	/// <summary>An area named as an empty string is as good as unnamed — it must not win over the id.</summary>
	[TestMethod]
	public void An_Empty_Registry_Name_Is_No_Name()
	{
		FakeAreaRegistry registry = Registry();
		registry.Names["bod"] = "";

		Assert.AreEqual("bod", AreaNaming.DisplayName(new AreaConfig { AreaId = "bod" }, registry));
	}

	/// <summary>A registry that answers by callback works the same way — that is how the re-setup panel asks.</summary>
	[TestMethod]
	public void A_Name_Source_Works_The_Same_As_A_Registry()
	{
		Assert.AreEqual(
			"Kjøkken",
			AreaNaming.DisplayName(new AreaConfig { AreaId = "kjokken" }, areaId => areaId == "kjokken" ? "Kjøkken" : null));
	}
}
