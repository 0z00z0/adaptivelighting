namespace AdaptiveLighting.Configuration;

/// <summary>
///     What to call a stored row whose Home Assistant dropdown no longer offers its value.
/// </summary>
/// <remarks>
///     The house mode and the period select both have this row, and both the validator and the two screens
///     described it. Four wordings, and two of them guessed differently at the cause: one badge read "renamed in
///     Home Assistant", the other "removed from the helper". Nothing here can tell those apart — a rename is a
///     removal and an addition — so none of them says.
///     <para>
///         The consequence stays with the caller. Losing a period mapping and losing a mode's reset triggers are
///         different losses and the household wants to be told which.
///     </para>
/// </remarks>
public static class HelperOrphan
{
	/// <summary>The badge on a row the helper no longer offers.</summary>
	public const string Badge = "not in the helper";

	/// <summary>The badge's title, and what the row means wherever there is room to say it.</summary>
	public const string Explanation =
		"Home Assistant's dropdown no longer offers this value. It was renamed or removed — from here the two "
		+ "look the same.";

	/// <summary>Opens a warning about one: <c>'Natt' is no longer one of the helper's options</c>.</summary>
	public static string NoLongerOffered(string? value) =>
		$"'{value?.Trim()}' is no longer one of the helper's options";
}
