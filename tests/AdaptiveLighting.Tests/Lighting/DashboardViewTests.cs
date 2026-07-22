using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The dashboard's decisions about what to draw, tested where they live rather than in markup.
/// </summary>
/// <remarks>
///     This repo has no Razor render-test harness and deliberately does not gain one, so the three decisions the
///     grid rests on were extracted as pure functions: which live reports earn a card, how many rooms are hidden,
///     and whether the house is still waiting for its first choice of rooms. Each fails silently if it is wrong —
///     a card wrongly hidden looks exactly like an engine that stopped reporting, and a first-run state shown over
///     a broken connection sends somebody to the settings page to fix something that is not there.
/// </remarks>
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

	/// <summary>
	///     Only rooms the owner switched on get cards. The engine keeps observing and publishing the rest — that is
	///     deliberate and unchanged — so the reports still arrive and the dashboard still has to decline them.
	/// </summary>
	[TestMethod]
	public void Only_Switched_On_Rooms_Get_A_Card()
	{
		RoomView[] rooms = [Room("Stue", enabled: true), Room("Kjøkken", enabled: false)];
		AreaSnapshot[] reports = [Report("Stue", "Stue"), Report("Kjøkken", "Kjøkken")];

		IReadOnlyList<AreaSnapshot> visible = AreaView.VisibleCards(reports, rooms);

		Assert.AreEqual(1, visible.Count);
		Assert.AreEqual("Stue", visible[0].AreaName);
	}

	/// <summary>
	///     The area id decides, not the name: a room renamed in the document while the page is open must still be
	///     recognised as the room whose switch was flipped.
	/// </summary>
	[TestMethod]
	public void A_Report_Is_Matched_By_Area_Id_First()
	{
		RoomView[] rooms = [new("stue", "Living room", false, 3)];

		IReadOnlyList<AreaSnapshot> visible = AreaView.VisibleCards([Report("Stue", "stue")], rooms);

		Assert.AreEqual(0, visible.Count, "the switched-off room was recognised by its id, not its old name");
	}

	/// <summary>
	///     A room configured with explicit entities has no area id, and neither do reports from builds that predate
	///     one. The display name is the fallback join, exactly as the snapshot cache and the settings list use it.
	/// </summary>
	[TestMethod]
	public void A_Report_With_No_Area_Id_Falls_Back_To_The_Name()
	{
		RoomView[] rooms = [new(null, "Loftstue", false, 2), new(null, "Bod", true, 1)];

		IReadOnlyList<AreaSnapshot> visible = AreaView.VisibleCards([Report("Loftstue"), Report("Bod")], rooms);

		Assert.AreEqual(1, visible.Count);
		Assert.AreEqual("Bod", visible[0].AreaName);
	}

	/// <summary>
	///     A report from a room the document does not name is shown, never hidden. The dashboard's job is to say
	///     what the engine is doing, and a card dropped because the document changed underneath it would be
	///     indistinguishable from a fault.
	/// </summary>
	[TestMethod]
	public void A_Report_From_No_Known_Room_Is_Still_Shown()
	{
		RoomView[] rooms = [Room("Stue", enabled: true)];

		IReadOnlyList<AreaSnapshot> visible = AreaView.VisibleCards([Report("Vaskerom", "vaskerom")], rooms);

		Assert.AreEqual(1, visible.Count);
		Assert.AreEqual("Vaskerom", visible[0].AreaName);
	}

	/// <summary>The cards keep the order the cache handed them in — the filter decides membership, not sequence.</summary>
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

	/// <summary>
	///     The count comes from the document, not from the cards that failed to render. A room switched off before
	///     the engine ever reported it has no snapshot to be missing, and a footer counting absences would say
	///     "0 rooms are switched off" to somebody looking at a house that is doing nothing.
	/// </summary>
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

	/// <summary>No footer when nothing is hidden — a line reporting zero would be noise on every healthy house.</summary>
	[TestMethod]
	public void Nothing_Hidden_Means_No_Footer_At_All()
	{
		Assert.IsNull(AreaView.HiddenNote(0));
		Assert.IsNull(AreaView.HiddenNote(-1), "a negative count is still nothing to report");
	}

	/// <summary>
	///     The footer says how many and offers the way back, and it says both in grammatical English on either side
	///     of one. The link half has to read as an instruction on its own, because that is the part a person clicks.
	/// </summary>
	[TestMethod]
	public void The_Footer_Names_The_Count_And_Offers_The_Way_Back()
	{
		HiddenRoomsNote many = AreaView.HiddenNote(4)!;
		Assert.AreEqual("4 rooms are switched off —", many.Lead);
		Assert.AreEqual("turn them on in Settings", many.LinkText);

		HiddenRoomsNote one = AreaView.HiddenNote(1)!;
		Assert.AreEqual("1 room is switched off —", one.Lead);
		Assert.AreEqual("turn it on in Settings", one.LinkText);
	}

	// ===================== waiting for the first choice =====================

	/// <summary>
	///     The state a fresh install lands in: set-up wrote every discovered room switched off, so the house is
	///     configured and deliberately doing nothing.
	/// </summary>
	[TestMethod]
	public void A_House_With_Rooms_And_None_Switched_On_Is_Waiting_To_Be_Chosen_From()
	{
		RoomView[] rooms = [Room("Stue", enabled: false), Room("Kjøkken", enabled: false)];

		Assert.IsTrue(AreaView.IsAwaitingRoomChoice(rooms, engineIsAttached: true));
	}

	/// <summary>One room switched on is a running house, however quiet the rest of it is.</summary>
	[TestMethod]
	public void One_Room_Switched_On_Is_Not_A_First_Run()
	{
		RoomView[] rooms = [Room("Stue", enabled: true), Room("Kjøkken", enabled: false)];

		Assert.IsFalse(AreaView.IsAwaitingRoomChoice(rooms, engineIsAttached: true));
	}

	/// <summary>
	///     A broken connection is never dressed up as onboarding. Offering "choose which rooms to switch on" to
	///     somebody whose Home Assistant is unreachable would hide the real problem behind a to-do list.
	/// </summary>
	[TestMethod]
	public void A_Disconnected_House_Is_Not_Onboarding()
	{
		RoomView[] rooms = [Room("Stue", enabled: false)];

		Assert.IsFalse(AreaView.IsAwaitingRoomChoice(rooms, engineIsAttached: false));
	}

	/// <summary>An empty document has nothing to choose from; the settings page already speaks to that state.</summary>
	[TestMethod]
	public void A_House_With_No_Rooms_Has_Nothing_To_Choose_From()
	{
		Assert.IsFalse(AreaView.IsAwaitingRoomChoice([], engineIsAttached: true));
	}
}
