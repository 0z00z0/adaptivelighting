using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Hosting;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The document's log level reaches the host's control on load and on every save, and a wrong word never stops a house.</summary>
[TestClass]
public sealed class LogLevelSettingTests
{
	private string _directory = "";
	private string _path = "";

	[TestInitialize]
	public void CreateTempDirectory()
	{
		_directory = Path.Combine(Path.GetTempPath(), $"log-level-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_directory);
		_path = Path.Combine(_directory, "AdaptiveLighting.yaml");
	}

	[TestCleanup]
	public void RemoveTempDirectory()
	{
		if (Directory.Exists(_directory))
			Directory.Delete(_directory, recursive: true);
	}

	private static AdaptiveLightingConfig Valid() => new()
	{
		ConfigName = "Adaptive lighting [test]",
		Periods = [new TimePeriodConfig { Id = "day", Name = "day", Start = "06:00", BrightnessPct = 80, ColorTempKelvin = 3500 }],
		Areas = [new AreaConfig { Name = "Room A", Lights = ["light.room_lamp"] }]
	};

	private sealed class RecordingLevelControl : ILogLevelControl
	{
		public List<string> Calls { get; } = [];

		public bool IsSupported => true;

		public void Apply(LogLevel level) => Calls.Add(level.ToString());

		public void RestoreInitial() => Calls.Add("initial");
	}

	[TestMethod]
	public void The_Level_Follows_The_Document_On_Load_And_On_Every_Save()
	{
		AdaptiveLightingConfig config = Valid();
		config.Global.LogLevel = "debug";
		File.WriteAllText(_path, LightingConfigDocument.Serialize(config));

		RecordingLevelControl control = new();
		using LightingEngineHost host = new(
			new LightingConfigStore(_path, NullLogger<LightingConfigStore>.Instance), NullLoggerFactory.Instance, logLevel: control);

		host.Reload();
		CollectionAssert.AreEqual(new[] { "Debug" }, control.Calls, "applied on the first load");

		config.Global.LogLevel = "Warning";
		host.Save(config);
		CollectionAssert.AreEqual(new[] { "Debug", "Warning" }, control.Calls, "and on a save");

		config.Global.LogLevel = null;
		host.Save(config);
		CollectionAssert.AreEqual(new[] { "Debug", "Warning", "initial" }, control.Calls, "unset puts the host's own level back");
	}

	[TestMethod]
	public void An_Unknown_Level_Is_A_Warning_And_Both_Settings_Round_Trip()
	{
		AdaptiveLightingConfig config = Valid();
		config.Global.LogLevel = "Loud";
		config.Global.CommandLog = true;

		AdaptiveLightingConfig loaded = LightingConfigDocument.Deserialize(LightingConfigDocument.Serialize(config)).Config;

		Assert.AreEqual("Loud", loaded.Global.LogLevel);
		Assert.IsTrue(loaded.Global.CommandLog);

		ValidationResult result = ConfigValidator.Validate(loaded, new ValidationContext());

		Assert.IsTrue(result.IsValid, result.ToString());
		Assert.IsTrue(result.Warnings.Any(warning => warning.Contains("Global.LogLevel", StringComparison.Ordinal)), result.ToString());

		using LightingEngineHost host = new(new LightingConfigStore(_path, NullLogger<LightingConfigStore>.Instance), NullLoggerFactory.Instance);
		Assert.AreEqual(SaveStatus.Saved, host.Save(loaded).Status, "never a refused save");
	}
}
