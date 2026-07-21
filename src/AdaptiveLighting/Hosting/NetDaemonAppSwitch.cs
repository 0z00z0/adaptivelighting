using System.Linq;
using System.Text;

namespace AdaptiveLighting.Hosting;

/// <summary>
///     Derives the entity id of the enable helper NetDaemon's state manager publishes for an app (09 §7).
/// </summary>
/// <remarks>
///     <para>
///         <c>AddNetDaemonStateManager()</c> creates, per discovered app, an <c>input_boolean</c> whose object id
///         is <c>netdaemon_</c> followed by the app's fully qualified type name snake-cased — dots become
///         underscores and every PascalCase word boundary is split. So
///         <c>Example.NetDaemon.Home.AdaptiveLightingApp</c> becomes
///         <c>input_boolean.netdaemon_example_net_daemon_home_adaptive_lighting_app</c>, and a second host's twin differs
///         only in the host segment. The slug is derived from the type here rather than hardcoded so separate hosts
///         cannot drift apart.
///     </para>
///     <para>
///         Verified against the entities the generator already emits (e.g.
///         <c>netdaemon_example_net_daemon_site1_generic_trigger</c> for <c>Example.NetDaemon.Site1.GenericTrigger</c>).
///         Turning this switch off pauses the whole NetDaemon app via the state manager, so it is a true master
///         switch — which is exactly why it is a sound default for an unset <c>KillSwitchEntity</c>.
///     </para>
/// </remarks>
public static class NetDaemonAppSwitch
{
	private const string Prefix = "input_boolean.netdaemon_";

	/// <summary>The enable-switch entity id for <paramref name="appType"/>.</summary>
	/// <param name="appType">The <c>[NetDaemonApp]</c> class. Its <see cref="Type.FullName"/> drives the slug.</param>
	/// <returns>e.g. <c>input_boolean.netdaemon_example_net_daemon_site1_adaptive_lighting_app</c>.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="appType"/> is <c>null</c>.</exception>
	public static string EntityIdFor(Type appType)
	{
		ArgumentNullException.ThrowIfNull(appType);

		// An app may pin a short id with [NetDaemonApp(Id = "...")]; the state manager then names the enable
		// switch from that id, not the type's full name. Read it reflectively by attribute name so this engine
		// assembly need not reference NetDaemon.AppModel just to learn the app's own id.
		object? appAttribute = appType.GetCustomAttributes(inherit: false)
			.FirstOrDefault(attribute => string.Equals(attribute.GetType().Name, "NetDaemonAppAttribute", StringComparison.Ordinal));
		string? explicitId = appAttribute?.GetType().GetProperty("Id")?.GetValue(appAttribute) as string;

		return string.IsNullOrWhiteSpace(explicitId)
			? EntityIdForTypeName(appType.FullName ?? appType.Name)
			: Prefix + Slug(explicitId);
	}

	/// <summary>The enable-switch entity id for an app whose fully qualified type name is <paramref name="typeFullName"/>.</summary>
	/// <remarks>The string overload exists so the slug can be asserted against apps in assemblies a test does not reference.</remarks>
	public static string EntityIdForTypeName(string typeFullName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(typeFullName);
		return Prefix + Slug(typeFullName);
	}

	/// <summary>
	///     NetDaemon's own normalisation: '.' → '_', a '_' inserted before an uppercase that follows a lowercase or
	///     digit, everything lowercased, and runs of '_' collapsed.
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
