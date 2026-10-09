using System.Text.RegularExpressions;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Hosting;
using AdaptiveLighting.Tests.Common;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The light command log: one line per command, the not-sent outcome included, and nothing while switched off.</summary>
[TestClass]
public sealed class CommandLogTests
{
	private const string Stamp = @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}[+-]\d{2}:\d{2}";

	private string _directory = "";

	[TestInitialize]
	public void CreateTempDirectory()
	{
		_directory = Path.Combine(Path.GetTempPath(), $"command-log-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_directory);
	}

	[TestCleanup]
	public void RemoveTempDirectory()
	{
		if (Directory.Exists(_directory))
			Directory.Delete(_directory, recursive: true);
	}

	[TestMethod]
	public void A_Command_Is_One_Line_And_A_Repeat_The_Light_Already_Shows_Is_Logged_As_Not_Sent()
	{
		AreaFixture t = new AreaTestBuilder()
			.LogsCommandsTo(new CommandLog(_directory, NullLogger.Instance), throughHomeAssistant: true)
			.Build();

		t.Ha.Trigger(AreaTestBuilder.Motion, "on");

		string file = Path.Combine(_directory, "test_area", "light.area.log");
		string[] lines = File.ReadAllLines(file);

		Assert.AreEqual(2, lines.Length, string.Join(Environment.NewLine, lines));
		Assert.AreEqual("# Test", lines[0]);
		StringAssert.Matches(lines[1], new Regex(
			$"^{Stamp} reason=Motion state=\\w+ action=turn_on brightness_pct=70 color_temp_kelvin=2700 transition=\\S+ outcome=sent$"));

		// The light now shows the evening levels, so the same levels again are worked out and never sent.
		t.Ha.SetState(AreaTestBuilder.Light, "on", new Dictionary<string, object> { ["brightness"] = 179, ["color_temp_kelvin"] = 2700 });
		Assert.IsNull(t.Area.TestPeriod("evening"));

		lines = File.ReadAllLines(file);

		Assert.AreEqual(3, lines.Length, string.Join(Environment.NewLine, lines));
		StringAssert.Matches(lines[2], new Regex(
			$"^{Stamp} reason=LevelTestStarted state=\\w+ action=turn_on brightness_pct=70 color_temp_kelvin=2700 .*outcome=not-sent-already-matches$"));
	}

	[TestMethod]
	public void A_Command_Whose_Call_Throws_Is_Logged_As_Failed_And_The_Exception_Still_Reaches_The_Caller()
	{
		ILightActuator actuator = new CommandLog(_directory, NullLogger.Instance).Wrap(
			new ThrowingActuator(), "room", "Room", () => DateTimeOffset.Now,
			() => new CommandContext(TransitionReason.Motion, AreaState.AutoActive));

		Assert.ThrowsException<InvalidOperationException>(() => actuator.Apply("light.lamp", new LightCommand(true, 70, 2700)));

		string[] lines = File.ReadAllLines(Path.Combine(_directory, "room", "light.lamp.log"));

		Assert.AreEqual(2, lines.Length, string.Join(Environment.NewLine, lines));
		StringAssert.Matches(lines[1], new Regex(
			$"^{Stamp} reason=Motion state=AutoActive action=turn_on brightness_pct=70 color_temp_kelvin=2700 outcome=failed exception=InvalidOperationException$"));
	}

	private sealed class ThrowingActuator : ILightActuator
	{
		public ActuatorOutcome Apply(string entityId, LightCommand command) =>
			throw new InvalidOperationException("No connection to Home Assistant");

		public void ActivateScene(string sceneId) => throw new InvalidOperationException("No connection to Home Assistant");
	}

	[TestMethod]
	[DataRow(true)]
	[DataRow(false)]
	public void The_Switch_Decides_Whether_Anything_Is_Written_Beside_The_Document(bool switchedOn)
	{
		FakeHaContext ha = new();
		ha.SetState("light.room_lamp", "off");
		ha.SetState("binary_sensor.room_motion", "off");

		TestScheduler scheduler = new();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		using LightingEngineHost host = new(
			new LightingConfigStore(Path.Combine(_directory, "AdaptiveLighting.yaml"), NullLogger<LightingConfigStore>.Instance),
			NullLoggerFactory.Instance);
		host.Attach(ha, new FakeHaRegistry(), scheduler);

		AdaptiveLightingConfig config = new()
		{
			ConfigName = "Adaptive lighting [test]",
			Global = { CommandLog = switchedOn },
			Periods = [new TimePeriodConfig { Id = "day", Name = "day", Start = "06:00", BrightnessPct = 80, ColorTempKelvin = 3500 }],
			Areas =
			[
				new AreaConfig
				{
					Name = "Room A",
					Lights = ["light.room_lamp"],
					MotionSensors = ["binary_sensor.room_motion"],
					Darkness = DarknessSource.Always
				}
			]
		};

		Assert.AreEqual(SaveStatus.Saved, host.Save(config).Status);

		ha.Trigger("binary_sensor.room_motion", "on");

		Assert.AreEqual(switchedOn, File.Exists(Path.Combine(_directory, "log", "commands", "room_a", "light.room_lamp.log")));

		if (!switchedOn)
			Assert.IsFalse(Directory.Exists(Path.Combine(_directory, "log")), "switched off, not even the folder is made");
	}
}
