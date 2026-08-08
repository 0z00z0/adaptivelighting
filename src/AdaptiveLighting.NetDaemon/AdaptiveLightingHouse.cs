using AdaptiveLighting.Web;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaptiveLighting.NetDaemon;

/// <summary>What a house may override when it adopts the lighting engine. The defaults are right on an add-on.</summary>
/// <param name="KeyRingPath">
///     Where the DataProtection key ring is persisted. <c>null</c> puts it beside the lighting document, which is
///     the only directory a host has already promised to keep across deploys.
/// </param>
/// <param name="Port">
///     The port the UI listens on, overriding <c>AdaptiveLighting:Port</c>. <c>0</c> means the host owns its own
///     Kestrel and this must not touch it.
/// </param>
public sealed record AdaptiveLightingHouseOptions(string? KeyRingPath = null, int? Port = null);

/// <summary>Adopts AdaptiveLighting into a NetDaemon host.</summary>
/// <remarks>
///     <para>
///         Everything here is knowledge the library has about hosting itself. It used to sit in each house's
///         <c>program.cs</c>, which meant every new house re-learned the same traps: static web assets are wired
///         up automatically only in Development, <c>MapStaticAssets</c> is what serves both the class library's
///         <c>_content/**</c> and <c>blazor.web.js</c>, and a key ring left inside the container is destroyed by
///         the next deploy.
///     </para>
///     <para>
///         <b>This owns the process's only root Blazor component.</b> <see cref="UseAdaptiveLighting"/> calls
///         <c>MapRazorComponents&lt;App&gt;()</c>, and a second root in the same service container puts two "/"
///         endpoints in one route table: every request to every port then fails with an
///         <c>AmbiguousMatchException</c>. A host that needs a second Blazor app must give it its own container
///         and its own Kestrel, the way the ESPHome console does.
///     </para>
///     <para>
///         <b>EXPOSURE.</b> The UI listens on every interface with <b>no authentication of any kind</b>. It is not
///         a read-only dashboard: anyone who reaches the port can rewrite the lighting configuration and rebuild
///         the running engine — disable every room, point them at other people's lights, set the night cap to
///         100%. The write surface is exactly one file, resolved server-side at start-up and never influenced by
///         a request, which bounds the hole without closing it.
///     </para>
///     <para>
///         That is acceptable only while the port stays on a trusted LAN. Do not forward or NAT it. It is not
///         reachable through Nabu Casa, which proxies only Home Assistant's own 8123. On a Home Assistant add-on
///         it is exposed only if the port is mapped in the Network panel. If it ever has to be reachable from
///         outside, put it behind Home Assistant ingress or an authenticating reverse proxy — do not add a login
///         form here, because hand-rolled credentials would be worse than none. The port is logged as a warning
///         at every start so this is never a silent default.
///     </para>
/// </remarks>
public static class AdaptiveLightingHouse
{
	private const string ConfigPathKey = "AdaptiveLighting:ConfigPath";
	private const string PortKey = "AdaptiveLighting:Port";
	private const string KeyRingFolder = "dataprotection-keys";

	/// <summary>The port the UI listens on when nothing says otherwise. The NetDaemon add-on declares 10000-10004.</summary>
	public const int DefaultPort = 10000;

	/// <summary>Registers the engine, the UI, and the hosting the UI needs. Pair with <see cref="UseAdaptiveLighting"/>.</summary>
	/// <remarks>Call before <c>builder.Build()</c>. Ordering against other registrations does not matter.</remarks>
	public static WebApplicationBuilder AddAdaptiveLighting(
		this WebApplicationBuilder builder,
		AdaptiveLightingHouseOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(builder);

		builder.Services.AddLightingWeb();
		builder.Services.AddRazorComponents().AddInteractiveServerComponents();

		// Static web assets are wired up automatically only in Development. Without this the class library's
		// _content/** 404s in production and every page renders unstyled and inert.
		builder.WebHost.UseStaticWebAssets();

		AdaptiveLightingHouseOptions settings = options ?? new AdaptiveLightingHouseOptions();

		AddKeyRing(builder, settings);
		Listen(builder, settings);

		return builder;
	}

	/// <summary>
	///     Binds the UI's port, and says out loud what that means. <c>0</c> leaves Kestrel alone for a host that
	///     configures its own.
	/// </summary>
	/// <remarks>
	///     Kestrel's option delegates are additive, so a host that also listens on its own port keeps doing so.
	///     The warning is logged rather than left as a comment in somebody's program.cs, which is where it used to
	///     live and therefore had to be re-pasted into every new house.
	/// </remarks>
	private static void Listen(WebApplicationBuilder builder, AdaptiveLightingHouseOptions options)
	{
		int port = options.Port
			?? (int.TryParse(builder.Configuration[PortKey], out int configured) ? configured : DefaultPort);

		if (port == 0)
			return;

		builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(port));

		Logger().LogWarning(
			"The lighting UI is listening on port {Port} on every interface, with no authentication: anyone who "
			+ "can reach it can rewrite this house's lighting configuration. Keep it on the LAN — do not forward "
			+ "or NAT it. Set {Key} to 0 to bind it yourself.",
			port, PortKey);
	}

	/// <summary>Maps the UI's assets and endpoints.</summary>
	/// <remarks>
	///     Calls <c>UseAntiforgery</c>, so **any middleware that isolates a port must be installed before this**.
	///     A host that maps its own routes on a second port and installs them afterwards finds them running behind
	///     this app's antiforgery instead of its own gate.
	/// </remarks>
	public static WebApplication UseAdaptiveLighting(this WebApplication app)
	{
		ArgumentNullException.ThrowIfNull(app);

		// MapStaticAssets, not UseStaticFiles: it serves from the build-time asset manifest, which is what makes
		// the class library's CSS and Blazor's own _framework/blazor.web.js resolve outside Development.
		app.MapStaticAssets();
		app.UseAntiforgery();
		app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

		return app;
	}

	/// <summary>
	///     Persists the DataProtection key ring beside the lighting document, so a deploy does not invalidate
	///     every open tab's antiforgery token.
	/// </summary>
	/// <remarks>
	///     The parent is the test, not the directory itself, matching <c>LightingConfigPath.Resolve</c>:
	///     <c>/config</c> exists on a Home Assistant box while the app's own folder may not yet. Testing the
	///     folder would skip the key ring on a first run and fall back to the in-container default, which is
	///     thrown away on the next restart — the exact failure this exists to prevent.
	/// </remarks>
	private static void AddKeyRing(WebApplicationBuilder builder, AdaptiveLightingHouseOptions options)
	{
		ILogger logger = Logger();

		if (KeyRingDirectory(builder, options) is not { } keyRing)
		{
			// Said out loud: a silent fallback here reads as "working" until a deploy logs everybody out.
			logger.LogInformation(
				"No durable directory for the DataProtection key ring, so Blazor's antiforgery keys stay inside "
				+ "the container and are lost on restart. Set {Key} to a path that survives a deploy, or pass "
				+ "KeyRingPath. Expected on a development machine.",
				ConfigPathKey);

			return;
		}

		Directory.CreateDirectory(keyRing);

		// The fixed application name pins the key ring's isolation identifier, which otherwise derives from the
		// content-root path and would change as the deploy folder moves, silently invalidating the keys.
		builder.Services.AddDataProtection()
			.PersistKeysToFileSystem(new DirectoryInfo(keyRing))
			.SetApplicationName("AdaptiveLighting");

		logger.LogInformation("DataProtection keys are kept at {Path}.", keyRing);
	}

	// Registration runs before the host is built, so there is no ILogger to resolve yet. Both messages here have
	// to reach an operator on the run that decides them, which is why they are not deferred to a hosted service.
	private static ILogger Logger() => LoggerFactory
		.Create(logging => logging.AddConsole())
		.CreateLogger(typeof(AdaptiveLightingHouse).FullName!);

	private static string? KeyRingDirectory(WebApplicationBuilder builder, AdaptiveLightingHouseOptions options)
	{
		if (options.KeyRingPath is { Length: > 0 } explicitPath)
			return explicitPath;

		if (builder.Configuration[ConfigPathKey] is not { Length: > 0 } document)
			return null;

		// Against the content root, not the working directory, so a relative path lands where the document does.
		string? directory = Path.GetDirectoryName(Path.GetFullPath(document, builder.Environment.ContentRootPath));

		if (directory is null)
			return null;

		bool onThisMachine = Directory.Exists(directory)
			|| Directory.Exists(Path.GetDirectoryName(directory) ?? directory);

		return onThisMachine ? Path.Combine(directory, KeyRingFolder) : null;
	}
}
