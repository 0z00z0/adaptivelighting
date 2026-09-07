using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The room page's light list: what each line says, what a light's table shows, and what an edit writes.</summary>
[TestClass]
public sealed class LightLevelsViewTests
{
	private const string Group = "light.stue_taklys";
	private const string First = "light.stue_tak_1";
	private const string Second = "light.stue_tak_2";
	private const string Lamp = "light.stue_leselampe";
	private const string Only = "light.bad_tak";

	private static List<TimePeriodConfig> Periods() =>
	[
		new() { Id = "evening", Name = "Kveld", Start = "18:00", Brightness = 179, ColorTempKelvin = 2700 },
		new() { Id = "night", Name = "Natt", Start = "22:30", Brightness = 38, ColorTempKelvin = 2200 }
	];

	/// <summary>A room of one group of two bulbs and one stand-alone lamp.</summary>
	private static ResolvedArea Room() =>
		Resolved([Group, Lamp], new() { [Group] = [First, Second], [Lamp] = [Lamp] });

	private static ResolvedArea Resolved(string[] lights, Dictionary<string, string[]> leaves)
	{
		Dictionary<string, IReadOnlySet<string>> beneath = new(StringComparer.Ordinal);

		foreach ((string entry, string[] under) in leaves)
			beneath[entry] = new HashSet<string>(under, StringComparer.Ordinal);

		return new ResolvedArea("Stue", new AreaSettings(), lights, [], [], [])
		{
			LeavesOfEntry = beneath
		};
	}

	private static AreaConfig With(params LightLevelOverride[] lights) =>
		new() { Name = "Stue", LightLevels = lights.Length > 0 ? [.. lights] : null };

	private static LightLevelOverride Pins(string entityId, string periodId, int? brightness = null, int? kelvin = null, bool curve = false) =>
		new()
		{
			EntityId = entityId,
			Levels = [new RoomLevelOverride
			{
				PeriodId = periodId,
				Brightness = brightness,
				ColorTempKelvin = kelvin,
				FollowDaylightCurve = curve ? true : null
			}]
		};

	// ===================== whether the question is asked at all =====================

	[TestMethod]
	public void A_One_Lamp_Room_Is_Never_Asked_To_Adjust_Lights_Individually()
	{
		Assert.IsFalse(LightLevels.Applies(Resolved([Only], new() { [Only] = [Only] })));
	}

	[TestMethod]
	public void A_Room_Of_One_Group_Of_Two_Is_Asked()
	{
		Assert.IsTrue(
			LightLevels.Applies(Resolved([Group], new() { [Group] = [First, Second] })),
			"one entry, two lights: the room has something to tell apart");
	}

	[TestMethod]
	public void A_Room_That_Cannot_Be_Resolved_Is_Not_Asked()
	{
		Assert.IsFalse(LightLevels.Applies(null));
	}

	[TestMethod]
	public void The_Box_Is_Ticked_On_Load_Exactly_When_A_Light_States_Something()
	{
		Assert.IsFalse(LightLevels.InUse(With()));
		Assert.IsTrue(LightLevels.InUse(With(Pins(First, "evening", brightness: 77))));
	}

	// ===================== what a line says =====================

	[TestMethod]
	public void A_Group_Line_Names_Its_Size_And_How_Many_Are_Set_Differently()
	{
		IReadOnlyList<LightEntry> entries = LightLevels.Entries(Periods(), Room(), With(Pins(First, "evening", brightness: 77)));

		Assert.AreEqual("a group of 2, 1 set differently", entries[0].Summary);
		Assert.AreEqual("follows the room", entries[1].Summary);
	}

	[TestMethod]
	public void A_Group_Whose_Lights_All_Follow_The_Room_Says_So()
	{
		Assert.AreEqual("a group of 2, all follow the room", LightLevels.Entries(Periods(), Room(), With())[0].Summary);
	}

	[TestMethod]
	public void A_Lamp_Line_Counts_Its_Own_Periods()
	{
		AreaConfig room = With(new LightLevelOverride
		{
			EntityId = Lamp,
			Levels =
			[
				new RoomLevelOverride { PeriodId = "evening", Brightness = 77 },
				new RoomLevelOverride { PeriodId = "night", Brightness = 26 }
			]
		});

		Assert.AreEqual("own levels for 2 periods", LightLevels.Entries(Periods(), Room(), room)[1].Summary);
	}

	[TestMethod]
	public void One_Period_Is_Not_Two()
	{
		Assert.AreEqual(
			"own levels for 1 period",
			LightLevels.Entries(Periods(), Room(), With(Pins(Lamp, "evening", brightness: 77)))[1].Summary);
	}

	[TestMethod]
	public void A_Light_Inside_A_Group_Is_Not_A_Line_At_Room_Level()
	{
		IReadOnlyList<LightEntry> entries = LightLevels.Entries(Periods(), Room(), With());

		CollectionAssert.AreEqual(
			new[] { Group, Lamp },
			entries.Select(entry => entry.EntityId).ToArray(),
			"the room's settled entries and nothing else; the bulbs are inside the group's own fold");
	}

	// ===================== a light's own table =====================

	[TestMethod]
	public void A_Light_Stating_Nothing_Shows_The_Rooms_Answer_As_Inherited()
	{
		LightLevelRow evening = LightLevels.Rows(Periods(), With(), First).Single(row => row.PeriodId == "evening");

		Assert.AreEqual(RawBrightness.ToPercent(179), evening.BrightnessPct);
		Assert.IsFalse(evening.BrightnessIsOwn, "the slider sits at its leftmost stop, labelled the room's");
		Assert.IsFalse(evening.IsOwn);
	}

	[TestMethod]
	public void A_Lights_Own_Brightness_Shows_As_Its_Own_And_Its_Warmth_Still_Inherits()
	{
		LightLevelRow evening = LightLevels
			.Rows(Periods(), With(Pins(First, "evening", brightness: 77)), First)
			.Single(row => row.PeriodId == "evening");

		Assert.AreEqual(RawBrightness.ToPercent(77), evening.BrightnessPct);
		Assert.IsTrue(evening.BrightnessIsOwn);
		Assert.AreEqual(2700, evening.ColorTempKelvin);
		Assert.IsFalse(evening.ColourIsOwn, "warmth travels alone");
	}

	[TestMethod]
	public void A_Light_Following_The_Rooms_Curve_Is_Shown_On_It_Without_Claiming_It()
	{
		AreaConfig room = With();
		room.Levels = [new RoomLevelOverride { PeriodId = "evening", FollowDaylightCurve = true }];

		LightLevelRow evening = LightLevels.Rows(Periods(), room, First).Single(row => row.PeriodId == "evening");

		Assert.IsTrue(evening.FollowsDaylightCurve, "the room's curve reaches every light in it");
		Assert.IsFalse(evening.CurveIsOwn);
		Assert.IsFalse(evening.IsOwn, "or every row of every light in a curve-following room would carry a mark");
	}

	[TestMethod]
	public void A_Light_That_Claims_The_Curve_Itself_Is_Marked_As_Its_Own()
	{
		LightLevelRow evening = LightLevels
			.Rows(Periods(), With(Pins(First, "evening", curve: true)), First)
			.Single(row => row.PeriodId == "evening");

		Assert.IsTrue(evening.CurveIsOwn);
		Assert.IsTrue(evening.IsOwn, "the line above counts this as one of the light's own periods");
	}

	[TestMethod]
	public void A_Light_Pinned_To_A_Brightness_Comes_Off_The_Rooms_Curve()
	{
		AreaConfig room = With(Pins(First, "evening", brightness: 77));
		room.Levels = [new RoomLevelOverride { PeriodId = "evening", FollowDaylightCurve = true }];

		LightLevelRow evening = LightLevels.Rows(Periods(), room, First).Single(row => row.PeriodId == "evening");

		Assert.IsFalse(evening.FollowsDaylightCurve, "the engine's own merge rule, asked of the engine's own function");
		Assert.AreEqual(RawBrightness.ToPercent(77), evening.BrightnessPct);
	}

	// ===================== the write path =====================

	[TestMethod]
	public void Setting_A_Brightness_Creates_The_Light_And_Its_Row()
	{
		AreaConfig room = With();

		LightLevels.SetBrightness(room, First, "evening", 30);

		LightLevelOverride light = room.LightLevels!.Single();

		Assert.AreEqual(First, light.EntityId);
		Assert.AreEqual(30d, light.Levels.Single().BrightnessPct);
	}

	[TestMethod]
	public void Clearing_The_Last_Value_Drops_The_Row_The_Light_And_The_Key()
	{
		AreaConfig room = With(Pins(First, "evening", brightness: 77));

		LightLevels.SetBrightness(room, First, "evening", null);

		Assert.IsNull(room.LightLevels, "a cleared row counts as a level to everything that reads the key");
	}

	[TestMethod]
	public void Clearing_One_Value_Keeps_The_Other()
	{
		AreaConfig room = With(Pins(First, "evening", brightness: 77, kelvin: 2000));

		LightLevels.SetBrightness(room, First, "evening", null);

		Assert.AreEqual(2000, room.LightLevels!.Single().Levels.Single().ColorTempKelvin);
	}

	[TestMethod]
	public void Coming_Off_A_Curve_The_Room_Follows_Pins_This_Lights_Own_Brightness()
	{
		AreaConfig room = With();

		LightLevels.SetFollowsDaylightCurve(room, First, "evening", currentBrightnessPct: 30, roomFollowsCurve: true, follow: false);

		Assert.AreEqual(
			30d,
			room.LightLevels!.Single().Levels.Single().BrightnessPct,
			"a row saying nothing about brightness goes on inheriting the room's curve, so opting out has to pin a number");
	}

	[TestMethod]
	public void Coming_Off_A_Curve_The_Room_Does_Not_Follow_Pins_Nothing()
	{
		AreaConfig room = With(Pins(First, "evening", curve: true));

		LightLevels.SetFollowsDaylightCurve(room, First, "evening", currentBrightnessPct: 30, roomFollowsCurve: false, follow: false);

		Assert.IsNull(room.LightLevels, "nothing is left to say, so the row goes rather than acquiring a number nobody chose");
	}

	[TestMethod]
	public void Unticking_The_Box_Forgets_Every_Lights_Own_Levels()
	{
		AreaConfig room = With(Pins(First, "evening", brightness: 77), Pins(Lamp, "night", brightness: 26));

		LightLevels.Forget(room);

		Assert.IsNull(room.LightLevels);
	}

	[TestMethod]
	public void The_Box_Says_How_Many_Lights_Would_Be_Forgotten()
	{
		Assert.AreEqual(
			2,
			LightLevels.StatingCount(With(Pins(First, "evening", brightness: 77), Pins(Lamp, "night", brightness: 26))));
	}

	// ===================== orphans =====================

	[TestMethod]
	public void A_Light_The_Room_No_Longer_Commands_Is_Listed_As_An_Orphan()
	{
		AreaConfig room = With(Pins("light.stue_gammel_lampe", "evening", brightness: 77));

		LightLevelOrphan orphan = LightLevels.Orphans(Room(), room).Single();

		Assert.AreEqual("light.stue_gammel_lampe", orphan.EntityId);
		Assert.AreEqual(1, orphan.PeriodCount);
	}

	[TestMethod]
	public void A_Light_Inside_A_Group_Is_Never_An_Orphan()
	{
		Assert.AreEqual(
			0,
			LightLevels.Orphans(Room(), With(Pins(First, "evening", brightness: 77))).Count,
			"the room commands it through the group, which is the whole point of following membership down");
	}

	[TestMethod]
	public void Removing_An_Orphan_Takes_Its_Levels_With_It()
	{
		AreaConfig room = With(Pins("light.stue_gammel_lampe", "evening", brightness: 77));

		Assert.IsTrue(LightLevels.Remove(room, "light.stue_gammel_lampe"));
		Assert.IsNull(room.LightLevels);
	}

	[TestMethod]
	public void A_Room_That_Cannot_Be_Resolved_Calls_Nothing_An_Orphan()
	{
		Assert.AreEqual(
			0,
			LightLevels.Orphans(null, With(Pins(First, "evening", brightness: 77))).Count,
			"a room the engine cannot resolve knows nothing about what it commands");
	}
}
