namespace AdaptiveLighting.Configuration;

/// <summary>
///     The pure save-time normaliser: drops deprecated fields once they are provably redundant, so a document
///     that adopts the house-mode model stops carrying inert legacy keys — but never before that, because
///     dropping a field a live path still needs is exactly the silent break this project was bitten by.
/// </summary>
/// <remarks>
///     Applied by <see cref="Hosting.LightingEngineHost.Save"/> before validation. The startup load path does
///     <b>not</b> normalise: a hand-edited file must never be rewritten by the act of booting.
/// </remarks>
public static class ConfigNormalizer
{
	/// <summary>
	///     Returns a document with redundant deprecated fields dropped. Mutates and returns <paramref name="config"/>
	///     in place — the caller passes the object it is about to serialise, and the drops are the intended change.
	/// </summary>
	/// <param name="config">The document to normalise.</param>
	/// <returns>The same instance, normalised.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="config"/> is <c>null</c>.</exception>
	public static AdaptiveLightingConfig Normalize(AdaptiveLightingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		GlobalConfig global = config.Global;

		// Drop pure-default option rows (Kind: Normal, no scene/clamp/reset) EXCEPT the designated Normal row, so
		// the document stays minimal but the single reset target stays explicit (09 §6). A row a period's SetsMode
		// names is kept whatever it carries: dropping it would leave that SetsMode pointing at no option, which the
		// validator then rejects — a save that unmakes itself.
		if (global.HouseMode is { } mode)
		{
			HouseModeOptionConfig? normal = mode.NormalOption;
			HashSet<string> referenced = config.Periods
				.Where(period => period.SetsMode is { Length: > 0 })
				.Select(period => period.SetsMode!.Trim())
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			mode.Options.RemoveAll(option =>
				!ReferenceEquals(option, normal)
				&& IsPureDefault(option)
				&& !(option.Value is { Length: > 0 } value && referenced.Contains(value.Trim())));
		}

		// Drop an empty HouseMode so a never-adopted document acquires no HouseMode: block.
		if (global.HouseMode is { } houseMode
			&& string.IsNullOrWhiteSpace(houseMode.Entity)
			&& houseMode.Options.Count == 0)
			global.HouseMode = null;

		// A levels row with neither value set says nothing at all, and an editor that draws a row per period
		// produces one the moment somebody clears both fields. Dropped on save so the file records only the
		// periods a room actually disagrees about — which is also what makes "this room has levels" a question the
		// document can answer by looking. The engine ignores an empty row either way, so this is tidying, not a
		// behaviour change, and it happens on save alone: a hand-edited file is never rewritten by booting.
		foreach (AreaConfig area in config.Areas)
			area.Levels.RemoveAll(level => level.IsEmpty);

		return config;
	}

	/// <summary>A row that carries nothing but its value: Normal kind, no scene, no clamp, no reset trigger, no activation list.</summary>
	private static bool IsPureDefault(HouseModeOptionConfig option) =>
		option.Kind == ModeKind.Normal
		&& string.IsNullOrWhiteSpace(option.Scene)
		&& string.IsNullOrWhiteSpace(option.ClampPeriod)
		&& option.ActivateWhileOn.Count == 0
		&& option.ActivateAfterNoMotionMinutes is null
		&& !option.HasResetTrigger;
}
