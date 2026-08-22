using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Hosting;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The save path: validate first, write second, and never the other way round.</summary>
// A refused save must reach no byte of the disk, so "the file is untouched" is the assertion that matters. With
// nothing attached the host validates without the referential checks.
[TestClass]
public sealed class LightingEngineHostTests
{
	private string _directory = "";
	private string _path = "";

	[TestInitialize]
	public void CreateTempDirectory()
	{
		_directory = Path.Combine(Path.GetTempPath(), $"lighting-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_directory);
		_path = Path.Combine(_directory, "AdaptiveLighting.yaml");
	}

	[TestCleanup]
	public void RemoveTempDirectory()
	{
		if (Directory.Exists(_directory))
			Directory.Delete(_directory, recursive: true);
	}

	private LightingEngineHost BuildHost() =>
		new(new LightingConfigStore(_path, NullLogger<LightingConfigStore>.Instance), NullLoggerFactory.Instance);

	/// <summary>A document the validator accepts: a circadian table and one area naming a registry area id.</summary>
	private static AdaptiveLightingConfig Valid() => new()
	{
		ConfigName = "Adaptive lighting [test]",
		Periods = [new TimePeriodConfig { Name = "day", Start = "06:00", BrightnessPct = 80, ColorTempKelvin = 3500 }],
		Areas = [new AreaConfig { Name = "Stue", AreaId = "stue" }]
	};

	[TestMethod]
	public void Save_WithNoPeriods_IsRefusedAndNothingIsWritten()
	{
		var config = Valid();
		config.Periods = [];

		var result = BuildHost().Save(config);

		Assert.AreEqual(SaveStatus.Rejected, result.Status);
		Assert.IsFalse(result.Written);
		Assert.IsFalse(File.Exists(_path), "A document the engine cannot run must never reach the disk.");
		Assert.IsFalse(result.Validation.IsValid);
	}

	[TestMethod]
	public void Save_WithNoAreas_IsAccepted()
	{
		var config = Valid();
		config.Areas = [];

		var result = BuildHost().Save(config);

		Assert.AreNotEqual(SaveStatus.Rejected, result.Status, "an area-less document is valid, just idle");
		Assert.IsTrue(File.Exists(_path), "and it reaches the disk");
	}

	/// <summary>The page renders these messages verbatim, so the refusal has to carry the validator's own words.</summary>
	[TestMethod]
	public void Save_WhenRefused_ReportsTheValidatorsOwnMessages()
	{
		var config = Valid();
		config.Defaults.PreOffSeconds = 900;
		config.Defaults.VacancyTimeoutSeconds = 600;

		var result = BuildHost().Save(config);

		Assert.AreEqual(SaveStatus.Rejected, result.Status);
		Assert.IsTrue(
			result.Validation.Errors.Any(error => error.Contains("PreOffSeconds", StringComparison.Ordinal)),
			$"Expected the validator's PreOffSeconds message, got: {result.Validation}");
	}

	[TestMethod]
	public void Save_WhenRefused_LeavesTheExistingDocumentExactlyAsItWas()
	{
		var host = BuildHost();
		Assert.AreEqual(SaveStatus.Saved, host.Save(Valid()).Status);

		var before = File.ReadAllText(_path);

		var broken = Valid();
		broken.Periods = [];

		Assert.AreEqual(SaveStatus.Rejected, host.Save(broken).Status);
		Assert.AreEqual(before, File.ReadAllText(_path));
	}

	[TestMethod]
	public void Save_OfAValidDocument_WritesItAndItLoadsBack()
	{
		var host = BuildHost();

		var result = host.Save(Valid());

		Assert.AreEqual(SaveStatus.Saved, result.Status);
		Assert.IsTrue(result.Written);
		Assert.IsTrue(File.Exists(_path));

		var reloaded = host.Store.Load();

		Assert.AreEqual("Adaptive lighting [test]", reloaded.ConfigName);
		Assert.AreEqual("stue", reloaded.Areas.Single().AreaId);
	}

	[TestMethod]
	public void Save_WithNoHomeAssistantAttached_SavesButDoesNotClaimToBeRunning()
	{
		var host = BuildHost();

		var result = host.Save(Valid());

		Assert.AreEqual(SaveStatus.Saved, result.Status);
		Assert.IsFalse(host.IsRunning);
		Assert.IsFalse(host.IsAttached);
		Assert.AreEqual(0, host.RunningAreaCount);
	}

	// ---- the record's only sight of a rebuild ---------------------------------------------------
	// One notice per rebuild is what puts the cause in the timeline; the activity log is fed from per-area events.

	/// <summary>A scheduler on a real date, because from tick zero the engine's periodic timers overflow.</summary>
	private static TestScheduler Clocked()
	{
		TestScheduler scheduler = new();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		return scheduler;
	}

	/// <summary>A document with no areas, so the rebuild turns on the connection alone and not on the registry.</summary>
	private static AdaptiveLightingConfig Roomless()
	{
		AdaptiveLightingConfig config = Valid();
		config.Areas = [];

		return config;
	}

	/// <summary>The settings page resolves its preview through this latch, so a fresh one would call every held period not begun.</summary>
	[TestMethod]
	public void ARunningEngine_HandsOverItsLatchForPeriodsThatWaitForMovement()
	{
		LightingEngineHost host = BuildHost();
		host.Attach(new FakeHaContext(), new FakeHaRegistry(), Clocked());

		Assert.IsNull(host.MotionPeriods, "nothing is running yet, so there is nothing to ask");

		AdaptiveLightingConfig config = Roomless();

		// Ids, not names: the latch keys on Key, and a save without one mints a random-suffixed id.
		config.Periods[0].Id = "day";
		config.Periods.Add(new TimePeriodConfig
		{
			Id = "morning",
			Name = "morning",
			Start = "07:00",
			StartsOnMotion = true,
			BrightnessPct = 60,
			ColorTempKelvin = 3000
		});

		host.Save(config);

		Assert.IsNotNull(host.MotionPeriods);
		Assert.IsTrue(host.MotionPeriods.Holds("morning"), "the document says this period waits for movement");
		Assert.IsFalse(host.MotionPeriods.Holds("day"), "and this one does not");

		MotionPeriodLatch first = host.MotionPeriods;
		host.Reload();

		Assert.AreNotSame(first, host.MotionPeriods, "a rebuild is a new engine, so a cached reference would go stale");

		host.Dispose();

		Assert.IsNull(host.MotionPeriods);
	}

	[TestMethod]
	public void EachRebuild_RaisesOneNotice_SayingWhichKindItWas()
	{
		List<EngineNotice> notices = [];
		LightingEngineHost host = BuildHost();

		using IDisposable subscription = host.Notices.Subscribe(notices.Add);

		host.Attach(new FakeHaContext(), new FakeHaRegistry(), Clocked());
		host.Save(Roomless());
		host.Reload();

		CollectionAssert.AreEqual(
			new[] { EngineNoticeKind.SettingsSaved, EngineNoticeKind.Started },
			notices.ConvertAll(notice => notice.Kind),
			"a save and a start are the two rebuilds, and each is one notice");

		host.Dispose();
	}

	/// <summary>The engine is started by the bootstrap app, which can beat the web host's recorder to it.</summary>
	[TestMethod]
	public void TheStartNotice_ReachesARecorderThatSubscribedAfterwards()
	{
		LightingEngineHost host = BuildHost();
		host.Attach(new FakeHaContext(), new FakeHaRegistry(), Clocked());
		host.Save(Roomless());

		List<EngineNotice> notices = [];
		using IDisposable subscription = host.Notices.Subscribe(notices.Add);

		Assert.AreEqual(1, notices.Count, "the rebuild that already happened is still the one that explains the rows");

		host.Dispose();
	}

	[TestMethod]
	public void ARebuildThatCannotRun_RaisesNothing()
	{
		List<EngineNotice> notices = [];
		LightingEngineHost host = BuildHost();

		using IDisposable subscription = host.Notices.Subscribe(notices.Add);

		// Nothing attached, so the document is written and no engine is built.
		host.Save(Roomless());

		Assert.AreEqual(0, notices.Count, "nothing was rebuilt, so the record must not say the house was");
	}

	[TestMethod]
	public void Save_OverAnExistingDocument_KeepsOneBackup()
	{
		var host = BuildHost();
		host.Save(Valid());

		var first = File.ReadAllText(_path);

		var second = Valid();
		second.ConfigName = "Adaptive lighting [second]";
		host.Save(second);

		Assert.IsTrue(host.Store.HasBackup);
		Assert.AreEqual(first, File.ReadAllText(host.Store.BackupPath));
		StringAssert.Contains(File.ReadAllText(_path), "[second]");
	}

	[TestMethod]
	public void Save_LeavesNoTemporaryFilesBehind()
	{
		BuildHost().Save(Valid());

		Assert.AreEqual(0, Directory.GetFiles(_directory, "*.tmp").Length);
	}

	[TestMethod]
	public void Reload_WhenTheFileIsMissing_ReportsItRatherThanThrowing()
	{
		var host = BuildHost();

		var result = host.Reload();

		Assert.AreEqual(SaveStatus.Failed, result.Status);
		Assert.IsFalse(host.IsRunning);
		Assert.IsNotNull(host.Fault);
	}

	[TestMethod]
	public void Reload_WhenTheFileIsCorrupt_ReportsItRatherThanThrowing()
	{
		File.WriteAllText(_path, "\tnot: [valid\n  yaml\n");

		var result = BuildHost().Reload();

		Assert.AreEqual(SaveStatus.Failed, result.Status);
		Assert.IsFalse(result.Validation.IsValid);
	}

	[TestMethod]
	public void Save_WithAnAreaThatCannotResolve_StillSaves()
	{
		var config = Valid();
		config.Areas.Add(new AreaConfig { Name = "Broken" });

		var result = BuildHost().Save(config);

		Assert.AreEqual(SaveStatus.Saved, result.Status);
		Assert.IsTrue(result.Validation.IsValid, "An area error is not a document error.");
		Assert.AreEqual("Broken", result.Validation.AreaErrors.Single().AreaName);
		Assert.IsTrue(File.Exists(_path));
	}

	/// <summary>A room with no <c>Name</c> is identified by its area id, so re-pointing it at a taken area gives two rows one <see cref="AreaConfig.DisplayName"/>.</summary>
	// Room.razor navigates to the new area only on SaveResult.Written; downgrading this refusal to a warning makes
	// that guard dead code.
	[TestMethod]
	public void Save_WhenTwoRoomsWouldShareAnAreaId_IsRefusedAndNothingIsWritten()
	{
		AdaptiveLightingConfig config = Valid();
		config.Areas = [new AreaConfig { AreaId = "stue" }, new AreaConfig { AreaId = "kjokken" }];

		LightingEngineHost host = BuildHost();
		Assert.AreEqual(SaveStatus.Saved, host.Save(config).Status, "two distinct rooms save perfectly well");

		string before = File.ReadAllText(_path);

		config.Areas[1].AreaId = "stue";

		SaveResult result = host.Save(config);

		Assert.AreEqual(SaveStatus.Rejected, result.Status);
		Assert.IsFalse(result.Written);
		Assert.IsFalse(result.Validation.IsValid);
		Assert.IsTrue(
			result.Validation.Errors.Any(error => error.Contains("Duplicate area name", StringComparison.Ordinal)),
			$"Expected the validator's duplicate-name message, got: {result.Validation}");
		Assert.AreEqual(before, File.ReadAllText(_path), "a refused save leaves the document exactly as it was");
	}

	[TestMethod]
	public void Validate_DoesNotTouchTheDisk()
	{
		BuildHost().Validate(Valid());

		Assert.IsFalse(File.Exists(_path));
	}

	/// <summary>The pre-2.0 schema, as it sits on disk in a house that has not been upgraded yet.</summary>
	private const string LegacySchema =
		"""
		AdaptiveLighting.Configuration.AdaptiveLightingConfig:
		  ConfigName: "Adaptive lighting [test]"
		  Global:
		    ZonesAutoDiscovered: true
		  Periods:
		    - Name: day
		      Start: "06:00"
		      BrightnessPct: 80
		      ColorTempKelvin: 3500
		  Zones:
		    - Name: Stue
		      AreaId: stue
		""";

	/// <summary>The migration is a write like any other, so the pre-upgrade file lands in the store's one backup slot.</summary>
	[TestMethod]
	public void Reload_OfALegacyDocument_RewritesItOnceAndLeavesThePreviousFileAtTheBackupPath()
	{
		File.WriteAllText(_path, LegacySchema);
		string original = File.ReadAllText(_path);

		LightingEngineHost host = BuildHost();

		SaveResult first = host.Reload();

		Assert.AreEqual(SaveStatus.Saved, first.Status, "the document is runnable; only its key names were old");
		Assert.AreEqual("stue", host.Store.Load().Areas.Single().AreaId, "and it loaded with its rooms");

		string migrated = File.ReadAllText(_path);

		StringAssert.Contains(migrated, "Areas:");
		StringAssert.Contains(migrated, "AreasAutoDiscovered:");
		StringAssert.DoesNotMatch(migrated, new System.Text.RegularExpressions.Regex("Zones"));

		Assert.IsTrue(host.Store.HasBackup);
		Assert.AreEqual(original, File.ReadAllText(host.Store.BackupPath),
			"the backup is the pre-migration file, byte for byte");

		// Once, not on every start: a second rewrite pushes the only pre-migration copy out of the backup slot.
		host.Reload();

		Assert.AreEqual(migrated, File.ReadAllText(_path));
		Assert.AreEqual(original, File.ReadAllText(host.Store.BackupPath));
	}

	/// <summary>A writer that never heard of the normaliser or the validator gets both anyway.</summary>
	[TestMethod]
	public void TheStore_NormalisesAndValidatesWhoeverWrites()
	{
		LightingConfigStore store = new(_path, NullLogger<LightingConfigStore>.Instance);

		AdaptiveLightingConfig broken = Valid();
		broken.Periods = [];

		ConfigNormalizer.Passes = 0;

		ConfigWriteResult refused = store.Save(broken, InvalidDocument.Refuse);

		Assert.IsFalse(refused.Written);
		Assert.IsFalse(refused.Validation.IsValid);
		Assert.IsFalse(File.Exists(_path), "a refused write reaches no byte of the disk");
		Assert.AreEqual(1, ConfigNormalizer.Passes, "and it was normalised on the way to being refused");

		ConfigWriteResult forced = store.Save(broken, InvalidDocument.WriteAnyway);

		Assert.IsTrue(forced.Written);
		Assert.IsFalse(forced.Validation.IsValid, "written, and the errors come back with it to be reported");
		Assert.IsTrue(File.Exists(_path));
		Assert.AreEqual(2, ConfigNormalizer.Passes);
	}

	/// <summary>A warning is no error at any level: the write goes ahead and the dangling row survives it.</summary>
	[TestMethod]
	public void Save_WithADanglingLevelsRow_WarnsAndTheRowSurvivesTheWrite()
	{
		AdaptiveLightingConfig config = Valid();
		config.Periods[0].Id = "day";
		config.Areas[0].Levels = [new RoomLevelOverride { PeriodId = "deleted-period", BrightnessPct = 40 }];

		LightingEngineHost host = BuildHost();

		SaveResult result = host.Save(config);

		Assert.AreEqual(SaveStatus.Saved, result.Status);
		Assert.IsTrue(
			result.Validation.Warnings.Any(warning => warning.Contains("deleted-period", StringComparison.Ordinal)),
			$"Expected the dangling-row warning, got: {result.Validation}");
		Assert.AreEqual(40d, host.Store.Load().Areas.Single().Levels.Single().BrightnessPct,
			"the row is worth more than the tidiness, so it survives the write it did not block");
	}

	/// <summary>The pre-2.0 schema holding a document the engine cannot run: a brightness outside the physical range.</summary>
	private const string LegacySchemaTheEngineCannotRun =
		"""
		AdaptiveLighting.Configuration.AdaptiveLightingConfig:
		  ConfigName: "Adaptive lighting [test]"
		  Periods:
		    - Name: day
		      Start: "06:00"
		      BrightnessPct: 500
		      ColorTempKelvin: 3500
		  Zones:
		    - Name: Stue
		      AreaId: stue
		""";

	/// <summary>The title the forced write reports itself under, its own card and not the invalid-document one.</summary>
	private const string ForcedWriteTitle = "Adaptive lighting: the settings file was written with errors";

	/// <summary>The titles Home Assistant was asked to raise a persistent notification card under.</summary>
	private static List<string> NotificationTitles(FakeHaContext ha) =>
	[
		.. ha.Calls
			.Where(call => call.Domain == "persistent_notification" && call.Service == "create")
			.Select(call => call.Data as Dictionary<string, object>)
			.OfType<Dictionary<string, object>>()
			.Select(data => data.GetValueOrDefault("title") as string)
			.OfType<string>()
	];

	/// <summary>The migrating write goes out over a document that does not validate, because refusing it would strand the house on an unreadable file.</summary>
	[TestMethod]
	public void Reload_OfALegacyDocumentTheEngineCannotRun_MigratesItAnywayAndReportsTheErrors()
	{
		File.WriteAllText(_path, LegacySchemaTheEngineCannotRun);

		FakeHaContext ha = new();
		LightingEngineHost host = BuildHost();
		host.Attach(ha, new FakeHaRegistry(), Clocked());

		SaveResult result = host.Reload();

		Assert.IsFalse(result.Validation.IsValid, "the document is exactly the one the engine cannot run");

		string migrated = File.ReadAllText(_path);

		StringAssert.Contains(migrated, "Areas:", "the write happened");
		StringAssert.DoesNotMatch(migrated, new System.Text.RegularExpressions.Regex("Zones"));

		CollectionAssert.Contains(NotificationTitles(ha), ForcedWriteTitle, "and it said so");

		host.Dispose();
	}

	/// <summary>A migrating write over a runnable document reports nothing: there is nothing to report.</summary>
	[TestMethod]
	public void Reload_OfALegacyDocumentThatRuns_MigratesItWithoutReportingAFault()
	{
		File.WriteAllText(_path, LegacySchema);

		FakeHaContext ha = new();
		LightingEngineHost host = BuildHost();
		host.Attach(ha, new FakeHaRegistry(), Clocked());

		host.Reload();

		CollectionAssert.DoesNotContain(NotificationTitles(ha), ForcedWriteTitle);

		host.Dispose();
	}

	/// <summary>One normalisation pass per write, made by the store and by nothing above it.</summary>
	[TestMethod]
	public void Save_NormalisesTheDocumentOncePerWrite()
	{
		LightingEngineHost host = BuildHost();

		ConfigNormalizer.Passes = 0;
		host.Save(Valid());

		Assert.AreEqual(1, ConfigNormalizer.Passes, "one write, one pass");

		host.Save(Valid());

		Assert.AreEqual(2, ConfigNormalizer.Passes, "and one more for the second write");
	}

	/// <summary>The migrating write is a write, so it is normalised, and once.</summary>
	[TestMethod]
	public void Reload_OfALegacyDocument_NormalisesTheMigratingWriteOnce()
	{
		File.WriteAllText(_path, LegacySchema);

		LightingEngineHost host = BuildHost();

		ConfigNormalizer.Passes = 0;
		host.Reload();

		Assert.AreEqual(1, ConfigNormalizer.Passes);

		// The document is current now, so there is no second migrating write and nothing left to normalise.
		host.Reload();

		Assert.AreEqual(1, ConfigNormalizer.Passes, "a reload that writes nothing normalises nothing");
	}

	/// <summary>A document in the current key names but written before periods had ids.</summary>
	private const string SchemaWithoutIds =
		"""
		AdaptiveLighting.Configuration.AdaptiveLightingConfig:
		  ConfigName: "Adaptive lighting [test]"
		  Periods:
		    - Name: dag
		      Start: "06:00"
		      BrightnessPct: 80
		      ColorTempKelvin: 3500
		  Areas:
		    - Name: Stue
		      AreaId: stue
		      Levels:
		        - PeriodId: dag
		          BrightnessPct: 40
		""";

	[TestMethod]
	public void Reload_OfADocumentWithoutIds_MintsThemOnce_AndKeepsTheRoomsOwnLevel()
	{
		File.WriteAllText(_path, SchemaWithoutIds);
		string original = File.ReadAllText(_path);

		LightingEngineHost host = BuildHost();

		Assert.AreEqual(SaveStatus.Saved, host.Reload().Status);

		string migrated = File.ReadAllText(_path);
		AdaptiveLightingConfig config = host.Store.Load();

		Assert.AreEqual(config.Periods.Single().Id, config.Areas.Single().Levels.Single().PeriodId,
			"the row that named the period by name now names it by id");
		Assert.AreEqual(40d, config.Areas.Single().Levels.Single().BrightnessPct, "and still says what it said");
		Assert.AreEqual(original, File.ReadAllText(host.Store.BackupPath), "the backup is the pre-migration file");

		host.Reload();

		Assert.AreEqual(migrated, File.ReadAllText(_path), "nothing to mint the second time");
		Assert.AreEqual(original, File.ReadAllText(host.Store.BackupPath), "so the one backup slot still holds it");
	}

	/// <summary><see cref="LightingEngineHost.Reload"/> never throws: its caller is the <c>[NetDaemonApp]</c> bootstrap, and an app that throws takes its DI scope and <c>IHaContext</c> with it.</summary>
	[TestMethod]
	public void Reload_OfADocumentWithASectionEmptiedByHand_LoadsItRatherThanThrowing()
	{
		File.WriteAllText(_path,
			$"""
			{LightingConfigDocument.RootKey}:
			  ConfigName: "Adaptive lighting [test]"
			  Global:
			  Defaults:
			  Areas:
			  Periods:
			    - Name: day
			      Start: "06:00"
			      BrightnessPct: 80
			      ColorTempKelvin: 3500
			""");

		LightingEngineHost host = BuildHost();

		SaveResult result = host.Reload();

		Assert.AreEqual(SaveStatus.Saved, result.Status, "an emptied section is a document with nothing in it, not a broken one");
		Assert.AreEqual(0, host.Store.Load().Areas.Count);
		Assert.AreEqual("day", host.Store.Load().Periods.Single().Name, "and what the file does say is still read");
	}

	/// <summary>A stray <c>-</c> is a null inside the list every consumer iterates, which the emptied-section test does not cover.</summary>
	[TestMethod]
	public void Reload_OfADocumentWithABlankListEntry_LoadsItRatherThanThrowing()
	{
		File.WriteAllText(_path,
			$"""
			{LightingConfigDocument.RootKey}:
			  Periods:
			    - Name: day
			      Start: "06:00"
			      BrightnessPct: 80
			      ColorTempKelvin: 3500
			  Areas:
			    -
			    - Name: Stue
			      AreaId: stue
			""");

		LightingEngineHost host = BuildHost();

		SaveResult result = host.Reload();

		Assert.AreEqual(SaveStatus.Saved, result.Status);
		Assert.AreEqual("stue", host.Store.Load().Areas.Single().AreaId, "the room that is really there survives");
	}

	/// <summary>Discovery runs on a timer with no caller left to catch anything, and an unobserved exception on a thread-pool scheduler ends the process.</summary>
	// NetDaemon's registry throws until its first connection completes, so a house on a slow link reaches this path.
	[TestMethod]
	public void AreaDiscovery_WhenTheRegistryIsStillUnreadable_IsAbandonedRatherThanThrownOntoTheScheduler()
	{
		File.WriteAllText(_path,
			$"""
			{LightingConfigDocument.RootKey}:
			  Periods:
			    - Name: day
			      Start: "06:00"
			      BrightnessPct: 80
			      ColorTempKelvin: 3500
			  Areas: []
			""");

		TestScheduler scheduler = new();
		LightingEngineHost host = BuildHost();

		host.Attach(new FakeHaContext(), new UnreadableRegistry(), scheduler);
		host.Reload();

		// Fires the armed discovery; unguarded, the registry's exception comes straight back out here.
		scheduler.AdvanceBy(TimeSpan.FromMinutes(1).Ticks);

		Assert.IsFalse(host.Store.Load().Global.AreasAutoDiscovered,
			"and the once-only flag stays clear, so the rooms are proposed again on the next start");

		host.Dispose();
	}

	/// <summary>An <see cref="IHaRegistry"/> behaving as NetDaemon's does before its first connection completes.</summary>
	private sealed class UnreadableRegistry : IHaRegistry
	{
		public IReadOnlyCollection<EntityRegistration> Entities => throw new InvalidOperationException("not connected");

		public IReadOnlyCollection<Device> Devices => throw new InvalidOperationException("not connected");

		public IReadOnlyCollection<Area> Areas => throw new InvalidOperationException("not connected");

		public IReadOnlyCollection<Floor> Floors => throw new InvalidOperationException("not connected");

		public IReadOnlyCollection<Label> Labels => throw new InvalidOperationException("not connected");

		public EntityRegistration? GetEntityRegistration(string entityId) => throw new InvalidOperationException("not connected");

		public Device? GetDevice(string deviceId) => throw new InvalidOperationException("not connected");

		public Area? GetArea(string areaId) => throw new InvalidOperationException("not connected");

		public Floor? GetFloor(string floorId) => throw new InvalidOperationException("not connected");

		public Label? GetLabel(string labelId) => throw new InvalidOperationException("not connected");
	}

	[TestMethod]
	public void Reload_OfACurrentSchemaDocument_WritesNothing()
	{
		LightingEngineHost host = BuildHost();
		host.Save(Valid());

		string before = File.ReadAllText(_path);
		DateTime writtenAtUtc = File.GetLastWriteTimeUtc(_path);
		bool hadBackup = host.Store.HasBackup;

		host.Reload();

		Assert.AreEqual(before, File.ReadAllText(_path));
		Assert.AreEqual(writtenAtUtc, File.GetLastWriteTimeUtc(_path), "no write means no new timestamp");
		Assert.AreEqual(hadBackup, host.Store.HasBackup, "and nothing new lands in the backup slot");
	}
}
