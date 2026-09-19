using AdaptiveLighting.Lamplight;
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
///     Owns the process's root Blazor components: the first design's, and Lamplight's when
///     <c>AdaptiveLighting:LamplightPort</c> is set. Two roots put two "/" endpoints in one route table, which fails
///     every request with <c>AmbiguousMatchException</c> unless <see cref="LamplightSite.MapLamplight"/> splits them
///     by listening port.
///     EXPOSURE: the UI listens on every interface with no authentication. Keep it on a trusted LAN, or behind Home
///     Assistant ingress or an authenticating proxy.
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

		// No host logger exists before Build. Disposing the factory flushes the console queue, so both messages print.
		using ILoggerFactory loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
		ILogger logger = loggerFactory.CreateLogger(typeof(AdaptiveLightingHouse).FullName!);

		AddKeyRing(builder, settings, logger);
		Listen(builder, settings, logger);

		return builder;
	}

	/// <summary>Binds the UI's port; <c>0</c> leaves Kestrel alone for a host that configures its own.</summary>
	/// <remarks>Kestrel's option delegates are additive, so a host that also listens on its own port keeps doing so.</remarks>
	private static void Listen(WebApplicationBuilder builder, AdaptiveLightingHouseOptions options, ILogger logger)
	{
		int port = options.Port
			?? (int.TryParse(builder.Configuration[PortKey], out int configured) ? configured : DefaultPort);

		if (port != 0)
		{
			builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(port));

			logger.LogWarning(
				"The lighting UI is listening on port {Port} on every interface, with no authentication: anyone who "
				+ "can reach it can rewrite this house's lighting configuration. Keep it on the LAN — do not forward "
				+ "or NAT it. Set {Key} to 0 to bind it yourself.",
				port, PortKey);
		}

		ListenForLamplight(builder, port, logger);
	}

	/// <summary>Binds Lamplight's port when the setting names one; otherwise adds nothing at all.</summary>
	private static void ListenForLamplight(WebApplicationBuilder builder, int firstPort, ILogger logger)
	{
		if (LamplightSite.Port(builder.Configuration) is not { } lamplight)
		{
			if (builder.Configuration[LamplightSite.PortKey] is { Length: > 0 } unreadable && unreadable != "0")
				logger.LogWarning("{Key} is {Value}, which is not a port, so Lamplight is not served.", LamplightSite.PortKey, unreadable);

			return;
		}

		if (lamplight == firstPort)
			throw new InvalidOperationException(
				$"{LamplightSite.PortKey} and {PortKey} are both {lamplight}; Lamplight needs a port of its own.");

		builder.Services.AddLamplight();
		builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(lamplight));

		logger.LogWarning(
			"Lamplight is listening on port {Port} on every interface, with no authentication, like the first "
			+ "design. Remove {Key} to turn it off.",
			lamplight, LamplightSite.PortKey);
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
		RazorComponentsEndpointConventionBuilder firstDesign = app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

		if (LamplightSite.Port(app.Configuration) is { } lamplight)
			app.MapLamplight(lamplight, firstDesign);

		return app;
	}

	/// <summary>Persists the DataProtection key ring beside the lighting document, so a deploy keeps every open tab signed in.</summary>
	/// <remarks>
	///     The parent directory is the test, as in <c>LightingConfigPath.Resolve</c>: <c>/config</c> exists on a Home
	///     Assistant box before the app's own folder does.
	/// </remarks>
	private static void AddKeyRing(WebApplicationBuilder builder, AdaptiveLightingHouseOptions options, ILogger logger)
	{
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

	private static string? KeyRingDirectory(WebApplicationBuilder builder, AdaptiveLightingHouseOptions options, ILogger logger) =>
		options.KeyRingPath is { Length: > 0 } explicitPath
			? explicitPath
			: DurableDirectory.Subfolder(builder.Configuration, builder.Environment.ContentRootPath, KeyRingFolder, logger);
}
