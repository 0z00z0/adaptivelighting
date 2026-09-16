using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Ha;
using AdaptiveLighting.Hosting;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NetDaemon.HassModel;

namespace AdaptiveLighting.Tests.Web;

/// <summary>The room page's rules, with no renderer: the save timer, a refused write, and a running level test.</summary>
// The model is a plain class precisely so these can be asserted without a component. Its dispatcher runs the work
// inline here, which is what makes the delayed write observable in a test.
[TestClass]
public sealed class RoomPageModelTests
{
	private const string AreaId = "stue";
	private const string PeriodId = "evening-test";

	private string _directory = "";
	private string _path = "";
	private FakeHaContext _ha = new();
	private ServiceProvider? _provider;
	private AreaSnapshotCache? _cache;
	private ActivityLog _activity = new();

	// The cache subscribes before any test publishes, because a report raised before it is listening is a report
	// nobody heard.
	[TestInitialize]
	public void CreateTempDirectory()
	{
		_directory = Path.Combine(Path.GetTempPath(), $"lighting-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_directory);
		_path = Path.Combine(_directory, "AdaptiveLighting.yaml");
		_ha = new FakeHaContext();
		_activity = new ActivityLog();

		_provider = new ServiceCollection()
			.AddSingleton<IHaContext>(_ha)
			.BuildServiceProvider();

		_cache = new AreaSnapshotCache(
			_provider.GetRequiredService<IServiceScopeFactory>(),
			NullLogger<AreaSnapshotCache>.Instance,
			_activity);

		_cache.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
	}

	[TestCleanup]
	public void RemoveTempDirectory()
	{
		if (_cache is { } cache)
			cache.DisposeAsync().AsTask().GetAwaiter().GetResult();

		_provider?.Dispose();

		if (Directory.Exists(_directory))
			Directory.Delete(_directory, recursive: true);
	}

	[TestMethod]
	public async Task AnEditArmsTheSaveAndWritesAfterTheQuietWindow()
	{
		RoomPageModel model = OpenRoom(out LightingEngineHost host);

		await model.SetLuxSensor("sensor.stue_lux");

		Assert.IsTrue(model.Dirty, "the edit has to arm the save");
		Assert.AreEqual(RoomSaveState.Waiting, model.SaveState, "the save line has to say a write is coming");
		Assert.IsNull(
			Room(host.Store.Load()).LuxSensor,
			"nothing may reach the file until the quiet window runs out");

		await WaitUntil(() => !model.Dirty);

		Assert.AreEqual("sensor.stue_lux", Room(host.Store.Load()).LuxSensor, "the write has to land on its own");
		Assert.IsNull(model.Failure, "a write that landed leaves no refusal behind");

		model.Dispose();
	}

	[TestMethod]
	public async Task ARefusedSaveKeepsTheEdit()
	{
		RoomPageModel model = OpenRoom(out LightingEngineHost host);

		// The same room changed somewhere else while the page was open. The page's token no longer matches the
		// file, which is what the write refuses on.
		AdaptiveLightingConfig elsewhere = host.Store.Load();
		Room(elsewhere).VacancyTimeoutSeconds = 777;
		Assert.IsTrue(host.Save(elsewhere).Written, "the competing write has to reach the disk first");

		await model.SetLuxSensor("sensor.stue_lux");
		model.Commit();

		Assert.IsNotNull(model.Failure, "the write has to be refused");
		Assert.AreEqual(SaveStatus.Conflicted, model.Failure!.Status);
		Assert.IsTrue(model.Dirty, "a refused write must not throw the edit away");
		Assert.AreEqual(RoomSaveState.Refused, model.SaveState);
		Assert.AreEqual("sensor.stue_lux", model.LuxSensor, "the edit stays on screen");
		Assert.IsTrue(model.HasRefusedEdit, "leaving the page now has something to warn about");

		model.Dispose();
	}

	[TestMethod]
	public void ARunningTestIsReadFromTheReport()
	{
		DateTimeOffset now = DateTimeOffset.Now;

		Report(TestingUntil(now.AddSeconds(20)));

		RoomPageModel model = OpenRoom(out _);

		Assert.AreEqual(PeriodId, model.TestingPeriod, "the report says a test is running");
		Assert.AreEqual("light.stue_taklys", model.TestingLight);
		Assert.IsTrue(model.TestSecondsLeft > 0, "a running test has time left");

		model.Dispose();
	}

	[TestMethod]
	public void ARefusedPressArmsNothingAndLeavesTheReportToDecide()
	{
		DateTimeOffset now = DateTimeOffset.Now;

		Report(TestingUntil(now.AddSeconds(20)));

		RoomPageModel model = OpenRoom(out _);

		// Nothing is attached, so the engine refuses. The press must not leave the page claiming a test of its own.
		model.TestPeriod("some-other-period");

		Assert.IsTrue(model.TestRefusal is { Length: > 0 }, "the press has to be refused");
		Assert.AreEqual(PeriodId, model.TestingPeriod, "the report still decides which test is running");

		model.Dispose();
	}

	[TestMethod]
	public void AFinishedTestStopsBeingReported()
	{
		DateTimeOffset now = DateTimeOffset.Now;

		Report(TestingUntil(now.AddSeconds(-1)));

		RoomPageModel model = OpenRoom(out _);

		Assert.IsNull(model.TestingPeriod, "a deadline in the past is not a running test");
		Assert.AreEqual(0, model.TestSecondsLeft);

		model.Dispose();
	}

	/// <summary>A document the validator accepts, holding the one room these tests work on.</summary>
	private static AdaptiveLightingConfig OneRoom() => new()
	{
		ConfigName = "Adaptive lighting [test]",
		Periods =
		[
			new TimePeriodConfig { Id = PeriodId, Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 }
		],
		Areas =
		[
			new AreaConfig { AreaId = AreaId, Name = "Living room", Lights = ["light.stue_taklys"] }
		]
	};

	private static AreaConfig Room(AdaptiveLightingConfig document) =>
		document.Areas.Single(area => string.Equals(area.AreaId, AreaId, StringComparison.Ordinal));

	/// <summary>Builds the model over a real store, a real catalog and a real snapshot cache, and opens the room.</summary>
	private RoomPageModel OpenRoom(out LightingEngineHost host)
	{
		host = new LightingEngineHost(
			new LightingConfigStore(_path, NullLogger<LightingConfigStore>.Instance),
			NullLoggerFactory.Instance);

		Assert.IsTrue(host.Save(OneRoom()).Written, "the test document has to reach the disk first");

		HaCatalog catalog = new(_ha, new FakeHaRegistry(), NullLoggerFactory.Instance);

		RoomPageModel model = new(
			host,
			catalog,
			_cache!,
			_activity,
			NullLogger.Instance,
			work =>
			{
				work();

				return Task.CompletedTask;
			});

		model.Start();
		model.Show(AreaId);

		return model;
	}

	/// <summary>Publishes a snapshot and echoes it back, which is the round trip the cache actually reads.</summary>
	private void Report(AreaSnapshot snapshot)
	{
		HaStatePublisher publisher = new(_ha, NullLogger.Instance);

		publisher.Publish(snapshot);

		(string type, object? data) = _ha.SentEvents[^1];
		_ha.RaiseEvent(type, data);
	}

	private static AreaSnapshot TestingUntil(DateTimeOffset ends) => new(
		"Living room",
		AreaState.AutoVacant,
		TransitionReason.LevelTestStarted,
		HouseMode.Home,
		KillSwitchActive: false,
		IsDark: true,
		PeriodName: "evening",
		BrightnessPct: null,
		ColorTempKelvin: null,
		Timestamp: DateTimeOffset.Now,
		LastCommandAt: null,
		LastMotionAt: null,
		NextChangeAt: null,
		NextChangeFrom: null,
		AreaId: AreaId,
		TestingPeriodId: PeriodId,
		TestEndsAt: ends,
		TestingLightId: "light.stue_taklys");

	/// <summary>Waits for the model's own timer, with a ceiling well above the quiet window it is waiting on.</summary>
	private static async Task WaitUntil(Func<bool> settled)
	{
		for (int attempt = 0; attempt < 100; attempt++)
		{
			if (settled())
				return;

			await Task.Delay(100).ConfigureAwait(false);
		}

		Assert.Fail("the model never finished what it was waiting on");
	}
}
