using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Hosting;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

using NetDaemon.HassModel;

namespace AdaptiveLighting.Tests.Web;

/// <summary>The house page's rules, with no renderer: what arms the save bar, what refuses a save, and where an
/// old bookmark lands.</summary>
/// <remarks>The page model is a plain class precisely so these can be asked directly. Nothing here mounts a
/// component.</remarks>
[TestClass]
public sealed class HousePageModelTests
{
	private string _path = "";
	private ServiceProvider? _provider;
	private LightingEngineHost? _engine;

	[TestCleanup]
	public void Cleanup()
	{
		_engine?.Dispose();
		_provider?.Dispose();

		foreach (string file in new[] { _path, _path + ".bak" })
		{
			if (file.Length > 0 && File.Exists(file))
				File.Delete(file);
		}
	}

	[TestMethod]
	public void An_Edit_Marks_The_Document_Dirty()
	{
		HousePageModel model = Model();
		model.Start(null);

		Assert.IsFalse(model.HasUnsavedEdits, "A freshly loaded document matches the file.");
		Assert.IsFalse(model.ShowSaveBar);

		model.HouseName = "Somewhere else";

		Assert.IsTrue(model.HasUnsavedEdits);
		Assert.IsTrue(model.ShowSaveBar, "An edit is what puts the save bar on screen.");
	}

	[TestMethod]
	public void A_Document_That_Moved_On_Disk_Is_Refused_As_A_Conflict()
	{
		HousePageModel model = Model();
		model.Start(null);

		model.HouseName = "Edited in the page";

		// Somebody else writes the file while the page is open.
		AdaptiveLightingConfig elsewhere = Document();
		elsewhere.ConfigName = "Edited somewhere else";
		File.WriteAllText(_path, LightingConfigDocument.Serialize(elsewhere));

		model.Save();

		Assert.IsNotNull(model.Result);
		Assert.IsFalse(model.Result.Written, "Saving would have reverted the other write.");
		Assert.AreEqual(SaveStatus.Conflicted, model.Result.Status);
		Assert.AreEqual("Edited somewhere else", LightingConfigDocument.Deserialize(File.ReadAllText(_path)).Config.ConfigName);
	}

	[TestMethod]
	public void Reload_From_Home_Assistant_Is_Refused_While_An_Edit_Is_Unsaved()
	{
		HousePageModel model = Model();
		model.Start(null);

		model.HouseName = "Edited, not saved";
		Assert.IsTrue(model.HasUnsavedEdits);
		Assert.IsFalse(model.CanReloadFromHomeAssistant);

		model.ReloadFromHomeAssistant();

		StringAssert.Contains(model.ReloadOutcome, "Save");
		Assert.IsNull(_engine!.LastValidation, "A refused reload must never reach the engine — it would rebuild off the file, silently dropping the edit on screen.");
	}

	[TestMethod]
	public void A_Section_Alias_Resolves_To_Its_Section()
	{
		Assert.AreEqual(HouseSection.Schedule, HousePageModel.Resolve("periods"));
		Assert.AreEqual(HouseSection.Areas, HousePageModel.Resolve("rooms"));
		Assert.AreEqual(HouseSection.House, HousePageModel.Resolve("defaults"));
		Assert.AreEqual(HouseSection.House, HousePageModel.Resolve("people"));
		Assert.AreEqual(HouseSection.HouseModes, HousePageModel.Resolve("housemodes"));
		Assert.IsNull(HousePageModel.Resolve("nothing-by-that-name"));
		Assert.IsNull(HousePageModel.Resolve(null));
	}

	[TestMethod]
	public void An_Alias_Opens_Its_Section_On_Load()
	{
		HousePageModel model = Model();
		model.Start("periods");

		Assert.IsTrue(model.IsActive(HouseSection.Schedule));
		Assert.IsFalse(model.IsActive(HouseSection.Areas));
	}

	[TestMethod]
	public void Moving_A_Room_Reaches_The_File_And_Comes_Back_In_That_Order()
	{
		HousePageModel model = Model(ThreeRooms());
		model.Start(null);

		CollectionAssert.AreEqual(new[] { "stue", "kjokken", "bad" }, Listed(model));
		Assert.IsFalse(model.CanMoveRoomUp(model.Areas[0]), "The first room has nothing above it.");
		Assert.IsFalse(model.CanMoveRoomDown(model.Areas[2]), "The last room has nothing below it.");

		model.MoveRoomDown(model.Areas[0]);

		CollectionAssert.AreEqual(new[] { "kjokken", "stue", "bad" }, Listed(model));
		Assert.IsTrue(model.HasUnsavedEdits, "A move is an edit like any other: it arms the save bar.");
		CollectionAssert.AreEqual(new[] { "stue", "kjokken", "bad" }, OnDisk(), "Nothing reaches the file before the save.");

		model.Save();

		Assert.IsFalse(model.HasUnsavedEdits, "The save was refused: " + (model.Result?.Message ?? model.LoadError));
		CollectionAssert.AreEqual(new[] { "kjokken", "stue", "bad" }, OnDisk());

		// Re-read from disk, which is what a fresh visit to the page does.
		model.Reload();

		CollectionAssert.AreEqual(new[] { "kjokken", "stue", "bad" }, Listed(model));
	}

	/// <summary>The rooms as the page lists them: through the floor groups, which is what both designs draw.</summary>
	private static string[] Listed(HousePageModel model) =>
		[.. model.AreaGroups.SelectMany(group => group.Items).Select(area => area.AreaId ?? "")];

	private string[] OnDisk() =>
		[.. LightingConfigDocument.Deserialize(File.ReadAllText(_path)).Config.Areas.Select(area => area.AreaId ?? "")];
	/// <summary>
	///     Switching a room on has to reach the engine on the press. Until it does the room resolves nothing and
	///     runs nothing, whatever the page shows.
	/// </summary>
	/// <remarks>Read off the engine's rebuild notice and the document it rebuilt on, which is as close to the
	/// running room as a test can stand: HassModel's <c>Area</c> cannot be constructed, so no fake registry knows
	/// an area id and no room carrying one resolves here.</remarks>
	[TestMethod]
	public void Switching_A_Room_On_Rebuilds_The_Engine_Without_Pressing_Save()
	{
		List<EngineNoticeKind> rebuilds = [];
		HousePageModel model = Model(SwitchedOff(), running: true);

		using IDisposable subscription = _engine!.Notices.Subscribe(notice => rebuilds.Add(notice.Kind));

		// The engine replays its last rebuild to a late subscriber, and the start that brought it up is not what
		// this test is about.
		rebuilds.Clear();

		model.Start(null);

		AreaConfig room = model.Areas.Single();

		Assert.IsFalse(model.IsEnabled(room));
		Assert.AreEqual(0, rebuilds.Count, "opening the page rebuilds nothing");

		model.ToggleEnabled(room);

		CollectionAssert.AreEqual(
			new[] { EngineNoticeKind.SettingsSaved },
			rebuilds,
			"the press has to rebuild the engine, not just arm the save bar");

		Assert.IsTrue(RoomIsOn(_engine.Store.Load()), "and rebuild it on a document that runs this room");
		Assert.IsFalse(model.HasUnsavedEdits, "the switch is written, so the save bar has nothing left to hold");
	}

	/// <summary>The immediate write is scoped to the one room: everything else on the page is still the page's to
	/// save.</summary>
	[TestMethod]
	public void An_Ordinary_Edit_Is_Not_Swept_Into_A_Room_Switch_And_Still_Waits_For_Save()
	{
		List<EngineNoticeKind> rebuilds = [];
		HousePageModel model = Model(SwitchedOff(), running: true);

		using IDisposable subscription = _engine!.Notices.Subscribe(notice => rebuilds.Add(notice.Kind));

		rebuilds.Clear();

		model.Start(null);
		model.HouseName = "Somewhere else";

		Assert.AreEqual(0, rebuilds.Count, "an ordinary field edit reaches nothing on its own");
		Assert.AreEqual("Preview house", LightingConfigDocument.Deserialize(File.ReadAllText(_path)).Config.ConfigName);

		model.ToggleEnabled(model.Areas.Single());

		AdaptiveLightingConfig onDisk = LightingConfigDocument.Deserialize(File.ReadAllText(_path)).Config;

		Assert.IsTrue(RoomIsOn(onDisk), "the switch reached the file on the press");
		Assert.AreEqual("Preview house", onDisk.ConfigName, "and took no other edit with it");
		Assert.IsTrue(model.HasUnsavedEdits, "which is what the save bar is still up for");

		model.Save();

		Assert.AreEqual(
			"Somewhere else",
			LightingConfigDocument.Deserialize(File.ReadAllText(_path)).Config.ConfigName,
			"the scoped write must not leave the page saving against a token the file has moved past");
	}

	/// <summary>A refused write must leave the switch where the file left it, not where the press did.</summary>
	[TestMethod]
	public void A_Room_Switch_Goes_Back_When_Its_Write_Is_Refused()
	{
		HousePageModel model = Model();
		model.Start(null);

		AreaConfig room = model.Areas.Single();

		Assert.IsTrue(model.IsEnabled(room), "arranged: the room starts switched on");

		// Somebody else writes that same room while the page is open.
		AdaptiveLightingConfig elsewhere = Document();
		elsewhere.Areas[0].Lights = ["light.stue_taklys", "light.stue_vindu"];
		File.WriteAllText(_path, LightingConfigDocument.Serialize(elsewhere));

		model.ToggleEnabled(room);

		Assert.IsNotNull(model.Result);
		Assert.AreEqual(SaveStatus.Conflicted, model.Result.Status, "arranged: the write is refused as a conflict");
		Assert.IsTrue(model.IsEnabled(room), "nothing was written, so the switch must not read as switched off");
		Assert.IsTrue(RoomIsOn(LightingConfigDocument.Deserialize(File.ReadAllText(_path)).Config),
			"and the file still says what the other write left");
	}

	/// <summary>An unsaved edit to the same room is this page's own, and must not read as somebody else's
	/// write.</summary>
	[TestMethod]
	public void Switching_A_Floor_In_The_Draft_Does_Not_Refuse_A_Room_Switch_After_It()
	{
		HousePageModel model = Model(ThreeRooms());
		model.Start(null);

		// Switches every room off in the draft, which touches the very slot the press below writes.
		model.SwitchFloor(model.AreaGroups.Single());

		Assert.IsTrue(model.HasUnsavedEdits, "arranged: the floor switch is an unsaved edit");

		AreaConfig room = model.Areas.First(area => string.Equals(area.AreaId, "kjokken", StringComparison.Ordinal));

		model.ToggleEnabled(room);

		Assert.IsNull(model.Result, "the page's own pending edit is not a conflict: " + (model.Result?.Message ?? ""));
		Assert.IsTrue(model.IsEnabled(room), "the press switched the room back on");
		Assert.IsTrue(
			LightingConfigDocument.Deserialize(File.ReadAllText(_path)).Config.Areas
				.Single(area => string.Equals(area.AreaId, "kjokken", StringComparison.Ordinal))
				.Enabled == true,
			"and it reached the file");
	}

	/// <summary>A room moves within its own floor and never out of it.</summary>
	[TestMethod]
	public void A_Room_Moves_Within_Its_Floor_And_Never_Across_A_Floor_Boundary()
	{
		HousePageModel model = Model(ThreeRooms(), areas: TwoFloors());
		model.Start(null);

		CollectionAssert.AreEqual(new[] { "stue", "bad", "kjokken" }, Listed(model),
			"arranged: the ground floor's two rooms are listed before the upper floor's one");

		AreaConfig first = model.AreaGroups[0].Items[0];
		AreaConfig last = model.AreaGroups[0].Items[1];
		AreaConfig alone = model.AreaGroups[1].Items.Single();

		Assert.IsFalse(model.CanMoveRoomUp(first), "the first room on a floor has nothing above it on that floor");
		Assert.IsFalse(model.CanMoveRoomDown(last), "the last room on a floor has nothing below it on that floor");
		Assert.IsFalse(model.CanMoveRoomUp(alone), "a floor's only room has nowhere to go");
		Assert.IsFalse(model.CanMoveRoomDown(alone));

		model.MoveRoomUp(first);
		model.MoveRoomDown(last);
		model.MoveRoomUp(alone);

		CollectionAssert.AreEqual(new[] { "stue", "bad", "kjokken" }, Listed(model), "a refused move changes nothing");
		Assert.IsFalse(model.HasUnsavedEdits, "and arms nothing");

		model.MoveRoomDown(first);

		CollectionAssert.AreEqual(new[] { "bad", "stue", "kjokken" }, Listed(model),
			"the swap is within the floor, and the other floor's room stays where it is");
		CollectionAssert.AreEqual(new[] { "kjokken" }, Floor(model, 1), "no room crossed the boundary");
	}

	/// <summary>Two rooms on the ground floor and one above, with the upper floor's room sitting between them in
	/// the document, so a move that ignored the grouping would be visible.</summary>
	private static FakeAreaRegistry TwoFloors()
	{
		FakeAreaRegistry registry = new();

		registry.Floors["stue"] = new AreaFloor("ground", "Ground floor", 0);
		registry.Floors["kjokken"] = new AreaFloor("upper", "Upper floor", 1);
		registry.Floors["bad"] = new AreaFloor("ground", "Ground floor", 0);

		return registry;
	}

	private static string[] Floor(HousePageModel model, int index) =>
		[.. model.AreaGroups[index].Items.Select(area => area.AreaId ?? "")];

	private static bool RoomIsOn(AdaptiveLightingConfig document) =>
		document.Areas.Single().Effective(document.Defaults).Enabled;

	private static AdaptiveLightingConfig Document() => new()
	{
		ConfigName = "Preview house",
		Areas =
		[
			new AreaConfig { AreaId = "stue", Name = "Stue", Enabled = true, Lights = ["light.stue_taklys"] }
		]
	};

	/// <summary>A document a save will accept: the validator refuses one with no periods, and a period with no Id
	/// is minted a fresh one on every load, which reads as the file having moved under the page.</summary>
	private static AdaptiveLightingConfig ThreeRooms() => new()
	{
		ConfigName = "Preview house",
		Periods = [new TimePeriodConfig { Id = "day-0000", Name = "day", Start = "07:00", BrightnessPct = 80, ColorTempKelvin = 3500 }],
		Areas =
		[
			new AreaConfig { AreaId = "stue", Name = "Stue", Enabled = true, Lights = ["light.stue_taklys"] },
			new AreaConfig { AreaId = "kjokken", Name = "Kjøkken", Enabled = true, Lights = ["light.kjokken_taklys"] },
			new AreaConfig { AreaId = "bad", Name = "Bad", Enabled = true, Lights = ["light.bad_taklys"] }
		]
	};

	/// <summary>The same house with its one room switched off, which is where the issue starts.</summary>
	private static AdaptiveLightingConfig SwitchedOff()
	{
		AdaptiveLightingConfig config = Document();
		config.Periods = [new TimePeriodConfig { Id = "day", Name = "day", Start = "06:00", BrightnessPct = 80, ColorTempKelvin = 3500 }];
		config.Areas[0].Enabled = false;

		return config;
	}

	private HousePageModel Model(
		AdaptiveLightingConfig? document = null,
		bool running = false,
		IAreaRegistry? areas = null)
	{
		_path = Path.Combine(Path.GetTempPath(), $"adaptive-lighting-house-{Guid.NewGuid():N}.yaml");
		File.WriteAllText(_path, LightingConfigDocument.Serialize(document ?? Document()));

		LightingConfigStore store = new(_path, NullLogger<LightingConfigStore>.Instance);
		_engine = new LightingEngineHost(store, NullLoggerFactory.Instance);

		FakeHaContext ha = new();
		ha.SetState("light.stue_taklys", "off");

		if (running)
		{
			TestScheduler scheduler = new();
			scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

			_engine.Attach(ha, new FakeHaRegistry(), scheduler);
			_engine.Reload();
		}

		ServiceCollection services = new();
		services.AddScoped<IHaContext>(_ => ha);
		_provider = services.BuildServiceProvider();

		HaCatalog catalog = new(ha, new FakeHaRegistry(), NullLoggerFactory.Instance, areas);
		AreaSnapshotCache cache = new(
			_provider.GetRequiredService<IServiceScopeFactory>(),
			NullLogger<AreaSnapshotCache>.Instance,
			new ActivityLog());
		ModeService modes = new(ha, new FakeAppConfig(Document()), catalog, _engine, NullLogger<ModeService>.Instance);

		return new HousePageModel(
			_engine,
			catalog,
			new ConfigLocation(_path, ConfigLocationSource.External, null),
			new HomeLocation(ha),
			cache,
			modes,
			NullLogger<HousePageModel>.Instance);
	}
}
