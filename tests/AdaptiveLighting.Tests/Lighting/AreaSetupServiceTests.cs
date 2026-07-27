using System.Reflection;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     Setting rooms up from Home Assistant — the first start, and every later "set up rooms again".
/// </summary>
/// <remarks>
///     <para>
///         The two are one service on purpose, so the warning dialog cannot drift from the rebuild it warns
///         about. Most of what is asserted here is therefore the pair, not the parts: the plan's counts against
///         what the rebuild actually destroys, and the untouched rooms against the document they came from.
///     </para>
///     <para>
///         <b>The dialog must never under-warn.</b> A person who is told they will lose two hand-picked lights
///         and loses three has been lied to, and will not trust the next warning either. That is why the counting
///         helpers below read the settings off <see cref="AreaConfig"/> by reflection rather than listing them:
///         a setting added to the model tomorrow is counted here whether or not anybody remembered it.
///     </para>
/// </remarks>
[TestClass]
public sealed class AreaSetupServiceTests
{
	private sealed record House(FakeHaContext Ha, FakeAreaRegistry Registry, AreaEntityResolver Resolver);

	/// <summary>A house whose named areas each have a light and a motion sensor, plus two people.</summary>
	private static House Build(params string[] qualifyingAreaIds)
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();

		foreach (string areaId in qualifyingAreaIds)
		{
			ha.SetState($"light.{areaId}_tak", "off");
			ha.SetState($"binary_sensor.{areaId}_motion", "off", new() { ["device_class"] = "motion" });
			registry.Areas[areaId] = [$"light.{areaId}_tak", $"binary_sensor.{areaId}_motion"];
		}

		return new House(ha, registry, new AreaEntityResolver(ha, registry, new GlobalConfig(), NullLogger.Instance));
	}

	/// <summary>A document that is otherwise ordinary: a circadian table, and whatever areas the test adds.</summary>
	private static AdaptiveLightingConfig Document(params AreaConfig[] areas) => new()
	{
		ConfigName = "Adaptive lighting [test]",
		Periods = [new TimePeriodConfig { Name = "day", Start = "06:00", BrightnessPct = 80, ColorTempKelvin = 3500 }],
		Areas = [.. areas]
	};

	private static SetupPlan Plan(AdaptiveLightingConfig config, House house, params string[] ticked) =>
		AreaSetupService.Plan(config, house.Registry, house.Resolver, ticked);

	// ===================== a first run =====================

	/// <summary>
	///     Discovery does its half of the job and stops. Software installed ten minutes ago turning a bedroom
	///     light on is the wrong first experience; the owner switches on the rooms they trust it with.
	/// </summary>
	[TestMethod]
	public void A_First_Run_Proposes_Every_Qualifying_Room_Switched_Off()
	{
		House house = Build("stue", "gang");
		AdaptiveLightingConfig config = Document();

		SetupPlan plan = Plan(config, house);

		CollectionAssert.AreEquivalent(
			new[] { "stue", "gang" }, plan.NewAreas.Select(area => area.AreaId).ToArray());
		Assert.AreEqual(0, plan.Rebuilds.Count, "a document with no rooms has nothing to rebuild");

		AreaSetupService.Apply(config, plan);

		Assert.IsTrue(config.Areas.All(area => area.Enabled == false),
			"a discovered room starts off — and explicitly, never by a flipped default");
	}

	/// <summary>
	///     <c>Enabled = false</c> is written on the area, not achieved by flipping <c>Defaults.Enabled</c>.
	///     Flipping the default would retroactively switch off every room in a house whose document never wrote
	///     an explicit value, which is a silent regression in an already-running installation.
	/// </summary>
	[TestMethod]
	public void The_Default_Enabledness_Stays_True_So_Existing_Documents_Are_Unaffected()
	{
		Assert.IsTrue(new AreaSettings().Enabled);
		Assert.IsTrue(AdaptiveLightingConfig.CreateDefault().Defaults.Enabled);
	}

	/// <summary>A room already in the document is not proposed a second time.</summary>
	[TestMethod]
	public void A_Room_The_Document_Already_Has_Is_Not_Proposed_Again()
	{
		House house = Build("stue", "gang");
		AdaptiveLightingConfig config = Document(new AreaConfig { AreaId = "stue" });

		SetupPlan plan = Plan(config, house);

		Assert.AreEqual("gang", plan.NewAreas.Single().AreaId);
	}

	// ===================== people =====================

	[TestMethod]
	public void A_First_Run_Names_The_Tracker_Backed_People_When_Nobody_Is_Named()
	{
		House house = Build("stue");
		house.Ha.SetState("person.espen", "home", Trackers("device_tracker.espen_phone"));
		house.Ha.SetState("person.anne", "not_home", Trackers("device_tracker.anne_phone", "device_tracker.anne_watch"));
		house.Ha.SetState("device_tracker.bil", "home");

		AdaptiveLightingConfig config = Document();

		IReadOnlyList<string> seeded = AreaSetupService.SeedPersons(config, house.Ha);

		CollectionAssert.AreEqual(new[] { "person.anne", "person.espen" }, seeded.ToArray(),
			"the person domain only — a car tracker is not somebody who lives here");
		CollectionAssert.AreEqual(seeded.ToArray(), config.Global.Persons.ToArray());
	}

	/// <summary>
	///     A <c>person.*</c> with no device tracker can never resolve to home or away, so it is not a presence
	///     source and is not seeded. A live house carried exactly such a stray beside the two real people.
	/// </summary>
	[TestMethod]
	public void A_First_Run_Skips_A_Person_That_Has_No_Device_Tracker()
	{
		House house = Build("stue");
		house.Ha.SetState("person.anne", "home", Trackers("device_tracker.anne_phone"));
		house.Ha.SetState("person.espen", "unavailable");            // the stray: no device_trackers attribute at all
		house.Ha.SetState("person.gjest", "unknown", Trackers());   // present but with an empty tracker list

		AdaptiveLightingConfig config = Document();

		IReadOnlyList<string> seeded = AreaSetupService.SeedPersons(config, house.Ha);

		CollectionAssert.AreEqual(new[] { "person.anne" }, seeded.ToArray(),
			"only a person with at least one device tracker is a presence source");
		CollectionAssert.AreEqual(seeded.ToArray(), config.Global.Persons.ToArray());
	}

	/// <summary>A house whose only persons are tracker-less seeds none and leaves the list empty.</summary>
	[TestMethod]
	public void A_House_Whose_Only_People_Have_No_Trackers_Seeds_Nobody()
	{
		House house = Build("stue");
		house.Ha.SetState("person.espen", "unavailable");
		house.Ha.SetState("person.ghost", "home", Trackers());

		AdaptiveLightingConfig config = Document();

		Assert.AreEqual(0, AreaSetupService.SeedPersons(config, house.Ha).Count,
			"a tracker-less person is dead weight in Home/Away, so an all-strays house seeds nobody");
		Assert.AreEqual(0, config.Global.Persons.Count);
	}

	/// <summary>
	///     A list that names somebody is a decision, and seeding over it would replace the household's answer
	///     with Home Assistant's.
	/// </summary>
	[TestMethod]
	public void A_People_List_That_Names_Somebody_Is_Left_Exactly_As_It_Is()
	{
		House house = Build("stue");
		house.Ha.SetState("person.espen", "home");
		house.Ha.SetState("person.anne", "not_home");

		AdaptiveLightingConfig config = Document();
		config.Global.Persons = ["person.espen"];

		Assert.AreEqual(0, AreaSetupService.SeedPersons(config, house.Ha).Count);
		CollectionAssert.AreEqual(new[] { "person.espen" }, config.Global.Persons.ToArray());
	}

	/// <summary>
	///     The one-way rule, from the other side: a household that empties the list means it, and a later setup
	///     run must not undo that. Seeding is not part of a re-run at all — the same principle as the one-way
	///     discovery flag, which is what stops rooms deliberately removed from growing back.
	/// </summary>
	[TestMethod]
	public void A_Deliberately_Emptied_People_List_Survives_A_Later_Setup_Run()
	{
		House house = Build("stue");
		house.Ha.SetState("person.espen", "home");

		AdaptiveLightingConfig config = Document(new AreaConfig { AreaId = "stue", Name = "Living room" });
		config.Global.Persons = [];

		SetupPlan plan = Plan(config, house, "stue");
		AreaSetupService.Apply(config, plan);

		Assert.AreEqual(0, config.Global.Persons.Count,
			"re-running setup rebuilds rooms; it does not reopen a question the household already answered");
	}

	// ===================== a rebuild =====================

	/// <summary>
	///     Exactly two things survive a rebuild, and both because they are not discovery's output: the room's
	///     identity, and the owner's power switch. Re-tagging lights in HA must not silently switch a room on.
	/// </summary>
	[TestMethod]
	public void A_Ticked_Room_Is_Replaced_By_A_Fresh_Proposal_Keeping_Only_Its_Area_Id_And_Switch()
	{
		House house = Build("stue");
		AreaConfig before = Rich("stue");
		before.Enabled = true;

		AdaptiveLightingConfig config = Document(before);

		AreaSetupService.Apply(config, Plan(config, house, "stue"));

		AreaConfig after = config.Areas.Single();

		Assert.AreEqual("stue", after.AreaId, "identity survives");
		Assert.IsTrue(after.Enabled, "and so does the switch the owner threw");

		Assert.IsNull(after.Name, "the custom name is gone");
		Assert.IsNull(after.Lights, "and the hand-picked entities");
		Assert.IsNull(after.MotionSensors);
		Assert.IsNull(after.LuxSensor);
		Assert.IsNull(after.IgnoreWhenOn);
		Assert.IsNull(after.ExcludeEntities, "and the per-room exclusions");
		CollectionAssert.AreEqual(Array.Empty<string>(), OverridesOf(after).ToArray(),
			"and every changed setting — 'stue' names no role, so nothing is guessed back in");
	}

	/// <summary>An off room stays off, and a room that never said stays silent.</summary>
	[TestMethod]
	public void A_Rebuild_Carries_The_Switch_Through_Whatever_It_Said()
	{
		House house = Build("stue", "gang", "bad");

		AdaptiveLightingConfig config = Document(
			new AreaConfig { AreaId = "stue", Enabled = false },
			new AreaConfig { AreaId = "gang", Enabled = true },
			new AreaConfig { AreaId = "bad" });

		AreaSetupService.Apply(config, Plan(config, house, "stue", "gang", "bad"));

		Assert.IsFalse(config.Areas.Single(area => area.AreaId == "stue").Enabled);
		Assert.IsTrue(config.Areas.Single(area => area.AreaId == "gang").Enabled);
		Assert.IsNull(config.Areas.Single(area => area.AreaId == "bad").Enabled,
			"a room that never wrote a value keeps inheriting one");
	}

	/// <summary>The rebuild is a fresh proposal, so the role the room's name implies is guessed again.</summary>
	[TestMethod]
	public void A_Rebuild_Re_Guesses_The_Role_From_The_Area_Name()
	{
		House house = Build("gang");

		// A hallway whose guessed behaviour somebody had switched back off.
		AdaptiveLightingConfig config = Document(new AreaConfig { AreaId = "gang", WelcomeHome = false });

		AreaSetupService.Apply(config, Plan(config, house, "gang"));

		AreaConfig after = config.Areas.Single();

		Assert.IsTrue(after.WelcomeHome, "rebuilt as if newly found, and a hallway meets you at the door");
		Assert.IsTrue(after.RespectSleepMode);
	}

	// ===================== the warning must never under-warn =====================

	/// <summary>
	///     The plan and the outcome, asserted against each other rather than against numbers written here: what
	///     the dialog promises to destroy is exactly what disappears from the document.
	/// </summary>
	[TestMethod]
	public void The_Plans_Counts_Are_Exactly_What_The_Rebuild_Destroys()
	{
		House house = Build("stue");
		AreaConfig before = Rich("stue");
		AdaptiveLightingConfig config = Document(before);

		int pinnedBefore = PinnedCountOf(before);
		int overridesBefore = OverridesOf(before).Count;
		bool namedBefore = before.Name is { Length: > 0 };

		AreaRebuildPlan rebuild = Plan(config, house, "stue").Rebuilds.Single();

		Assert.AreEqual(pinnedBefore, rebuild.PinnedEntityCount, "the dialog counts what the document carries");
		Assert.AreEqual(overridesBefore, rebuild.OverrideCount);
		Assert.AreEqual(namedBefore, rebuild.HasCustomName);

		AreaSetupService.Apply(config, Plan(config, house, "stue"));

		AreaConfig after = config.Areas.Single();

		Assert.AreEqual(rebuild.PinnedEntityCount, pinnedBefore - PinnedCountOf(after),
			"and every entity it counted is one that actually went");
		Assert.AreEqual(rebuild.OverrideCount, overridesBefore - OverridesOf(after).Count,
			"and every setting it counted likewise");
		Assert.AreEqual(rebuild.HasCustomName, namedBefore && after.Name is null);
	}

	/// <summary>
	///     The same claim where the rebuild guesses some of the settings back in. A re-guessed flag is still a
	///     setting the rebuild threw away, so the warning may over-count — it must never count short.
	/// </summary>
	[TestMethod]
	public void A_Room_Whose_Role_Is_Re_Guessed_Is_Still_Never_Under_Warned()
	{
		House house = Build("gang");
		AreaConfig before = new()
		{
			AreaId = "gang",
			WelcomeHome = true,
			RespectSleepMode = true,
			VacancyTimeoutSeconds = 900
		};

		AdaptiveLightingConfig config = Document(before);
		int overridesBefore = OverridesOf(before).Count;

		AreaRebuildPlan rebuild = Plan(config, house, "gang").Rebuilds.Single();
		AreaSetupService.Apply(config, Plan(config, house, "gang"));

		int destroyed = overridesBefore - OverridesOf(config.Areas.Single()).Count;

		Assert.IsTrue(rebuild.OverrideCount >= destroyed,
			$"the warning said {rebuild.OverrideCount} settings and {destroyed} went — a warning must never count short");
		Assert.AreEqual(overridesBefore, rebuild.OverrideCount,
			"every override is destroyed; the two the role guesses back in were destroyed first");
	}

	/// <summary>
	///     Every per-room setting the model has counts toward the warning. Read off the type, so a setting added
	///     to <see cref="AreaConfig"/> without being added to the count fails here rather than quietly shrinking
	///     the number the owner is shown.
	/// </summary>
	[TestMethod]
	public void Every_Per_Room_Setting_The_Model_Has_Counts_Toward_The_Warning()
	{
		House house = Build("stue");
		AreaConfig before = new() { AreaId = "stue" };

		foreach (PropertyInfo property in SettingProperties)
			property.SetValue(before, SampleFor(property));

		AdaptiveLightingConfig config = Document(before);

		// The number the editor renders as "n of 21". Anchored so the reflection above cannot pass by finding
		// nothing, and so a settings model that grew has to be looked at rather than silently accommodated.
		// Sixteen until the five daylight-brightness settings arrived.
		Assert.AreEqual(21, SettingProperties.Count, "the per-room settings, minus Enabled");

		Assert.AreEqual(SettingProperties.Count, OverridesOf(before).Count, "the fixture set them all");
		Assert.AreEqual(OverridesOf(before).Count, Plan(config, house, "stue").Rebuilds.Single().OverrideCount);
	}

	/// <summary>
	///     The claim itself, with no number in it: fill every field the model has, rebuild, and check that the
	///     plan's three counts cover every field that actually disappeared.
	/// </summary>
	/// <remarks>
	///     The reflection above guards the <i>settings</i> half — a setting added to <see cref="AreaConfig"/>
	///     without being added to <c>OverrideCount</c> fails it. Nothing guarded the other half: an entity list
	///     added to the model and left out of <c>PinnedEntityCount</c> would be destroyed by every rebuild and
	///     counted by nobody, and the hand-written copy of that formula in this file would agree with the mistake.
	///     Stated this way the two halves are one assertion, and it does not have to be revisited every time the
	///     settings model grows.
	/// </remarks>
	[TestMethod]
	public void Nothing_The_Rebuild_Destroys_Goes_Uncounted()
	{
		House house = Build("stue");

		// Everything a rebuild can take. AreaId and Enabled are the two it gives back, so they are not losses.
		IReadOnlyList<PropertyInfo> destructible =
		[.. typeof(AreaConfig)
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(property => property.CanWrite)
			.Where(property => property.Name is not (nameof(AreaConfig.AreaId) or nameof(AreaConfig.Enabled)))];

		AreaConfig before = new() { AreaId = "stue" };

		foreach (PropertyInfo property in destructible)
			property.SetValue(before, FilledValueFor(property));

		AdaptiveLightingConfig config = Document(before);

		AreaRebuildPlan rebuild = Plan(config, house, "stue").Rebuilds.Single();
		AreaSetupService.Apply(config, Plan(config, house, "stue"));

		AreaConfig after = config.Areas.Single();

		Assert.AreEqual(
			destructible.Count, destructible.Count(property => property.GetValue(after) is null),
			"a rebuild replaces the room, so every field it carried is gone — 'stue' names no role, so nothing is "
			+ "guessed back in. A field that now survives is one the plan must stop counting as a loss.");

		// Every entity list is filled with exactly one id, so each destroyed field is worth exactly one count:
		// a name, an entity, or a setting.
		int warned = rebuild.PinnedEntityCount + rebuild.OverrideCount + (rebuild.HasCustomName ? 1 : 0);

		Assert.IsTrue(
			warned >= destructible.Count,
			$"the warning promised {warned} losses and {destructible.Count} fields went — a warning must never count short");
	}

	/// <summary><c>Enabled</c> is not counted: it survives, so warning about it would be warning about nothing.</summary>
	[TestMethod]
	public void The_Switch_Is_Not_Counted_As_A_Setting_The_Rebuild_Destroys()
	{
		House house = Build("stue");
		AdaptiveLightingConfig config = Document(new AreaConfig { AreaId = "stue", Enabled = false });

		Assert.AreEqual(0, Plan(config, house, "stue").Rebuilds.Single().OverrideCount);
	}

	/// <summary>
	///     A room tuned only through the newest settings still reports itself as tuned.
	/// </summary>
	/// <remarks>
	///     The editor used to keep a spelled-out twin of this count and was not updated when the five
	///     daylight-brightness settings arrived, so such a room summarised itself as "all automatic" while the
	///     re-setup warning correctly counted five. Both surfaces now ask the same method; this pins the case that
	///     drifted, in the units a reader sees.
	/// </remarks>
	[TestMethod]
	public void A_Room_Tuned_Only_By_Daylight_Brightness_Does_Not_Read_As_Untouched()
	{
		AreaConfig area = new()
		{
			AreaId = "gang",
			LuxBrightnessEnabled = true,
			LuxBrightnessStartLux = 250,
			LuxBrightnessFullLux = 8000,
			LuxBrightnessMaxPct = 80,
			LuxBrightnessGamma = 1.4
		};

		Assert.AreEqual(5, AreaSetupService.OverrideCount(area));
	}

	// ===================== what a run leaves alone =====================

	/// <summary>
	///     A run that ticks nothing writes nothing — asserted on the serialised document, because that is the
	///     thing the owner's next save puts on disk.
	/// </summary>
	[TestMethod]
	public void A_Run_With_Nothing_Ticked_Leaves_The_Document_Byte_Identical()
	{
		House house = Build("stue", "gang");
		AdaptiveLightingConfig config = Document(Rich("stue"), Rich("gang"));

		string before = LightingConfigDocument.Serialize(config);

		AreaSetupService.Apply(config, Plan(config, house));

		Assert.AreEqual(before, LightingConfigDocument.Serialize(config));
	}

	/// <summary>And a room nobody ticked is untouched even while the one beside it is rebuilt.</summary>
	[TestMethod]
	public void An_Unticked_Room_Is_Untouched_While_Its_Neighbour_Is_Rebuilt()
	{
		House house = Build("stue", "gang");
		AreaConfig untouched = Rich("stue");
		AdaptiveLightingConfig config = Document(untouched, Rich("gang"));

		AreaSetupService.Apply(config, Plan(config, house, "gang"));

		Assert.AreSame(untouched, config.Areas.Single(area => area.AreaId == "stue"),
			"not even replaced by an equal copy");
		Assert.AreEqual("Room stue", untouched.Name);
		Assert.AreEqual(3, OverridesOf(untouched).Count);

		Assert.IsNull(config.Areas.Single(area => area.AreaId == "gang").Name, "while its neighbour was rebuilt");
	}

	/// <summary>
	///     A room whose lights or motion sensors have gone is reported so the dialog can say so, and kept:
	///     removing a room stays the owner's explicit act, and the room says why it cannot resolve on its own.
	/// </summary>
	[TestMethod]
	public void A_Room_That_No_Longer_Qualifies_Is_Reported_And_Never_Removed()
	{
		House house = Build("stue");

		// Still an area in HA, but its motion sensor is gone, so discovery would not propose it today.
		house.Ha.SetState("light.bod_tak", "off");
		house.Registry.Areas["bod"] = ["light.bod_tak"];

		AdaptiveLightingConfig config = Document(
			new AreaConfig { AreaId = "stue" },
			new AreaConfig { AreaId = "bod" });

		SetupPlan plan = Plan(config, house, "stue", "bod");

		CollectionAssert.AreEqual(new[] { "bod" }, plan.NoLongerQualifying.ToArray());

		AreaSetupService.Apply(config, plan);

		CollectionAssert.AreEquivalent(
			new[] { "stue", "bod" }, config.Areas.Select(area => area.AreaId).ToArray(),
			"reported, not removed");
	}

	/// <summary>A room nobody ticked is not reported either — the plan describes this run, not the house.</summary>
	[TestMethod]
	public void A_Room_Outside_The_Run_Is_Neither_Rebuilt_Nor_Reported()
	{
		House house = Build("stue");
		house.Ha.SetState("light.bod_tak", "off");
		house.Registry.Areas["bod"] = ["light.bod_tak"];

		AdaptiveLightingConfig config = Document(
			new AreaConfig { AreaId = "stue" },
			new AreaConfig { AreaId = "bod" });

		SetupPlan plan = Plan(config, house, "stue");

		Assert.AreEqual("stue", plan.Rebuilds.Single().AreaId);
		Assert.AreEqual(0, plan.NoLongerQualifying.Count);
	}

	/// <summary>Planning is a question, not an action: the dialog can be opened and cancelled.</summary>
	[TestMethod]
	public void Planning_Changes_Nothing()
	{
		House house = Build("stue", "gang");
		AdaptiveLightingConfig config = Document(Rich("stue"));

		string before = LightingConfigDocument.Serialize(config);

		Plan(config, house, "stue");

		Assert.AreEqual(before, LightingConfigDocument.Serialize(config));
	}

	/// <summary>Rooms are added at the end, so a rebuild never reorders the list the owner is looking at.</summary>
	[TestMethod]
	public void New_Rooms_Are_Appended_And_The_Existing_Order_Is_Kept()
	{
		House house = Build("stue", "gang", "bad");

		AdaptiveLightingConfig config = Document(
			new AreaConfig { AreaId = "gang" },
			new AreaConfig { AreaId = "stue" });

		AreaSetupService.Apply(config, Plan(config, house, "gang", "stue"));

		CollectionAssert.AreEqual(
			new[] { "gang", "stue", "bad" }, config.Areas.Select(area => area.AreaId).ToArray());
	}

	// ===================== a plan the document has moved on from =====================

	/// <summary>
	///     A plan is a value somebody holds across an edit. The Areas page keeps the setup panel open beside its
	///     own "Add a room" and "Discard changes" buttons, so by the time the run is confirmed the document may
	///     already carry the room the plan meant to add. Adding it anyway leaves two rows for one Home Assistant
	///     area — which either refuses every save (the validator rejects a duplicate area name) or, once one row
	///     carries a name of its own, runs two state machines against the same lights.
	/// </summary>
	[TestMethod]
	public void A_Room_Added_By_Hand_After_The_Plan_Was_Made_Is_Not_Added_Twice()
	{
		House house = Build("stue", "loft");
		AdaptiveLightingConfig config = Document(new AreaConfig { AreaId = "stue" });

		SetupPlan plan = Plan(config, house, "stue");

		Assert.AreEqual("loft", plan.NewAreas.Single().AreaId, "the plan means to add the room the document lacks");

		// The owner adds it themselves while the confirmation step is still on screen.
		config.Areas.Add(new AreaConfig { AreaId = "loft", Name = "Loft" });

		AreaSetupService.Apply(config, plan);

		CollectionAssert.AreEqual(
			new[] { "stue", "loft" }, config.Areas.Select(area => area.AreaId).ToArray(),
			"one row per Home Assistant area");
		Assert.AreEqual("Loft", config.Areas[1].Name,
			"and the owner's own row stands: adding a room is not rebuilding one");
	}

	/// <summary>
	///     The same plan applied twice is the same document. Confirming is one click on a panel surrounded by
	///     other live controls, and appending the plan's own <see cref="AreaConfig"/> instances a second time put
	///     one object at two indices — so editing either row edited both.
	/// </summary>
	[TestMethod]
	public void Applying_The_Same_Plan_Twice_Leaves_The_Same_Document()
	{
		House house = Build("stue", "gang");
		AdaptiveLightingConfig config = Document();

		SetupPlan plan = Plan(config, house);

		AreaSetupService.Apply(config, plan);
		string once = LightingConfigDocument.Serialize(config);

		AreaSetupService.Apply(config, plan);

		Assert.AreEqual(2, config.Areas.Count, "applying a plan is not adding its rooms twice");
		Assert.AreEqual(once, LightingConfigDocument.Serialize(config));
	}

	// ===================== fixtures and counting =====================

	/// <summary>A person's <c>device_trackers</c> attribute — the presence sources backing them.</summary>
	private static Dictionary<string, object> Trackers(params string[] trackerIds) =>
		new() { ["device_trackers"] = trackerIds };

	/// <summary>An area carrying one of everything a rebuild destroys.</summary>
	private static AreaConfig Rich(string areaId) => new()
	{
		AreaId = areaId,
		Name = $"Room {areaId}",
		Lights = [$"light.{areaId}_a", $"light.{areaId}_b"],
		MotionSensors = [$"binary_sensor.{areaId}_m"],
		LuxSensor = $"sensor.{areaId}_lux",
		IgnoreWhenOn = [$"media_player.{areaId}"],
		ExcludeEntities = [$"sensor.{areaId}_fridge_lux"],
		VacancyTimeoutSeconds = 900,
		PreOffSeconds = 45,
		Darkness = DarknessSource.Sun
	};

	/// <summary>Entity ids the area lists instead of discovering, plus the ids it excludes from discovery.</summary>
	private static int PinnedCountOf(AreaConfig area) =>
		(area.Lights?.Count ?? 0)
		+ (area.MotionSensors?.Count ?? 0)
		+ (area.LuxSensor is { Length: > 0 } ? 1 : 0)
		+ (area.IgnoreWhenOn?.Count ?? 0)
		+ (area.ExcludeEntities?.Count ?? 0);

	/// <summary>Property names that are the room's identity or its entity lists, not one of its settings.</summary>
	private static readonly HashSet<string> NotASetting = new(StringComparer.Ordinal)
	{
		nameof(AreaConfig.Name),
		nameof(AreaConfig.AreaId),
		nameof(AreaConfig.Lights),
		nameof(AreaConfig.MotionSensors),
		nameof(AreaConfig.LuxSensor),
		nameof(AreaConfig.IgnoreWhenOn),
		nameof(AreaConfig.ExcludeEntities),

		// Survives the rebuild, so it is never one of the losses.
		nameof(AreaConfig.Enabled)
	};

	/// <summary>The per-room settings, taken from the model rather than listed, for the reason in the class remarks.</summary>
	private static readonly IReadOnlyList<PropertyInfo> SettingProperties =
	[.. typeof(AreaConfig)
		.GetProperties(BindingFlags.Public | BindingFlags.Instance)
		.Where(property => property.CanWrite && !NotASetting.Contains(property.Name))
		.OrderBy(property => property.Name, StringComparer.Ordinal)];

	/// <summary>Which of those settings the area actually overrides.</summary>
	private static IReadOnlyList<string> OverridesOf(AreaConfig area) =>
	[.. SettingProperties
		.Where(property => property.GetValue(area) is not null)
		.Select(property => property.Name)];

	/// <summary>
	///     Any non-null value of the property's type, including the entity lists — so a fixture can fill in
	///     everything a rebuild destroys, not only the settings.
	/// </summary>
	/// <remarks>
	///     One id per list on purpose: it makes each destroyed field worth exactly one count, which is what lets
	///     <see cref="Nothing_The_Rebuild_Destroys_Goes_Uncounted"/> compare fields against counts. A property of a
	///     shape nothing here can fill throws rather than being skipped — a field nobody can fill is a field
	///     nobody has thought about losing.
	/// </remarks>
	private static object FilledValueFor(PropertyInfo property)
	{
		Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

		return type == typeof(List<string>) ? new List<string> { "light.one" } : SampleFor(property);
	}

	/// <summary>Any non-null value of the property's type, so a fixture can fill every setting in.</summary>
	private static object SampleFor(PropertyInfo property)
	{
		Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

		if (type.IsEnum)
			return Enum.GetValues(type).GetValue(0)!;

		if (type == typeof(string))
			return "sun.sun";

		return Convert.ChangeType(1, type, System.Globalization.CultureInfo.InvariantCulture);
	}
}
