using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>
///     Proposes a starting set of zones by asking the Home Assistant area registry which rooms could plausibly be
///     lit automatically.
/// </summary>
/// <remarks>
///     <para>
///         This exists because the alternative was worse. A shipped example full of placeholder ids reads as
///         helpful and behaves as sabotage: every id is one Home Assistant does not know, so a new installation
///         starts refusing to run and the owner's first experience of the system is a list of errors about rooms
///         that were never theirs. Starting empty is honest but useless. Starting empty and then <i>looking</i> is
///         both.
///     </para>
///     <para>
///         The test for "could this room be lit automatically" is deliberately strict: it needs <b>at least one
///         light and at least one motion sensor</b>. A room with lights but nothing to sense presence cannot
///         participate in motion-driven lighting, and a room with motion but no lights has nothing to offer. Every
///         other area — a cupboard with a temperature probe, a "system" area holding a router — is left alone.
///         Being conservative matters more than being complete: a missed room is one the owner adds in a moment
///         from the UI, whereas an unwanted room is lights coming on in a bedroom at 03:00.
///     </para>
///     <para>
///         Only <see cref="ZoneConfig.AreaId"/> is set. Everything else — which lights, which sensors, the display
///         name — resolves from the area at run time, so a proposal stays true across a rename and the document
///         stays small enough to read.
///     </para>
/// </remarks>
public static class ZoneAutoDiscovery
{
	/// <summary>
	///     The zones worth proposing for this instance, in registry order.
	/// </summary>
	/// <param name="registry">Source of the area list.</param>
	/// <param name="resolver">Classifies each area's entities, applying the same exclusions real discovery uses.</param>
	/// <returns>A zone per qualifying area, naming only its area id. Empty when nothing qualifies.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public static IReadOnlyList<ZoneConfig> Propose(IAreaRegistry registry, ZoneEntityResolver resolver)
	{
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(resolver);

		List<ZoneConfig> proposed = [];

		foreach (string areaId in registry.AreaIds)
		{
			if (string.IsNullOrWhiteSpace(areaId))
				continue;

			// The same resolver the engine uses at run time, so a proposed zone is one that will actually resolve —
			// group members and excluded entities are already filtered out of these counts.
			AreaDiscovery found = resolver.DiscoverArea(areaId);

			if (found.Lights.Count > 0 && found.MotionSensors.Count > 0)
				proposed.Add(new ZoneConfig { AreaId = areaId });
		}

		return proposed;
	}
}
