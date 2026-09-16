using AdaptiveLighting.Hosting;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AdaptiveLighting.NetDaemon;

/// <summary>The lighting document's own directory: the one place a deploy keeps, for the key ring and the durable log.</summary>
internal static class DurableDirectory
{
	private const string FallbackStem = "adaptive-lighting";

	/// <summary>The document's directory on this machine, or <c>null</c> when nothing here outlives a deploy.</summary>
	// Ask LightingConfigPath.Resolve, never re-derive: Resolve can fall back to the in-tree file, and a second
	// derivation would aim the log at a document the app is not editing.
	public static string? Locate(IConfiguration configuration, string contentRootPath, ILogger logger)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		ArgumentNullException.ThrowIfNull(logger);
		ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

		ConfigLocation location = LightingConfigPath.Resolve(configuration, contentRootPath, logger);

		return location.SurvivesDeploy ? Path.GetDirectoryName(location.Path) : null;
	}

	/// <summary>A named subfolder of it, or <c>null</c> on the same terms; not created here.</summary>
	public static string? Subfolder(IConfiguration configuration, string contentRootPath, string folderName, ILogger logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(folderName);

		return Locate(configuration, contentRootPath, logger) is { } directory
			? Path.Combine(directory, folderName)
			: null;
	}

	/// <summary>The document's file stem, so two houses sharing a <c>/config</c> cannot collide.</summary>
	public static string Stem(IConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(configuration);

		if (configuration[LightingConfigPath.ConfigPathKey] is not { Length: > 0 } document)
			return FallbackStem;

		string stem = Path.GetFileNameWithoutExtension(document);

		return stem.Length > 0 ? stem : FallbackStem;
	}
}
