using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Which of a room's lights look like something other than room lighting.</summary>
// Every rule carries a test for what it must not fire on: a false accusation costs a real lamp.
[TestClass]
public sealed class LightAuditTests
{
	private static LightUnderReview Light(string entityId, string? name = null) =>
		new(entityId, name ?? entityId);

	private static string? Reason(string entityId, string? name = null, params string[] roommates) =>
		LightAudit.ReasonFor(Light(entityId, name), new HashSet<string>(roommates, StringComparer.Ordinal));

	private static bool IsFlagged(string entityId, string? name = null, params string[] roommates) =>
		Reason(entityId, name, roommates) is not null;

	// ===================== status and indicator wording =====================

	[TestMethod]
	public void Anything_Named_As_A_Status_Or_Indicator_Is_Flagged()
	{
		Assert.IsTrue(IsFlagged("light.garasje_arbeidsbenk_r3_status_led"));
		Assert.IsTrue(IsFlagged("light.seeedstudio_xiao_2ch_em_status_led"));
		Assert.IsTrue(IsFlagged("light.lab_taklys_indikator"));
	}

	/// <summary>Order matters: the status rule runs before the lamp guard, or "taklys" in <c>lab_taklys_status_led</c> excuses it.</summary>
	[TestMethod]
	public void A_Ceiling_Lights_Status_Led_Is_Still_A_Status_Light()
	{
		StringAssert.Contains(Reason("light.lab_taklys_status_led") ?? "", "status");
	}

	[TestMethod]
	public void The_Friendly_Name_Can_Give_It_Away()
	{
		Assert.IsTrue(IsFlagged("light.0x00124b0022a1", "Water leak sensor status"));
	}

	// ===================== a trailing LED =====================

	[TestMethod]
	public void Network_Hardware_Leds_Are_Flagged_By_Their_Suffix()
	{
		Assert.IsTrue(IsFlagged("light.u7_pro_livingroom_led"));
		Assert.IsTrue(IsFlagged("light.u7_pro_max_office_led"));
		Assert.IsTrue(IsFlagged("light.us_8_60w_lab_led"));
	}

	/// <summary>An LED strip is a real light, so the suffix must end the id and the name must not say lamp anywhere.</summary>
	[TestMethod]
	public void A_Real_Led_Strip_Is_Not_Flagged()
	{
		Assert.IsFalse(IsFlagged("light.led_strip", "LED strip"));
		Assert.IsFalse(IsFlagged("light.stue_led_strip", "Stue LED strip"));
		Assert.IsFalse(IsFlagged("light.kjokkenbenk_led_spot", "Kjøkkenbenk LED spot"));
	}

	/// <summary>Norwegian writes lamps as compounds, so the guard matches the end of a word, not a whole one.</summary>
	[TestMethod]
	public void A_Compound_Norwegian_Lamp_Name_Excuses_The_Suffix()
	{
		Assert.IsFalse(IsFlagged("light.stue_taklys_led", "Stue taklys LED"));
		Assert.IsFalse(IsFlagged("light.gang_vegglampe_led"));
		Assert.IsFalse(IsFlagged("light.kjokken_benkbelysning_led"));
	}

	[TestMethod]
	public void Led_In_The_Middle_Of_A_Name_Is_Not_A_Suffix()
	{
		Assert.IsFalse(IsFlagged("light.led_bar_over_bordet", "Ledbar over bordet"));
	}

	// ===================== colour channels =====================

	/// <summary>A WiZ bulb publishes itself plus one entity per channel, and commanding a channel fights the lamp.</summary>
	[TestMethod]
	public void A_Colour_Channel_Is_Flagged_When_Its_Lamp_Is_In_The_Room()
	{
		string[] room =
		[
			"light.stue_vegglys", "light.stue_vegglys_r", "light.stue_vegglys_g",
			"light.stue_vegglys_b", "light.stue_vegglys_w", "light.stue_vegglys_on_off"
		];

		foreach (string channel in room.Skip(1))
			StringAssert.Contains(Reason(channel, channel, room) ?? "", "stue_vegglys", $"{channel} should name its lamp");

		Assert.IsFalse(IsFlagged("light.stue_vegglys", "Stue vegglys", room), "the lamp itself is the real light");
	}

	[TestMethod]
	public void A_Channel_Suffix_Without_Its_Lamp_Is_Not_Flagged()
	{
		Assert.IsFalse(IsFlagged("light.stue_vegglys_r", "Stue vegglys R", "light.stue_vegglys_r"));
		Assert.IsFalse(IsFlagged("light.terrasse_w", "Terrasse W"));
	}

	// ===================== appliances =====================

	[TestMethod]
	public void An_Appliance_Light_Is_Flagged()
	{
		StringAssert.Contains(Reason("light.kjoleskap_colour_light", "Kjøleskap colour light") ?? "", "appliance");
	}

	/// <summary>"Oven" is Norwegian for above, so <c>light.oven_gang</c> is the upstairs hallway and a substring match would flag it.</summary>
	[TestMethod]
	public void An_Ordinary_Room_Light_Is_Not_Mistaken_For_An_Appliance()
	{
		Assert.IsFalse(IsFlagged("light.oven_gang", "Gangen oppe"));
		Assert.IsFalse(IsFlagged("light.oppvaskbenk_lys", "Lys over oppvaskbenken"));
	}

	// ===================== the ordinary room =====================

	[TestMethod]
	public void A_Room_Of_Ordinary_Lamps_Raises_Nothing()
	{
		IReadOnlyList<SuspectLight> suspects = LightAudit.Review(
		[
			Light("light.stue_taklys", "Stue taklys"),
			Light("light.stue_leselampe", "Leselampe"),
			Light("light.stue_gulvlampe", "Gulvlampe")
		]);

		Assert.AreEqual(0, suspects.Count);
	}

	[TestMethod]
	public void Review_Reports_Suspects_In_The_Order_Given()
	{
		IReadOnlyList<SuspectLight> suspects = LightAudit.Review(
		[
			Light("light.u7_pro_livingroom_led", "U7 Pro Livingroom LED"),
			Light("light.stue_taklys", "Stue taklys"),
			Light("light.kjoleskap_colour_light", "Kjøleskap colour light")
		]);

		CollectionAssert.AreEqual(
			new[] { "light.u7_pro_livingroom_led", "light.kjoleskap_colour_light" },
			suspects.Select(suspect => suspect.EntityId).ToArray());
	}

	// ===================== a bulb two rooms both command =====================

	private static RoomUnderReview Room(string name, params string[] entityIds) =>
		new(name, [.. entityIds.Select(entityId => Light(entityId))]);

	/// <summary>No light has an area of its own, which every assertion below turns on.</summary>
	private static bool Homeless(string entityId) => false;

	[TestMethod]
	public void A_Bulb_Two_Rooms_Command_Earns_One_Piece_Of_Advice_Naming_Both()
	{
		IReadOnlyList<SuspectLight> shared = LightAudit.SharedBetweenRooms(
			[
				Room("Stue", "light.stue_taklys", "light.benklys"),
				Room("Kjøkken", "light.kjokken_taklys", "light.benklys")
			],
			Homeless);

		Assert.AreEqual(1, shared.Count, "one bulb is shared, so there is one thing to say");
		Assert.AreEqual("light.benklys", shared[0].EntityId);
		StringAssert.Contains(shared[0].Reason, "Stue");
		StringAssert.Contains(shared[0].Reason, "Kjøkken");
	}

	[TestMethod]
	public void The_Advice_Says_To_Give_The_Bulb_An_Area_And_Nothing_About_Groups()
	{
		IReadOnlyList<SuspectLight> shared = LightAudit.SharedBetweenRooms(
			[Room("Bad", "light.speil"), Room("Gang", "light.speil")], Homeless);

		string reason = shared[0].Reason;
		string[] jargon = ["group", "resolver", "discovery", "member", "overlap"];

		StringAssert.Contains(reason, "area");
		Assert.IsTrue(char.IsLower(reason[0]), "the reason is read after the name, so it runs on from it");

		foreach (string word in jargon)
			Assert.IsFalse(
				reason.Contains(word, StringComparison.OrdinalIgnoreCase),
				$"the household never wrote their groups thinking about {word}, so the advice must not either");
	}

	[TestMethod]
	public void Three_Rooms_Sharing_A_Bulb_Are_All_Named_In_One_Piece_Of_Advice()
	{
		IReadOnlyList<SuspectLight> shared = LightAudit.SharedBetweenRooms(
			[Room("Bad", "light.speil"), Room("Gang", "light.speil"), Room("Stue", "light.speil")], Homeless);

		Assert.AreEqual(1, shared.Count);
		StringAssert.Contains(shared[0].Reason, "Bad, Gang and Stue");
	}

	/// <summary>A bulb with its own area is already caught by the resolver's cross-area clip, which warns about it.</summary>
	[TestMethod]
	public void A_Bulb_With_A_Room_Of_Its_Own_Is_Left_To_The_Cross_Area_Rule()
	{
		IReadOnlyList<SuspectLight> shared = LightAudit.SharedBetweenRooms(
			[Room("Stue", "light.benklys"), Room("Kjøkken", "light.benklys")],
			entityId => string.Equals(entityId, "light.benklys", StringComparison.Ordinal));

		Assert.AreEqual(0, shared.Count);
	}

	/// <summary>One room reaches the bulb twice when it holds both the group and the bulb itself.</summary>
	[TestMethod]
	public void A_Bulb_Reached_Twice_By_One_Room_Is_Not_Shared()
	{
		IReadOnlyList<SuspectLight> shared = LightAudit.SharedBetweenRooms(
			[new RoomUnderReview("Stue", [Light("light.benklys"), Light("light.benklys")])], Homeless);

		Assert.AreEqual(0, shared.Count, "one room commanding it twice is still one room");
	}

	[TestMethod]
	public void Rooms_That_Share_Nothing_Raise_Nothing()
	{
		IReadOnlyList<SuspectLight> shared = LightAudit.SharedBetweenRooms(
			[Room("Stue", "light.stue_taklys"), Room("Kjøkken", "light.kjokken_taklys")], Homeless);

		Assert.AreEqual(0, shared.Count);
	}

	[TestMethod]
	public void Every_Reason_Reads_As_Words()
	{
		IReadOnlyList<SuspectLight> suspects = LightAudit.Review(
		[
			Light("light.u7_pro_livingroom_led", "U7 Pro Livingroom LED"),
			Light("light.lab_taklys_status_led", "Lab taklys status LED"),
			Light("light.kjoleskap_colour_light", "Kjøleskap colour light")
		]);

		Assert.AreEqual(3, suspects.Count);

		foreach (SuspectLight suspect in suspects)
		{
			Assert.IsTrue(suspect.Reason.Length > 20, $"{suspect.EntityId} needs a reason somebody can judge");
			Assert.IsTrue(char.IsLower(suspect.Reason[0]), "the reason is read after the name, so it runs on from it");
		}
	}
}
