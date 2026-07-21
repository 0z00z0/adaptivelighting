using AdaptiveLighting.Configuration;
using AdaptiveLighting.Hosting;

using Microsoft.Extensions.Logging.Abstractions;

namespace NetDaemon_Test.Lighting;

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

	/// <summary>A document the validator accepts: a circadian table and one zone that names an area.</summary>
	private static AdaptiveLightingConfig Valid() => new()
	{
		ConfigName = "Adaptive lighting [test]",
		Periods = [new TimePeriodConfig { Name = "day", Start = "06:00", BrightnessPct = 80, ColorTempKelvin = 3500 }],
		Zones = [new ZoneConfig { Name = "Stue", AreaId = "stue" }]
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
	public void Save_WithNoZones_IsRefused()
	{
		var config = Valid();
		config.Zones = [];

		var result = BuildHost().Save(config);

		Assert.AreEqual(SaveStatus.Rejected, result.Status);
		Assert.IsFalse(File.Exists(_path));
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
		Assert.AreEqual("stue", reloaded.Zones.Single().AreaId);
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
		Assert.AreEqual(0, host.RunningZoneCount);
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
	///     Zone-level errors cost a zone, not the save. A household whose entity got renamed in Home Assistant
	///     must still be able to fix the rest of the document from the browser.
	/// </summary>
	[TestMethod]
	public void Save_WithAZoneThatCannotResolve_StillSaves()
	{
		var config = Valid();
		config.Zones.Add(new ZoneConfig { Name = "Broken" });

		var result = BuildHost().Save(config);

		Assert.AreEqual(SaveStatus.Saved, result.Status);
		Assert.IsTrue(result.Validation.IsValid, "A zone error is not a document error.");
		Assert.AreEqual("Broken", result.Validation.ZoneErrors.Single().ZoneName);
		Assert.IsTrue(File.Exists(_path));
	}

	[TestMethod]
	public void Validate_DoesNotTouchTheDisk()
	{
		BuildHost().Validate(Valid());

		Assert.IsFalse(File.Exists(_path));
	}
}
