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

	/// <summary>The same house with its one room switched off, which is where the issue starts.</summary>
	private static AdaptiveLightingConfig SwitchedOff()
	{
		AdaptiveLightingConfig config = Document();
		config.Periods = [new TimePeriodConfig { Id = "day", Name = "day", Start = "06:00", BrightnessPct = 80, ColorTempKelvin = 3500 }];
		config.Areas[0].Enabled = false;

		return config;
	}

	private HousePageModel Model(AdaptiveLightingConfig? document = null, bool running = false)
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

		HaCatalog catalog = new(ha, new FakeHaRegistry(), NullLoggerFactory.Instance);
		AreaSnapshotCache cache = new(
			_provider.GetRequiredService<IServiceScopeFactory>(),
			NullLogger<AreaSnapshotCache>.Instance,
			new ActivityLog());

		return new HousePageModel(
			_engine,
			catalog,
			new ConfigLocation(_path, ConfigLocationSource.External, null),
			new HomeLocation(ha),
			cache,
			NullLogger<HousePageModel>.Instance);
	}
}
