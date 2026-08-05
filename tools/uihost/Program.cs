using AdaptiveLighting.Configuration;
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

// UseStaticFiles, not MapStaticAssets: the RCL's _content assets come back as 0-byte bodies under the latter.
app.UseStaticFiles();
app.UseAntiforgery();

// Both, and in this order. UseStaticFiles serves the RCL's _content/** off the runtime manifest;
// MapStaticAssets is what serves _framework/blazor.web.js, without which the page renders once and never
// becomes interactive.
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

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

internal sealed class SeedConfig : IAppConfig<AdaptiveLightingConfig>
{
	public AdaptiveLightingConfig Value { get; set; } = new();
}
