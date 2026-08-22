using AdaptiveLighting.Configuration;
using AdaptiveLighting.Hosting;
using AdaptiveLighting.Tests.Lighting;
using AdaptiveLighting.Web;

using NetDaemon.AppModel;
using NetDaemon.HassModel;

// A host for the Razor Class Library, so a UI change can be looked at before it reaches a house. The RCL ships
// App.razor and Routes.razor over its own assembly, so there is nothing to add but services and a fake house.
//
// Run:  dotnet run --project scratchpad/uihost   ->  http://localhost:5199

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Explicit, not left to the environment: static web assets are wired up automatically only in Development, and
// without them the RCL's _content/** and _framework/blazor.web.js both 404 — the page server-renders and then
// sits there with no circuit, which looks like a broken UI rather than a broken host.
builder.WebHost.UseStaticWebAssets();

// local.yaml is gitignored: point it at a copy of a real document to reproduce a house, or leave it absent and
// the engine writes a starting one. A real document must never be committed — this repository is public.
string document = builder.Configuration["ConfigPath"]
	?? Path.Combine(builder.Environment.ContentRootPath, "local.yaml");

builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
	["AdaptiveLighting:ConfigPath"] = document
});

// The fake house. Populated below so the two helper screens have live options to reconcile against, including
// one deliberate orphan on each — the state the new "move it to…" control exists for.
FakeHaContext ha = new();
FakeHaRegistry registry = new();

Seed(ha);

builder.Services.AddSingleton<IHaContext>(ha);
builder.Services.AddSingleton<IHaRegistry>(registry);
builder.Services.AddSingleton<IAppConfig<AdaptiveLightingConfig>>(new SeedConfig());

builder.Services.AddLightingWeb();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

WebApplication app = builder.Build();

app.UseAntiforgery();

// This alone serves the RCL's _content/** and _framework/blazor.web.js. No UseStaticFiles: measured without
// it, both come back 200 with real bodies and the circuit opens.
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// Nothing starts the engine here, so the dashboard and every room page would sit on "hasn't reported yet".
// After the cache has subscribed, one snapshot per area in the document, shaped by what that area is
// configured to do. Absent a local.yaml the seed document holds no areas and this raises nothing.
app.Lifetime.ApplicationStarted.Register(() => SeedSnapshots(ha, app.Services.GetRequiredService<LightingConfigStore>()));

app.Run("http://localhost:5199");

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
}

static void SeedSnapshots(FakeHaContext ha, LightingConfigStore store)
{
	AdaptiveLightingConfig config = store.Load();
	DateTimeOffset now = DateTimeOffset.Now;
	int index = 0;

	foreach (AreaConfig area in config.Areas)
	{
		// NonEmpty, not ??: a document with "scene_on_motion:" and nothing after it carries "", which is present
		// to ?? and absent to everything the page does with it.
		string name = NonEmpty(area.Name) ?? NonEmpty(area.AreaId) ?? $"Room {index}";
		string? scene = NonEmpty(area.SceneOnMotion) ?? NonEmpty(area.SceneWhenEmpty);
		string? holder = NonEmpty(area.KeepLitWhenOn?.FirstOrDefault());

		// A scene nulls both levels, as the engine's own does: the page must read the scene, not invent a level.
		bool lit = scene is not null || holder is not null || index % 3 != 0;

		// The catalog resolves these to friendly names on the page, so the fake house has to know them.
		if (scene is not null)
			ha.SetState(scene, "scening", new() { ["friendly_name"] = Friendly(scene) });

		if (holder is not null)
			ha.SetState(holder, "playing", new() { ["friendly_name"] = Friendly(holder) });

		ha.RaiseEvent("adaptive_lighting_area", new
		{
			area = name,
			area_id = area.AreaId,
			state = lit ? "AutoActive" : "AutoVacant",
			reason = "MotionDetected",
			mode = "Home",
			house_mode_value = "Home",
			kill_switch_active = false,
			is_dark = true,
			period = "Kveld",
			brightness_pct = scene is null && lit ? 62.0 : (double?)null,
			color_temp_kelvin = scene is null && lit ? 2700 : (int?)null,
			timestamp = now.AddMinutes(-2),
			last_command_at = now.AddMinutes(-2),
			last_motion_at = lit ? now.AddMinutes(-2) : now.AddMinutes(-40),
			next_change_at = lit && holder is null && scene is null ? now.AddMinutes(8) : (DateTimeOffset?)null,
			next_change_from = lit && holder is null && scene is null ? now.AddMinutes(-2) : (DateTimeOffset?)null,
			darkness_detail = "lux 18, dark below 40",
			auto_on_blocked_by = "None",
			is_held_lit = holder is not null,
			held_lit_by = holder,
			scene_applied = scene,
			is_anyone_home = true
		});

		index++;
	}
}

static string? NonEmpty(string? value) => value is { Length: > 0 } text ? text : null;

static string Friendly(string entityId)
{
	string tail = entityId[(entityId.IndexOf('.', StringComparison.Ordinal) + 1)..].Replace('_', ' ');

	return tail.Length == 0 ? entityId : string.Concat(char.ToUpperInvariant(tail[0]), tail[1..]);
}

internal sealed class SeedConfig : IAppConfig<AdaptiveLightingConfig>
{
	public AdaptiveLightingConfig Value { get; set; } = new();
}
