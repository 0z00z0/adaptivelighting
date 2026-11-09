using AdaptiveLighting.Abstractions;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>Whether a light already shows a command, by the rule the actuator uses to skip sending it.</summary>
// One rule for both: the room page's "= now" and the actuator's skip must never disagree.
internal static class LightMatch
{
	/// <summary>Every colour channel at the top of its range: neutral white, with the brightness doing the dimming.</summary>
	public const int EqualChannelValue = 255;

	private const string SupportedColorModesAttribute = "supported_color_modes";
	private const string RgbwMode = "rgbw";
	private const string RgbwwMode = "rgbww";

	/// <summary>Whether <paramref name="state"/> already shows <paramref name="command"/> within <see cref="LightTolerance"/>.</summary>
	public static bool AlreadyShows(EntityState? state, LightCommand command) =>
		command.On ? Matches(state, command, ChannelKey(state, command)) : state?.IsOff() == true;

	/// <summary>The colour-channel key a command is sent under, or <c>null</c> when it carries no channels.</summary>
	// An explicit vector names its own key by its length; EqualChannels asks the fixture, widest first.
	public static string? ChannelKey(EntityState? state, LightCommand command) =>
		command.Channels is { Count: > 0 } explicitChannels ? ChannelKeyForCount(explicitChannels.Count)
		: command.EqualChannels ? ChannelKeyFor(state)
		: null;

	private static string ChannelKeyFor(EntityState? state)
	{
		IReadOnlyList<string> modes = state.AttrStringList(SupportedColorModesAttribute);

		if (modes.Contains(RgbwwMode, StringComparer.OrdinalIgnoreCase))
			return LightAttributes.RgbwwColor;

		return modes.Contains(RgbwMode, StringComparer.OrdinalIgnoreCase) ? LightAttributes.RgbwColor : LightAttributes.RgbColor;
	}

	private static string ChannelKeyForCount(int count) => count switch
	{
		5 => LightAttributes.RgbwwColor,
		4 => LightAttributes.RgbwColor,
		_ => LightAttributes.RgbColor
	};

	private static bool OffersAColourChannel(EntityState state)
	{
		IReadOnlyList<string> modes = state.AttrStringList(SupportedColorModesAttribute);

		return AreaEntityResolver.ColourChannelModes.Any(mode => modes.Contains(mode, StringComparer.OrdinalIgnoreCase));
	}

	private static bool Matches(EntityState? state, LightCommand command, string? channelKey)
	{
		if (state?.IsOn() != true)
			return false;

		if (command.BrightnessPct is { } wantedBrightness)
		{
			double? currentRaw = state.AttrDouble(LightAttributes.Brightness);
			if (currentRaw is null)
				return false;

			if (Math.Abs((currentRaw.Value / LightAttributes.MaxRawBrightness * 100) - wantedBrightness) > LightTolerance.BrightnessPct)
				return false;
		}

		if (command.ColorTempKelvin is { } wantedKelvin)
		{
			double? currentKelvin = state.AttrDouble(LightAttributes.ColorTempKelvin);

			// A light with no colour temperature cannot drift from one; only a mismatch counts.
			if (currentKelvin is { } kelvin && Math.Abs(kelvin - wantedKelvin) > LightTolerance.ColorTempKelvin)
				return false;
		}

		if (channelKey is not null)
		{
			IReadOnlyList<double> channels = state.AttrDoubleList(channelKey);

			if (channels.Count > 0)
			{
				bool matches = command.Channels is { Count: > 0 } wantedChannels
					? channels.Count == wantedChannels.Count && !channels.Where((channel, index) => Math.Abs(channel - wantedChannels[index]) > 0.5).Any()
					: !channels.Any(channel => Math.Abs(channel - EqualChannelValue) > 0.5);

				if (!matches)
					return false;
			}
			// HA publishes rgbw_color / rgbww_color only while the fixture is in that colour mode, so one that offers
			// a channel and reports none is in another mode and has to be commanded. A fixture with no colour at all
			// stays a match.
			else if (OffersAColourChannel(state))
			{
				return false;
			}
		}

		return true;
	}
}
