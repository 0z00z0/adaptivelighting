using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>
///     What one setup run will do, itemised so the warning dialog can be concrete about losses.
/// </summary>
/// <param name="NewAreas">
///     Areas Home Assistant now qualifies that the document does not have yet, already switched off. Always
///     included: adding a room nobody has switched on cannot change what any light does.
/// </param>
/// <param name="Rebuilds">One entry per area the run will rebuild, with what that rebuild costs.</param>
/// <param name="NoLongerQualifying">
///     Area ids in the run that discovery can no longer furnish — the room lost its light or its motion sensor.
///     Reported so the dialog can say so; never removed, because removing a room stays the owner's explicit act.
/// </param>
public sealed record SetupPlan(
	IReadOnlyList<AreaConfig> NewAreas,
	IReadOnlyList<AreaRebuildPlan> Rebuilds,
	IReadOnlyList<string> NoLongerQualifying);

/// <summary>
///     One existing area's rebuild. The three counts are the three things a rebuild destroys — hand-picked
///     entities, changed settings, a custom name — which is exactly what the dialog lists.
/// </summary>
/// <param name="AreaId">The area being rebuilt. Its identity, and the one field that survives besides the switch.</param>
/// <param name="PinnedEntityCount">
///     How many entity ids the area lists instead of discovering: explicit lights, motion sensors, a lux sensor,
///     the blockers under <c>IgnoreWhenOn</c>, and the per-room exclusions under <c>ExcludeEntities</c> — all of
///     which a rebuild throws away, so all of which the warning must count.
/// </param>
/// <param name="OverrideCount">
///     How many of the sixteen per-room settings the area overrides. <c>Enabled</c> is not among them: it survives
///     the rebuild, so counting it would warn about a loss that never happens.
/// </param>
/// <param name="HasCustomName">Whether the area carries a display name of its own, which the rebuild drops.</param>
public sealed record AreaRebuildPlan(string AreaId, int PinnedEntityCount, int OverrideCount, bool HasCustomName);

/// <summary>
///     Sets areas up from what Home Assistant knows — on a first start, and again whenever the owner asks.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this is not inside the host.</b> First run and "set up rooms again" must be the same code
///         observed twice. While discovery lived inside <c>LightingEngineHost</c> the only way to re-run it was to
///         delete the document, and any UI button would have been a second implementation of the same rules —
///         which is how a warning dialog starts lying about what the rebuild actually does.
///     </para>
///     <para>
///         <b>There is deliberately no merge parameter and no preserve-list.</b> The owner chose a clear warning
///         over clever preservation: a service with one behaviour is a service whose warning is always true. Every
///         option added here would be a case the dialog has to describe and a case nobody tested.
///     </para>
///     <para>
///         <see cref="Plan"/> is pure — registry in, plan out — so the dialog can be rendered, cancelled, or
///         asserted on without anything being written. <see cref="Apply"/> mutates the in-memory document and
///         nothing else; writing stays the save path's job, which is why it is still the only write path.
///     </para>
/// </remarks>
public static class AreaSetupService
{
	/// <summary>The domain presence is read from when the document names nobody.</summary>
	private const string PersonDomain = "person";

	/// <summary>
	///     The attribute a <c>person.*</c> state carries: the device-tracker entity ids that back it. An empty or
	///     missing list means the person has no presence source, so it is never seeded.
	/// </summary>
	private const string DeviceTrackersAttribute = "device_trackers";

	/// <summary>
	///     Works out what setting up <paramref name="scope"/> again would do, without touching anything.
	/// </summary>
	/// <param name="config">The document as it stands. Not mutated.</param>
	/// <param name="registry">Source of the area list.</param>
	/// <param name="resolver">Classifies each area's entities, by exactly the rules the engine runs on.</param>
	/// <param name="scope">
	///     The area ids ticked for rebuild. Empty on a first run, where the document has no areas to rebuild;
	///     every id in the document when the owner presses "Set up rooms again"; one when they press it inside a
	///     room. Ids the document does not have are ignored — a plan describes this document, not a wish.
	/// </param>
	/// <returns>What a following <see cref="Apply"/> would do.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
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

			rebuilds.Add(new AreaRebuildPlan(
				areaId,
				PinnedEntityCount(area),
				OverrideCount(area),
				area.Name is { Length: > 0 }));

			// Ticked and no longer qualifying is still a rebuild — "ticked means rebuilt" has no exceptions, or the
			// dialog would have to describe one. It just also earns a line saying the house changed underneath it.
			if (!qualifying.Contains(areaId))
				noLongerQualifying.Add(areaId);
		}

		return new SetupPlan(newAreas, rebuilds, noLongerQualifying);
	}

	/// <summary>
	///     Carries <paramref name="plan"/> out on <paramref name="config"/>, in memory.
	/// </summary>
	/// <remarks>
	///     A rebuilt area is <i>replaced</i> by a fresh proposal rather than edited: exactly two things survive,
	///     and both because they are not discovery's output — <see cref="AreaConfig.AreaId"/>, the room's identity,
	///     and <see cref="AreaConfig.Enabled"/>, the owner's power switch. Re-tagging lights in Home Assistant must
	///     not silently switch a room off, or on. Everything else — the name, the pinned entity lists, the setting
	///     overrides — is what the dialog warned about, and it goes.
	///     <para>
	///         Nothing is written and nothing is removed. Areas outside the plan keep their exact instance, so a
	///         document that had no rebuilds serialises byte for byte as it was.
	///     </para>
	///     <para>
	///         <b>A room the document already has is never added again.</b> A plan is a value somebody holds
	///         across an edit — the Areas page keeps the setup panel open beside its own "Add a room" and "Discard
	///         changes" buttons, and a confirmation can be delivered twice — so the document being mutated is not
	///         always the one <see cref="Plan"/> read. Two rows for one Home Assistant area is not a cosmetic
	///         duplicate: it either refuses every save (the validator rejects a duplicate area name) or, once one
	///         row carries a name of its own, runs two state machines against the same lights. So the check
	///         <see cref="Plan"/> already makes is made again here, against the document actually in hand, which
	///         also makes applying the same plan twice the same document as applying it once.
	///     </para>
	/// </remarks>
	/// <param name="config">The document to mutate.</param>
	/// <param name="plan">The plan, from <see cref="Plan"/> against this same document.</param>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static void Apply(AdaptiveLightingConfig config, SetupPlan plan)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(plan);

		HashSet<string> rebuilding = new(plan.Rebuilds.Select(rebuild => rebuild.AreaId), StringComparer.Ordinal);

		// By index, so an area keeps its place in the document and a duplicated area id rebuilds both rows rather
		// than only the first one a search would find.
		for (int index = 0; index < config.Areas.Count; index++)
		{
			AreaConfig existing = config.Areas[index];

			if (existing.AreaId is not { Length: > 0 } areaId || !rebuilding.Contains(areaId))
				continue;

			AreaConfig fresh = new() { AreaId = areaId, Enabled = existing.Enabled };
			AreaAutoDiscovery.ApplyRole(fresh);

			config.Areas[index] = fresh;
		}

		// Grown as it goes, so a registry that named an area twice cannot slip two rows past it either.
		HashSet<string> present = new(AreaIdsOf(config), StringComparer.Ordinal);

		foreach (AreaConfig added in plan.NewAreas)
			if (added.AreaId is not { Length: > 0 } areaId || present.Add(areaId))
				config.Areas.Add(added);
	}

	/// <summary>
	///     Names the people Home Assistant knows in <c>Global.Persons</c>, but only while nobody is named yet.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Called on a first setup and never on a re-run, and it guards the empty case itself so both halves of
	///         that rule hold: a household that deliberately empties the list must find it still empty next start,
	///         the same principle as the one-way discovery flag.
	///     </para>
	///     <para>
	///         The trade-off, stated honestly rather than hidden: an empty list means "everyone, forever, including
	///         the person added next year", and an explicit list freezes membership. Seeding it anyway is the
	///         requirement, because a non-technical owner should be able to <i>see</i> who decides Home and Away —
	///         and remove the car tracker — instead of trusting a rule they cannot read.
	///     </para>
	///     <para>
	///         Only a person <i>with a device tracker</i> is seeded. A <c>person.*</c> entity with no tracker can
	///         never resolve to home or away — it is not a presence source at all — so it is dead weight in the
	///         Home/Away calculation and, having no friendly name, renders as its raw entity id in the UI. A live
	///         house carried exactly such a stray (<c>person.espen</c>, <c>unavailable</c>, no trackers) beside the
	///         two real people; the tracker filter keeps it out.
	///     </para>
	/// </remarks>
	/// <param name="config">The document to seed. Only <c>Global.Persons</c> is touched.</param>
	/// <param name="ha">Where the <c>person.*</c> entities are read from.</param>
	/// <returns>
	///     The ids written, in entity-id order. Empty when the list was already non-empty, or HA knows nobody with
	///     a device tracker.
	/// </returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
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

	/// <summary>Every area id the document names, skipping rows that name none.</summary>
	private static IEnumerable<string> AreaIdsOf(AdaptiveLightingConfig config) =>
		config.Areas
			.Select(area => area.AreaId)
			.Where(areaId => areaId is { Length: > 0 })
			.Select(areaId => areaId!);

	/// <summary>How many entity ids the area lists instead of discovering them, plus the ids it excludes from discovery.</summary>
	private static int PinnedEntityCount(AreaConfig area) =>
		(area.Lights?.Count ?? 0)
		+ (area.MotionSensors?.Count ?? 0)
		+ (area.LuxSensor is { Length: > 0 } ? 1 : 0)
		+ (area.IgnoreWhenOn?.Count ?? 0)
		+ (area.ExcludeEntities?.Count ?? 0);

	/// <summary>
	///     How many of the twenty-one per-room settings the area overrides.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Spelled out rather than reflected over, so that a setting has to be named to be counted and the
	///         list can be read against the model. <c>Enabled</c> is deliberately absent — it survives the rebuild.
	///     </para>
	///     <para>
	///         <b>Public, and the only copy, because the previous arrangement drifted.</b> The editor kept its own
	///         spelled-out twin on the theory that two lists read side by side would be kept in step; when the five
	///         daylight-brightness settings arrived only this one was updated, so a room tuned solely through them
	///         reported "all automatic" while the re-setup dialog correctly counted five. Both surfaces now ask
	///         here. A test pins this count against the model by reflection, so a setting added without being
	///         named fails loudly rather than going quietly uncounted.
	///     </para>
	/// </remarks>
	/// <param name="area">The room to count.</param>
	/// <exception cref="ArgumentNullException"><paramref name="area"/> is <c>null</c>.</exception>
	public static int OverrideCount(AreaConfig area) =>
		(area.VacancyTimeoutSeconds is not null ? 1 : 0) + (area.PreOffSeconds is not null ? 1 : 0)
		+ (area.PreOffBrightnessFactor is not null ? 1 : 0) + (area.OverrideDurationMinutes is not null ? 1 : 0)
		+ (area.VacancyResetMinutes is not null ? 1 : 0) + (area.Darkness is not null ? 1 : 0)
		+ (area.LuxThreshold is not null ? 1 : 0) + (area.LuxHysteresis is not null ? 1 : 0)
		+ (area.LuxBrightnessEnabled is not null ? 1 : 0) + (area.LuxBrightnessStartLux is not null ? 1 : 0)
		+ (area.LuxBrightnessFullLux is not null ? 1 : 0) + (area.LuxBrightnessMaxPct is not null ? 1 : 0)
		+ (area.LuxBrightnessGamma is not null ? 1 : 0)
		+ (area.SunElevationThreshold is not null ? 1 : 0) + (area.SunEntity is not null ? 1 : 0)
		+ (area.DayTransitionSeconds is not null ? 1 : 0) + (area.NightTransitionSeconds is not null ? 1 : 0)
		+ (area.RespectSleepMode is not null ? 1 : 0) + (area.SleepBlocksAutoOn is not null ? 1 : 0)
		+ (area.SkipAwaySweep is not null ? 1 : 0) + (area.WelcomeHome is not null ? 1 : 0);
}
