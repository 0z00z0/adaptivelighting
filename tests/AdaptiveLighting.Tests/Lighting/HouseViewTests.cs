using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     What the House tab says about a house full of rooms: the row summaries, the counted lines, and which
///     areas are still free to add.
/// </summary>
/// <remarks>
///     <para>
///         Every line here is a sentence assembled from a count, and every one of them is wrong in a way nobody
///         notices until a house has seventeen rooms — "1 rooms follow these exactly", a list that names all
///         eight strays, a footer counting rooms that are not switched off. There is no Razor render harness in
///         this repo, so the words live outside the markup and are asserted here.
///     </para>
///     <para>
///         The row summary is the other thing worth being sure about: it is the only thing a collapsed list says
///         about a room, and it counts through <c>AreaSetupService.OverrideCount</c> rather than through a list
///         of its own precisely because the shipped editor kept a twin of that list and it drifted.
///     </para>
/// </remarks>
[TestClass]
public sealed class HouseViewTests
{
	private static AreaSettings House => new();

	private static AreaConfig Room(string name, string? areaId = null) =>
		new() { Name = name, AreaId = areaId ?? name.ToLowerInvariant() };

	// ===================== a room's row =====================

	/// <summary>A room that states nothing says so, rather than saying "0 of 21 changed".</summary>
	[TestMethod]
	public void An_Untouched_Room_Is_All_Automatic()
	{
		Assert.AreEqual("all automatic", HouseView.RoomSummary(Room("Stue")));
	}

	/// <summary>The count is the schema's, so a room tuned only through the newest settings is not reported as untouched.</summary>
	[TestMethod]
	public void A_Changed_Room_Counts_Its_Own_Settings()
	{
		AreaConfig room = Room("Kontor");
		room.LuxBrightnessEnabled = true;
		room.LuxBrightnessMaxPct = 90;

		Assert.AreEqual($"2 of {AreaView.OverridableSettingCount} changed", HouseView.RoomSummary(room));
	}

	/// <summary>Hand-picked gear is a different fact from a changed setting, and a room can carry both.</summary>
	[TestMethod]
	public void Pinned_Gear_And_Changed_Settings_Are_Both_Reported()
	{
		AreaConfig room = Room("Stue");
		room.Lights = ["light.a", "light.b"];
		room.LuxSensor = "sensor.stue_lux";

		Assert.AreEqual("2 lights, light-level sensor picked by hand", HouseView.RoomSummary(room));

		room.VacancyTimeoutSeconds = 1800;

		Assert.AreEqual(
			$"2 lights, light-level sensor picked by hand · 1 of {AreaView.OverridableSettingCount} changed",
			HouseView.RoomSummary(room));
	}

	/// <summary>Singular and plural, because "1 lights" is the kind of thing a reader stops on.</summary>
	[TestMethod]
	public void Pinned_Gear_Is_Counted_In_English()
	{
		AreaConfig room = Room("Gang");
		room.Lights = ["light.a"];
		room.MotionSensors = ["binary_sensor.a", "binary_sensor.b"];
		room.IgnoreWhenOn = ["binary_sensor.projector"];

		Assert.AreEqual("1 light, 2 motion sensors, 1 blocker", HouseView.PinnedSummary(room));
	}

	// ===================== the stray line =====================

	/// <summary>The empty house says what these settings are for rather than counting nothing.</summary>
	[TestMethod]
	public void With_No_Rooms_The_Line_Says_What_The_Defaults_Are_For()
	{
		StringAssert.Contains(HouseView.StrayLine([], null), "no rooms yet");
	}

	/// <summary>A house where nothing has been tuned says so in one clause, with the count in it.</summary>
	[TestMethod]
	public void A_House_That_Follows_The_Defaults_Says_So()
	{
		Assert.AreEqual("all 3 rooms follow these exactly", HouseView.StrayLine([Room("A"), Room("B"), Room("C")], null));
		Assert.AreEqual("the one room follows these exactly", HouseView.StrayLine([Room("A")], null));
	}

	/// <summary>
	///     The design's own wording: how many follow, then who does not — named while the list is short and
	///     counted once it would be a wall.
	/// </summary>
	[TestMethod]
	public void Straying_Rooms_Are_Named_Then_Counted()
	{
		List<AreaConfig> rooms = [.. Enumerable.Range(0, 14).Select(index => Room($"Room{index}"))];

		AreaConfig stue = Room("Stue");
		stue.VacancyTimeoutSeconds = 1800;

		AreaConfig kontor = Room("Kontor");
		kontor.LuxThreshold = 80;

		rooms.Add(stue);
		rooms.Add(kontor);

		Assert.AreEqual("14 rooms follow these exactly; Stue and Kontor carry their own values", HouseView.StrayLine(rooms, null));

		for (int index = 0; index < 6; index++)
			rooms[index].WelcomeHome = true;

		StringAssert.Contains(HouseView.StrayLine(rooms, null), "and 6 others carry their own values");
	}

	/// <summary>Plurals on both halves: one follower follows, one stray carries.</summary>
	[TestMethod]
	public void The_Stray_Line_Agrees_With_Itself_About_Number()
	{
		AreaConfig stue = Room("Stue");
		stue.VacancyTimeoutSeconds = 1800;

		Assert.AreEqual(
			"1 room follows these exactly; Stue carries their own values",
			HouseView.StrayLine([Room("Gang"), stue], null));
	}

	/// <summary>A house where every room has been tuned is a different sentence, not a "0 rooms follow" one.</summary>
	[TestMethod]
	public void A_House_Where_Nothing_Follows_Says_That_Instead()
	{
		AreaConfig stue = Room("Stue");
		stue.VacancyTimeoutSeconds = 1800;

		StringAssert.StartsWith(HouseView.StrayLine([stue], null), "every room carries values of its own");
	}

	// ===================== the switched-off line =====================

	/// <summary>No line at all when nothing is hidden — the rule lives beside the wording it governs.</summary>
	[TestMethod]
	public void Nothing_Switched_Off_Gets_No_Line()
	{
		Assert.IsNull(HouseView.SwitchedOffLine([Room("A"), Room("B")], House));
	}

	/// <summary>Inheritance counts: a room that states nothing follows the house's own default.</summary>
	[TestMethod]
	public void Switched_Off_Rooms_Are_Counted_Through_Inheritance()
	{
		AreaConfig off = Room("Bod");
		off.Enabled = false;

		StringAssert.StartsWith(HouseView.SwitchedOffLine([Room("Stue"), off], House), "1 room is switched off");

		AreaSettings houseOff = new() { Enabled = false };

		StringAssert.StartsWith(HouseView.SwitchedOffLine([Room("Stue"), off], houseOff), "2 rooms are switched off");
	}

	// ===================== adding a room =====================

	/// <summary>An area a room already claims is not offered again — two rooms over one area is a fight over lights.</summary>
	[TestMethod]
	public void Only_Unclaimed_Areas_Can_Be_Added()
	{
		IReadOnlyList<AreaOption> areas =
		[
			new AreaOption("stue", "Stue", 2, 1, 1),
			new AreaOption("bod", "Bod", 1, 0, 0),
			new AreaOption("loft", "Loftet", 1, 1, 0)
		];

		IReadOnlyList<AreaOption> offered = HouseView.Unconfigured(areas, [Room("Stue", "stue")]);

		CollectionAssert.AreEqual(new[] { "bod", "loft" }, offered.Select(area => area.Id).ToArray());
	}

	/// <summary>A room with no area id claims nothing, so it hides nothing from the picker.</summary>
	[TestMethod]
	public void A_Room_With_No_Area_Claims_Nothing()
	{
		IReadOnlyList<AreaOption> areas = [new AreaOption("stue", "Stue", 2, 1, 1)];

		Assert.AreEqual(1, HouseView.Unconfigured(areas, [new AreaConfig { Name = "Loose" }]).Count);
	}

	// ===================== the small print =====================

	/// <summary>
	///     The name falls back the same way the room page's own header does — and with no registry to ask, the
	///     area id is as far as it gets, which is what a house whose Home Assistant is still connecting sees.
	/// </summary>
	[TestMethod]
	public void A_Room_Is_Named_By_Name_Then_Area_Then_Nothing()
	{
		Assert.AreEqual("Stue", HouseView.DisplayName(new AreaConfig { Name = "Stue", AreaId = "stue" }, null));
		Assert.AreEqual("stue", HouseView.DisplayName(new AreaConfig { AreaId = "stue" }, null));
		Assert.AreEqual("New room", HouseView.DisplayName(new AreaConfig(), null));
	}

	/// <summary>
	///     The row reads the registry's name for the area, so a room proposed with nothing but an area id is
	///     called what Home Assistant calls it. A name in the document still outranks it.
	/// </summary>
	[TestMethod]
	public void A_Room_With_No_Name_Takes_The_Registrys()
	{
		FakeAreaRegistry registry = new();
		registry.Names["kjeller_bad"] = "Kjeller - Bad";

		Assert.AreEqual("Kjeller - Bad", HouseView.DisplayName(new AreaConfig { AreaId = "kjeller_bad" }, registry));
		Assert.AreEqual("Kjellerbadet", HouseView.DisplayName(new AreaConfig { AreaId = "kjeller_bad", Name = "Kjellerbadet" }, registry));
		Assert.AreEqual("sykkelbod", HouseView.DisplayName(new AreaConfig { AreaId = "sykkelbod" }, registry));
	}

	/// <summary>The stray line names rooms the same way the rows do, rather than listing slugs beside names.</summary>
	[TestMethod]
	public void The_Stray_Line_Names_Rooms_As_Home_Assistant_Does()
	{
		FakeAreaRegistry registry = new();
		registry.Names["kjokken"] = "Kjøkken";

		AreaConfig kitchen = new() { AreaId = "kjokken" };
		kitchen.VacancyTimeoutSeconds = 1800;

		StringAssert.Contains(HouseView.StrayLine([kitchen], registry), "Kjøkken");
	}

	/// <summary>An area id reaches a URL, so it is escaped; a room without one has no address at all.</summary>
	[TestMethod]
	public void A_Room_Link_Escapes_Its_Area_Id()
	{
		Assert.AreEqual("room/stue", HouseView.RoomHref("stue"));
		Assert.AreEqual("room/rom%20med%20mellomrom", HouseView.RoomHref("rom med mellomrom"));
		Assert.IsNull(HouseView.RoomHref(null));
		Assert.IsNull(HouseView.RoomHref(""));
	}

	/// <summary>English joins a list with a comma and an "and" — and never says "and 1 other".</summary>
	[TestMethod]
	public void Names_Are_Joined_The_Way_English_Joins_Them()
	{
		Assert.AreEqual("", HouseView.NameList([]));
		Assert.AreEqual("Stue", HouseView.NameList(["Stue"]));
		Assert.AreEqual("Stue and Bod", HouseView.NameList(["Stue", "Bod"]));
		Assert.AreEqual("Stue, Bod and Gang", HouseView.NameList(["Stue", "Bod", "Gang"]));
		Assert.AreEqual("Stue, Bod and 2 others", HouseView.NameList(["Stue", "Bod", "Gang", "Kontor"]));
	}
}
