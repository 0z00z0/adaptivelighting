using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Hosting;

/// <summary>What Home Assistant knows at one moment, as the validator's referential checks need it.</summary>
/// <remarks>
///     A <c>null</c> member means Home Assistant could not be asked, which the validator reads as "skip that
///     check". It never means "nothing exists", so a member that is genuinely empty is an empty collection.
/// </remarks>
internal sealed record HouseFacts(
	IReadOnlyCollection<string>? EntityIds,
	IReadOnlyCollection<string>? AreaIds,
	IReadOnlyCollection<string>? LabelsInUse,
	IReadOnlyCollection<string>? HouseModeOptions,
	IReadOnlyCollection<string>? PeriodSelectOptions,
	IReadOnlyCollection<string>? LabelIds)
{
	/// <summary>Nothing is connected, so every referential check is skipped.</summary>
	public static HouseFacts Unknown { get; } = new(null, null, null, null, null, null);

	/// <summary>Reads everything the validator asks about, for the two selects <paramref name="config"/> names.</summary>
	public static HouseFacts Read(IHaContext? ha, IHaRegistry? registry, AdaptiveLightingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		if (ha is null && registry is null)
			return Unknown;

		// The two selects are different helpers, so their live options are read separately.
		return new HouseFacts(
			ReadEntityIds(ha),
			ReadAreaIds(registry),
			ReadLabelsInUse(registry),
			ReadSelectOptions(ha, config.Global.HouseMode?.Entity),
			ReadSelectOptions(ha, config.Global.PeriodSelect?.EntityId),
			ReadLabelIds(registry));
	}

	/// <summary>Every label id the house has, carried or not.</summary>
	/// <remarks>Apart from <see cref="LabelsInUse"/>, which mixes both forms: only an id set can tell a stored id from a stored name.</remarks>
	private static IReadOnlyCollection<string>? ReadLabelIds(IHaRegistry? registry)
	{
		if (registry is null)
			return null;

		try
		{
			return [.. registry.KnownLabels().Select(label => label.Id)];
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}

	private static IReadOnlyCollection<string>? ReadEntityIds(IHaContext? ha)
	{
		if (ha is null)
			return null;

		try
		{
			return [.. ha.GetAllEntities().Select(entity => entity.EntityId)];
		}
		catch (InvalidOperationException)
		{
			// NetDaemon's state cache throws until its first connection to HA completes.
			return null;
		}
	}

	private static IReadOnlyCollection<string>? ReadAreaIds(IHaRegistry? registry)
	{
		if (registry is null)
			return null;

		try
		{
			return registry.AreaIds();
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}

	/// <summary>Every label at least one entity carries, by id and by name.</summary>
	/// <remarks>
	///     Both forms, because <see cref="AdaptiveLighting.Extensions.RegistryExtensions.LabelsOf"/> matches either
	///     way. Labels nobody carries are left out: one on no entity filters every light out as thoroughly as a typo.
	/// </remarks>
	private static IReadOnlyCollection<string>? ReadLabelsInUse(IHaRegistry? registry)
	{
		if (registry is null)
			return null;

		try
		{
			return
			[
				.. registry.Labels
					.Where(label => label.Entities.Any())
					.SelectMany(label => new[] { label.Id, label.Name })
					.OfType<string>()
					.Where(value => value.Length > 0)
			];
		}
		catch (InvalidOperationException)
		{
			// NetDaemon's registry throws until its first connection to HA completes.
			return null;
		}
	}

	/// <summary>The live <c>options</c> of a select, or <c>null</c> when there is no select, no connection, or no readable attribute.</summary>
	private static IReadOnlyCollection<string>? ReadSelectOptions(IHaContext? ha, string? entityId)
	{
		if (ha is null || entityId is not { Length: > 0 })
			return null;

		try
		{
			IReadOnlyList<string> options = ha.GetState(entityId).AttrStringList("options");

			return options.Count > 0 ? options : null;
		}
		catch (InvalidOperationException)
		{
			// NetDaemon's state cache throws until its first connection to HA completes.
			return null;
		}
	}
}
