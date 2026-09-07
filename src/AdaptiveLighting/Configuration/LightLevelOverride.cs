using YamlDotNet.Serialization;

namespace AdaptiveLighting.Configuration;

/// <summary>What one light in a room does instead of the room, period by period.</summary>
/// <remarks>
///     A level belongs to one light and never to a group: a group is only a way of reaching lights, so a bulb in
///     two groups holds one value and shows it under both. A period this light omits follows the room, and a value
///     it omits within a period follows the room too; see <see cref="LightLevelMerge"/> for the whole rule.
/// </remarks>
public class LightLevelOverride
{
	/// <summary>The light these levels belong to, by Home Assistant entity id, at the bottom of any group.</summary>
	public string EntityId { get; set; } = "";

	/// <summary>What this light runs instead of its room, period by period.</summary>
	public List<RoomLevelOverride> Levels { get; set; } = [];

	/// <summary>Whether this entry says anything, so <see cref="ConfigNormalizer"/> can drop it on save.</summary>
	[YamlIgnore]
	public bool IsEmpty => Levels is null || Levels.Count == 0 || Levels.TrueForAll(level => level.IsEmpty);
}
