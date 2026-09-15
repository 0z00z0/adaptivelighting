using System.Reflection;

using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     Guards the pre-pass in <see cref="LightingConfigDocument"/> that renames legacy keys, reports retired ones,
///     and translates a handful of retired values, all by key name alone with no idea which type it belongs to. A
///     future property sharing one of those names would be silently renamed, reported as retired, or mistranslated
///     on every load.
/// </summary>
[TestClass]
public sealed class ConfigKeyCollisionTests
{
	/// <summary>
	///     <see cref="AreaSettings.Darkness"/> is meant to collide with <see cref="LightingConfigDocument.LegacyValues"/>:
	///     it is translated by value (<c>Either</c> becomes <c>Lux</c>), never by key rename or retirement.
	/// </summary>
	private static readonly HashSet<string> ExemptPropertyNames = new(StringComparer.Ordinal)
	{
		nameof(AreaSettings.Darkness)
	};

	[TestMethod]
	public void No_Configuration_Property_Is_Named_Like_A_Legacy_Or_Retired_Key()
	{
		HashSet<string> reservedNames = new(StringComparer.OrdinalIgnoreCase);
		reservedNames.UnionWith(LightingConfigDocument.LegacyKeys.Keys);
		reservedNames.UnionWith(LightingConfigDocument.RetiredKeys.Keys);
		reservedNames.UnionWith(LightingConfigDocument.LegacyValues.Keys);

		List<string> collisions =
		[.. ConfigurationTypes()
			.SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
			.Where(property => !ExemptPropertyNames.Contains(property.Name))
			.Where(property => reservedNames.Contains(property.Name))
			.Select(property => $"{property.DeclaringType!.Name}.{property.Name}")];

		Assert.AreEqual(0, collisions.Count,
			"these properties share a name the legacy-key, retired-key or legacy-value pass rewrites, reports or "
			+ $"translates on sight, with no idea which type it belongs to: {string.Join(", ", collisions)}. "
			+ "Rename the property, or the key it collides with.");
	}

	/// <summary>Every configuration class reachable from the document root, walked once each.</summary>
	private static IEnumerable<Type> ConfigurationTypes()
	{
		HashSet<Type> visited = [];
		Queue<Type> pending = new();
		pending.Enqueue(typeof(AdaptiveLightingConfig));

		while (pending.Count > 0)
		{
			Type type = pending.Dequeue();

			if (!visited.Add(type))
				continue;

			foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
			{
				Type elementType = ElementTypeOf(property.PropertyType);

				if (elementType.Namespace == typeof(AdaptiveLightingConfig).Namespace && elementType.IsClass)
					pending.Enqueue(elementType);
			}
		}

		return visited;
	}

	/// <summary>Unwraps <c>List&lt;T&gt;</c> and <c>Nullable&lt;T&gt;</c> so the walk reaches the element type, not the wrapper.</summary>
	private static Type ElementTypeOf(Type type)
	{
		if (Nullable.GetUnderlyingType(type) is { } underlying)
			return underlying;

		if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
			return type.GetGenericArguments()[0];

		return type;
	}
}
