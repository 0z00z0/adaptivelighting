using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     What the commissioning board's one commit does to a document, and mostly what it does not.
/// </summary>
/// <remarks>
///     An unanswered sheet leaves the document byte-identical. Nearly every test here asserts an absence.
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

	// The name reaches the document trimmed.
	[TestMethod]
	public void House_Name_Is_Staged_Then_Written()
	{
		AdaptiveLightingConfig config = Discovered();

		CommissioningDraft draft = new();
		draft.SetHouseName("  B1  ");
		draft.Apply(config, Watched);

		Assert.AreEqual("B1", config.ConfigName);
	}

	// Clearing the box clears the name; the placeholder is not pinned into the document.
	[TestMethod]
	public void Cleared_House_Name_Clears_The_Document()
	{
		AdaptiveLightingConfig config = Discovered();

		CommissioningDraft draft = new();
		draft.SetHouseName("");
		draft.Apply(config, Watched);

		Assert.IsNull(config.ConfigName);
	}

	[TestMethod]
	public void Away_Debounce_Is_Written_When_Picked()
	{
		AdaptiveLightingConfig config = Discovered();

		CommissioningDraft draft = new();
		draft.SetAwayDebounce(15);
		draft.Apply(config, Watched);

		Assert.AreEqual(15, config.Global.AwayDebounceMinutes);
	}

	[TestMethod]
	public void Dropping_A_Person_Writes_The_Rest()
	{
		AdaptiveLightingConfig config = Discovered();

		CommissioningDraft draft = new();
		draft.TogglePerson("device_tracker.bilen");
		draft.Apply(config, Watched);

		CollectionAssert.AreEqual(new[] { "person.espen", "person.nora" }, config.Global.Persons);
	}

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

	// An empty Persons list means watch every person.* HA knows, so dropping one has to pin the rest.
	// Filtering the document's own empty list does nothing, and the car keeps the house occupied.
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

	[TestMethod]
	public void Keeping_The_House_Mode_Writes_Nothing()
	{
		AdaptiveLightingConfig config = Discovered();

		CommissioningDraft draft = new();
		draft.SetHouseMode(keep: true);
		draft.Apply(config, Watched);

		Assert.IsNotNull(config.Global.HouseMode);
	}

	[TestMethod]
	public void Detaching_Drops_The_House_Mode()
	{
		AdaptiveLightingConfig config = Discovered();

		CommissioningDraft draft = new();
		draft.SetHouseMode(keep: false);
		draft.Apply(config, Watched);

		Assert.IsNull(config.Global.HouseMode);
	}

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

	[TestMethod]
	public void A_Floor_Can_Be_Picked_And_Dropped_As_One()
	{
		CommissioningDraft draft = new();

		draft.PickAll(["stue", "kjokken"]);
		Assert.AreEqual(2, draft.PickedCount);

		draft.DropAll(["stue", "kjokken"]);
		Assert.AreEqual(0, draft.PickedCount);
	}

	// ===================== the board opens with nothing picked =====================

	[TestMethod]
	public void A_Fresh_Board_Has_Picked_Nothing()
	{
		AdaptiveLightingConfig config = Discovered();
		string[] keys = [.. config.Areas.Select(CommissioningDraft.RoomKey)];

		CommissioningDraft draft = new();

		Assert.AreEqual(0, draft.PickedCount);
		Assert.IsFalse(Array.Exists(keys, key => draft.IsPicked(key)));
		Assert.IsFalse(draft.IncludesAll(keys), "and the include-all box is clear along with the rooms");
		Assert.AreEqual("Switch on the rooms you pick", CommissioningVerdicts.CommitLabel(draft.PickedCount));
	}

	[TestMethod]
	public void Include_All_Picks_Every_Room_And_Leaves_None_Behind()
	{
		AdaptiveLightingConfig config = Discovered();
		string[] keys = [.. config.Areas.Select(CommissioningDraft.RoomKey)];

		CommissioningDraft draft = new();
		draft.PickAll(keys);

		Assert.IsTrue(draft.IncludesAll(keys));
		Assert.AreEqual("Switch on 3 rooms", CommissioningVerdicts.CommitLabel(draft.PickedCount));
		Assert.IsNull(CommissioningVerdicts.RestLine(draft.PickedCount, keys.Length), "there is no rest to send anywhere");

		draft.Apply(config, Watched);

		Assert.IsTrue(config.Areas.TrueForAll(area => area.Enabled == true));
	}

	[TestMethod]
	public void Clearing_Include_All_Drops_Every_Room()
	{
		AdaptiveLightingConfig config = Discovered();
		string[] keys = [.. config.Areas.Select(CommissioningDraft.RoomKey)];

		CommissioningDraft draft = new();
		draft.PickAll(keys);
		draft.DropAll(keys);

		Assert.AreEqual(0, draft.PickedCount);
		Assert.IsFalse(draft.IncludesAll(keys));

		draft.Apply(config, Watched);

		Assert.IsTrue(config.Areas.TrueForAll(area => area.Enabled == false), "and the document is as discovery left it");
	}

	[TestMethod]
	public void Include_All_Goes_Clear_The_Moment_One_Room_Is_Dropped()
	{
		AdaptiveLightingConfig config = Discovered();
		string[] keys = [.. config.Areas.Select(CommissioningDraft.RoomKey)];

		CommissioningDraft draft = new();
		draft.PickAll(keys);
		draft.ToggleRoom("gang");

		Assert.IsFalse(draft.IncludesAll(keys));
		Assert.AreEqual("Switch on 2 rooms", CommissioningVerdicts.CommitLabel(draft.PickedCount));
		Assert.AreEqual(
			"The other room stays listed under House, with its own switch.",
			CommissioningVerdicts.RestLine(draft.PickedCount, keys.Length));
	}

	// The floor headers read the same predicate, so it has to answer for a subset without seeing the whole board.
	[TestMethod]
	public void Everything_Included_Is_Asked_Of_The_Rooms_On_Screen()
	{
		CommissioningDraft draft = new();
		draft.PickAll(["stue", "kjokken"]);

		Assert.IsTrue(draft.IncludesAll(["stue"]));
		Assert.IsFalse(draft.IncludesAll(["stue", "loft"]));
		Assert.IsFalse(draft.IncludesAll([]), "with no rooms drawn there is nothing to have included");
	}

	// A room with explicit entities has no area id, so the key falls back to the display name.
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

	// Picking rooms is not answering a sheet, so the save-answers offer must stay hidden.
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
