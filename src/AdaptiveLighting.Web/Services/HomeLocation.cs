using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     Reads the house's latitude and longitude from Home Assistant's <c>zone.home</c>, for the daylight chart.
/// </summary>
/// <remarks>
///     Presentation-only; no engine surface is added. With <c>zone.home</c> unreadable, <see cref="TryRead"/>
///     answers <c>null</c> and the chart shows a note in place of a guessed location.
/// </remarks>
public sealed class HomeLocation
{
	private const string ZoneHomeEntityId = "zone.home";
	private const string LatitudeAttribute = "latitude";
	private const string LongitudeAttribute = "longitude";

	private readonly IHaContext _ha;

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
