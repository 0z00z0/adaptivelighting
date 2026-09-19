using System.Globalization;
using System.Reactive.Concurrency;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Ha;
using AdaptiveLighting.Hosting;
using AdaptiveLighting.Lamplight;
using AdaptiveLighting.TestFakes;
using AdaptiveLighting.Web;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.Logging.Abstractions;

using NetDaemon.AppModel;
using NetDaemon.HassModel;

// A host for the Razor Class Library, so a UI change can be looked at before it reaches a house. The RCL ships
// App.razor and Routes.razor over its own assembly, so there is nothing to add but services and a fake house.
//
// Run:  dotnet run --project tools/uihost                 ->  http://localhost:5199
//       dotnet run --project tools/uihost -- --port 5200  ->  http://localhost:5200
//       dotnet run --project tools/uihost -- --port 0     ->  any free port, printed on startup
//       dotnet run --project tools/uihost -- --lamplight-port 5198  ->  Lamplight as well, on http://localhost:5198
//       dotnet run --project tools/uihost -- --house lit            ->  the seeded rooms enabled and lit

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// --port so two worktrees can preview at once. A port that is taken fails on bind rather than moving to a free
// one quietly: a host that lands somewhere unannounced points the next verification run at another worktree's
// UI. --port 0 asks for that behaviour deliberately, and the bound port is printed either way.
if (!TryResolvePort(builder.Configuration["port"], out int port))
{
	Console.Error.WriteLine("uihost: --port takes a number from 0 to 65535.");

	return 1;
}

// Explicit, not left to the environment: static web assets are wired up automatically only in Development, and
// without them the RCL's _content/** and _framework/blazor.web.js both 404. The page server-renders and then
// sits there with no circuit, which looks like a broken UI, not a broken host.
builder.WebHost.UseStaticWebAssets();

// local.yaml is gitignored: point it at a copy of a real document to reproduce a house, or leave it absent and
// the engine writes a starting one. A real document must never be committed; this repository is public.
string document = builder.Configuration["ConfigPath"]
	?? Path.Combine(builder.Environment.ContentRootPath, "local.yaml");

builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
	["AdaptiveLighting:ConfigPath"] = document,
	[LamplightSite.PortKey] = builder.Configuration["lamplight-port"]
});

int? lamplightPort = LamplightSite.Port(builder.Configuration);

// The commissioning board's own state — rooms discovered but nothing switched on — is the default a house
// starts from. --house lit is the other state a design needs to look at: every seeded room enabled, some lit
// at a brightness and warmth, at least one left dark.
bool litHouse = string.Equals(builder.Configuration["house"], "lit", StringComparison.OrdinalIgnoreCase);

if (lamplightPort is not null)
	builder.Services.AddLamplight();

// The fake house. Populated below so the two helper screens have live options to reconcile against, including
// one orphan on each, the state the "move it to…" control exists for.
FakeHaContext ha = new();
FakeHaRegistry registry = new();

Seed(ha);

builder.Services.AddSingleton<IHaContext>(ha);
builder.Services.AddSingleton<IHaRegistry>(registry);
builder.Services.AddSingleton<IAppConfig<AdaptiveLightingConfig>>(new SeedConfig());

builder.Services.AddLightingWeb();

// This host's own preview pages join the RCL's. A house registers nothing here and routes exactly what it did.
builder.Services.AddSingleton(new AdditionalRoutes([typeof(Program).Assembly]));

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

WebApplication app = builder.Build();

app.UseAntiforgery();

// This alone serves the RCL's _content/** and _framework/blazor.web.js. No UseStaticFiles: measured without
// it, both come back 200 with real bodies and the circuit opens.
app.MapStaticAssets();
// AddAdditionalAssemblies as well as the AdditionalRoutes service: the first registers this host's preview
// pages as endpoints, the second is what the Router resolves once a circuit is navigating on its own.
RazorComponentsEndpointConventionBuilder firstDesign = app.MapRazorComponents<App>()
	.AddAdditionalAssemblies(typeof(Program).Assembly)
	.AddInteractiveServerRenderMode();

// The same split a house gets from AdaptiveLighting:LamplightPort, so both sites share this one engine.
if (lamplightPort is { } lamplight)
	app.MapLamplight(lamplight, firstDesign);

// Hands the engine the fake house and a handful of not-yet-committed rooms, so the commissioning board and
// every room's light switch answer from a running engine instead of "nothing is running" — and files a dozen
// activity reports across the record's own categories, so the Activity page is drivable as shipped.
app.Lifetime.ApplicationStarted.Register(() => AttachEngineAndSeedActivity(ha, registry, app.Services, litHouse));

// Read after start, not from the port above: with --port 0 the requested port is 0 and only the server knows
// which one it got.
app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine($"uihost listening on {string.Join(", ", app.Urls)}"));

// 127.0.0.1 only for port 0: Kestrel refuses a dynamic port on the "localhost" name, which resolves to both
// loopback families. A fixed port keeps the name, so the default still answers on ::1 as it always has.
string address = port == 0
	? "http://127.0.0.1:0"
	: $"http://localhost:{port.ToString(CultureInfo.InvariantCulture)}";

app.Urls.Add(address);

if (lamplightPort is { } lamplightListen)
	app.Urls.Add($"http://localhost:{lamplightListen.ToString(CultureInfo.InvariantCulture)}");

try
{
	app.Run();
}
catch (IOException error)
{
	Console.Error.WriteLine($"uihost: could not bind port {port.ToString(CultureInfo.InvariantCulture)} — {error.Message}");
	Console.Error.WriteLine("uihost: pass --port <number> for another one, or --port 0 for any free port.");

	return 1;
}

return 0;

static bool TryResolvePort(string? requested, out int port)
{
	if (requested is not { Length: > 0 })
	{
		port = 5199;

		return true;
	}

	// Invariant, not the current culture: this is a machine-readable argument, and nb-NO digit grouping would
	// otherwise accept "5 199".
	return int.TryParse(requested, NumberStyles.None, CultureInfo.InvariantCulture, out port)
		&& port <= 65535;
}

static void Seed(FakeHaContext ha)
{
	// The house-mode helper offers Ferie, which nothing is mapped to, and no longer offers Guests, which is
	// mapped. That is exactly one orphan and one free value: the remap control's whole precondition.
	ha.SetState("input_select.house_state", "Home", new()
	{
		["options"] = new List<string> { "Home", "Away", "Sleeping", "Ferie" },
		["friendly_name"] = "Husmodus"
	});

	// Same shape on the schedule's helper: Natt is mapped and no longer offered, Natt sen is offered and free.
	ha.SetState("input_select.time_of_day", "Dag", new()
	{
		["options"] = new List<string> { "Tidlig morgen", "Morgen", "Dag", "Ettermiddag", "Kveld", "Natt sen" },
		["friendly_name"] = "Tid på døgnet"
	});

	ha.SetState("zone.home", "0", new() { ["latitude"] = 59.9, ["longitude"] = 10.75 });
	ha.SetState("sun.sun", "above_horizon", new() { ["elevation"] = 12.0 });

	SeedLights(ha);
}

// Lights for a local.yaml to name. FakeHaRegistry answers nothing, because HassModel's Area and
// EntityRegistration have no public constructor, so discovery finds no room here and a document has to list its
// lights explicitly. Group membership does work: the resolver reads it off the entity_id attribute, which is
// what puts a real group fold on the room page.
static void SeedLights(FakeHaContext ha)
{
	string[] ceiling = ["light.stue_tak_1", "light.stue_tak_2", "light.stue_tak_3"];

	Lamp(ha, "light.stue_taklys", "Stue taklys", ceiling);

	foreach (string bulb in ceiling)
		Lamp(ha, bulb, $"Taklys {bulb[^1]}");

	Lamp(ha, "light.stue_leselampe", "Leselampe");
	Lamp(ha, "light.stue_gulvlampe", "Gulvlampe");

	// Named by a document and commanded by no room: the orphan the light list exists to surface.
	Lamp(ha, "light.stue_gammel_lampe", "Gammel lampe");

	Lamp(ha, "light.bad_tak", "Bad tak");

	// One ceiling bulb off the network, so a room naming the ceiling group shows the not-responding warning.
	ha.SetState("light.stue_tak_2", "unavailable", new() { ["friendly_name"] = "Taklys 2" });
	ha.SetState("binary_sensor.stue_bevegelse", "off", new() { ["device_class"] = "motion", ["friendly_name"] = "Stue bevegelse" });
	ha.SetState("sensor.stue_lux", "18", new() { ["device_class"] = "illuminance", ["friendly_name"] = "Stue lysnivå" });

	SeedMoreRooms(ha);
}

// The rest of the invented house: enough rooms with explicit lights for the commissioning board's findings strip
// to count more than one room, and for the activity seed below to have somewhere to report from.
static void SeedMoreRooms(FakeHaContext ha)
{
	Lamp(ha, "light.kjokken_tak", "Kjøkken tak");
	Lamp(ha, "light.kjokken_benk", "Kjøkken benk");
	ha.SetState("binary_sensor.kjokken_bevegelse", "off", new() { ["device_class"] = "motion", ["friendly_name"] = "Kjøkken bevegelse" });

	Lamp(ha, "light.kontor_skrivebord", "Kontor skrivebord");
	Lamp(ha, "light.kontor_tak", "Kontor tak");
	ha.SetState("binary_sensor.kontor_bevegelse", "off", new() { ["device_class"] = "motion", ["friendly_name"] = "Kontor bevegelse" });
	ha.SetState("sensor.kontor_lux", "6", new() { ["device_class"] = "illuminance", ["friendly_name"] = "Kontor lysnivå" });

	Lamp(ha, "light.soverom_tak", "Soverom tak");
	Lamp(ha, "light.soverom_nattbord", "Soverom nattbord");

	Lamp(ha, "light.gjesterom_tak", "Gjesterom tak");
	ha.SetState("scene.gjester_kos", "scening", new() { ["friendly_name"] = "Gjester kos" });

	// A room whose only light is a plain on/off switch: Home Assistant reports "onoff" and nothing else, no
	// brightness attribute at all. Exercises the room page's capability hiding, which every other seeded light
	// (colour temperature, always dimmable) never reaches.
	SwitchOnlyLamp(ha, "light.bod_bryter", "Bod bryterlys");
}

static void Lamp(FakeHaContext ha, string entityId, string name, IReadOnlyList<string>? members = null)
{
	Dictionary<string, object> attributes = new(StringComparer.Ordinal)
	{
		["friendly_name"] = name,
		["supported_color_modes"] = new List<string> { "color_temp" },
		["brightness"] = 178.0
	};

	if (members is { Count: > 0 })
		attributes["entity_id"] = members.ToList();

	ha.SetState(entityId, "on", attributes);
}

// A plain switch behind a "light" entity: HA reports its one mode and no brightness attribute, since it has
// none to report.
static void SwitchOnlyLamp(FakeHaContext ha, string entityId, string name) =>
	ha.SetState(entityId, "on", new Dictionary<string, object>(StringComparer.Ordinal)
	{
		["friendly_name"] = name,
		["supported_color_modes"] = new List<string> { "onoff" }
	});

// Attaches the engine exactly as a house's own [NetDaemonApp] does (see samples/MinimalHost), then saves a
// document holding a handful of discovered-but-not-yet-committed rooms. Saving (not Reload) is deliberate: it
// writes the document, re-reads it and rebuilds the orchestrator in one step, without arming the discovery
// scan, which this fake registry cannot answer.
static void AttachEngineAndSeedActivity(FakeHaContext ha, FakeHaRegistry registry, IServiceProvider services, bool litHouse)
{
	LightingEngineHost engine = services.GetRequiredService<LightingEngineHost>();

	// DefaultScheduler stands in for the scheduler a house gets from NetDaemon's own bootstrap, which this host
	// never starts. Without an attached engine, the commissioning board never renders — the dashboard's
	// AwaitingRoomChoice needs IsAttached — and every room's light switch answers "nothing is running" instead
	// of its own real reason.
	engine.Attach(ha, registry, DefaultScheduler.Instance);

	AdaptiveLightingConfig config = AdaptiveLightingConfig.CreateDefault();
	config.Areas.AddRange(CommissioningRooms(litHouse));

	SaveResult result = engine.Save(config);

	if (!result.Written)
		Console.Error.WriteLine($"uihost: seed configuration was not accepted: {result.Message}");

	SeedActivity(ha);

	if (litHouse)
		SeedLitRooms(ha);
}

// A first-run house: rooms discovery would have found, none committed yet. Every light is named explicitly,
// never by Home Assistant area, because FakeHaRegistry answers no area membership. --house lit enables every
// one instead, so there is a grid of switched-on rooms for a dashboard to draw rather than the commissioning
// board's empty first run.
static List<AreaConfig> CommissioningRooms(bool enabled) =>
[
	new()
	{
		Name = "Stue",
		Lights = ["light.stue_taklys", "light.stue_leselampe", "light.stue_gulvlampe"],
		MotionSensors = ["binary_sensor.stue_bevegelse"],
		LuxSensor = "sensor.stue_lux",
		Enabled = enabled
	},
	new() { Name = "Bad", Lights = ["light.bad_tak"], Enabled = enabled },
	new()
	{
		Name = "Kjøkken",
		Lights = ["light.kjokken_tak", "light.kjokken_benk"],
		MotionSensors = ["binary_sensor.kjokken_bevegelse"],
		Enabled = enabled
	},
	new()
	{
		Name = "Kontor",
		Lights = ["light.kontor_skrivebord", "light.kontor_tak"],
		MotionSensors = ["binary_sensor.kontor_bevegelse"],
		LuxSensor = "sensor.kontor_lux",
		Enabled = enabled
	},
	new() { Name = "Soverom", Lights = ["light.soverom_tak", "light.soverom_nattbord"], Enabled = enabled },
	new() { Name = "Gjesterom", Lights = ["light.gjesterom_tak"], Enabled = enabled },
	new() { Name = "Bod", Lights = ["light.bod_bryter"], Enabled = enabled }
];

// A dozen reports spread across every chip the Activity page draws, so the page is drivable without hand-editing.
// Reason and state are picked off ActivityView.Categorise, not guessed: each comment names the chip the report
// lands in. The twelfth is free — Save above raises the engine's own "settings saved" notice, which is Background.
static void SeedActivity(FakeHaContext ha)
{
	HaStatePublisher publisher = new(ha, NullLogger.Instance);
	DateTimeOffset now = DateTimeOffset.Now;
	int minute = 22;

	AreaSnapshot Base(string area, AreaState state, TransitionReason reason, bool isDark = true, string period = "Kveld") => new(
		area, state, reason, ModeKind.Normal,
		KillSwitchActive: false,
		IsDark: isDark,
		PeriodName: period,
		BrightnessPct: null,
		ColorTempKelvin: null,
		Timestamp: now.AddMinutes(-minute),
		LastCommandAt: now.AddMinutes(-minute),
		LastMotionAt: now.AddMinutes(-minute),
		NextChangeAt: null,
		NextChangeFrom: null);

	void Report(AreaSnapshot snapshot)
	{
		publisher.Publish(snapshot);

		// Publish only records the event for inspection; in a house, Home Assistant echoes the event back and
		// that echo is what the dashboard's cache actually reads. Re-raising the same payload stands in for it.
		(string type, object? data) = ha.SentEvents[^1];
		ha.RaiseEvent(type, data);

		minute -= 2;
	}

	// Movement: a motion sensor reported.
	Report(Base("Kjøkken", AreaState.AutoActive, TransitionReason.Motion, period: "Morgen") with { BrightnessPct = 70, ColorTempKelvin = 3000 });
	Report(Base("Soverom", AreaState.AutoActive, TransitionReason.Motion, period: "Natt") with { BrightnessPct = 15, ColorTempKelvin = 2200 });

	// Light change: the engine commanded the lights on its own.
	Report(Base("Stue", AreaState.PreOff, TransitionReason.VacancyTimeout) with { BrightnessPct = 20, ColorTempKelvin = 2200 });
	Report(Base("Kontor", AreaState.AutoActive, TransitionReason.CircadianTick) with
	{
		BrightnessPct = 55,
		ColorTempKelvin = 3500,
		LightsMoved = ["light.kontor_tak"],
		LightLevels = [new LightStanding("light.kontor_tak", 55, 3500)]
	});

	// Manual changes: somebody set or switched the lights by hand.
	Report(Base("Gjesterom", AreaState.OverriddenOn, TransitionReason.ManualOn) with { BrightnessPct = 80, ChangedBy = "By hand", ChangedAt = now.AddMinutes(-minute) });
	Report(Base("Bad", AreaState.SuppressedOff, TransitionReason.ManualOff) with { ChangedBy = "By hand", ChangedAt = now.AddMinutes(-minute) });

	// Nothing happened: the engine could have lit the room and did not.
	Report(Base("Soverom", AreaState.AutoVacant, TransitionReason.CircadianTick, isDark: false) with { DarknessDetail = "lux 420, dark below 40" });
	Report(Base("Kontor", AreaState.AutoVacant, TransitionReason.CircadianTick) with { AutoOnBlockedBy = AutoOnBlock.Sleep });

	// Darkness: the standing verdict a quiet re-check republishes.
	Report(Base("Gjesterom", AreaState.AutoVacant, TransitionReason.CircadianTick) with { DarknessDetail = "lux 8, dark below 40" });

	// Mode: the house moved to a different mode.
	Report(Base("Stue", AreaState.AutoVacant, TransitionReason.HouseModeChanged) with { HouseModeValue = "Home" });

	// House: a guest scene has this room.
	Report(Base("Gjesterom", AreaState.SceneHold, TransitionReason.SceneHold) with { SceneApplied = "scene.gjester_kos" });
}

// --house lit: a current report per enabled room, published after the activity history above so each room's
// cached state is this one and not whichever history entry happened to run last. Warm to cool across the
// Kelvin ramp, brightness varied, one room hand-held, two left dark — so a tile grid, a timeline and a room
// header all have brightness and warmth to draw, and at least one off room to draw dark.
static void SeedLitRooms(FakeHaContext ha)
{
	HaStatePublisher publisher = new(ha, NullLogger.Instance);
	DateTimeOffset now = DateTimeOffset.Now;

	void Report(AreaSnapshot snapshot)
	{
		publisher.Publish(snapshot);

		// See the identical comment in SeedActivity: Publish only records the event, the echo is what the
		// dashboard's cache actually reads.
		(string type, object? data) = ha.SentEvents[^1];
		ha.RaiseEvent(type, data);
	}

	AreaSnapshot Lit(string area, AreaState state, double brightnessPct, int kelvin) => new(
		area, state, TransitionReason.CircadianTick, ModeKind.Normal,
		KillSwitchActive: false,
		IsDark: true,
		PeriodName: "Kveld",
		BrightnessPct: brightnessPct,
		ColorTempKelvin: kelvin,
		Timestamp: now,
		LastCommandAt: now,
		LastMotionAt: now,
		NextChangeAt: null,
		NextChangeFrom: null);

	AreaSnapshot Dark(string area) => new(
		area, AreaState.AutoVacant, TransitionReason.CircadianTick, ModeKind.Normal,
		KillSwitchActive: false,
		IsDark: false,
		PeriodName: "Dag",
		BrightnessPct: null,
		ColorTempKelvin: null,
		Timestamp: now,
		LastCommandAt: null,
		LastMotionAt: null,
		NextChangeAt: null,
		NextChangeFrom: null);

	Report(Lit("Stue", AreaState.AutoActive, 65, 2700));
	Report(Lit("Kjøkken", AreaState.AutoActive, 90, 4000));
	Report(Lit("Kontor", AreaState.AutoActive, 40, 3000));
	Report(Lit("Gjesterom", AreaState.OverriddenOn, 80, 2200));

	Report(Dark("Soverom"));
	Report(Dark("Bad"));
}

internal sealed class SeedConfig : IAppConfig<AdaptiveLightingConfig>
{
	public AdaptiveLightingConfig Value { get; set; } = new();
}
