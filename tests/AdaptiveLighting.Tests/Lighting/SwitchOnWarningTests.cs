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

	/// <summary>The three ordinary lamps a normal living room holds.</summary>
	private static IReadOnlyList<LightUnderReview> ThreeLamps =>
	[
		Light("light.stue_taklys", "Taklys"),
		Light("light.stue_leselampe", "Leselampe"),
		Light("light.stue_gulvlampe", "Gulvlampe")
	];

	/// <summary>A cut of the live house: two real lamps, an access point, a fridge and a colour channel.</summary>
	private static IReadOnlyList<LightUnderReview> TheLivingRoom =>
	[
		Light("light.stue_taklys", "Taklys"),
		Light("light.stue_vegglys", "Vegglys"),
		Light("light.stue_vegglys_r", "Vegglys R"),
		Light("light.u7_pro_livingroom_led", "U7 Pro Livingroom LED"),
		Light("light.kjoleskap_colour_light", "Kjøleskap colour light")
	];

	// ===================== when it says nothing =====================

	/// <summary>One lamp with nothing wrong with it raises no doubt, so it raises no note.</summary>
	[TestMethod]
	public void One_Ordinary_Light_Says_Nothing()
	{
		Assert.IsNull(SwitchOnWarning.For("Bod", [Light("light.bod_taklys", "Bod taklys")], null));
	}

	/// <summary>One light that <i>is</i> flagged still warns — a count of one is not a reason to stay quiet.</summary>
	[TestMethod]
	public void One_Suspicious_Light_Still_Warns()
	{
		SwitchOnNote? note = SwitchOnWarning.For("Lab", [Light("light.lab_taklys_indikator", "Lab taklys indikator")], null);

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

		SwitchOnNote? note = SwitchOnWarning.For("Stue", many, null);

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
			[Light("light.lab_taklys_status_led", "Lab status LED"), Light("light.us_8_60w_lab_led", "US-8-60W LED")],
			null);

		Assert.IsNotNull(note);
		StringAssert.Contains(note.Lead, "none of them looks like room lighting");
		Assert.IsNull(note.OthersLine);
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
