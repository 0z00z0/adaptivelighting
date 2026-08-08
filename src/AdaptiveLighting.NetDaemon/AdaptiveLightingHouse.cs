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
public sealed record AdaptiveLightingHouseOptions(string? KeyRingPath = null);

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
///         The port is deliberately not set here. The UI has no authentication, so the <c>ListenAnyIP</c> call
///         and the warning that belongs beside it are a statement about one network, which a package cannot make.
///     </para>
/// </remarks>
public static class AdaptiveLightingHouse
{
	private const string ConfigPathKey = "AdaptiveLighting:ConfigPath";
	private const string KeyRingFolder = "dataprotection-keys";

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

		AddKeyRing(builder, options ?? new AdaptiveLightingHouseOptions());

		return builder;
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
		ILogger logger = LoggerFactory
			.Create(logging => logging.AddConsole())
			.CreateLogger(typeof(AdaptiveLightingHouse).FullName!);

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
