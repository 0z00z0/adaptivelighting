using AdaptiveLighting.Configuration;
using AdaptiveLighting.Hosting;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The save path: validate first, write second, and never the other way round.
/// </summary>
/// <remarks>
///     <para>
///         The ordering is the whole safety property. A document with document-level errors is one the engine
///         refuses to run, so writing it would mean the next host start finds a config it cannot use — the UI
///         would have bricked the thing it exists to fix, from the page whose job is to fix it. So the refusal
///         has to happen before any byte reaches the disk, and "the file is untouched" is the assertion that
///         matters, not the return value.
///     </para>
///     <para>
///         No Home Assistant here. <see cref="LightingEngineHost"/> with nothing attached validates without the
///         referential checks and reports that it cannot start an engine, which is exactly the shape of the
///         write path under test.
///     </para>
/// </remarks>
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

		// Removing your last room is a legitimate thing to do, and a fresh install has none to begin with. The
		// save must land so the document reflects what the owner asked for; the validator says so with a warning.
		Assert.AreNotEqual(SaveStatus.Rejected, result.Status, "an area-less document is valid, just idle");
		Assert.IsTrue(File.Exists(_path), "and it reaches the disk");
	}

	/// <summary>
	///     The refusal must carry the validator's own words. The page renders these verbatim, so a save that
	///     said only "invalid" would leave the operator with a form full of fields and no idea which one.
	/// </summary>
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

	/// <summary>An existing, working document must survive a refused save completely untouched.</summary>
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

	/// <summary>
	///     Valid but unattached: the document is good and the engine still cannot start, because no Home
	///     Assistant connection has been handed over. That is a save, not a failure — and it must say so rather
	///     than claiming an engine is running.
	/// </summary>
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

	/// <summary>A save must not leave the temp file it wrote through lying next to the real one.</summary>
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

	/// <summary>
	///     Area-level errors cost an area, not the save. A household whose entity got renamed in Home Assistant
	///     must still be able to fix the rest of the document from the browser.
	/// </summary>
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

	/// <summary>
	///     Pointing one room at an area another room already uses is a document-level refusal, not an area-level one.
	/// </summary>
	/// <remarks>
	///     <para>
	///         This is the room page's "Home Assistant area" picker in one call. A room that states no
	///         <c>Name</c> is identified by its area id, so re-pointing it at a taken area gives two rows the same
	///         <see cref="AreaConfig.DisplayName"/> — which <c>ConfigValidator</c> refuses, and refusal means
	///         nothing is written and the running engine is untouched.
	///     </para>
	///     <para>
	///         Asserted here because the page's own behaviour depends on it and cannot be tested: <c>Room.razor</c>
	///         used to navigate to the new area's URL whether or not the save landed, which reloaded the page from
	///         disk and wiped both the failure and the edit, dropping the reader on whichever room really does own
	///         that area id. The page now only follows a save that reported <see cref="SaveResult.Written"/>. If
	///         this refusal is ever downgraded to a warning, that guard is dead code and this test is where it says so.
	///     </para>
	/// </remarks>
	[TestMethod]
	public void Save_WhenTwoRoomsWouldShareAnAreaId_IsRefusedAndNothingIsWritten()
	{
		AdaptiveLightingConfig config = Valid();
		config.Areas = [new AreaConfig { AreaId = "stue" }, new AreaConfig { AreaId = "kjokken" }];

		LightingEngineHost host = BuildHost();
		Assert.AreEqual(SaveStatus.Saved, host.Save(config).Status, "two distinct rooms save perfectly well");

		string before = File.ReadAllText(_path);

		// The edit the picker makes: the kitchen is told it is the living room.
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

	// ===================== the pre-2.0 schema, migrated on first load =====================

	/// <summary>A document as it sits on disk in a house that has not been upgraded yet.</summary>
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

	/// <summary>
	///     The migration is a write, and every write in this host goes through the store — which is the whole
	///     point: the store already keeps one previous version, so the document as it was before the upgrade
	///     survives at the path the Configuration page already shows, with no second backup mechanism invented
	///     for the occasion.
	/// </summary>
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

		// Once, not on every start. A second rewrite would push the only pre-migration copy out of the backup
		// slot and replace it with a copy of the migrated file — the safety net quietly emptying itself.
		host.Reload();

		Assert.AreEqual(migrated, File.ReadAllText(_path));
		Assert.AreEqual(original, File.ReadAllText(host.Store.BackupPath));
	}

	// ===================== a half-finished hand-edit must not kill the host =====================

	/// <summary>
	///     <see cref="LightingEngineHost.Reload"/> says it never throws, and that is not a nicety: the caller is
	///     the per-host <c>[NetDaemonApp]</c> bootstrap, and an app that throws goes to
	///     <c>ApplicationState.Error</c>, taking its DI scope and its <c>IHaContext</c> with it. The browser can
	///     then save a corrected file and still not start the engine — the one thing this host exists to make
	///     possible. A line reading <c>Areas:</c> with nothing under it used to be enough to do that.
	/// </summary>
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

	/// <summary>
	///     The same guarantee for a stray <c>-</c>, which is what a half-deleted room leaves behind. Kept separate
	///     from the emptied-section test because it is a null <i>inside</i> a list the model believes is full of
	///     rooms, which is the shape every consumer iterates.
	/// </summary>
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

	/// <summary>
	///     Area discovery runs on a timer half an hour into the future as far as the caller is concerned: nobody is
	///     left to catch anything it throws, and on a thread-pool scheduler an unobserved exception ends the
	///     process — the whole Home Assistant host, not just the lighting engine.
	/// </summary>
	/// <remarks>
	///     The trigger is ordinary. The settle delay is a guess at how long Home Assistant needs before its
	///     registry is readable; a house on a slow link can still be filling it when the timer fires, and
	///     NetDaemon's registry throws until its first connection completes. Discovery finding nothing then is a
	///     thing to log and retry on the next start — never a reason to take the house down.
	/// </remarks>
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

		// Fires the armed discovery. Before this was guarded, the registry's exception came straight back out.
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

	/// <summary>A start that had nothing to migrate must be a start that wrote nothing.</summary>
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
