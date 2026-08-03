using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     What a room's switch says the moment it is turned on. Which lights get flagged is
///     <see cref="LightAuditTests"/>.
/// </summary>
[TestClass]
public sealed class SwitchOnWarningTests
{
	private static LightUnderReview Light(string entityId, string name) => new(entityId, name);

	/// <summary>A room whose lights are what it commands, with nothing hidden behind a group.</summary>
	private static RoomLights Room(IReadOnlyList<LightUnderReview> commanded) =>
		new(commanded, new HashSet<string>(commanded.Select(light => light.EntityId), StringComparer.OrdinalIgnoreCase));

	private static RoomLights ThreeLamps => Room(
	[
		Light("light.stue_taklys", "Taklys"),
		Light("light.stue_leselampe", "Leselampe"),
		Light("light.stue_gulvlampe", "Gulvlampe")
	]);

	/// <summary>A cut of the live house: two real lamps, an access point, a fridge and a colour channel.</summary>
	private static RoomLights TheLivingRoom => Room(
	[
		Light("light.stue_taklys", "Taklys"),
		Light("light.stue_vegglys", "Vegglys"),
		Light("light.stue_vegglys_r", "Vegglys R"),
		Light("light.u7_pro_livingroom_led", "U7 Pro Livingroom LED"),
		Light("light.kjoleskap_colour_light", "Kjøleskap colour light")
	]);

	// ===================== when it says nothing =====================

	[TestMethod]
	public void One_Ordinary_Light_Says_Nothing()
	{
		Assert.IsNull(SwitchOnWarning.For("Bod", Room([Light("light.bod_taklys", "Bod taklys")]), null));
	}

	[TestMethod]
	public void One_Suspicious_Light_Still_Warns()
	{
		SwitchOnNote? note = SwitchOnWarning.For("Lab", Room([Light("light.lab_taklys_indikator", "Lab taklys indikator")]), null);

		Assert.IsNotNull(note);
		Assert.IsTrue(note.IsWarning);
		StringAssert.Contains(note.Lead, "The one light");
	}

	// ===================== the quiet tier =====================

	[TestMethod]
	public void Several_Ordinary_Lights_Are_A_Remark_Rather_Than_A_Warning()
	{
		SwitchOnNote? note = SwitchOnWarning.For("Stue", ThreeLamps, null);

		Assert.IsNotNull(note);
		Assert.IsFalse(note.IsWarning, "nothing here deserves amber");
		Assert.IsNull(note.Advice, "there is nothing to advise about a room of ordinary lamps");
		Assert.AreEqual("Stue is on, and will command 3 lights.", note.Lead);
		Assert.AreEqual("They are Taklys, Leselampe and Gulvlampe.", note.OthersLine);
	}

	// ===================== naming, never counting =====================

	[TestMethod]
	public void The_Warning_Names_Every_Light()
	{
		SwitchOnNote? note = SwitchOnWarning.For("Stue", TheLivingRoom, null);

		Assert.IsNotNull(note);
		Assert.IsTrue(note.IsWarning);

		CollectionAssert.AreEqual(
			new[] { "light.stue_vegglys_r", "light.u7_pro_livingroom_led", "light.kjoleskap_colour_light" },
			note.Suspicious.Select(suspect => suspect.EntityId).ToArray());

		CollectionAssert.AreEqual(new[] { "Taklys", "Vegglys" }, note.Others.ToArray());
		StringAssert.Contains(note.OthersLine ?? "", "Taklys and Vegglys");
	}

	[TestMethod]
	public void A_Long_List_Is_Not_Truncated()
	{
		List<LightUnderReview> many =
		[
			Light("light.u7_pro_livingroom_led", "U7 Pro Livingroom LED"),
			.. Enumerable.Range(1, 12).Select(index => Light($"light.stue_lampe_{index}", $"Lampe {index}"))
		];

		SwitchOnNote? note = SwitchOnWarning.For("Stue", Room(many), null);

		Assert.IsNotNull(note);
		Assert.AreEqual(12, note.Others.Count);
		StringAssert.Contains(note.OthersLine ?? "", "Lampe 12");
		Assert.IsFalse((note.OthersLine ?? "").Contains("others", StringComparison.Ordinal));
	}

	[TestMethod]
	public void The_Lead_Counts_Every_Light_And_Then_The_Doubtful_Ones()
	{
		SwitchOnNote? note = SwitchOnWarning.For("Stue", TheLivingRoom, null);

		Assert.IsNotNull(note);
		Assert.AreEqual("Stue is on, and will command 5 lights. 3 of them look like something other than room lighting.", note.Lead);
	}

	[TestMethod]
	public void A_Room_Of_Nothing_But_Indicators_Says_So()
	{
		SwitchOnNote? note = SwitchOnWarning.For(
			"Lab",
			Room([Light("light.lab_taklys_status_led", "Lab status LED"), Light("light.us_8_60w_lab_led", "US-8-60W LED")]),
			null);

		Assert.IsNotNull(note);
		StringAssert.Contains(note.Lead, "none of them looks like room lighting");
		Assert.IsNull(note.OthersLine);
	}

	// ===================== the lamp behind the group =====================

	// The resolver prefers a group, so a lamp reached through one is absent from what the room commands. The
	// sibling check reads the room's own lights, not the commanded list, or a channel's parent is never found.
	[TestMethod]
	public void A_Colour_Channel_Is_Flagged_When_Its_Lamp_Is_Reached_Through_A_Group()
	{
		IReadOnlyList<LightUnderReview> commanded =
		[
			Light("light.stue_alle", "Stue alle"),
			Light("light.stue_vegglys_r", "Vegglys R"),
			Light("light.stue_vegglys_g", "Vegglys G"),
			Light("light.stue_vegglys_b", "Vegglys B")
		];

		// The lamp the group covers is in the room, and is what group preference removed from the list above.
		HashSet<string> inTheRoom = new(commanded.Select(light => light.EntityId), StringComparer.OrdinalIgnoreCase)
		{
			"light.stue_vegglys"
		};

		SwitchOnNote? note = SwitchOnWarning.For("Stue", new RoomLights(commanded, inTheRoom), null);

		Assert.IsNotNull(note);

		CollectionAssert.AreEqual(
			new[] { "light.stue_vegglys_r", "light.stue_vegglys_g", "light.stue_vegglys_b" },
			note.Suspicious.Select(suspect => suspect.EntityId).ToArray(),
			"three channels of one lamp the room already drives, and not one of them was raised before");

		StringAssert.Contains(note.Suspicious[0].Reason, "colour channel of stue_vegglys");

		Assert.AreEqual(
			"Stue is on, and will command 4 lights. 3 of them look like something other than room lighting.",
			note.Lead,
			"the count is still what the engine will drive — the lamp behind the group is context, never a light to name");

		CollectionAssert.AreEqual(new[] { "Stue alle" }, note.Others.ToArray());
	}

	[TestMethod]
	public void A_Channel_Whose_Lamp_Is_Elsewhere_Is_Not_Flagged()
	{
		IReadOnlyList<LightUnderReview> commanded =
		[
			Light("light.stue_taklys", "Taklys"),
			Light("light.stue_vegglys_r", "Vegglys R")
		];

		SwitchOnNote? note = SwitchOnWarning.For(
			"Stue",
			new RoomLights(commanded, new HashSet<string>(commanded.Select(light => light.EntityId), StringComparer.OrdinalIgnoreCase)),
			null);

		Assert.IsNotNull(note);
		Assert.IsFalse(note.IsWarning, "no lamp for it to be a channel of, so there is nothing to accuse it of");
	}

	// ===================== the recommendation =====================

	// The include label is house-wide: applying it to fix one room changes every room.
	[TestMethod]
	public void The_Advice_Names_The_Label_The_Setting_And_Its_Reach()
	{
		SwitchOnNote? note = SwitchOnWarning.For("Stue", TheLivingRoom, null);

		Assert.IsNotNull(note?.Advice);
		StringAssert.Contains(note.Advice, "Room light");
		StringAssert.Contains(note.Advice, "Only manage lights with");
		StringAssert.Contains(note.Advice, "every room");
		StringAssert.Contains(note.Advice, "Stue");
	}

	[TestMethod]
	public void A_House_With_An_Include_Label_Is_Not_Advised_Again()
	{
		SwitchOnNote? note = SwitchOnWarning.For("Stue", TheLivingRoom, "Room light");

		Assert.IsNotNull(note);
		Assert.IsTrue(note.IsWarning, "the lights are still worth naming");
		Assert.IsNull(note.Advice);
	}
}
