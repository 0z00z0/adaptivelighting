using System.Globalization;
using System.Reflection;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The room page's model of what a room can override: the sections, the provenance, and every conversion
///     between what a control shows and what the document stores.
/// </summary>
/// <remarks>
///     <para>
///         There is no Razor render harness in this repo and there is not going to be one, so the parts of the
///         room page worth being sure about live outside its markup. Three of them would be wrong silently and
///         expensively: a setting that belongs to no section is a setting nobody can find, a provenance guessed
///         from the value erases the overrides somebody set deliberately, and a unit conversion off by a factor
///         of sixty writes a ten-second timeout where ten minutes was asked for with nothing on screen looking
///         wrong.
///     </para>
///     <para>
///         The section membership is asserted against the schema rather than against a list, so a setting added
///         to <see cref="AreaSettings"/> tomorrow fails here instead of quietly going homeless.
///     </para>
/// </remarks>
[TestClass]
public sealed class RoomSettingsTests
{
	private static AreaSettings House => new();

	private static IReadOnlyList<RoomSetting> AllSettings =>
		[.. RoomSettings.Groups.SelectMany(group => group.Settings)];

	// ===================== the sections =====================

	/// <summary>
	///     Every overridable setting has a section, and the sections invent none. A setting the detail view does
	///     not carry is a setting reachable from nowhere.
	/// </summary>
	[TestMethod]
	public void Every_Overridable_Setting_Belongs_To_Exactly_One_Section()
	{
		string[] sectioned = [.. AllSettings.Select(setting => setting.Key)];

		CollectionAssert.AreEquivalent(
			RoomSettings.Keys.ToArray(),
			sectioned,
			"the sections and the schema must name the same settings");

		CollectionAssert.AllItemsAreUnique(sectioned, "a setting in two sections is a setting somebody looks for twice");
	}

	/// <summary>
	///     The set of overridable settings is derived from the schema, not written down — the same reading
	///     <see cref="AreaView.OverridableSettingCount"/> takes, so "n of 21" cannot mean two different things on
	///     two surfaces.
	/// </summary>
	[TestMethod]
	public void The_Settings_Are_Read_Off_The_Schema_Rather_Than_Listed()
	{
		string[] expected =
		[
			.. typeof(AreaSettings)
				.GetProperties(BindingFlags.Public | BindingFlags.Instance)
				.Select(property => property.Name)
				.Where(name => !string.Equals(name, nameof(AreaSettings.Enabled), StringComparison.Ordinal))
		];

		CollectionAssert.AreEquivalent(expected, RoomSettings.Keys.ToArray());

		Assert.AreEqual(AreaView.OverridableSettingCount, RoomSettings.Keys.Count,
			"the room page's denominator and the settings list's are one number");

		Assert.IsFalse(RoomSettings.Keys.Contains(nameof(AreaSettings.Enabled)),
			"the room's power switch owns Enabled, so it is not one of the settings the detail view offers");
	}

	/// <summary>
	///     The rare sections start folded. This is the design's one sanctioned answer to a long page, and a
	///     regression here is a page that arrives twice as tall as it should.
	/// </summary>
	[TestMethod]
	public void The_Rare_Section_Starts_Folded_And_The_Common_Ones_Do_Not()
	{
		Assert.IsFalse(RoomSettings.Groups.Single(group => group.Title == "Rarely needed").StartsOpen);

		Assert.IsTrue(RoomSettings.Groups.Single(group => group.Title == "Movement & timing").StartsOpen);
		Assert.IsTrue(RoomSettings.Groups.Single(group => group.Title == "Darkness").StartsOpen);
		Assert.IsTrue(RoomSettings.Groups.Single(group => group.Title == "Room behaviour").StartsOpen);
	}

	/// <summary>Every section says what is in it, so a folded one is still readable.</summary>
	[TestMethod]
	public void Every_Section_And_Setting_Says_What_It_Is_For()
	{
		foreach (RoomSettingGroup group in RoomSettings.Groups)
		{
			Assert.IsFalse(string.IsNullOrWhiteSpace(group.Title));
			Assert.IsFalse(string.IsNullOrWhiteSpace(group.Note), $"{group.Title} must say what it holds");
			Assert.IsTrue(group.Settings.Count > 0, $"{group.Title} must hold something");
		}

		foreach (RoomSetting setting in AllSettings)
		{
			Assert.IsFalse(string.IsNullOrWhiteSpace(setting.Label), $"{setting.Key} needs a name a person recognises");
			Assert.IsFalse(string.IsNullOrWhiteSpace(setting.Help), $"{setting.Key} needs a line on what it changes");
		}
	}

	/// <summary>
	///     The overlap with the sentences is deliberate: a value in a sentence is also a row, because the sentence
	///     is how somebody reads the room and the row is how somebody finds a setting by what it changes.
	/// </summary>
	[TestMethod]
	public void Every_Value_The_Sentences_Show_Is_Also_A_Row()
	{
		foreach (string key in TokenKeys(new AreaConfig()))
		{
			Assert.IsTrue(RoomSettings.Knows(key),
				$"{key} appears in a sentence, so the detail view must carry it too — the overlap is the point");
		}
	}

	// ===================== provenance =====================

	/// <summary>
	///     A room that pins the house's own number has still made a decision, and the amber dot must say so.
	///     Comparing values instead would erase exactly the overrides somebody set on purpose, so that a later
	///     change to the house leaves this room alone.
	/// </summary>
	[TestMethod]
	public void Provenance_Is_Read_Off_Null_Never_Guessed_From_The_Value()
	{
		AreaConfig room = new() { VacancyTimeoutSeconds = House.VacancyTimeoutSeconds };

		Assert.IsTrue(RoomSettings.IsOwn(room, nameof(AreaSettings.VacancyTimeoutSeconds)),
			"an explicit value equal to the house's is still this room's own");
		Assert.AreEqual(1, RoomSettings.OwnCount(room));

		Assert.IsFalse(RoomSettings.IsOwn(room, nameof(AreaSettings.PreOffSeconds)));
	}

	/// <summary>The count follows the schema's nullables, whatever their type.</summary>
	[TestMethod]
	public void The_Count_Covers_Every_Kind_Of_Setting()
	{
		AreaConfig room = new()
		{
			VacancyTimeoutSeconds = 300,
			LuxThreshold = 25,
			WelcomeHome = true,
			Darkness = DarknessSource.Sun,
			SunEntity = "sun.other"
		};

		Assert.AreEqual(5, RoomSettings.OwnCount(room), "an int, a double, a bool, an enum and a string all count");
	}

	/// <summary>
	///     Reverting clears the room's value so it follows the house again — never copies today's number in.
	///     Asserted for every setting, because one that could not be reverted would be a one-way door.
	/// </summary>
	[TestMethod]
	public void Every_Setting_Can_Be_Sent_Back_To_Following_The_House()
	{
		AreaConfig room = new();

		foreach (RoomSetting setting in AllSettings)
		{
			Own(room, setting);

			Assert.IsTrue(RoomSettings.IsOwn(room, setting.Key), $"{setting.Key} should now be the room's own");
			Assert.IsTrue(RoomSettings.Clear(room, setting.Key), $"{setting.Key} must be revertable");
			Assert.IsFalse(RoomSettings.IsOwn(room, setting.Key), $"{setting.Key} must follow the house again");
		}

		Assert.AreEqual(0, RoomSettings.OwnCount(room));
		Assert.IsFalse(RoomSettings.Clear(room, "NotASetting"), "an unknown key changes nothing and says so");
	}

	// ===================== reading and writing values =====================

	/// <summary>
	///     A proportion is stored as a 0-1 factor and shown as a percentage. A stepper offering 0.05 steps of an
	///     unnamed fraction is a control nobody can read, so the conversion happens once, here.
	/// </summary>
	[TestMethod]
	public void A_Proportion_Is_Shown_And_Stored_In_Different_Units()
	{
		AreaConfig room = new();

		Assert.AreEqual(50, RoomSettings.Shown(room, House, nameof(AreaSettings.PreOffBrightnessFactor)), 0.001);
		Assert.AreEqual("50 %", RoomSettings.Describe(room, House, nameof(AreaSettings.PreOffBrightnessFactor)));

		RoomSettings.SetShown(room, nameof(AreaSettings.PreOffBrightnessFactor), 30);

		Assert.AreEqual(0.3, room.PreOffBrightnessFactor!.Value, 0.0001, "the schema keeps a 0-1 factor");
		Assert.AreEqual("30 %", RoomSettings.Describe(room, House, nameof(AreaSettings.PreOffBrightnessFactor)));
	}

	/// <summary>A whole-number setting rounds rather than truncates: a half-step has to land somewhere.</summary>
	[TestMethod]
	public void A_Whole_Number_Setting_Rounds_Rather_Than_Truncates()
	{
		AreaConfig room = new();

		RoomSettings.SetShown(room, nameof(AreaSettings.VacancyTimeoutSeconds), 90.6);

		Assert.AreEqual(91, room.VacancyTimeoutSeconds);
	}

	/// <summary>
	///     A value is held to the setting's own limits wherever it came from, so the keyboard escape hatch cannot
	///     reach somewhere the arrows refuse to.
	/// </summary>
	[TestMethod]
	public void A_Value_Is_Bounded_Whether_It_Was_Stepped_Or_Typed()
	{
		AreaConfig room = new();

		RoomSettings.SetShown(room, nameof(AreaSettings.LuxBrightnessMaxPct), 400);
		Assert.AreEqual(100, room.LuxBrightnessMaxPct);

		RoomSettings.SetShown(room, nameof(AreaSettings.LuxBrightnessGamma), 0);
		Assert.AreEqual(0.1, room.LuxBrightnessGamma!.Value, 0.0001);

		RoomSettings.SetShown(room, nameof(AreaSettings.SunElevationThreshold), -200);
		Assert.AreEqual(-90, room.SunElevationThreshold!.Value, 0.0001);
	}

	/// <summary>
	///     Every value is written the way the sentences write it, so a reader meets one vocabulary rather than a
	///     row and a sentence disagreeing about what 600 means.
	/// </summary>
	[TestMethod]
	public void A_Value_Is_Written_The_Way_The_Sentences_Write_It()
	{
		AreaConfig room = new();

		Assert.AreEqual("10 min", RoomSettings.Describe(room, House, nameof(AreaSettings.VacancyTimeoutSeconds)));
		Assert.AreEqual("30 s", RoomSettings.Describe(room, House, nameof(AreaSettings.PreOffSeconds)));
		Assert.AreEqual("2 h", RoomSettings.Describe(room, House, nameof(AreaSettings.OverrideDurationMinutes)));
		Assert.AreEqual("1000 lx", RoomSettings.Describe(room, House, nameof(AreaSettings.LuxThreshold)));
		Assert.AreEqual("3°", RoomSettings.Describe(room, House, nameof(AreaSettings.SunElevationThreshold)));
		Assert.AreEqual("no", RoomSettings.Describe(room, House, nameof(AreaSettings.WelcomeHome)));
		Assert.AreEqual("Either", RoomSettings.Describe(room, House, nameof(AreaSettings.Darkness)));
		Assert.AreEqual("sun.sun", RoomSettings.Describe(room, House, nameof(AreaSettings.SunEntity)));
	}

	/// <summary>
	///     The conversions are invariant, whatever the machine's culture. On a <c>nb-NO</c> host a half written
	///     "0,5" and parsed back would become five — a room dimming to 500 % of its brightness, or timing out
	///     five times too late.
	/// </summary>
	[TestMethod]
	public void The_Conversions_Survive_A_Comma_Decimal_Culture()
	{
		CultureInfo original = CultureInfo.CurrentCulture;

		try
		{
			CultureInfo.CurrentCulture = new CultureInfo("nb-NO");

			AreaConfig room = new();
			RoomSettings.SetShown(room, nameof(AreaSettings.LuxBrightnessGamma), 1.6);

			Assert.AreEqual(1.6, room.LuxBrightnessGamma!.Value, 0.0001);
			Assert.AreEqual("1.6", RoomSettings.Describe(room, House, nameof(AreaSettings.LuxBrightnessGamma)));
		}
		finally
		{
			CultureInfo.CurrentCulture = original;
		}
	}

	// ===================== the gates =====================

	/// <summary>
	///     A setting that cannot take effect is not drawn at all. Greying it out still spends the reader's
	///     attention, still invites the tap and still has to explain itself; the row comes back on the same tap
	///     that turns its gate on.
	/// </summary>
	[TestMethod]
	public void A_Setting_That_Cannot_Take_Effect_Is_Not_Drawn()
	{
		RoomSetting luxThreshold = Setting(nameof(AreaSettings.LuxThreshold));
		RoomSetting sunBelow = Setting(nameof(AreaSettings.SunElevationThreshold));
		RoomSetting startLux = Setting(nameof(AreaSettings.LuxBrightnessStartLux));

		Assert.IsFalse(luxThreshold.AppliesTo(new AreaSettings { Darkness = DarknessSource.Sun }),
			"a room that gates on the sun has no lux threshold to set");
		Assert.IsTrue(luxThreshold.AppliesTo(new AreaSettings { Darkness = DarknessSource.Either }));

		Assert.IsFalse(sunBelow.AppliesTo(new AreaSettings { Darkness = DarknessSource.Always }),
			"a windowless room consults neither signal");
		Assert.IsTrue(sunBelow.AppliesTo(new AreaSettings { Darkness = DarknessSource.Lux }),
			"the sun is still the fallback for a room whose lux sensor never resolves");

		Assert.IsFalse(startLux.AppliesTo(new AreaSettings { LuxBrightnessEnabled = false }));
		Assert.IsTrue(startLux.AppliesTo(new AreaSettings { LuxBrightnessEnabled = true }));
	}

	/// <summary>A setting with no gate is always drawn.</summary>
	[TestMethod]
	public void An_Ungated_Setting_Is_Always_Drawn()
	{
		Assert.IsTrue(Setting(nameof(AreaSettings.VacancyTimeoutSeconds)).AppliesTo(new AreaSettings()));
		Assert.IsTrue(Setting(nameof(AreaSettings.WelcomeHome)).AppliesTo(new AreaSettings()));
	}

	// ===================== applying a sentence edit =====================

	/// <summary>
	///     Every value the sentences offer can be applied. A key the page cannot apply renders as a control that
	///     does nothing when tapped, which is the one failure mode a sentence-driven surface must not have.
	/// </summary>
	[TestMethod]
	public void Every_Sentence_Token_Can_Be_Applied()
	{
		AreaConfig room = new()
		{
			// Every darkness rule, so the sentence variants that only appear under one of them are covered too.
			Darkness = DarknessSource.Either
		};

		foreach (DarknessSource source in Enum.GetValues<DarknessSource>())
		{
			room.Darkness = source;

			foreach (SentenceToken token in Tokens(room))
			{
				Assert.IsTrue(
					RoomSettings.Apply(new AreaConfig(), new SentenceEdit(token.Key, token.Kind, token.Choices[0].Value)),
					$"the sentence offers {token.Key}, so the page must know how to apply it");
			}
		}
	}

	/// <summary>
	///     A duration token carries seconds; some of the settings it edits are stored in minutes. Getting this
	///     backwards writes a two-minute hold where two hours was picked, and nothing on screen looks wrong.
	/// </summary>
	[TestMethod]
	public void An_Edit_Is_Stored_In_The_Unit_The_Schema_Wants()
	{
		AreaConfig room = new();

		RoomSettings.Apply(room, new SentenceEdit(nameof(AreaSettings.OverrideDurationMinutes), TokenKind.Duration, "7200"));
		Assert.AreEqual(120, room.OverrideDurationMinutes, "7200 seconds is 120 minutes, and the schema keeps minutes");

		RoomSettings.Apply(room, new SentenceEdit(nameof(AreaSettings.VacancyTimeoutSeconds), TokenKind.Duration, "600"));
		Assert.AreEqual(600, room.VacancyTimeoutSeconds, "this one really is seconds");

		RoomSettings.Apply(room, new SentenceEdit(nameof(AreaSettings.PreOffBrightnessFactor), TokenKind.Percentage, "30"));
		Assert.AreEqual(0.3, room.PreOffBrightnessFactor!.Value, 0.0001, "a percentage token becomes the schema's factor");

		RoomSettings.Apply(room, new SentenceEdit(nameof(AreaSettings.Darkness), TokenKind.Choice, nameof(DarknessSource.Always)));
		Assert.AreEqual(DarknessSource.Always, room.Darkness);
	}

	/// <summary>An edit the page cannot apply says so rather than silently doing nothing.</summary>
	[TestMethod]
	public void An_Unknown_Edit_Is_Refused_Rather_Than_Swallowed()
	{
		Assert.IsFalse(RoomSettings.Apply(new AreaConfig(), new SentenceEdit("NotASetting", TokenKind.Number, "1")));

		Assert.IsFalse(
			RoomSettings.Apply(new AreaConfig(), new SentenceEdit(nameof(AreaSettings.Darkness), TokenKind.Choice, "Twilight")),
			"a choice that names no member of the enum is not a change to make");
	}

	// ===================== the house's own copy of the same settings =====================

	/// <summary>
	///     The House tab writes the very same settings against <see cref="AreaSettings"/>, and the conversion
	///     between what a control shows and what the document stores has to be the same one — a warning dim
	///     written as 50 in the house and 0.5 in a room is two documents that disagree about the same word.
	/// </summary>
	[TestMethod]
	public void The_House_Writes_A_Shown_Value_The_Way_A_Room_Does()
	{
		AreaSettings house = House;
		AreaConfig room = new();

		foreach (RoomSetting setting in AllSettings.Where(item => item.Control is not (RoomControl.Flag or RoomControl.Choice or RoomControl.Entity)))
		{
			double shown = Math.Max(setting.Min, 5);

			RoomSettings.SetShown(house, setting.Key, shown);
			RoomSettings.SetShown(room, setting.Key, shown);

			Assert.AreEqual(
				RoomSettings.Shown(room, House, setting.Key),
				RoomSettings.Shown(null, house, setting.Key),
				1e-9,
				$"{setting.Key} must mean the same number on both surfaces");
		}
	}

	/// <summary>A house value is bounded by the same limits a room's is, including through the keyboard escape hatch.</summary>
	[TestMethod]
	public void A_House_Value_Is_Held_To_The_Settings_Own_Bounds()
	{
		AreaSettings house = House;

		RoomSettings.SetShown(house, nameof(AreaSettings.PreOffBrightnessFactor), 400);

		Assert.AreEqual(1.0, house.PreOffBrightnessFactor, 1e-9, "a percentage cannot exceed its own ceiling");

		RoomSettings.SetShown(house, nameof(AreaSettings.VacancyTimeoutSeconds), -30);

		Assert.AreEqual(1, house.VacancyTimeoutSeconds, "the lights cannot stay on for a negative time");
	}

	/// <summary>Flags and entities land on the house's non-nullable twins, with empty meaning none rather than null.</summary>
	[TestMethod]
	public void The_House_Writes_Flags_And_Entities()
	{
		AreaSettings house = House;

		RoomSettings.SetFlag(house, nameof(AreaSettings.WelcomeHome), true);
		RoomSettings.SetEntity(house, nameof(AreaSettings.SunEntity), "sun.other");

		Assert.IsTrue(house.WelcomeHome);
		Assert.AreEqual("sun.other", house.SunEntity);

		RoomSettings.SetEntity(house, nameof(AreaSettings.SunEntity), null);

		Assert.AreEqual(string.Empty, house.SunEntity, "the house has no null to fall back to, so none is written as empty");
	}

	/// <summary>
	///     A sentence edit applies to the house exactly as it applies to a room — the same unit conversions,
	///     so the two surfaces cannot write different values for one pick.
	/// </summary>
	[TestMethod]
	public void A_Sentence_Edit_Applies_To_The_House_As_It_Does_To_A_Room()
	{
		AreaSettings house = House;
		AreaConfig room = new();

		(string Key, TokenKind Kind, string Value)[] edits =
		[
			(nameof(AreaSettings.VacancyTimeoutSeconds), TokenKind.Duration, "600"),
			(nameof(AreaSettings.PreOffSeconds), TokenKind.Duration, "45"),
			(nameof(AreaSettings.PreOffBrightnessFactor), TokenKind.Percentage, "30"),
			(nameof(AreaSettings.OverrideDurationMinutes), TokenKind.Duration, "7200"),
			(nameof(AreaSettings.VacancyResetMinutes), TokenKind.Duration, "900"),
			(nameof(AreaSettings.LuxThreshold), TokenKind.Number, "60"),
			(nameof(AreaSettings.SunElevationThreshold), TokenKind.Number, "-3"),
			(nameof(AreaSettings.Darkness), TokenKind.Choice, nameof(DarknessSource.Always))
		];

		foreach ((string key, TokenKind kind, string value) in edits)
		{
			SentenceEdit edit = new(key, kind, value);

			Assert.IsTrue(RoomSettings.Apply(house, edit), $"{key} is a key the House tab must know how to apply");
			Assert.IsTrue(RoomSettings.Apply(room, edit), $"{key} is a key the room page must know how to apply");
		}

		Assert.AreEqual(600, house.VacancyTimeoutSeconds);
		Assert.AreEqual(45, house.PreOffSeconds);
		Assert.AreEqual(0.3, house.PreOffBrightnessFactor, 1e-9);
		Assert.AreEqual(120, house.OverrideDurationMinutes);
		Assert.AreEqual(15, house.VacancyResetMinutes);
		Assert.AreEqual(60, house.LuxThreshold, 1e-9);
		Assert.AreEqual(-3, house.SunElevationThreshold, 1e-9);
		Assert.AreEqual(DarknessSource.Always, house.Darkness);
	}

	/// <summary>An unknown key is refused rather than silently swallowed, so a drifted sentence can be logged.</summary>
	[TestMethod]
	public void The_House_Refuses_A_Key_It_Cannot_Apply()
	{
		Assert.IsFalse(RoomSettings.Apply(House, new SentenceEdit("NotASetting", TokenKind.Number, "1")));
		Assert.IsFalse(RoomSettings.Apply(House, new SentenceEdit(nameof(AreaSettings.Darkness), TokenKind.Choice, "Twilight")));
	}

	// ===================== helpers =====================

	private static RoomSetting Setting(string key) => AllSettings.Single(setting => setting.Key == key);

	private static IEnumerable<SentenceToken> Tokens(AreaConfig room) =>
		AreaSentences.ForArea(room, House)
			.SelectMany(sentence => sentence.Parts)
			.OfType<SentenceToken>()
			.Where(token => token.Choices.Count > 0);

	private static IEnumerable<string> TokenKeys(AreaConfig room) => Tokens(room).Select(token => token.Key).Distinct(StringComparer.Ordinal);

	/// <summary>Gives a room its own value for one setting, whatever kind it is.</summary>
	private static void Own(AreaConfig room, RoomSetting setting)
	{
		switch (setting.Control)
		{
			case RoomControl.Flag:
				RoomSettings.SetFlag(room, setting.Key, true);
				break;

			case RoomControl.Choice:
				room.Darkness = DarknessSource.Always;
				break;

			case RoomControl.Entity:
				RoomSettings.SetEntity(room, setting.Key, "sun.other");
				break;

			default:
				RoomSettings.SetShown(room, setting.Key, Math.Max(setting.Min, 1));
				break;
		}
	}
}
