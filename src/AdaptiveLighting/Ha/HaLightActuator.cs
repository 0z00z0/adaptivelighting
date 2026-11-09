using System.Globalization;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Ha;

/// <summary>Applies light commands by calling <c>light.turn_on</c> / <c>light.turn_off</c>.</summary>
/// <remarks>
///     Commands the light already satisfies are suppressed: every circadian tick would otherwise re-send the same
///     levels to every light in the house, and a light told to fade to where it already is visibly restarts the fade.
/// </remarks>
internal sealed class HaLightActuator : ILightActuator
{
	private const string LightDomain = "light";
	private const string SceneDomain = "scene";
	private const string TurnOnService = "turn_on";
	private const string TurnOffService = "turn_off";

	private const string BrightnessPctKey = "brightness_pct";
	private const string ColorTempKelvinKey = "color_temp_kelvin";
	private const string TransitionKey = "transition";
	private const string RgbwColorKey = LightAttributes.RgbwColor;
	private const string RgbwwColorKey = LightAttributes.RgbwwColor;

	private readonly IHaContext _ha;
	private readonly ILogger _logger;

	public HaLightActuator(IHaContext ha, ILogger logger)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <inheritdoc/>
	public ActuatorOutcome Apply(string entityId, LightCommand command)
	{
		ArgumentNullException.ThrowIfNull(command);

		EntityState? state = _ha.GetState(entityId);

		// The skip rule the room page's "= now" also reads.
		if (LightMatch.AlreadyShows(state, command))
		{
			_logger.LogTrace("{EntityId} already matches {Command}; not calling.", entityId, command);
			return ActuatorOutcome.AlreadyMatches;
		}

		if (!command.On)
			return Call(entityId, TurnOffService, BuildOffData(command));

		string? channelKey = LightMatch.ChannelKey(state, command);

		return Call(entityId, TurnOnService, BuildOnData(command, channelKey));
	}

	/// <inheritdoc/>
	public void ActivateScene(string sceneId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sceneId);

		_logger.LogInformation("scene.turn_on {SceneId}", sceneId);
		_ha.CallService(SceneDomain, TurnOnService, ServiceTarget.FromEntity(sceneId));
	}

	private static int[] EqualChannels(string channelKey) =>
		[.. Enumerable.Repeat(LightMatch.EqualChannelValue, ChannelCountOf(channelKey))];

	private static int ChannelCountOf(string channelKey) => channelKey switch
	{
		RgbwwColorKey => 5,
		RgbwColorKey => 4,
		_ => 3
	};

	// A dictionary, not an anonymous type: an absent key stays absent, where a serialized null would be rejected.
	private static Dictionary<string, object> BuildOnData(LightCommand command, string? channelKey)
	{
		Dictionary<string, object> data = new(StringComparer.Ordinal);

		if (command.BrightnessPct is { } brightness)
			data[BrightnessPctKey] = Math.Round(brightness, 1);

		if (command.ColorTempKelvin is { } kelvin)
			data[ColorTempKelvinKey] = kelvin;

		if (channelKey is not null)
			data[channelKey] = command.Channels is { Count: > 0 } channels ? channels.ToArray() : EqualChannels(channelKey);

		if (command.TransitionSeconds is { } transition)
			data[TransitionKey] = Math.Round(transition, 1);

		return data;
	}

	private static Dictionary<string, object> BuildOffData(LightCommand command)
	{
		Dictionary<string, object> data = new(StringComparer.Ordinal);

		if (command.TransitionSeconds is { } transition)
			data[TransitionKey] = Math.Round(transition, 1);

		return data;
	}

	private ActuatorOutcome Call(string entityId, string service, Dictionary<string, object> data)
	{
		_logger.LogDebug("light.{Service} {EntityId} {Data}", service, entityId,
			string.Join(", ", data.Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key}={Describe(pair.Value)}"))));

		_ha.CallService(LightDomain, service, ServiceTarget.FromEntity(entityId), data);

		return new ActuatorOutcome(Sent: true, service, data);
	}

	// A colour value is an array, and the default formatting of one is its type name.
	private static string Describe(object value) =>
		value is int[] channels
			? "[" + string.Join(" ", channels.Select(channel => channel.ToString(CultureInfo.InvariantCulture))) + "]"
			: string.Create(CultureInfo.InvariantCulture, $"{value}");
}
