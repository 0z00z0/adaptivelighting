using AdaptiveLighting.Configuration;

using Microsoft.Extensions.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>Where a resolved configuration document came from, so the UI can say which file it is editing.</summary>
public enum ConfigLocationSource
{
	External,

	/// <summary>The external file, created on this run, holding a fresh
	///     <see cref="AdaptiveLightingConfig.CreateDefault"/> and never a copy of the in-tree example.</summary>
	SeededFromTree,

	InTreeFallback
}

/// <summary>The resolved location of this host's configuration document.</summary>
public sealed record ConfigLocation(string Path, ConfigLocationSource Source, string? Warning)
{
	public bool SurvivesDeploy => Source is not ConfigLocationSource.InTreeFallback;
}

/// <summary>Works out which file is the lighting configuration document for this host, and seeds it on first run.</summary>
/// <remarks>
///     The editable document lives outside the publish tree, which the deploy script wipes and re-copies. Resolved
///     once at start-up and baked into the singleton <c>LightingConfigStore</c>, so no request, component or browser
///     can influence it, which holds the UI's write surface to one file. The only type in the web UI that touches
///     <see cref="IConfiguration"/>; the root configuration object carries <c>HomeAssistant:Token</c> and never
///     leaves here.
/// </remarks>
public static class LightingConfigPath
{
	/// <summary>Where the editable document lives, answered per host in its tracked <c>appsettings.json</c>.</summary>
	public const string ConfigPathKey = "AdaptiveLighting:ConfigPath";

	/// <summary>NetDaemon's own setting for where app YAML lives. The shipped example hangs off this.</summary>
	public const string AppsFolderKey = "NetDaemon:ApplicationConfigurationFolder";

	private const string DefaultAppsFolder = "./apps";
	private const string DocumentName = "AdaptiveLighting.yaml";
	private const string DefaultSubFolder = "AdaptiveLighting";

	/// <summary>Resolves the document's absolute path, writing a starting document to it on first run.</summary>
	/// <returns>The chosen location. The file need not exist; a host whose document is missing must still start.</returns>
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

		// The parent is the test, not the directory itself: /config exists on a Home Assistant box while
		// /config/adaptive-lighting does not. With the parent missing too, the path belongs to another machine.
		if (!Directory.Exists(directory) && !Directory.Exists(Path.GetDirectoryName(directory) ?? directory))
			return Fallback(inTree, external, "neither it nor its parent directory exists on this machine", logger);

		try
		{
			Directory.CreateDirectory(directory);

			// A clean CreateDefault, never a copy of the in-tree example: its REPLACE_ME ids override the discovery
			// that would have filled the same field, and person.REPLACE_ME finds nothing and blocks the engine.
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

		// A deployment may rearrange its apps folder. One unambiguous match is worth taking; two is not.
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
			// An unreadable apps folder is the store's problem to report, not a reason to fail host start-up.
			return [];
		}
	}
}
