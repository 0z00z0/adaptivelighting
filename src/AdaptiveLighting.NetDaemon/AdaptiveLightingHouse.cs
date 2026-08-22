using AdaptiveLighting.Web;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaptiveLighting.NetDaemon;

/// <summary>What a house may override when it adopts the lighting engine; the defaults suit an add-on.</summary>
/// <param name="KeyRingPath">
///     Where the DataProtection key ring is persisted. <c>null</c> puts it beside the lighting document, the only
///     directory a host has already promised to keep across deploys.
/// </param>
/// <param name="Port">
///     The port the UI listens on, overriding <c>AdaptiveLighting:Port</c>. <c>0</c> leaves Kestrel to the host.
/// </param>
public sealed record AdaptiveLightingHouseOptions(string? KeyRingPath = null, int? Port = null);

/// <summary>Adopts AdaptiveLighting into a NetDaemon host.</summary>
/// <remarks>
///     <para>
///         This owns the process's only root Blazor component: <see cref="UseAdaptiveLighting"/> calls
///         <c>MapRazorComponents&lt;App&gt;()</c>, and a second root in the same service container puts two "/"
///         endpoints in one route table, failing every request with an <c>AmbiguousMatchException</c>. A second
///         Blazor app needs its own service container and its own Kestrel.
///     </para>
///     <para>
///         EXPOSURE: the UI listens on every interface with no authentication, so anyone who reaches the port can
///         rewrite the lighting configuration. Keep it on a trusted LAN, and put it behind Home Assistant ingress
///         or an authenticating proxy if it ever has to be reachable from outside.
///     </para>
/// </remarks>
public static class AdaptiveLightingHouse
{
	private const string ConfigPathKey = "AdaptiveLighting:ConfigPath";
	private const string PortKey = "AdaptiveLighting:Port";
	private const string KeyRingFolder = "dataprotection-keys";

	/// <summary>The port the UI listens on when nothing says otherwise; the NetDaemon add-on declares 10000-10004.</summary>
	public const int DefaultPort = 10000;

	/// <summary>Registers the engine, the UI and the hosting it needs, to be paired with <see cref="UseAdaptiveLighting"/>.</summary>
	/// <remarks>Call before <c>builder.Build()</c>; ordering against other registrations does not matter.</remarks>
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

	/// <summary>Binds the UI's port; <c>0</c> leaves Kestrel alone for a host that configures its own.</summary>
	/// <remarks>Kestrel's option delegates are additive, so a host that also listens on its own port keeps doing so.</remarks>
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
	///     Calls <c>UseAntiforgery</c>, so any middleware that isolates a port must be installed before this; routes
	///     mapped on a second port afterwards run behind this app's antiforgery instead of their own gate.
	/// </remarks>
	public static WebApplication UseAdaptiveLighting(this WebApplication app)
	{
		ArgumentNullException.ThrowIfNull(app);

		// MapStaticAssets serves from the build-time asset manifest, which is what makes the class library's CSS and
		// Blazor's own _framework/blazor.web.js resolve outside Development.
		app.MapStaticAssets();
		app.UseAntiforgery();
		app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

		return app;
	}

	/// <summary>Persists the DataProtection key ring beside the lighting document, so a deploy keeps every open tab signed in.</summary>
	/// <remarks>
	///     The parent directory is the test, matching <c>LightingConfigPath.Resolve</c>: <c>/config</c> exists on a
	///     Home Assistant box while the app's own folder may not yet, and testing the folder would skip the key ring
	///     on a first run for the in-container default, which the next restart throws away.
	/// </remarks>
	private static void AddKeyRing(WebApplicationBuilder builder, AdaptiveLightingHouseOptions options)
	{
		ILogger logger = Logger();

		if (KeyRingDirectory(builder, options, logger) is not { } keyRing)
		{
			// A silent fallback here reads as working until a deploy logs everybody out.
			logger.LogInformation(
				"No durable directory for the DataProtection key ring, so Blazor's antiforgery keys stay inside "
				+ "the container and are lost on restart. Set {Key} to a path that survives a deploy, or pass "
				+ "KeyRingPath. Expected on a development machine.",
				ConfigPathKey);

			return;
		}

		Directory.CreateDirectory(keyRing);

		// The fixed application name pins the key ring's isolation identifier, which otherwise derives from the
		// content-root path and would change as the deploy folder moves, invalidating the keys.
		builder.Services.AddDataProtection()
			.PersistKeysToFileSystem(new DirectoryInfo(keyRing))
			.SetApplicationName("AdaptiveLighting");

		logger.LogInformation("DataProtection keys are kept at {Path}.", keyRing);
	}

	// Registration runs before the host is built, so there is no ILogger to resolve yet, and both messages have to
	// reach an operator on the run that decides them.
	private static ILogger Logger() => LoggerFactory
		.Create(logging => logging.AddConsole())
		.CreateLogger(typeof(AdaptiveLightingHouse).FullName!);

	private static string? KeyRingDirectory(WebApplicationBuilder builder, AdaptiveLightingHouseOptions options, ILogger logger) =>
		options.KeyRingPath is { Length: > 0 } explicitPath
			? explicitPath
			: DurableDirectory.Subfolder(builder.Configuration, builder.Environment.ContentRootPath, KeyRingFolder, logger);
}
