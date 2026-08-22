using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AdaptiveLighting.NetDaemon;

/// <summary>The lighting document's own directory, for the things that have to outlive a deploy.</summary>
/// <remarks>
///     The deploy folder is wiped and re-copied every time, so <c>AdaptiveLighting:ConfigPath</c> names the only
///     directory a host has already promised to keep. The key ring and the durable log each get a subfolder of it.
/// </remarks>
public static class DurableDirectory
{
	private const string FallbackStem = "adaptive-lighting";

	/// <summary>The document's directory on this machine, or <c>null</c> when nothing here outlives a deploy.</summary>
	/// <remarks>
	///     Asked of <see cref="LightingConfigPath.Resolve"/>, never re-derived from <see cref="IConfiguration"/>: a
	///     configured path whose directory cannot be created makes <c>Resolve</c> fall back to the in-tree file, and a
	///     second derivation would aim the durable log at a document the app is not editing.
	/// </remarks>
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
