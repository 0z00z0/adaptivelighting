using System.Globalization;
using System.Text.Json;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Ha;

/// <summary>
///     Applies light commands by calling <c>light.turn_on</c> / <c>light.turn_off</c>.
/// </summary>
/// <remarks>
///     Suppresses commands the light already satisfies. Every circadian tick would otherwise re-send the same levels
///     to every light in the house, and a light told to fade to where it already is visibly restarts the fade.
/// </remarks>
public sealed class HaLightActuator : ILightActuator
{
	private const string LightDomain = "light";
	private const string SceneDomain = "scene";
	private const string TurnOnService = "turn_on";
	private const string TurnOffService = "turn_off";
	private const string BrightnessAttribute = "brightness";
	private const string ColorTempAttribute = "color_temp_kelvin";
	private const string SupportedColorModesAttribute = "supported_color_modes";

	private const string BrightnessPctKey = "brightness_pct";
	private const string ColorTempKelvinKey = "color_temp_kelvin";
	private const string TransitionKey = "transition";
	private const string RgbColorKey = "rgb_color";
	private const string RgbwColorKey = "rgbw_color";
	private const string RgbwwColorKey = "rgbww_color";
	private const string RgbwMode = "rgbw";
	private const string RgbwwMode = "rgbww";

	// Home Assistant reports brightness on 0-255 but accepts it as a percentage. Convert before comparing.
	private const double MaxRawBrightness = 255.0;

	// Every channel at the top of its range: neutral white, with brightness_pct doing the dimming on its own.
	private const int EqualChannelValue = 255;

	private readonly IHaContext _ha;
	private readonly GlobalConfig _global;
	private readonly ILogger _logger;

	public HaLightActuator(IHaContext ha, GlobalConfig global, ILogger logger)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_global = global ?? throw new ArgumentNullException(nameof(global));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <inheritdoc/>
	public void Apply(string entityId, LightCommand command)
	{
		ArgumentNullException.ThrowIfNull(command);

		EntityState? state = _ha.GetState(entityId);

		if (!command.On)
		{
			if (state?.IsOff() == true)
				return;

			Call(entityId, TurnOffService, BuildOffData(command));
			return;
		}

		// The one place the channel key is chosen, off the state this method already read. No extra round trip.
		string? channelKey = command.EqualChannels ? ChannelKeyFor(state) : null;

		if (AlreadyMatches(state, command, channelKey))
		{
			_logger.LogTrace("{EntityId} already matches {Command}; not calling.", entityId, command);
			return;
		}

		Call(entityId, TurnOnService, BuildOnData(command, channelKey));
	}

	/// <inheritdoc/>
	public void ActivateScene(string sceneId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sceneId);

		_logger.LogInformation("scene.turn_on {SceneId}", sceneId);
		_ha.CallService(SceneDomain, TurnOnService, ServiceTarget.FromEntity(sceneId));
	}

	/// <summary>
	///     Which colour-channel key this fixture takes, widest first. Falls back to <c>rgb_color</c>, which every
	///     colour light accepts, when nothing could be read.
	/// </summary>
	private static string ChannelKeyFor(EntityState? state)
	{
		IReadOnlyList<string> modes = state.AttrStringList(SupportedColorModesAttribute);

		if (modes.Contains(RgbwwMode, StringComparer.OrdinalIgnoreCase))
			return RgbwwColorKey;

		return modes.Contains(RgbwMode, StringComparer.OrdinalIgnoreCase) ? RgbwColorKey : RgbColorKey;
	}

	private static int[] EqualChannels(string channelKey) =>
		[.. Enumerable.Repeat(EqualChannelValue, ChannelCountOf(channelKey))];

	private static int ChannelCountOf(string channelKey) => channelKey switch
	{
		RgbwwColorKey => 5,
		RgbwColorKey => 4,
		_ => 3
	};

	private bool AlreadyMatches(EntityState? state, LightCommand command, string? channelKey)
	{
		if (state?.IsOn() != true)
			return false;

		if (command.BrightnessPct is { } wantedBrightness)
		{
			double? currentRaw = state.AttrDouble(BrightnessAttribute);
			if (currentRaw is null)
				return false;

			if (Math.Abs((currentRaw.Value / MaxRawBrightness * 100) - wantedBrightness) > GlobalConfig.BrightnessTolerancePct)
				return false;
		}

		if (command.ColorTempKelvin is { } wantedKelvin)
		{
			double? currentKelvin = state.AttrDouble(ColorTempAttribute);

			// A light with no colour temperature cannot drift from one; only a mismatch counts.
			if (currentKelvin is { } kelvin && Math.Abs(kelvin - wantedKelvin) > GlobalConfig.ColorTempToleranceKelvin)
				return false;
		}

		if (channelKey is not null)
		{
			// Same reading as the colour temperature above: an unreported colour cannot have drifted.
			IReadOnlyList<double> channels = ChannelsOf(state, channelKey);

			if (channels.Count > 0 && channels.Any(channel => Math.Abs(channel - EqualChannelValue) > 0.5))
				return false;
		}

		return true;
	}

	// Not AttrStringList: a colour arrives as a JSON array of numbers, which that helper drops on the floor.
	private static IReadOnlyList<double> ChannelsOf(EntityState state, string attribute)
	{
		if (state.Attributes?.TryGetValue(attribute, out object? value) != true)
			return [];

		return value switch
		{
			JsonElement { ValueKind: JsonValueKind.Array } element =>
				[.. element.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Number).Select(item => item.GetDouble())],
			IEnumerable<int> numbers => [.. numbers.Select(number => (double)number)],
			IEnumerable<double> numbers => [.. numbers],
			_ => []
		};
	}

	// A dictionary, not an anonymous type: an absent key stays absent, where a serialized null would be rejected.
	private static Dictionary<string, object> BuildOnData(LightCommand command, string? channelKey)
	{
		Dictionary<string, object> data = new(StringComparer.Ordinal);

		if (command.BrightnessPct is { } brightness)
			data[BrightnessPctKey] = Math.Round(brightness, 1);

		if (command.ColorTempKelvin is { } kelvin)
			data[ColorTempKelvinKey] = kelvin;

		if (channelKey is not null)
			data[channelKey] = EqualChannels(channelKey);

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

	private void Call(string entityId, string service, Dictionary<string, object> data)
	{
		_logger.LogDebug("light.{Service} {EntityId} {Data}", service, entityId,
			string.Join(", ", data.Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key}={Describe(pair.Value)}"))));

		_ha.CallService(LightDomain, service, ServiceTarget.FromEntity(entityId), data);
	}

	// A colour value is an array, and the default formatting of one is its type name.
	private static string Describe(object value) =>
		value is int[] channels
			? "[" + string.Join(" ", channels.Select(channel => channel.ToString(CultureInfo.InvariantCulture))) + "]"
			: string.Create(CultureInfo.InvariantCulture, $"{value}");
}
