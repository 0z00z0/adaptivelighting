using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>What a room is called, and in what order that is decided; discovery writes only an area id.</summary>
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

	[TestMethod]
	public void A_Room_With_No_Name_Takes_The_Registrys()
	{
		Assert.AreEqual("Kjeller - Bad", AreaNaming.DisplayName(new AreaConfig { AreaId = "kjeller_bad" }, Registry()));
		Assert.AreEqual("Kjøkken", AreaNaming.DisplayName(new AreaConfig { AreaId = "kjokken" }, Registry()));
	}

	[TestMethod]
	public void A_Stated_Name_Wins_Over_The_Registry()
	{
		Assert.AreEqual(
			"Kjellerbadet",
			AreaNaming.DisplayName(new AreaConfig { AreaId = "kjeller_bad", Name = "Kjellerbadet" }, Registry()));
	}

	[TestMethod]
	public void An_Unknown_Area_Falls_Back_To_Its_Id()
	{
		Assert.AreEqual("sykkelbod", AreaNaming.DisplayName(new AreaConfig { AreaId = "sykkelbod" }, Registry()));
	}

	// A room configured with explicit entities has no area at all.
	[TestMethod]
	public void A_Room_With_No_Area_Reaches_The_Placeholder()
	{
		Assert.IsNull(AreaNaming.Resolve(new AreaConfig(), Registry()));
		Assert.AreEqual("(unnamed area)", AreaNaming.DisplayName(new AreaConfig(), Registry()));
	}

	// ===================== a registry that cannot be read =====================

	[TestMethod]
	public void Without_A_Registry_The_Area_Id_Still_Names_The_Room()
	{
		Assert.AreEqual("kjeller_bad", AreaNaming.DisplayName(new AreaConfig { AreaId = "kjeller_bad" }, registry: null));
		Assert.AreEqual("Stue", AreaNaming.DisplayName(new AreaConfig { AreaId = "stue", Name = "Stue" }, registry: null));
	}

	// NetDaemon's registry throws until its first connection completes, and Kestrel serves pages in that window.
	[TestMethod]
	public void A_Registry_That_Throws_Does_Not_Blank_The_Name()
	{
		Assert.AreEqual(
			"kjeller_bad",
			AreaNaming.DisplayName(new AreaConfig { AreaId = "kjeller_bad" }, new ThrowingAreaRegistry()));
	}

	[TestMethod]
	public void An_Empty_Registry_Name_Is_No_Name()
	{
		FakeAreaRegistry registry = Registry();
		registry.Names["bod"] = "";

		Assert.AreEqual("bod", AreaNaming.DisplayName(new AreaConfig { AreaId = "bod" }, registry));
	}

	// ===================== a name made only of spaces =====================

	// A whitespace Name reaches SwitchOnWarning.For, whose ThrowIfNullOrWhiteSpace faults the settings page as it loads.
	[TestMethod]
	public void A_Name_Of_Only_Spaces_Does_Not_Name_The_Room()
	{
		FakeAreaRegistry registry = Registry();
		registry.Names["bod"] = " ";

		Assert.AreEqual(
			"Kjeller - Bad",
			AreaNaming.DisplayName(new AreaConfig { AreaId = "kjeller_bad", Name = "  " }, registry),
			"a stated name of spaces must lose to the registry");

		Assert.AreEqual(
			"bod",
			AreaNaming.DisplayName(new AreaConfig { AreaId = "bod" }, registry),
			"a registry name of spaces must lose to the id");

		Assert.IsNull(
			AreaNaming.Resolve(new AreaConfig { AreaId = "   ", Name = "\t" }, registry),
			"nothing readable anywhere leaves the caller's own placeholder");
	}

	// The re-setup panel asks by callback; it has no registry to hand.
	[TestMethod]
	public void A_Name_Source_Works_The_Same_As_A_Registry()
	{
		Assert.AreEqual(
			"Kjøkken",
			AreaNaming.DisplayName(new AreaConfig { AreaId = "kjokken" }, areaId => areaId == "kjokken" ? "Kjøkken" : null));
	}
}
