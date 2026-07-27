using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     One room's line in the re-setup warning: what it is called, what the rebuild costs it, and anything the
///     house has changed underneath it.
/// </summary>
/// <param name="Name">The room as the page calls it now — its custom name, else its area id.</param>
/// <param name="Consequence">What this room loses, counted. Never generic: a room with nothing to lose says so.</param>
/// <param name="Note">
///     An extra fact about the room, or <c>null</c>. Today that is only "Home Assistant no longer shows a light
///     and a motion sensor here" — a ticked room is rebuilt either way, so this is a report, not a refusal.
/// </param>
public sealed record SetupWarningLine(string Name, string Consequence, string? Note);

/// <summary>
///     Turns a <see cref="SetupPlan"/> into the words the re-setup warning shows.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this is not written in the markup.</b> The dialog's promise is that its per-room lines are
///         computed rather than generic — "are you sure?" without stakes is noise, and a warning that
///         under-counts is worse than none. Kept out of the Razor file, the counting can be asserted directly
///         against a plan, which is the only way this repo can test it at all.
///     </para>
///     <para>
///         The three things counted are the three things <c>AreaSetupService.Apply</c> destroys — a custom name,
///         hand-picked entities, changed settings — and they come from the plan itself rather than from a second
///         reading of the document, so the warning cannot describe a rebuild other than the one about to happen.
///     </para>
/// </remarks>
public static class SetupWarning
{
	/// <summary>The dialog's question, sized to what the plan actually does.</summary>
	/// <param name="plan">The plan being confirmed.</param>
	/// <exception cref="ArgumentNullException"><paramref name="plan"/> is <c>null</c>.</exception>
	public static string Title(SetupPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);

		if (plan.Rebuilds.Count > 0)
			return $"Set up {Count(plan.Rebuilds.Count, "room")} again?";

		return plan.NewAreas.Count > 0
			? $"Add {Count(plan.NewAreas.Count, "new room")}?"
			: "Nothing to set up";
	}

	/// <summary>
	///     One line per room the plan rebuilds, in the plan's own order.
	/// </summary>
	/// <remarks>
	///     One line per <i>row</i>, because that is what the plan is: <see cref="AreaSetupService.Plan"/> emits an
	///     <see cref="AreaRebuildPlan"/> for every row it will rebuild, and a document can carry two rows for one
	///     Home Assistant area. So each line takes the row at its own position among the rows sharing that id, and
	///     not the first row a search for the id finds.
	/// </remarks>
	/// <param name="plan">The plan being confirmed.</param>
	/// <param name="config">The document the plan was made against, for the names and the custom name it quotes.</param>
	/// <returns>The lines, empty when nothing is being rebuilt.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static IReadOnlyList<SetupWarningLine> Lines(SetupPlan plan, AdaptiveLightingConfig config)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(config);

		HashSet<string> stopped = new(plan.NoLongerQualifying, StringComparer.Ordinal);
		List<SetupWarningLine> lines = [];

		// Resolved by position among the rows sharing an id, never by first match on it. Plan emits one rebuild
		// per row in the document's own order, so the nth rebuild for an id is the nth row carrying it — whereas a
		// search would hand every rebuild the first such row, and a second row would be warned about under the
		// first one's custom name. A document that has drifted from the plan runs out of rows and the line falls
		// back to the area id, which is honest; quoting some other room's name is not.
		ILookup<string, AreaConfig> rows = config.Areas
			.Where(candidate => candidate.AreaId is { Length: > 0 })
			.ToLookup(candidate => candidate.AreaId!, StringComparer.Ordinal);

		Dictionary<string, int> taken = new(StringComparer.Ordinal);

		foreach (AreaRebuildPlan rebuild in plan.Rebuilds)
		{
			int position = taken.GetValueOrDefault(rebuild.AreaId);
			taken[rebuild.AreaId] = position + 1;

			AreaConfig? area = rows[rebuild.AreaId].ElementAtOrDefault(position);

			lines.Add(new SetupWarningLine(
				area?.DisplayName ?? rebuild.AreaId,
				Consequence(rebuild, area?.Name),
				stopped.Contains(rebuild.AreaId)
					? "Home Assistant no longer shows both a light and a motion sensor here."
					: null));
		}

		return lines;
	}

	/// <summary>
	///     The sentence about rooms the run adds, or <c>null</c> when it adds none.
	/// </summary>
	/// <param name="plan">The plan being confirmed.</param>
	/// <param name="nameOf">
	///     Resolves an area id to the name Home Assistant shows. Optional — without it the ids are named, which
	///     is what a house whose registry has stopped answering would see anyway.
	/// </param>
	/// <exception cref="ArgumentNullException"><paramref name="plan"/> is <c>null</c>.</exception>
	public static string? NewRooms(SetupPlan plan, Func<string, string>? nameOf)
	{
		ArgumentNullException.ThrowIfNull(plan);

		if (plan.NewAreas.Count == 0)
			return null;

		IEnumerable<string> names = plan.NewAreas
			.Select(area => area.AreaId ?? string.Empty)
			.Where(areaId => areaId.Length > 0)
			.Select(areaId => nameOf?.Invoke(areaId) ?? areaId);

		return $"{Count(plan.NewAreas.Count, "new room")} will be added, switched off: {string.Join(", ", names)}.";
	}

	/// <summary>What one rebuild costs, counted — or that it costs nothing, said out loud.</summary>
	private static string Consequence(AreaRebuildPlan rebuild, string? customName)
	{
		List<string> losses = [];

		if (rebuild.HasCustomName)
		{
			losses.Add(customName is { Length: > 0 } name
				? $"its custom name (“{name}”)"
				: "its custom name");
		}

		if (rebuild.PinnedEntityCount > 0)
			losses.Add($"{Count(rebuild.PinnedEntityCount, "hand-picked entity", "hand-picked entities")}");

		if (rebuild.OverrideCount > 0)
			losses.Add($"{Count(rebuild.OverrideCount, "changed setting")}");

		return losses.Count == 0
			? "nothing to lose — it is rebuilt from what Home Assistant knows now"
			: $"loses {Join(losses)}";
	}

	/// <summary>"a", "a and b", "a, b and c" — a list a person reads rather than parses.</summary>
	private static string Join(IReadOnlyList<string> parts) => parts.Count switch
	{
		1 => parts[0],
		2 => $"{parts[0]} and {parts[1]}",
		_ => $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}"
	};

	private static string Count(int count, string singular, string? plural = null) =>
		$"{count} {(count == 1 ? singular : plural ?? singular + "s")}";
}
