using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     Reads the house's latitude and longitude from Home Assistant's <c>zone.home</c>, for the daylight chart.
/// </summary>
/// <remarks>
///     No engine surface is added: the location is presentation-only. When <c>zone.home</c> cannot be read — HA
///     down, or an exotic install without it — <see cref="TryRead"/> returns <c>null</c> and the chart shows a
///     note rather than a guessed location. "Never a default dressed as a fact" applies to geography too.
/// </remarks>
public sealed class HomeLocation
{
	private const string ZoneHomeEntityId = "zone.home";
	private const string LatitudeAttribute = "latitude";
	private const string LongitudeAttribute = "longitude";

	private readonly IHaContext _ha;

	/// <summary>Creates the service.</summary>
	/// <param name="ha">The HA context <c>zone.home</c> is read through.</param>
	/// <exception cref="ArgumentNullException"><paramref name="ha"/> is <c>null</c>.</exception>
	public HomeLocation(IHaContext ha) => _ha = ha ?? throw new ArgumentNullException(nameof(ha));

	/// <summary>The home's latitude/longitude in degrees, or <c>null</c> when Home Assistant did not provide one.</summary>
	public (double Latitude, double Longitude)? TryRead()
	{
		try
		{
			EntityState? state = _ha.GetState(ZoneHomeEntityId);
			if (state.AttrDouble(LatitudeAttribute) is { } latitude && state.AttrDouble(LongitudeAttribute) is { } longitude)
				return (latitude, longitude);

			return null;
		}
		catch (InvalidOperationException)
		{
			// NetDaemon's state cache throws until its first connection to Home Assistant completes.
			return null;
		}
	}
}
