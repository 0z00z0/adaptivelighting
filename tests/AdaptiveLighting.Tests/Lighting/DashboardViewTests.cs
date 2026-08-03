using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The dashboard's decisions about what to draw: which reports earn a card, how many rooms are hidden, and
///     whether the house is still waiting for its first choice of rooms.
/// </summary>
[TestClass]
public sealed class DashboardViewTests
{
	private static RoomView Room(string name, bool enabled, string? areaId = null, int lights = 1) =>
		new(areaId ?? name, name, enabled, lights);

	private static AreaSnapshot Report(string name, string? areaId = null) =>
		new(
			name,
			AreaState.AutoVacant,
			TransitionReason.Startup,
			HouseMode.Home,
			false,
			null,
			null,
			null,
			null,
			DateTimeOffset.UnixEpoch,
			null,
			null,
			null,
			null,
			AreaId: areaId);

	// ===================== which reports earn a card =====================

	// The engine observes and publishes switched-off rooms too, so their reports arrive and must be declined here.
	[TestMethod]
	public void Only_Switched_On_Rooms_Get_A_Card()
	{
		RoomView[] rooms = [Room("Stue", enabled: true), Room("Kjøkken", enabled: false)];
		AreaSnapshot[] reports = [Report("Stue", "Stue"), Report("Kjøkken", "Kjøkken")];

		IReadOnlyList<AreaSnapshot> visible = AreaView.VisibleCards(reports, rooms);

		Assert.AreEqual(1, visible.Count);
		Assert.AreEqual("Stue", visible[0].AreaName);
	}

	[TestMethod]
	public void A_Report_Is_Matched_By_Area_Id_First()
	{
		RoomView[] rooms = [new("stue", "Living room", false, 3)];

		IReadOnlyList<AreaSnapshot> visible = AreaView.VisibleCards([Report("Stue", "stue")], rooms);

		Assert.AreEqual(0, visible.Count, "the switched-off room was recognised by its id, not its old name");
	}

	// A room configured with explicit entities has no area id. The display name is the fallback join, the same
	// one the snapshot cache and the settings list use.
	[TestMethod]
	public void A_Report_With_No_Area_Id_Falls_Back_To_The_Name()
	{
		RoomView[] rooms = [new(null, "Loftstue", false, 2), new(null, "Bod", true, 1)];

		IReadOnlyList<AreaSnapshot> visible = AreaView.VisibleCards([Report("Loftstue"), Report("Bod")], rooms);

		Assert.AreEqual(1, visible.Count);
		Assert.AreEqual("Bod", visible[0].AreaName);
	}

	[TestMethod]
	public void A_Report_From_No_Known_Room_Is_Still_Shown()
	{
		RoomView[] rooms = [Room("Stue", enabled: true)];

		IReadOnlyList<AreaSnapshot> visible = AreaView.VisibleCards([Report("Vaskerom", "vaskerom")], rooms);

		Assert.AreEqual(1, visible.Count);
		Assert.AreEqual("Vaskerom", visible[0].AreaName);
	}

	[TestMethod]
	public void Filtering_Keeps_The_Order_It_Was_Given()
	{
		RoomView[] rooms =
		[
			Room("Stue", enabled: true),
			Room("Kjøkken", enabled: false),
			Room("Gang", enabled: true)
		];

		IReadOnlyList<AreaSnapshot> visible = AreaView.VisibleCards(
			[Report("Stue", "Stue"), Report("Kjøkken", "Kjøkken"), Report("Gang", "Gang")],
			rooms);

		CollectionAssert.AreEqual(
			new[] { "Stue", "Gang" },
			visible.Select(snapshot => snapshot.AreaName).ToArray());
	}

	// ===================== how many are hidden =====================

	// The count comes from the document, not from missing cards: a room switched off before the engine ever
	// reported it has no snapshot to be absent.
	[TestMethod]
	public void The_Hidden_Count_Counts_Rooms_The_Owner_Switched_Off()
	{
		RoomView[] rooms =
		[
			Room("Stue", enabled: true),
			Room("Kjøkken", enabled: false),
			Room("Gang", enabled: false),
			Room("Bad", enabled: false),
			Room("Soverom", enabled: false)
		];

		Assert.AreEqual(4, AreaView.SwitchedOffCount(rooms));
		Assert.AreEqual(0, AreaView.SwitchedOffCount([Room("Stue", enabled: true)]));
		Assert.AreEqual(0, AreaView.SwitchedOffCount([]));
	}

	[TestMethod]
	public void Nothing_Hidden_Means_No_Footer_At_All()
	{
		Assert.IsNull(AreaView.HiddenNote(0));
		Assert.IsNull(AreaView.HiddenNote(-1), "a negative count is still nothing to report");
	}

	[TestMethod]
	public void The_Footer_Names_The_Count_And_Offers_The_Way_Back()
	{
		HiddenRoomsNote many = AreaView.HiddenNote(4)!;
		Assert.AreEqual("4 rooms are switched off —", many.Lead);
		Assert.AreEqual("turn them on in Configuration", many.LinkText);

		HiddenRoomsNote one = AreaView.HiddenNote(1)!;
		Assert.AreEqual("1 room is switched off —", one.Lead);
		Assert.AreEqual("turn it on in Configuration", one.LinkText);
	}

	// ===================== waiting for the first choice =====================

	// Set-up writes every discovered room switched off, so a configured house doing nothing is the fresh-install
	// state, not a fault.
	[TestMethod]
	public void A_House_With_Rooms_And_None_Switched_On_Is_Waiting_To_Be_Chosen_From()
	{
		RoomView[] rooms = [Room("Stue", enabled: false), Room("Kjøkken", enabled: false)];

		Assert.IsTrue(AreaView.IsAwaitingRoomChoice(rooms, engineIsAttached: true));
	}

	[TestMethod]
	public void One_Room_Switched_On_Is_Not_A_First_Run()
	{
		RoomView[] rooms = [Room("Stue", enabled: true), Room("Kjøkken", enabled: false)];

		Assert.IsFalse(AreaView.IsAwaitingRoomChoice(rooms, engineIsAttached: true));
	}

	[TestMethod]
	public void A_Disconnected_House_Is_Not_Onboarding()
	{
		RoomView[] rooms = [Room("Stue", enabled: false)];

		Assert.IsFalse(AreaView.IsAwaitingRoomChoice(rooms, engineIsAttached: false));
	}

	[TestMethod]
	public void A_House_With_No_Rooms_Has_Nothing_To_Choose_From()
	{
		Assert.IsFalse(AreaView.IsAwaitingRoomChoice([], engineIsAttached: true));
	}
}
