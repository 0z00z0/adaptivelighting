using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     What the commissioning board's one commit does to a document — and, far more often, what it deliberately
///     does not.
/// </summary>
/// <remarks>
///     The load-bearing property is that an unanswered sheet leaves the document byte-identical, because that is
///     what makes abandoning the board halfway safe and what makes a second visit a review rather than an
///     interrogation. Nearly every test here asserts an absence.
/// </remarks>
[TestClass]
public sealed class CommissioningDraftTests
{
	/// <summary>The document discovery writes: three rooms, all switched off, one adopted mode select.</summary>
	private static AdaptiveLightingConfig Discovered() => new()
	{
		ConfigName = "Adaptive lighting",
		Global = new GlobalConfig
		{
			Persons = ["person.espen", "person.nora", "device_tracker.bilen"],
			AwayDebounceMinutes = 5,
			HouseMode = new HouseModeConfig { Entity = "input_select.husmodus" }
		},
		Areas =
		[
			new AreaConfig { Name = "Stue", AreaId = "stue", Enabled = false },
			new AreaConfig { Name = "Kjøkken", AreaId = "kjokken", Enabled = false },
			new AreaConfig { Name = "Gang", AreaId = "gang", Enabled = false }
		]
	};

	private static readonly string[] Watched = ["person.espen", "person.nora", "device_tracker.bilen"];

	// ===================== nothing answered =====================

	/// <summary>
	///     An untouched draft changes nothing at all. This is the whole promise of the awaiting state: somebody who
	///     opens the board, reads it and closes the tab has not altered their house.
	/// </summary>
	[TestMethod]
	public void An_Untouched_Draft_Changes_Nothing()
	{
		AdaptiveLightingConfig config = Discovered();

		new CommissioningDraft().Apply(config, Watched);

		Assert.AreEqual("Adaptive lighting", config.ConfigName);
		Assert.AreEqual(5, config.Global.AwayDebounceMinutes);
		Assert.AreEqual(3, config.Global.Persons.Count);
		Assert.IsNotNull(config.Global.HouseMode);
		Assert.IsTrue(config.Areas.TrueForAll(area => area.Enabled == false));
	}

	/// <summary>Rooms nobody picked keep the switched-off state discovery wrote — they are not re-stated, either.</summary>
	[TestMethod]
	public void Rooms_Left_Alone_Keep_Discoverys_Own_Value()
	{
		AdaptiveLightingConfig config = Discovered();
		config.Areas[2].Enabled = null;

		CommissioningDraft draft = new();
		draft.ToggleRoom("stue");
		draft.Apply(config, Watched);

		Assert.IsTrue(config.Areas[0].Enabled);
		Assert.IsFalse(config.Areas[1].Enabled);
		Assert.IsNull(config.Areas[2].Enabled, "a room nobody touched must keep inheriting rather than gain a value.");
	}

	// ===================== the identity sheet =====================

	/// <summary>A typed name reaches the document trimmed.</summary>
	[TestMethod]
	public void House_Name_Is_Staged_Then_Written()
	{
		AdaptiveLightingConfig config = Discovered();

		CommissioningDraft draft = new();
		draft.SetHouseName("  B1  ");
		draft.Apply(config, Watched);

		Assert.AreEqual("B1", config.ConfigName);
	}

	/// <summary>Clearing the box clears the document's name rather than pinning the placeholder into it.</summary>
	[TestMethod]
	public void Cleared_House_Name_Clears_The_Document()
	{
		AdaptiveLightingConfig config = Discovered();

		CommissioningDraft draft = new();
		draft.SetHouseName("");
		draft.Apply(config, Watched);

		Assert.IsNull(config.ConfigName);
	}

	/// <summary>The empty-house delay is written as picked.</summary>
	[TestMethod]
	public void Away_Debounce_Is_Written_When_Picked()
	{
		AdaptiveLightingConfig config = Discovered();

		CommissioningDraft draft = new();
		draft.SetAwayDebounce(15);
		draft.Apply(config, Watched);

		Assert.AreEqual(15, config.Global.AwayDebounceMinutes);
	}

	/// <summary>Evicting the car leaves the people who live here, and only them.</summary>
	[TestMethod]
	public void Dropping_A_Person_Writes_The_Rest()
	{
		AdaptiveLightingConfig config = Discovered();

		CommissioningDraft draft = new();
		draft.TogglePerson("device_tracker.bilen");
		draft.Apply(config, Watched);

		CollectionAssert.AreEqual(new[] { "person.espen", "person.nora" }, config.Global.Persons);
	}

	/// <summary>Toggling somebody out and back in again is not an answer, so the list is not pinned.</summary>
	[TestMethod]
	public void Toggling_A_Person_Back_In_Leaves_The_List_Alone()
	{
		AdaptiveLightingConfig config = Discovered();
		config.Global.Persons = [];

		CommissioningDraft draft = new();
		draft.TogglePerson("device_tracker.bilen");
		draft.TogglePerson("device_tracker.bilen");
		draft.Apply(config, Watched);

		Assert.AreEqual(0, config.Global.Persons.Count, "an empty list means 'watch everyone', and nothing was decided.");
	}

	/// <summary>
	///     The case the watched list exists for: a house that configured no people is watching every
	///     <c>person.*</c> Home Assistant knows, so evicting the car has to pin the rest. Filtering the document's
	///     own empty list would silently do nothing and the car would keep the house permanently occupied.
	/// </summary>
	[TestMethod]
	public void Dropping_A_Person_From_An_Undeclared_House_Pins_The_Rest()
	{
		AdaptiveLightingConfig config = Discovered();
		config.Global.Persons = [];

		CommissioningDraft draft = new();
		draft.TogglePerson("device_tracker.bilen");
		draft.Apply(config, Watched);

		CollectionAssert.AreEqual(new[] { "person.espen", "person.nora" }, config.Global.Persons);
	}

	// ===================== the mode sheet =====================

	/// <summary>Keeping the adopted select is the do-nothing answer: the select is already in the document.</summary>
	[TestMethod]
	public void Keeping_The_House_Mode_Writes_Nothing()
	{
		AdaptiveLightingConfig config = Discovered();

		CommissioningDraft draft = new();
		draft.SetHouseMode(keep: true);
		draft.Apply(config, Watched);

		Assert.IsNotNull(config.Global.HouseMode);
	}

	/// <summary>Detaching drops the select, which is the one thing this sheet can change.</summary>
	[TestMethod]
	public void Detaching_Drops_The_House_Mode()
	{
		AdaptiveLightingConfig config = Discovered();

		CommissioningDraft draft = new();
		draft.SetHouseMode(keep: false);
		draft.Apply(config, Watched);

		Assert.IsNull(config.Global.HouseMode);
	}

	/// <summary>A house with no mode at all passes straight through — the cabin has none, and must commit cleanly.</summary>
	[TestMethod]
	public void A_House_With_No_Mode_Commits_Cleanly()
	{
		AdaptiveLightingConfig config = Discovered();
		config.Global.HouseMode = null;

		CommissioningDraft draft = new();
		draft.ToggleRoom("stue");
		draft.Apply(config, Watched);

		Assert.IsNull(config.Global.HouseMode);
		Assert.IsTrue(config.Areas[0].Enabled);
	}

	// ===================== the roll-call =====================

	/// <summary>A picked room is written switched on explicitly, never left to inherit a decision somebody made.</summary>
	[TestMethod]
	public void A_Picked_Room_Is_Written_On()
	{
		AdaptiveLightingConfig config = Discovered();

		CommissioningDraft draft = new();
		draft.ToggleRoom("stue");
		draft.ToggleRoom("gang");
		draft.Apply(config, Watched);

		Assert.IsTrue(config.Areas[0].Enabled);
		Assert.IsFalse(config.Areas[1].Enabled);
		Assert.IsTrue(config.Areas[2].Enabled);
	}

	/// <summary>Un-picking a room before commit leaves it off; the switch is a toggle, not a one-way latch.</summary>
	[TestMethod]
	public void Un_Picking_A_Room_Leaves_It_Off()
	{
		AdaptiveLightingConfig config = Discovered();

		CommissioningDraft draft = new();
		draft.ToggleRoom("stue");
		draft.ToggleRoom("stue");
		draft.Apply(config, Watched);

		Assert.IsFalse(config.Areas[0].Enabled);
		Assert.AreEqual(0, draft.PickedCount);
	}

	/// <summary>The floor header's bulk action picks a whole floor, and can put it back.</summary>
	[TestMethod]
	public void A_Floor_Can_Be_Picked_And_Dropped_As_One()
	{
		CommissioningDraft draft = new();

		draft.PickAll(["stue", "kjokken"]);
		Assert.AreEqual(2, draft.PickedCount);

		draft.DropAll(["stue", "kjokken"]);
		Assert.AreEqual(0, draft.PickedCount);
	}

	/// <summary>
	///     A room configured with explicit entities has no area id, so it is keyed by its display name — otherwise
	///     it would be unswitchable from the one surface that exists to switch rooms on.
	/// </summary>
	[TestMethod]
	public void A_Room_Without_An_Area_Id_Is_Keyed_By_Name()
	{
		AreaConfig loft = new() { Name = "Loftet", Lights = ["light.loft"] };

		Assert.AreEqual("Loftet", CommissioningDraft.RoomKey(loft));

		AdaptiveLightingConfig config = Discovered();
		config.Areas.Add(loft);

		CommissioningDraft draft = new();
		draft.ToggleRoom("Loftet");
		draft.Apply(config, Watched);

		Assert.IsTrue(loft.Enabled);
	}

	// ===================== the checklist's own reads =====================

	/// <summary>Picking rooms is not "answering a sheet": the offer to save answers alone must not appear for it.</summary>
	[TestMethod]
	public void Picking_Rooms_Is_Not_An_Answered_Sheet()
	{
		CommissioningDraft draft = new();
		draft.ToggleRoom("stue");

		Assert.IsFalse(draft.HasAnswers);

		draft.SetAwayDebounce(10);

		Assert.IsTrue(draft.HasAnswers);
	}
}
