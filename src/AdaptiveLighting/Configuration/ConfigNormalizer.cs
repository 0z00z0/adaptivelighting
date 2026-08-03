namespace AdaptiveLighting.Configuration;

/// <summary>The save-time normaliser: drops deprecated and empty fields once they are provably redundant.</summary>
/// <remarks>
///     Runs on save only, from <see cref="Hosting.LightingEngineHost.Save"/> and before validation. The load path
///     must never rewrite a hand-edited file.
/// </remarks>
public static class ConfigNormalizer
{
	/// <summary>Mutates and returns <paramref name="config"/> in place. The drops are the intended change.</summary>
	public static AdaptiveLightingConfig Normalize(AdaptiveLightingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		GlobalConfig global = config.Global;

		// Written out as Lux, so a file passes through here on its first save and never says Either again.
		if (config.Defaults.Darkness is DarknessSource.Either)
			config.Defaults.Darkness = DarknessSource.Lux;

		foreach (AreaConfig retiring in config.Areas.Where(area => area.Darkness is DarknessSource.Either))
			retiring.Darkness = DarknessSource.Lux;

		// Drop pure-default option rows, except the designated Normal row and any row a period's SetsMode names.
		// Dropping a named row would leave that SetsMode pointing at nothing, which the validator rejects: a save
		// that unmakes itself.
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

		// Two steps, so a block that held only cleared rows can then be recognised as empty and dropped too.
		if (global.PeriodSelect is { } periodSelect)
		{
			periodSelect.Options.RemoveAll(option => option.IsEmpty);

			// Authority is not consulted: it means nothing without an entity, so a block carrying only a non-default
			// Authority is still a block saying nothing.
			if (string.IsNullOrWhiteSpace(periodSelect.Entity) && periodSelect.Options.Count == 0)
				global.PeriodSelect = null;
		}

		// Levels is null-coalesced where Areas, Periods and HouseMode.Options are dereferenced bare: those are
		// structural and the deserialiser repairs them, whereas AreaSetupService.Apply can hand Levels through as
		// null from the area it rebuilt.
		foreach (AreaConfig area in config.Areas)
		{
			area.Levels ??= [];
			area.Levels.RemoveAll(level => level.IsEmpty);
		}

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
