using System.Reflection;
using System.Text;

using NetDaemon.AppModel;

namespace AdaptiveLighting.Hosting;

/// <summary>Derives the entity id of the enable helper NetDaemon's state manager publishes for an app.</summary>
/// <remarks>
///     <c>AddNetDaemonStateManager()</c> names it <c>netdaemon_</c> plus the app id snake-cased, so
///     <c>Example.NetDaemon.Home.AdaptiveLightingApp</c> becomes
///     <c>input_boolean.netdaemon_example_net_daemon_home_adaptive_lighting_app</c>.
/// </remarks>
public static class NetDaemonAppSwitch
{
	private const string Prefix = "input_boolean.netdaemon_";

	/// <summary>The enable-switch entity id for the app that owns the current NetDaemon scope.</summary>
	// ICurrentApp.Id is the id the state manager names the switch from, pinned Id included.
	public static string EntityIdFor(ICurrentApp currentApp)
	{
		ArgumentNullException.ThrowIfNull(currentApp);
		ArgumentException.ThrowIfNullOrWhiteSpace(currentApp.Id);
		return Prefix + Slug(currentApp.Id);
	}

	/// <summary>The enable-switch entity id for <paramref name="appType"/>, for a caller with no app scope to inject <see cref="ICurrentApp"/> from.</summary>
	public static string EntityIdFor(Type appType)
	{
		ArgumentNullException.ThrowIfNull(appType);

		// [NetDaemonApp(Id = "...")] pins a short id, and the state manager names the switch from that instead of the type.
		string? explicitId = appType.GetCustomAttribute<NetDaemonAppAttribute>(inherit: false)?.Id;

		return string.IsNullOrWhiteSpace(explicitId)
			? EntityIdForTypeName(appType.FullName ?? appType.Name)
			: Prefix + Slug(explicitId);
	}

	/// <summary>The enable-switch entity id for an app whose fully qualified type name is <paramref name="typeFullName"/>.</summary>
	/// <remarks>A string overload so the slug can be asserted against apps in assemblies a test does not reference.</remarks>
	public static string EntityIdForTypeName(string typeFullName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(typeFullName);
		return Prefix + Slug(typeFullName);
	}

	/// <summary>
	///     NetDaemon's own normalisation: '.' becomes '_', a '_' is inserted before an uppercase following a
	///     lowercase or digit, everything is lowercased, and runs of '_' collapse.
	/// </summary>
	private static string Slug(string typeName)
	{
		StringBuilder builder = new(typeName.Length + 8);

		foreach (char c in typeName)
		{
			if (c == '.')
			{
				Append(builder, '_');
				continue;
			}

			if (char.IsUpper(c) && builder.Length > 0)
			{
				char previous = builder[^1];
				if (char.IsLower(previous) || char.IsDigit(previous))
					builder.Append('_');
			}

			Append(builder, char.ToLowerInvariant(c));
		}

		return builder.ToString().Trim('_');

		// Never two underscores in a row: a namespace segment ending in an uppercase would otherwise double up.
		static void Append(StringBuilder builder, char c)
		{
			if (c == '_' && builder.Length > 0 && builder[^1] == '_')
				return;

			builder.Append(c);
		}
	}
}
