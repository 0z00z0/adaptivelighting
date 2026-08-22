namespace AdaptiveLighting.Configuration;

/// <summary>The save-time normaliser: drops deprecated and empty fields once they are provably redundant.</summary>
/// <remarks>Runs on write only, before validation. The load path must never rewrite a hand-edited file.</remarks>
public static class ConfigNormalizer
{
	// Counted on the calling thread, so one parallel test cannot see another's passes. Every write is synchronous
	// on its caller's thread.
	[ThreadStatic]
	internal static int Passes;

	/// <summary>Mutates and returns <paramref name="config"/> in place; the drops are the intended change.</summary>
	public static AdaptiveLightingConfig Normalize(AdaptiveLightingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		Passes++;

		GlobalConfig global = config.Global;

		// Before anything that reads a key: a period or option added since the last save has no id yet, and the
		// referenced-options scan below and the whole engine resolve by one.
		StableKeyMigration.Apply(config);

		// Written out as Lux, so a file passes through here on its first save and never says Either again.
		if (config.Defaults.Darkness is DarknessSource.Either)
			config.Defaults.Darkness = DarknessSource.Lux;

		foreach (AreaConfig retiring in config.Areas.Where(area => area.Darkness is DarknessSource.Either))
			retiring.Darkness = DarknessSource.Lux;

		// Drop pure-default option rows, except the designated Normal row and any row a period's SetsModeId names.
		// Dropping a named row would leave that SetsModeId pointing at nothing, which the validator rejects: a save
		// that unmakes itself.
		if (global.HouseMode is { } mode)
		{
			HouseModeOptionConfig? normal = mode.NormalOption;
			HashSet<string> referenced = config.Periods
				.Where(period => period.SetsModeId is { Length: > 0 })
				.Select(period => period.SetsModeId!.Trim())
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			mode.Options.RemoveAll(option =>
				!ReferenceEquals(option, normal)
				&& IsPureDefault(option)
				&& !referenced.Contains(option.Key));
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

		// Nothing reads the room list while StartsOnMotion is off, so it is cleared instead of rotting into a list of
		// rooms renamed years ago. ColorControl and HouseMode.Authority need nothing: their zero values are the old
		// behaviour, and OmitNull leaves a document that never set them untouched.
		foreach (TimePeriodConfig period in config.Periods)
		{
			if (!period.StartsOnMotion)
			{
				period.StartsOnMotionAreas.Clear();
				continue;
			}

			period.StartsOnMotionAreas =
			[
				.. period.StartsOnMotionAreas
					.Where(areaId => !string.IsNullOrWhiteSpace(areaId))
					.Select(areaId => areaId.Trim())
					.Distinct(StringComparer.Ordinal)
			];
		}

		// Levels is null-coalesced where Areas, Periods and HouseMode.Options are dereferenced bare: those are structural
		// and the deserialiser repairs them, while AreaSetupService.Apply can hand Levels through as null.
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
		&& string.IsNullOrWhiteSpace(option.ClampPeriodId)
		&& option.ActivateWhileOn.Count == 0
		&& option.ActivateAfterNoMotionMinutes is null
		&& !option.HasResetTrigger;
}
