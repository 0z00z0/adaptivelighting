using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The card naming rooms that could not be set up: raised once per problem, not once per start.</summary>
[TestClass]
public sealed class AreaSetupNotificationTests
{
	private sealed class TempDirectory : IDisposable
	{
		public TempDirectory()
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "adaptive-lighting-setup-notify-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path);
		}

		public string Path { get; }

		public AreaSetupMemoryStore Memory() =>
			new(System.IO.Path.Combine(Path, "b1.yaml"), NullLogger<AreaSetupMemoryStore>.Instance);

		public void Dispose()
		{
			try
			{
				Directory.Delete(Path, recursive: true);
			}
			catch (IOException)
			{
				// Litter, not a failure.
			}
		}
	}

	// The fake registry lists no areas at all, so any area id in the document fails to resolve.
	private static AdaptiveLightingConfig Config(params AreaConfig[] areas) => new()
	{
		Global = new GlobalConfig(),
		Periods = [new TimePeriodConfig { Name = "day", Start = "07:00" }],
		Areas = [.. areas]
	};

	/// <summary>Starts an engine on <paramref name="config"/> and returns what it notified.</summary>
	private static List<(string Title, string Message)> Start(AdaptiveLightingConfig config, IAreaSetupMemory? memory)
	{
		TestScheduler scheduler = new();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		FakeNotifier notifier = new();
		using LightingOrchestrator orchestrator = new(
			new FakeHaContext(), new FakeHaRegistry(), scheduler, config,
			new FakeLightActuator(), new FakeStatePublisher(), notifier, NullLoggerFactory.Instance,
			lastSeen: null, lastPeriod: null, setupMemory: memory);

		orchestrator.Start();

		return notifier.Notifications;
	}

	[TestMethod]
	public void A_Standing_Problem_Is_Reported_At_The_First_Start_Only()
	{
		using TempDirectory temp = new();
		AdaptiveLightingConfig config = Config(new AreaConfig { Name = "Stua", AreaId = "stue" });

		List<(string Title, string Message)> first = Start(config, temp.Memory());
		Assert.AreEqual(1, first.Count, "a room switched on that cannot be set up is worth saying");
		StringAssert.Contains(first[0].Message, "Stua");

		Assert.AreEqual(0, Start(config, temp.Memory()).Count, "and the restart says nothing, because nothing has changed");
		Assert.AreEqual(0, Start(config, temp.Memory()).Count, "nor the one after it");
	}

	[TestMethod]
	public void Without_A_Memory_The_Card_Comes_Back_At_Every_Start()
	{
		AdaptiveLightingConfig config = Config(new AreaConfig { Name = "Stua", AreaId = "stue" });

		Assert.AreEqual(1, Start(config, memory: null).Count);
		Assert.AreEqual(1, Start(config, memory: null).Count, "no memory reads as nothing remembered, never as silence");
	}

	[TestMethod]
	public void A_Room_That_Starts_Failing_Later_Is_Reported_Even_Though_Another_Already_Was()
	{
		using TempDirectory temp = new();

		Assert.AreEqual(1, Start(Config(new AreaConfig { Name = "Stua", AreaId = "stue" }), temp.Memory()).Count);

		List<(string Title, string Message)> second = Start(
			Config(
				new AreaConfig { Name = "Stua", AreaId = "stue" },
				new AreaConfig { Name = "Gangen", AreaId = "gang" }),
			temp.Memory());

		Assert.AreEqual(1, second.Count, "the new room is a new thing to say");
		StringAssert.Contains(second[0].Message, "Gangen");
		StringAssert.Contains(second[0].Message, "Stua", "and the card that replaces the old one still names every room standing");
	}

	[TestMethod]
	public void A_Room_Switched_Off_Is_Neither_Reported_Nor_Remembered()
	{
		using TempDirectory temp = new();

		Assert.AreEqual(0,
			Start(Config(new AreaConfig { Name = "Garasjen", AreaId = "garasje", Enabled = false }), temp.Memory()).Count,
			"a room the owner switched off is no fault");

		Assert.AreEqual(1,
			Start(Config(new AreaConfig { Name = "Garasjen", AreaId = "garasje" }), temp.Memory()).Count,
			"and switching it back on reports it for the first time");
	}

	// "areas disabled" read as "the rooms you switched off"; the rooms named are enabled ones that failed setup.
	[TestMethod]
	public void The_Title_Says_The_Rooms_Could_Not_Be_Set_Up()
	{
		List<(string Title, string Message)> raised = Start(Config(new AreaConfig { Name = "Stua", AreaId = "stue" }), memory: null);

		Assert.AreEqual("Adaptive lighting: rooms that could not be set up", raised[0].Title);
		StringAssert.Contains(raised[0].Message, "switched on but could not be set up");
	}
}
