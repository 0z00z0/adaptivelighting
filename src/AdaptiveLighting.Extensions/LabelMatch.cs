namespace Adaptive.Engine;

/// <summary>A Home Assistant label: its id is stored and matched, its name is shown.</summary>
public sealed record RegistryLabel(string Id, string Name);

/// <summary>Whether a stored label id matches a label Home Assistant reports. Ids only, never a name.</summary>
public static class LabelMatch
{
	public static bool Carries(IReadOnlyList<RegistryLabel>? carried, string? stored)
	{
		if (carried is null || stored is not { Length: > 0 })
			return false;

		foreach (RegistryLabel label in carried)
			if (string.Equals(label.Id, stored, StringComparison.OrdinalIgnoreCase))
				return true;

		return false;
	}

	public static T? WithId<T>(IEnumerable<T> labels, Func<T, string> idOf, string? stored)
		where T : class
	{
		ArgumentNullException.ThrowIfNull(labels);
		ArgumentNullException.ThrowIfNull(idOf);

		return stored is { Length: > 0 } ? labels.FirstOrDefault(label => string.Equals(idOf(label), stored, StringComparison.Ordinal)) : null;
	}

	/// <summary>The label's name; the stored value while no label is known, as when the registry is unreadable; else <c>null</c>.</summary>
	public static string? ShownName<T>(IReadOnlyCollection<T> labels, Func<T, string> idOf, Func<T, string> nameOf, string stored)
		where T : class
	{
		ArgumentNullException.ThrowIfNull(labels);
		ArgumentNullException.ThrowIfNull(nameOf);

		if (labels.Count == 0)
			return stored;

		return WithId(labels, idOf, stored) is { } label ? nameOf(label) : null;
	}

	/// <summary>The name to show for <paramref name="stored"/>, falling back to the stored value itself.</summary>
	public static string DisplayName(IReadOnlyList<RegistryLabel>? knownLabels, string stored)
	{
		if (knownLabels is not null)
			foreach (RegistryLabel label in knownLabels)
				if (string.Equals(label.Id, stored, StringComparison.OrdinalIgnoreCase))
					return label.Name;

		return stored;
	}
}
