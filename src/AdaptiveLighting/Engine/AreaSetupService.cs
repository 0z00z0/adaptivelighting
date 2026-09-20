using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>What one setup run will do, itemised so the warning dialog can be concrete about losses.</summary>
// NoLongerQualifying rooms are reported and never removed; removing a room stays the owner's act.
public sealed record SetupPlan(
	IReadOnlyList<AreaConfig> NewAreas,
	IReadOnlyList<AreaRebuildPlan> Rebuilds,
	IReadOnlyList<string> NoLongerQualifying)
{
	/// <summary>Areas Home Assistant reports that no room here names, each with why discovery leaves it out.</summary>
	// Never removed from Home Assistant's own answer, unlike NewAreas: an area with no light still exists there.
	public IReadOnlyList<SkippedArea> NotQualifying { get; init; } = [];
}

/// <summary>One area a "set up rooms again" run will not add, and why.</summary>
public sealed record SkippedArea(string AreaId, string Reason);

/// <summary>One existing area's rebuild; the three counts are what a rebuild destroys.</summary>
public sealed record AreaRebuildPlan(string AreaId, int PinnedEntityCount, int OverrideCount, bool HasCustomName)
{
	/// <summary>Why discovery no longer proposes this area, set only when a rebuild would drop it.</summary>
	public string? SkipReason { get; init; }
}

/// <summary>Sets areas up from what Home Assistant knows, on a first start and again whenever the owner asks.</summary>
// One behaviour, no merge parameter and no preserve-list, so the warning dialog is always true. Plan is pure:
// registry in, plan out, nothing written. Apply mutates the in-memory document only; writing stays the save
// path's job.
public static class AreaSetupService
{
	private const string PersonDomain = "person";

	// The device-tracker entity ids backing a person. Empty or missing means no presence source at all.
	private const string DeviceTrackersAttribute = "device_trackers";

	// scope is the area ids ticked for rebuild; ids the document lacks are ignored.
	/// <summary>Works out what setting up <paramref name="scope"/> again would do, without touching anything.</summary>
	public static SetupPlan Plan(
		AdaptiveLightingConfig config,
		IAreaRegistry registry,
		AreaEntityResolver resolver,
		IReadOnlyCollection<string> scope)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(resolver);
		ArgumentNullException.ThrowIfNull(scope);

		IReadOnlyList<AreaConfig> proposed = AreaAutoDiscovery.Propose(registry, resolver);

		HashSet<string> qualifying = new(proposed.Select(area => area.AreaId!), StringComparer.Ordinal);
		HashSet<string> alreadyConfigured = new(AreaIdsOf(config), StringComparer.Ordinal);
		HashSet<string> ticked = new(scope, StringComparer.Ordinal);

		List<AreaConfig> newAreas = [.. proposed.Where(area => !alreadyConfigured.Contains(area.AreaId!))];

		List<AreaRebuildPlan> rebuilds = [];
		List<string> noLongerQualifying = [];

		foreach (AreaConfig area in config.Areas)
		{
			// An area with no area id has nothing for discovery to rebuild it from, so it can never be ticked.
			if (area.AreaId is not { Length: > 0 } areaId || !ticked.Contains(areaId))
				continue;

			// Ticked and no longer qualifying is still a rebuild. "Ticked means rebuilt" has no exceptions.
			string? skipReason = qualifying.Contains(areaId) ? null : resolver.ExplainWhyNotARoom(areaId);

			rebuilds.Add(new AreaRebuildPlan(
				areaId,
				PinnedEntityCount(area),
				OverrideCount(area),
				area.Name is { Length: > 0 })
			{
				SkipReason = skipReason
			});

			if (skipReason is not null)
				noLongerQualifying.Add(areaId);
		}

		// Every area Home Assistant reports that is neither a room here nor about to become one, in a fixed order.
		List<SkippedArea> notQualifying = [.. registry.AreaIds
			.Where(id => !string.IsNullOrWhiteSpace(id))
			.Where(id => !alreadyConfigured.Contains(id) && !qualifying.Contains(id))
			.Order(StringComparer.Ordinal)
			.Select(id => new SkippedArea(id, resolver.ExplainWhyNotARoom(id)))];

		return new SetupPlan(newAreas, rebuilds, noLongerQualifying) { NotQualifying = notQualifying };
	}

	/// <summary>Carries <paramref name="plan"/> out on <paramref name="config"/>, in memory.</summary>
	// A rebuilt area is replaced by a fresh proposal. AreaId, Enabled, Levels and LightLevels survive because
	// discovery does not produce them. Areas outside the plan keep their instance, so a document with no rebuilds
	// serialises unchanged. The document may be newer than the one Plan read, so the duplicate check runs again.
	public static void Apply(AdaptiveLightingConfig config, SetupPlan plan)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(plan);

		HashSet<string> rebuilding = new(plan.Rebuilds.Select(rebuild => rebuild.AreaId), StringComparer.Ordinal);

		// By index, so an area keeps its place and a duplicated area id rebuilds both rows, not just the first.
		for (int index = 0; index < config.Areas.Count; index++)
		{
			AreaConfig existing = config.Areas[index];

			if (existing.AreaId is not { Length: > 0 } areaId || !rebuilding.Contains(areaId))
				continue;

			// Enabled, Levels and LightLevels survive, which is why none of them is counted as a loss.
			AreaConfig fresh = new()
			{
				AreaId = areaId,
				Enabled = existing.Enabled,
				Levels = existing.Levels,
				LightLevels = existing.LightLevels
			};
			AreaAutoDiscovery.ApplyRole(fresh);

			config.Areas[index] = fresh;
		}

		// Grown as it goes, so a registry naming an area twice cannot slip two rows past it either.
		HashSet<string> present = new(AreaIdsOf(config), StringComparer.Ordinal);

		foreach (AreaConfig added in plan.NewAreas)
			if (added.AreaId is not { Length: > 0 } areaId || present.Add(areaId))
				config.Areas.Add(added);
	}

	// Seeding freezes membership where an empty list means everyone, so a household that empties the list must
	// find it still empty next start. Only a person with a device tracker is seeded; one without can never
	// resolve to home or away.
	/// <summary>Names the people Home Assistant knows in <c>Global.Persons</c>, while nobody is named yet.</summary>
	/// <returns>The ids written, in entity-id order. Empty when the list was already non-empty.</returns>
	public static IReadOnlyList<string> SeedPersons(AdaptiveLightingConfig config, IHaContext ha)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(ha);

		if (config.Global.Persons.Count > 0)
			return [];

		List<string> persons = [.. ha.GetAllEntities()
			.Select(entity => entity.EntityId)
			.Where(entityId => entityId.HasDomain(PersonDomain))
			.Where(entityId => ha.AttrStringList(entityId, DeviceTrackersAttribute).Count > 0)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)];

		if (persons.Count == 0)
			return [];

		config.Global.Persons = persons;
		return persons;
	}

	private static IEnumerable<string> AreaIdsOf(AdaptiveLightingConfig config) =>
		config.Areas
			.Select(area => area.AreaId)
			.Where(areaId => areaId is { Length: > 0 })
			.Select(areaId => areaId!);

	// FollowOutdoorLux counts as a pinned entity because it answers the same question LuxSensor does; counted as a
	// setting, a room could report overriding more settings than the model has.
	private static int PinnedEntityCount(AreaConfig area) =>
		(area.Lights?.Count ?? 0)
		+ (area.MotionSensors?.Count ?? 0)
		+ (area.LeadInSensors?.Count ?? 0)
		+ (area.LuxSensor is { Length: > 0 } ? 1 : 0)
		+ (area.FollowOutdoorLux is not null ? 1 : 0)
		+ (area.DaylightSensor is { Length: > 0 } ? 1 : 0)
		+ (area.IgnoreWhenOn?.Count ?? 0)
		+ (area.IgnoreWhenOnInverted is not null ? 1 : 0)
		+ (area.KeepLitWhenOn?.Count ?? 0)
		+ (area.KeepLitWhenOnInverted is not null ? 1 : 0)
		+ (area.SceneOnMotion is { Length: > 0 } ? 1 : 0)
		+ (area.SceneWhenEmpty is { Length: > 0 } ? 1 : 0)
		+ (area.TreatAutomationsAsManual is not null ? 1 : 0)
		+ (area.ExcludeEntities?.Count ?? 0);

	// The only copy of this count, pinned against the model by a reflection test. Enabled and Levels are absent
	// because both survive a rebuild.
	public static int OverrideCount(AreaConfig area) =>
		(area.VacancyTimeoutSeconds is not null ? 1 : 0) + (area.PreOffSeconds is not null ? 1 : 0)
		+ (area.PreOffBrightnessFactor is not null ? 1 : 0) + (area.OverrideDurationMinutes is not null ? 1 : 0)
		+ (area.OverrideUntilVacant is not null ? 1 : 0)
		+ (area.VacancyResetMinutes is not null ? 1 : 0) + (area.Darkness is not null ? 1 : 0)
		+ (area.ColorControl is not null ? 1 : 0)
		+ (area.LuxThreshold is not null ? 1 : 0) + (area.LuxHysteresis is not null ? 1 : 0)
		+ (area.LuxBrightnessStartLux is not null ? 1 : 0) + (area.LuxBrightnessFullLux is not null ? 1 : 0)
		+ (area.LuxBrightnessMinPct is not null ? 1 : 0) + (area.LuxBrightnessMaxPct is not null ? 1 : 0)
		+ (area.LuxBrightnessGamma is not null ? 1 : 0)
		+ (area.SunElevationThreshold is not null ? 1 : 0)
		+ (area.DayTransitionSeconds is not null ? 1 : 0) + (area.NightTransitionSeconds is not null ? 1 : 0)
		+ (area.RespectSleepMode is not null ? 1 : 0) + (area.SleepBlocksAutoOn is not null ? 1 : 0)
		+ (area.SkipAwaySweep is not null ? 1 : 0) + (area.WelcomeHome is not null ? 1 : 0);
}
