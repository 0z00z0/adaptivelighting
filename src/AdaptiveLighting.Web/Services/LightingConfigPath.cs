using AdaptiveLighting.Configuration;

using Microsoft.Extensions.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>Where a resolved configuration document came from, so the UI can say which file it is editing.</summary>
public enum ConfigLocationSource
{
	/// <summary>The external, deploy-surviving file named by <see cref="LightingConfigPath.ConfigPathKey"/>.</summary>
	External,

	/// <summary>The external file, created on this run by copying the shipped in-tree example.</summary>
	SeededFromTree,

	/// <summary>The in-tree file under the apps folder, because no usable external location exists.</summary>
	InTreeFallback
}

/// <summary>
///     The resolved location of this host's configuration document.
/// </summary>
/// <param name="Path">Absolute path of the file the UI reads and writes.</param>
/// <param name="Source">How that path was arrived at.</param>
/// <param name="Warning">
///     What the operator should know about this choice, or <c>null</c> when the answer is unremarkable.
///     Rendered in the UI: a host silently editing a file that the next deploy will delete is exactly the
///     failure this type exists to prevent.
/// </param>
public sealed record ConfigLocation(string Path, ConfigLocationSource Source, string? Warning)
{
	/// <summary>Whether edits to this file survive a redeploy.</summary>
	public bool SurvivesDeploy => Source is not ConfigLocationSource.InTreeFallback;
}

/// <summary>
///     Works out which file is <i>the</i> lighting configuration document for this host, and seeds it on first
///     run.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why the document is not the one in the apps folder.</b> The obvious file to edit is the
///         <c>apps/AdaptiveLighting/AdaptiveLighting.yaml</c> the host ships — and it is the wrong one, because
///         it lives inside the publish tree. <c>publish_as_binaries.ps1</c> wipes and re-copies the whole deploy
///         folder, so every setting a person had chosen in the browser would vanish on the next deploy, silently,
///         and the lights would revert to the shipped example. Configuration that a deploy destroys is not
///         configuration. So the editable document lives outside the publish tree — on a Home Assistant box,
///         alongside <c>/config</c> rather than under <c>/config/netdaemon6</c> — and the in-tree file becomes
///         what it always really was: the shipped example, used once to seed the real one.
///     </para>
///     <para>
///         <b>The path is configuration, not a constant.</b> Each host names its own file in its tracked
///         <c>appsettings.json</c> under <see cref="ConfigPathKey"/>, so separate hosts do not have to agree
///         and neither has a path baked into an assembly.
///     </para>
///     <para>
///         <b>This runs once, at start-up, on the server.</b> Its answer is baked into the singleton
///         <c>LightingConfigStore</c> and never recomputed. No request, no component and no browser can
///         influence it — which is the property that keeps the UI's write surface to exactly one file rather
///         than to "any path someone can name".
///     </para>
///     <para>
///         Token safety: this is the only type in the web UI that touches <see cref="IConfiguration"/>, it reads
///         exactly three keys, and it returns a file path. It is never injected into a component, and the root
///         configuration object — which carries the Home Assistant long-lived token under
///         <c>HomeAssistant:Token</c> — does not leave this method.
///     </para>
/// </remarks>
public static class LightingConfigPath
{
	/// <summary>Each host's own answer, in its tracked <c>appsettings.json</c>: where the editable document lives.</summary>
	public const string ConfigPathKey = "AdaptiveLighting:ConfigPath";

	/// <summary>NetDaemon's own setting for where app YAML lives. The shipped example hangs off this.</summary>
	public const string AppsFolderKey = "NetDaemon:ApplicationConfigurationFolder";

	private const string DefaultAppsFolder = "./apps";
	private const string DocumentName = "AdaptiveLighting.yaml";
	private const string DefaultSubFolder = "AdaptiveLighting";

	/// <summary>
	///     Resolves the document's absolute path, copying the shipped example out to it on first run.
	/// </summary>
	/// <param name="configuration">The host's configuration. Only the keys named on this type are read.</param>
	/// <param name="contentRootPath">The host's content root, which relative settings are resolved against.</param>
	/// <param name="logger">Where the seeding and fallback decisions are recorded.</param>
	/// <returns>
	///     The chosen location. The file need not exist: a host whose document is missing must still serve a UI
	///     that says so, rather than failing to start.
	/// </returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static ConfigLocation Resolve(IConfiguration configuration, string contentRootPath, ILogger logger)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		ArgumentNullException.ThrowIfNull(logger);
		ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

		var inTree = InTreeExample(configuration, contentRootPath);

		if (configuration[ConfigPathKey] is not { Length: > 0 } configured)
		{
			logger.LogWarning(
				"{Key} is not set, so the lighting configuration falls back to the in-tree file at {Path}. "
				+ "Edits there are destroyed by the next deploy — set {Key} to a path outside the publish tree.",
				ConfigPathKey, inTree, ConfigPathKey);

			return new ConfigLocation(
				inTree,
				ConfigLocationSource.InTreeFallback,
				$"{ConfigPathKey} is not set in appsettings.json, so this host is editing the file inside its own deploy folder. The next deploy will overwrite it.");
		}

		var external = Path.GetFullPath(configured, contentRootPath);

		if (File.Exists(external))
			return new ConfigLocation(external, ConfigLocationSource.External, null);

		var directory = Path.GetDirectoryName(external);

		if (directory is null)
			return Fallback(inTree, external, "it has no directory", logger);

		// The directory itself is allowed to be missing — on a Home Assistant box /config exists but
		// /config/adaptive-lighting does not until something creates it, and refusing to create it would mean
		// the external file could never come into existence. The *parent* is the real test: if it is missing
		// too, the configured path belongs to a machine this is not (the Windows dev box has no /config), and
		// creating C:\config\… would be inventing a location rather than finding one.
		if (!Directory.Exists(directory) && !Directory.Exists(Path.GetDirectoryName(directory) ?? directory))
			return Fallback(inTree, external, "neither it nor its parent directory exists on this machine", logger);

		try
		{
			Directory.CreateDirectory(directory);

			// Seed a clean default rather than copying the shipped example. The example is a teaching document
			// full of REPLACE_ME ids, and copying it onto a real host was actively harmful: every placeholder is
			// an entity Home Assistant does not know, so a brand-new installation started with document-level
			// errors and refused to run. A placeholder also OVERRIDES the discovery that would have filled the
			// same field — an empty Persons list finds every person by itself; person.REPLACE_ME finds nothing
			// and blocks the engine. Better to name nothing and then look: the engine populates the area list
			// from the area registry on its first connected reload (LightingEngineHost.AutoDiscoverAreasIfNeeded).
			File.WriteAllText(external, LightingConfigDocument.Serialize(AdaptiveLightingConfig.CreateDefault()));

			logger.LogInformation(
				"Created a starting lighting configuration at {External}: a circadian schedule and nothing else. "
				+ "Rooms are discovered from the Home Assistant areas that have both a light and a motion sensor, "
				+ "and everything is editable on the Configuration page.",
				external);

			return new ConfigLocation(external, ConfigLocationSource.SeededFromTree, null);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Fallback(inTree, external, $"it could not be created ({exception.Message})", logger);
		}
	}

	private static ConfigLocation Fallback(string inTree, string external, string why, ILogger logger)
	{
		logger.LogWarning(
			"The configured lighting configuration path {External} is unusable: {Why}. Falling back to the in-tree "
			+ "file at {InTree}. This is expected on a development machine; on a Home Assistant host it means edits "
			+ "will not survive a deploy.",
			external, why, inTree);

		return new ConfigLocation(
			inTree,
			ConfigLocationSource.InTreeFallback,
			$"{ConfigPathKey} points at '{external}', but {why}. This host is editing the file inside its own deploy folder instead, and the next deploy will overwrite it. That is normal when running locally.");
	}

	private static string InTreeExample(IConfiguration configuration, string contentRootPath)
	{
		var appsFolder = Path.GetFullPath(
			configuration[AppsFolderKey] is { Length: > 0 } folder ? folder : DefaultAppsFolder,
			contentRootPath);

		var conventional = Path.Combine(appsFolder, DefaultSubFolder, DocumentName);

		if (File.Exists(conventional))
			return conventional;

		// The conventional layout is what both hosts ship, but a deployment is free to rearrange its apps
		// folder. One unambiguous match is worth finding; two is not a guess worth making.
		IReadOnlyList<string> found = SafeSearch(appsFolder);

		return found.Count == 1 ? found[0] : conventional;
	}

	private static IReadOnlyList<string> SafeSearch(string appsFolder)
	{
		try
		{
			return Directory.Exists(appsFolder)
				? Directory.GetFiles(appsFolder, DocumentName, SearchOption.AllDirectories)
				: [];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// An unreadable apps folder is the caller's problem to report through the store, not a reason to
			// fail host start-up here.
			return [];
		}
	}
}
