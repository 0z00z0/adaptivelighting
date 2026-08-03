using System.Globalization;

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

	private const string BrightnessPctKey = "brightness_pct";
	private const string ColorTempKelvinKey = "color_temp_kelvin";
	private const string TransitionKey = "transition";

	// Home Assistant reports brightness on 0-255 but accepts it as a percentage. Convert before comparing.
	private const double MaxRawBrightness = 255.0;

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

		if (AlreadyMatches(state, command))
		{
			_logger.LogTrace("{EntityId} already matches {Command}; not calling.", entityId, command);
			return;
		}

		Call(entityId, TurnOnService, BuildOnData(command));
	}

	/// <inheritdoc/>
	public void ActivateScene(string sceneId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sceneId);

		_logger.LogInformation("scene.turn_on {SceneId}", sceneId);
		_ha.CallService(SceneDomain, TurnOnService, ServiceTarget.FromEntity(sceneId));
	}

	private bool AlreadyMatches(EntityState? state, LightCommand command)
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

		return true;
	}

	// A dictionary, not an anonymous type: an absent key stays absent, where a serialized null would be rejected.
	private static Dictionary<string, object> BuildOnData(LightCommand command)
	{
		Dictionary<string, object> data = new(StringComparer.Ordinal);

		if (command.BrightnessPct is { } brightness)
			data[BrightnessPctKey] = Math.Round(brightness, 1);

		if (command.ColorTempKelvin is { } kelvin)
			data[ColorTempKelvinKey] = kelvin;

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
			string.Join(", ", data.Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key}={pair.Value}"))));

		_ha.CallService(LightDomain, service, ServiceTarget.FromEntity(entityId), data);
	}
}
