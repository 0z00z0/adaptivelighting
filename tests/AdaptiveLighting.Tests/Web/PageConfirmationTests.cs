using AdaptiveLighting.Configuration;
using AdaptiveLighting.Hosting;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NetDaemon.HassModel;

namespace AdaptiveLighting.Tests.Web;

/// <summary>
///     What the three pages say for a moment, and how long they say it: 3.5 seconds on the room and house
///     pages, 6 on the dashboard.
/// </summary>
/// <remarks>
///     No renderer anywhere here. Each message carries its own expiry, so the clock it is read against is the
///     only thing that clears it, and a test can ask when that is without waiting for it.
/// </remarks>
[TestClass]
public sealed class PageConfirmationTests
{
	private const string AreaId = "stue";

	private static readonly TimeSpan RoomAndHouse = TimeSpan.FromSeconds(3.5);

	private static readonly TimeSpan Dashboard = TimeSpan.FromSeconds(6);

	private static readonly DateTimeOffset Moment = new(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(1));

	private string _directory = "";
	private string _path = "";
	private FakeHaContext _ha = new();
	private ServiceProvider? _provider;
	private AreaSnapshotCache? _cache;
	private readonly ActivityLog _activity = new();
	private readonly List<LightingEngineHost> _hosts = [];

	[TestInitialize]
	public void CreateTempDirectory()
	{
		_directory = Path.Combine(Path.GetTempPath(), $"lighting-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_directory);
		_path = Path.Combine(_directory, "AdaptiveLighting.yaml");
		_ha = new FakeHaContext();

		_provider = new ServiceCollection()
			.AddSingleton<IHaContext>(_ha)
			.BuildServiceProvider();

		_cache = new AreaSnapshotCache(
			_provider.GetRequiredService<IServiceScopeFactory>(),
			NullLogger<AreaSnapshotCache>.Instance,
			_activity);
	}

	[TestCleanup]
	public void RemoveTempDirectory()
	{
		if (_cache is { } cache)
			cache.DisposeAsync().AsTask().GetAwaiter().GetResult();

		foreach (LightingEngineHost host in _hosts)
			host.Dispose();

		_provider?.Dispose();

		if (Directory.Exists(_directory))
			Directory.Delete(_directory, recursive: true);
	}

	// ---- the message itself ----------------------------------------------------------------------------

	[TestMethod]
	public void AMessageIsStillMadeUpToItsExpiryAndNotAtIt()
	{
		TransientMessage message = TransientMessage.For("Saved", Moment, RoomAndHouse);

		Assert.IsTrue(message.IsShownAt(Moment), "it has only just been said");
		Assert.IsTrue(
			message.IsShownAt(Moment + RoomAndHouse - TimeSpan.FromMilliseconds(1)),
			"it stands for the whole of its few seconds");
		Assert.IsFalse(message.IsShownAt(Moment + RoomAndHouse), "and goes the moment they are up");
		Assert.AreEqual(string.Empty, message.TextAt(Moment + RoomAndHouse));
	}

	[TestMethod]
	public void AStandingMessageNeverExpiresAndAnEmptyOneSaysNothing()
	{
		TransientMessage standing = TransientMessage.Standing("not saved");

		Assert.IsTrue(standing.IsShownAt(Moment.AddYears(1)), "a refusal stands until it is resolved");
		Assert.IsFalse(TransientMessage.None.IsShownAt(Moment));
		Assert.AreEqual(string.Empty, TransientMessage.None.TextAt(Moment));
	}

	// ---- the room page ---------------------------------------------------------------------------------

	[TestMethod]
	public async Task TheRoomPageConfirmsAWriteForThreeAndAHalfSeconds()
	{
		RoomPageModel model = OpenRoom(out _);

		DateTimeOffset before = DateTimeOffset.Now;

		await model.SetOwnName("Sitting room");
		model.Commit();

		TransientMessage confirmation = model.SaveConfirmation;
		DateTimeOffset after = DateTimeOffset.Now;

		Assert.AreEqual(RoomSaveState.Saved, model.SaveState, "the write has to be confirmed on screen");
		Assert.IsNotNull(confirmation.Until, "a confirmation that never ends is not transient");
		Assert.IsTrue(
			confirmation.Until!.Value >= before + RoomAndHouse && confirmation.Until.Value <= after + RoomAndHouse,
			"the confirmation runs out three and a half seconds after the write");
		Assert.IsFalse(
			confirmation.IsShownAt(confirmation.Until.Value),
			"and clears itself against the page's clock, with no timer behind it");

		model.Dispose();
	}

	// ---- the house page --------------------------------------------------------------------------------

	[TestMethod]
	public void TheHousePageConfirmsASaveForThreeAndAHalfSecondsAndKeepsTheBarUpMeanwhile()
	{
		HousePageModel model = House();
		model.Start(null);

		model.HouseName = "Somewhere";

		DateTimeOffset before = DateTimeOffset.Now;
		model.Save();
		DateTimeOffset after = DateTimeOffset.Now;

		TransientMessage confirmation = model.SaveConfirmation;

		Assert.IsNotNull(confirmation.Until, "a confirmation that never ends is not transient");
		Assert.IsTrue(
			confirmation.Until!.Value >= before + RoomAndHouse && confirmation.Until.Value <= after + RoomAndHouse,
			"the house page confirms for the same three and a half seconds as the room page");
		Assert.IsTrue(model.ShowSaveBar, "the bar stays up while it is still saying something");
		Assert.IsFalse(confirmation.IsShownAt(confirmation.Until.Value));

		model.Dispose();
	}

	// ---- the dashboard ---------------------------------------------------------------------------------

	[TestMethod]
	public void TheDashboardAcknowledgesAModePressForSixSeconds()
	{
		DashboardPageModel model = Board();
		model.Start();

		DateTimeOffset before = DateTimeOffset.Now;
		model.SelectMode("Sover");
		DateTimeOffset after = DateTimeOffset.Now;

		TransientMessage acknowledgement = model.ModeConfirmation;

		Assert.AreNotEqual(string.Empty, acknowledgement.Text, "the press has to be acknowledged");
		Assert.IsNotNull(acknowledgement.Until);
		Assert.IsTrue(
			acknowledgement.Until!.Value >= before + Dashboard && acknowledgement.Until.Value <= after + Dashboard,
			"the dashboard holds its acknowledgement for six seconds, not the pages' three and a half");
		Assert.IsFalse(acknowledgement.IsShownAt(acknowledgement.Until.Value));

		model.Dispose();
	}

	// ---- building the three models ---------------------------------------------------------------------

	private static AdaptiveLightingConfig OneRoom() => new()
	{
		ConfigName = "Adaptive lighting [test]",
		Periods =
		[
			new TimePeriodConfig { Id = "evening-test", Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 }
		],
		Areas = [new AreaConfig { AreaId = AreaId, Name = "Living room", Lights = ["light.stue_taklys"] }]
	};

	private LightingEngineHost Host(AdaptiveLightingConfig document)
	{
		LightingEngineHost host = new(
			new LightingConfigStore(_path, NullLogger<LightingConfigStore>.Instance),
			NullLoggerFactory.Instance);

		_hosts.Add(host);
		Assert.IsTrue(host.Save(document).Written, "the test document has to reach the disk first");

		return host;
	}

	private HaCatalog Catalog() => new(_ha, new FakeHaRegistry(), NullLoggerFactory.Instance);

	private RoomPageModel OpenRoom(out LightingEngineHost host)
	{
		host = Host(OneRoom());

		RoomPageModel model = new(host, Catalog(), _cache!, _activity, NullLogger.Instance, Inline);

		model.Start();
		model.Show(AreaId);

		return model;
	}

	private HousePageModel House()
	{
		AdaptiveLightingConfig document = OneRoom();
		LightingEngineHost host = Host(document);
		HaCatalog catalog = Catalog();
		ModeService modes = new(_ha, new FakeAppConfig(document), catalog, host, NullLogger<ModeService>.Instance);

		return new HousePageModel(
			host,
			catalog,
			new ConfigLocation(_path, ConfigLocationSource.External, null),
			new HomeLocation(_ha),
			_cache!,
			modes,
			NullLogger<HousePageModel>.Instance);
	}

	private DashboardPageModel Board()
	{
		AdaptiveLightingConfig document = OneRoom();
		document.Global.HouseMode = new HouseModeConfig { Entity = "input_select.husmodus" };

		LightingEngineHost host = Host(document);

		// The house-mode selector has to be a live input_select with the pressed option among its choices, or the
		// press is refused and there is nothing to acknowledge.
		_ha.SetState("input_select.husmodus", "Normal", new() { ["options"] = new[] { "Normal", "Sover" } });

		HaCatalog catalog = Catalog();
		DocumentCache documents = new(host, NullLogger<DocumentCache>.Instance);
		ModeService service = new(
			_ha, new FakeAppConfig(document), catalog, host, documents, NullLogger<ModeService>.Instance);

		return new DashboardPageModel(
			_cache!, _activity, host, documents, service, catalog, NullLogger.Instance, Inline);
	}

	// The models dispatch onto the render thread; a test runs the work where it stands.
	private static Task Inline(Action work)
	{
		work();

		return Task.CompletedTask;
	}
}
