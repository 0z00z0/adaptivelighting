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

	public static bool IsKnownId(IReadOnlyList<RegistryLabel>? knownLabels, string? stored) => Carries(knownLabels, stored);

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
