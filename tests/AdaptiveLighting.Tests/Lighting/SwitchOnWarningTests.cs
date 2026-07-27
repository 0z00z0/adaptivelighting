using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     What a room's switch says for itself the moment it is turned on.
/// </summary>
/// <remarks>
///     The words rather than the rules — <see cref="LightAuditTests"/> covers which lights are flagged. What is
///     asserted here is the part a person actually reads: that the lights are <i>named</i> rather than counted,
///     that a quiet room stays quiet, and that the advice about the include label is not given to a house that
///     has already taken it.
/// </remarks>
[TestClass]
public sealed class SwitchOnWarningTests
{
	private static LightUnderReview Light(string entityId, string name) => new(entityId, name);

	/// <summary>
	///     A room whose lights are exactly what it commands — a room with nothing behind a group, which is nearly
	///     every room and is the context these tests are about.
	/// </summary>
	private static RoomLights Room(IReadOnlyList<LightUnderReview> commanded) =>
		new(commanded, new HashSet<string>(commanded.Select(light => light.EntityId), StringComparer.OrdinalIgnoreCase));

	/// <summary>The three ordinary lamps a normal living room holds.</summary>
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

	/// <summary>One lamp with nothing wrong with it raises no doubt, so it raises no note.</summary>
	[TestMethod]
	public void One_Ordinary_Light_Says_Nothing()
	{
		Assert.IsNull(SwitchOnWarning.For("Bod", Room([Light("light.bod_taklys", "Bod taklys")]), null));
	}

	/// <summary>One light that <i>is</i> flagged still warns — a count of one is not a reason to stay quiet.</summary>
	[TestMethod]
	public void One_Suspicious_Light_Still_Warns()
	{
		SwitchOnNote? note = SwitchOnWarning.For("Lab", Room([Light("light.lab_taklys_indikator", "Lab taklys indikator")]), null);

		Assert.IsNotNull(note);
		Assert.IsTrue(note.IsWarning);
		StringAssert.Contains(note.Lead, "The one light");
	}

	// ===================== the quiet tier =====================

	/// <summary>
	///     A room of ordinary lamps is not a warning. It gets the same list with no amber and no advice, because
	///     the only thing worth saying there is what "on" now reaches.
	/// </summary>
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

	/// <summary>Every suspect is named with its reason, and the rest are named too — a count leaves them hunting.</summary>
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

	/// <summary>
	///     A long list is written out in full. Truncating it would leave somebody doing exactly the hunt the note
	///     exists to save them, and the total is already in the lead line.
	/// </summary>
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

	/// <summary>The count in the lead is the room's whole light list, which is the surprise worth stating.</summary>
	[TestMethod]
	public void The_Lead_Counts_Every_Light_And_Then_The_Doubtful_Ones()
	{
		SwitchOnNote? note = SwitchOnWarning.For("Stue", TheLivingRoom, null);

		Assert.IsNotNull(note);
		Assert.AreEqual("Stue is on, and will command 5 lights. 3 of them look like something other than room lighting.", note.Lead);
	}

	/// <summary>A room where nothing survives the audit says that instead of listing an empty remainder.</summary>
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

	/// <summary>
	///     <b>The case the audit was commissioned for, and was blind to.</b> The living room reaches
	///     <c>light.stue_vegglys</c> through the group <c>light.stue_alle</c>, and the resolver prefers the group —
	///     so the lamp is not in what the room commands, and the colour-channel rule, which fires only when the
	///     parent is present, matched nothing. The room drove the lamp through the group and fought it with three
	///     channel entities, and the note said nothing at all. The sibling check therefore reads the room's own
	///     lights rather than only the ones that survived group preference.
	/// </summary>
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

		// The lamp the group covers is in the room, and is exactly what group preference removed from the list above.
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

	/// <summary>
	///     The opposite property, which is the one worth being careful about: a channel whose lamp is genuinely not
	///     in this room is not accused of duplicating it. A one-letter suffix on its own is far too thin a thing to
	///     talk somebody out of managing a real light over.
	/// </summary>
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

	/// <summary>
	///     The owner's own suggestion, said concretely: the label, where to name it, and that it is house-wide.
	///     Somebody applying it to fix one room changes every room, and that has to be said before they act.
	/// </summary>
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

	/// <summary>A house that already names an include label has taken the advice, so it is not given again.</summary>
	[TestMethod]
	public void A_House_With_An_Include_Label_Is_Not_Advised_Again()
	{
		SwitchOnNote? note = SwitchOnWarning.For("Stue", TheLivingRoom, "Room light");

		Assert.IsNotNull(note);
		Assert.IsTrue(note.IsWarning, "the lights are still worth naming");
		Assert.IsNull(note.Advice);
	}
}
