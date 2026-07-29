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
	///     No section carries a say in whether it starts open, because none of them does. Four of the five used to,
	///     so revealing <i>All settings</i> unrolled four sections at once and the structure — the thing that makes
	///     twenty-one settings findable — was several screens of controls rather than five headings.
	/// </summary>
	[TestMethod]
	public void A_Section_Has_No_Say_In_Whether_It_Starts_Open()
	{
		Assert.IsNull(
			typeof(RoomSettingGroup).GetProperty("StartsOpen"),
			"a flag every section sets the same way is a default the pages should not each have to read");
	}

	/// <summary>
	///     The movement section is named once. The room page appends <i>Blocked while on</i> to it — a movement rule
	///     that is a list of entities rather than an overridable setting, so it cannot be a <see cref="RoomSetting"/>
	///     and has to be matched by title — and a rename that missed the page would drop the control silently.
	/// </summary>
	[TestMethod]
	public void The_Movement_Section_Is_Named_Once()
	{
		Assert.AreEqual(
			1,
			RoomSettings.Groups.Count(group => string.Equals(group.Title, RoomSettings.MovementSection, StringComparison.Ordinal)),
			"the constant the room page matches on has to name exactly one section");
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
		Assert.AreEqual("Sensor", RoomSettings.Describe(room, House, nameof(AreaSettings.Darkness)),
			"the default darkness source is Lux, which the vocabulary calls Sensor");
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

	// ===================== a value that spans decades =====================

	/// <summary>
	///     A light level is bounded by what a sensor can report, and typed rather than stepped.
	/// </summary>
	/// <remarks>
	///     The bound is the hardware's: illuminance sensors report 0–65 535 lx. The control follows from the same
	///     numbers rather than from a flag, because it is arithmetic — a five-lux step needs 7 992 presses to get
	///     from 40 to 40 000, and no other step is both usable at 3 lx and usable at 30 000.
	/// </remarks>
	[TestMethod]
	public void A_Light_Level_Is_Bounded_By_The_Sensor_And_Typed_Rather_Than_Stepped()
	{
		foreach (string key in new[] { nameof(AreaSettings.LuxThreshold), nameof(AreaSettings.LuxHysteresis) })
		{
			RoomSetting setting = Setting(key);

			Assert.AreEqual(RoomSettings.MaxLux, setting.Max, $"{key} takes what a 16-bit reading can carry");
			Assert.AreEqual(0, setting.Min, 1e-9, $"{key} starts at nothing");
			Assert.IsTrue(RoomSettings.SpansDecades(setting.Min, setting.Max), $"{key} is typed, not stepped");
		}
	}

	/// <summary>
	///     Nothing else changes control. A stepper is right for a timeout and for a percentage, and the wide box
	///     must not leak onto a setting whose whole range fits in one grain.
	/// </summary>
	[TestMethod]
	public void Only_The_Light_Levels_Span_Decades()
	{
		string[] wide =
		[
			.. AllSettings
				.Where(setting => RoomSettings.SpansDecades(setting.Min, setting.Max))
				.Select(setting => setting.Key)
		];

		CollectionAssert.AreEquivalent(
			new[] { nameof(AreaSettings.LuxThreshold), nameof(AreaSettings.LuxHysteresis) },
			wide);

		Assert.IsFalse(RoomSettings.SpansDecades(0, null), "unbounded above is not a reason to stop stepping");
		Assert.IsFalse(RoomSettings.SpansDecades(-90, 90), "a sun elevation is one grain from end to end");
		Assert.IsFalse(RoomSettings.SpansDecades(0, 100), "so is a percentage");
	}

	/// <summary>A number inside the range is taken as typed, including at either end of it.</summary>
	[TestMethod]
	public void A_Typed_Number_Inside_The_Range_Is_Taken()
	{
		Assert.AreEqual(1000, RoomSettings.ReadNumber("1000", 0, RoomSettings.MaxLux, "lx").Value);
		Assert.AreEqual(62.5, RoomSettings.ReadNumber("62.5", 0, RoomSettings.MaxLux, "lx").Value);
		Assert.AreEqual(0, RoomSettings.ReadNumber("0", 0, RoomSettings.MaxLux, "lx").Value);
		Assert.AreEqual(65535, RoomSettings.ReadNumber("65535", 0, RoomSettings.MaxLux, "lx").Value,
			"the bound itself is a value, not the first one over the line");

		Assert.IsNull(RoomSettings.ReadNumber("1000", 0, RoomSettings.MaxLux, "lx").Refusal,
			"a number that was taken has nothing to explain");

		Assert.AreEqual(1000, RoomSettings.ReadNumber("  1000  ", 0, RoomSettings.MaxLux, "lx").Value,
			"a box that was tabbed through picks up whitespace and that is not a mistake");
	}

	/// <summary>
	///     A number outside the range is refused and the refusal says what happened.
	/// </summary>
	/// <remarks>
	///     Never clamped. Turning 70 000 into 65 535 without a word answers a question nobody asked, and leaves
	///     somebody looking at a number they did not type with no way to tell it from a typo of their own.
	/// </remarks>
	[TestMethod]
	public void A_Typed_Number_Outside_The_Range_Is_Refused_And_Says_So()
	{
		TypedNumber over = RoomSettings.ReadNumber("70000", 0, RoomSettings.MaxLux, "lx");

		Assert.IsNull(over.Value, "refused, not reshaped into something nobody typed");
		StringAssert.Contains(over.Refusal, "65535 lx", "the refusal names the bound");
		StringAssert.Contains(over.Refusal, "70000 lx", "and the value it turned away");

		TypedNumber under = RoomSettings.ReadNumber("-5", 0, RoomSettings.MaxLux, "lx");

		Assert.IsNull(under.Value);
		StringAssert.Contains(under.Refusal, "0 lx");
		StringAssert.Contains(under.Refusal, "-5 lx");
	}

	/// <summary>Something that is not a number, and an empty box, both change nothing and both say why.</summary>
	[TestMethod]
	public void An_Unreadable_Entry_Changes_Nothing_And_Says_Why()
	{
		foreach (string? entry in new string?[] { null, "", "   " })
		{
			TypedNumber blank = RoomSettings.ReadNumber(entry, 0, RoomSettings.MaxLux, "lx");

			Assert.IsNull(blank.Value);
			Assert.IsFalse(string.IsNullOrWhiteSpace(blank.Refusal),
				"an empty box that silently snapped back would look like a control that does nothing");
		}

		TypedNumber words = RoomSettings.ReadNumber("dark", 0, RoomSettings.MaxLux, "lx");

		Assert.IsNull(words.Value);
		StringAssert.Contains(words.Refusal, "dark", "the refusal quotes what was actually typed");
	}

	/// <summary>
	///     A value typed on the owner's own machine round-trips, and every number this control writes is invariant.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <c>nb-NO</c> writes decimals with a comma and groups thousands with a non-breaking space. Two things
	///         have to survive that. A typed "62,5" has to reach the document as 62.5 rather than as 625; and every
	///         number this control writes back out — the bound in a refusal, the value in an HTML attribute — has to
	///         be invariant, because <c>value="62,5"</c> is not a number to any browser and renders as an empty box
	///         over a value that is really set. This project has already shipped that bug once.
	///     </para>
	///     <para>
	///         The invariant pass runs first and without thousands separators, on purpose: allowing them would read
	///         "1,5" as fifteen before the Norwegian pass ever saw it.
	///     </para>
	/// </remarks>
	[TestMethod]
	public void A_Value_Typed_Under_A_Comma_Decimal_Culture_Round_Trips()
	{
		CultureInfo original = CultureInfo.CurrentCulture;

		try
		{
			CultureInfo.CurrentCulture = new CultureInfo("nb-NO");

			Assert.AreEqual(1000, RoomSettings.ReadNumber("1000", 0, RoomSettings.MaxLux, "lx").Value);
			Assert.AreEqual(62.5, RoomSettings.ReadNumber("62,5", 0, RoomSettings.MaxLux, "lx").Value,
				"a Norwegian decimal comma is a decimal, not a thousands separator");
			Assert.AreEqual(62.5, RoomSettings.ReadNumber("62.5", 0, RoomSettings.MaxLux, "lx").Value,
				"and the browser's own invariant form still reads as itself");

			StringAssert.Contains(
				RoomSettings.ReadNumber("70000", 0, RoomSettings.MaxLux, "lx").Refusal,
				"65535 lx",
				"the bound is written invariant — '65 535 lx' carries a non-breaking space into an English sentence");

			AreaConfig room = new();
			RoomSettings.SetShown(room, nameof(AreaSettings.LuxThreshold),
				RoomSettings.ReadNumber("62,5", 0, RoomSettings.MaxLux, "lx").Value!.Value);

			Assert.AreEqual(62.5, room.LuxThreshold!.Value, 1e-9);
			Assert.AreEqual("62.5 lx", RoomSettings.Describe(room, House, nameof(AreaSettings.LuxThreshold)),
				"and it reads back as a number, not as 62,5 in the middle of an English sentence");
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
	/// <remarks>
	///     The sun threshold is drawn for the two rules that read the sun, and for neither of the two that do not.
	///     It used to be drawn for a Sensor room as well, because a room whose lux sensors said nothing fell back to
	///     the sun; that fallback is gone — such a room now counts as dark — so the row would be a control that
	///     changes nothing, which is exactly what this rule exists to keep off the page.
	/// </remarks>
	[TestMethod]
	public void A_Setting_That_Cannot_Take_Effect_Is_Not_Drawn()
	{
		RoomSetting luxThreshold = Setting(nameof(AreaSettings.LuxThreshold));
		RoomSetting sunBelow = Setting(nameof(AreaSettings.SunElevationThreshold));
		RoomSetting startLux = Setting(nameof(AreaSettings.LuxBrightnessStartLux));

		Assert.IsFalse(luxThreshold.AppliesTo(new AreaSettings { Darkness = DarknessSource.Sun }),
			"a room that gates on the sun has no lux threshold to set");
		Assert.IsTrue(luxThreshold.AppliesTo(new AreaSettings { Darkness = DarknessSource.Lux }));

		Assert.IsTrue(sunBelow.AppliesTo(new AreaSettings { Darkness = DarknessSource.Sun }));

		Assert.IsFalse(sunBelow.AppliesTo(new AreaSettings { Darkness = DarknessSource.Always }),
			"a windowless room consults neither signal");
		Assert.IsFalse(sunBelow.AppliesTo(new AreaSettings { Darkness = DarknessSource.Lux }),
			"a sensor room with nothing to read counts as dark; it no longer falls back to the sun");

		Assert.IsFalse(startLux.AppliesTo(new AreaSettings { LuxBrightnessEnabled = false }));
		Assert.IsTrue(startLux.AppliesTo(new AreaSettings { LuxBrightnessEnabled = true }));
	}

	/// <summary>
	///     No help line still promises the sun as a fallback for a room whose light-level sensors say nothing. That
	///     fallback is gone, and a help line that outlives the behaviour it describes is worse than none — this is
	///     the sentence a reader consults precisely when a room is behaving unexpectedly in the dark.
	/// </summary>
	[TestMethod]
	public void No_Help_Line_Still_Promises_The_Sun_As_A_Fallback()
	{
		foreach (RoomSetting setting in AllSettings)
		{
			Assert.IsFalse(
				setting.Help.Contains("falls back", StringComparison.OrdinalIgnoreCase),
				$"{setting.Key} still describes a fallback the engine no longer has");
		}

		StringAssert.Contains(
			Setting(nameof(AreaSettings.Darkness)).Help,
			"counts as dark",
			"the reader who can no longer see the sun row has to learn what a sensor room does with nothing to read");
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
			Darkness = DarknessSource.Lux
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
