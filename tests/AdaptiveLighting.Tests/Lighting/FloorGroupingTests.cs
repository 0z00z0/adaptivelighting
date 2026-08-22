using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Floors through the seam, and the one place the house's floor plan turns into display groups.</summary>
/// <remarks>Both screens group rooms through <see cref="FloorGrouping.Group"/>, so they cannot describe one house two ways.</remarks>
[TestClass]
public sealed class FloorGroupingTests
{
	private static AreaConfig Area(string areaId) => new() { Name = areaId, AreaId = areaId };

	private static IReadOnlyList<FloorGroup<AreaConfig>> Group(FakeAreaRegistry registry, params string[] areaIds) =>
		FloorGrouping.Group([.. areaIds.Select(Area)], area => area.AreaId, registry);

	// ===================== the seam =====================

	[TestMethod]
	public void An_Area_On_No_Floor_Has_No_Floor()
	{
		FakeAreaRegistry registry = new();
		registry.Areas["stue"] = [];
		registry.Floors["loft"] = new AreaFloor("first", "First floor", 1);

		Assert.IsNull(registry.FloorOf("stue"), "floors are optional in HA, so floorless is an ordinary answer");
		Assert.IsNull(registry.FloorOf("nowhere"), "and an area nobody knows cannot be on a floor either");
		Assert.AreEqual("First floor", registry.FloorOf("loft")!.Name);
	}

	// ===================== ordering =====================

	[TestMethod]
	public void Groups_Are_Ordered_By_Level_And_Then_By_Name()
	{
		FakeAreaRegistry registry = new();
		registry.Floors["loft"] = new AreaFloor("loft", "Loftet", 2);
		registry.Floors["ground"] = new AreaFloor("ground", "Ground floor", 0);
		registry.Floors["first"] = new AreaFloor("first", "First floor", 1);
		// Two floors sharing a level: the tie is broken by name, so the order is stable.
		registry.Floors["annex"] = new AreaFloor("annex", "Annexe", 0);

		IReadOnlyList<FloorGroup<AreaConfig>> groups = Group(registry, "loft", "first", "ground", "annex");

		CollectionAssert.AreEqual(
			new[] { "Annexe", "Ground floor", "First floor", "Loftet" },
			groups.Select(group => group.Floor!.Name).ToArray());
	}

	[TestMethod]
	public void A_Floor_With_No_Level_Sorts_After_The_Ones_That_Have_One()
	{
		FakeAreaRegistry registry = new();
		registry.Floors["ground"] = new AreaFloor("ground", "Ground floor", 0);
		registry.Floors["shed"] = new AreaFloor("shed", "Anneks", null);

		IReadOnlyList<FloorGroup<AreaConfig>> groups = Group(registry, "shed", "ground");

		CollectionAssert.AreEqual(
			new[] { "Ground floor", "Anneks" },
			groups.Select(group => group.Floor!.Name).ToArray(),
			"a house that never numbered a floor still knows where the numbered ones go");
	}

	[TestMethod]
	public void Floorless_Rooms_Trail_Every_Floor_In_One_Unnamed_Group()
	{
		FakeAreaRegistry registry = new();
		registry.Floors["ground"] = new AreaFloor("ground", "Ground floor", 0);
		registry.Floors["first"] = new AreaFloor("first", "First floor", 1);

		IReadOnlyList<FloorGroup<AreaConfig>> groups = Group(registry, "uteplass", "ground", "bod", "first");

		Assert.AreEqual(3, groups.Count);
		Assert.IsNull(groups[^1].Floor, "the rooms nobody put on a floor come last, under no header of their own");
		CollectionAssert.AreEqual(
			new[] { "uteplass", "bod" },
			groups[^1].Items.Select(area => area.AreaId).ToArray(),
			"and keep the order they were handed in");
	}

	[TestMethod]
	public void Rooms_On_One_Floor_Share_A_Group_In_The_Order_They_Were_Given()
	{
		FakeAreaRegistry registry = new();
		AreaFloor ground = new("ground", "Ground floor", 0);
		registry.Floors["stue"] = ground;
		registry.Floors["kjokken"] = ground;

		IReadOnlyList<FloorGroup<AreaConfig>> groups = Group(registry, "kjokken", "stue");

		Assert.AreEqual(1, groups.Count);
		CollectionAssert.AreEqual(new[] { "kjokken", "stue" }, groups[0].Items.Select(area => area.AreaId).ToArray());
	}

	// ===================== degradation: the house with no floors =====================

	// The common case. Renderers decide on headers with "more than one group, or a named one", so a floorless
	// house has to collapse to a single unnamed group.
	[TestMethod]
	public void A_House_With_No_Floors_At_All_Collapses_To_One_Unnamed_Group()
	{
		FakeAreaRegistry registry = new();

		IReadOnlyList<FloorGroup<AreaConfig>> groups = Group(registry, "stue", "kjokken", "gang");

		Assert.AreEqual(1, groups.Count, "a floorless house is one group, never a lone 'Other rooms' heading");
		Assert.IsNull(groups[0].Floor);
		Assert.AreEqual(3, groups[0].Items.Count);
	}

	[TestMethod]
	public void A_Room_With_No_Area_Id_Is_Floorless_By_Construction()
	{
		FakeAreaRegistry registry = new();
		registry.Floors["stue"] = new AreaFloor("ground", "Ground floor", 0);

		IReadOnlyList<FloorGroup<AreaConfig>> groups = FloorGrouping.Group(
			[Area("stue"), new AreaConfig { Name = "Hand-listed", Lights = ["light.l"] }],
			area => area.AreaId,
			registry);

		Assert.AreEqual(2, groups.Count);
		Assert.IsNull(groups[^1].Floor);
		Assert.AreEqual("Hand-listed", groups[^1].Items[0].DisplayName);
	}

	[TestMethod]
	public void Nothing_To_Group_Is_No_Groups()
	{
		Assert.AreEqual(0, FloorGrouping.Group<AreaConfig>([], area => area.AreaId, new FakeAreaRegistry()).Count,
			"an empty house must not render an empty group's worth of chrome");
	}
}
