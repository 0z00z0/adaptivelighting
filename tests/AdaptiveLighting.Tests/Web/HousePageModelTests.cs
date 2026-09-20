using AdaptiveLighting.Configuration;
using AdaptiveLighting.Hosting;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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

	private HousePageModel Model() => Model(Document());

	private HousePageModel Model(AdaptiveLightingConfig document)
	{
		_path = Path.Combine(Path.GetTempPath(), $"adaptive-lighting-house-{Guid.NewGuid():N}.yaml");
		File.WriteAllText(_path, LightingConfigDocument.Serialize(document));

		LightingConfigStore store = new(_path, NullLogger<LightingConfigStore>.Instance);
		_engine = new LightingEngineHost(store, NullLoggerFactory.Instance);

		FakeHaContext ha = new();
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
