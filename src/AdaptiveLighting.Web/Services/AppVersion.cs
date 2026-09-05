using System.Reflection;

namespace AdaptiveLighting.Web.Services;

/// <summary>The running version, as a house operator would quote it.</summary>
/// <remarks>
///     One derivation, read by the Configuration page's <c>Version</c> row and by the layout's feedback link, so
///     the number shown and the number reported can never disagree. This is the package version and not the build
///     token <c>App.razor</c> puts on asset URLs: the '+sha' suffix SourceLink appends is dropped, because what an
///     operator matches against is a release.
/// </remarks>
public static class AppVersion
{
	/// <summary>The version text, or "unknown" when the assembly carries neither attribute.</summary>
	public static string Text { get; } = Read(typeof(AppVersion).Assembly);

	/// <summary>The same derivation against any assembly, so it can be exercised against known attributes.</summary>
	public static string Read(Assembly assembly)
	{
		string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? assembly.GetName().Version?.ToString()
			?? "unknown";

		int plus = version.IndexOf('+');
		return plus >= 0 ? version[..plus] : version;
	}
}
