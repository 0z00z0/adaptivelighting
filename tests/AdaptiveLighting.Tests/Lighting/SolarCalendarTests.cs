using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The NOAA sunrise/sunset routine behind the daylight chart.
/// </summary>
/// <remarks>
///     Two things are load-bearing. The form <c>720 - 4*(lon ± ha) - eqtime</c> takes longitude positive-east, so
///     an easterly move brings the UTC sunrise earlier. And a day with no crossing has to say whether the sun
///     stayed above the angle (midnight sun) or below it (polar night); the chart draws from that distinction.
///     Time comparisons are wrap-normalised so the result holds in every host timezone.
/// </remarks>
[TestClass]
public sealed class SolarCalendarTests
{
	/// <summary>
	///     Signed minutes from <paramref name="from"/> to <paramref name="to"/>, the short way round the clock.
	///     Both times came through the same offset, so the modulo folds away any midnight wrap it introduced and
	///     the answer is the same in every host timezone.
	/// </summary>
	private static double MinutesBetween(TimeOnly from, TimeOnly to)
	{
		double raw = (to.ToTimeSpan() - from.ToTimeSpan()).TotalMinutes;
		double folded = ((raw % 1440) + 1440) % 1440;   // [0, 1440)
		return folded > 720 ? folded - 1440 : folded; // (-720, 720]
	}

	[TestMethod]
	public void An_Eastward_Move_Brings_Sunrise_Earlier_By_Four_Minutes_Per_Degree()
	{
		DateOnly day = new(2024, 3, 20);   // near the equinox, sunrise mid-morning — well clear of midnight

		TimeOnly? west = SolarCalendar.On(day, 40.0, 0.0).Sunrise;
		TimeOnly? east = SolarCalendar.On(day, 40.0, 10.0).Sunrise;

		Assert.IsNotNull(west);
		Assert.IsNotNull(east);

		// East of the prime meridian the sun rises earlier: the 10° step is 40 minutes, at 4 min/degree.
		// A negative or near-zero result is the inverted-sign bug this test exists to catch.
		double delta = MinutesBetween(east!.Value, west!.Value);
		Assert.IsTrue(delta > 0, "an easterly longitude must give an earlier sunrise, not a later one");
		Assert.AreEqual(40.0, delta, 2.0, "each degree of longitude is four minutes of sun time");
	}

	[TestMethod]
	public void Above_The_Arctic_Circle_At_Midsummer_The_Sun_Stays_Above_The_Horizon()
	{
		DateOnly midsummer = new(2024, 6, 21);

		SolarDay day = SolarCalendar.On(midsummer, 80.0, 10.0);

		// Midnight sun: the sun never dips to the horizon, so there is no sunrise or sunset. The state has to say
		// AlwaysAbove; the ambiguous "no crossing" is something the chart cannot draw from.
		Assert.AreEqual(SunState.AlwaysAbove, day.State, "high-Arctic midsummer is the midnight sun");
		Assert.IsNull(day.Sunrise, "the polar day has no sunrise to report");
		Assert.IsNull(day.Sunset, "…and no sunset either");
	}

	[TestMethod]
	public void Above_The_Arctic_Circle_At_Midwinter_The_Sun_Stays_Below_The_Horizon()
	{
		DateOnly midwinter = new(2024, 12, 21);

		SolarDay day = SolarCalendar.On(midwinter, 80.0, 10.0);

		// Polar night: the sun never climbs to the horizon. The opposite of the midnight sun, and the state is
		// what lets the chart fill the day dark.
		Assert.AreEqual(SunState.AlwaysBelow, day.State, "high-Arctic midwinter is the polar night");
		Assert.IsNull(day.Sunrise, "the polar night has no sunrise");
		Assert.IsNull(day.Sunset, "…and no sunset");
	}

	[TestMethod]
	public void At_A_White_Night_Latitude_The_Sun_Rises_But_Civil_Twilight_Never_Ends()
	{
		DateOnly midsummer = new(2024, 6, 21);

		// 63°N is below the Arctic Circle, so the sun still sets at the official horizon, but it never sinks the
		// full 6° to civil-twilight depth. This is the transition the twilight band has to draw: an ordinary day
		// at the official zenith, AlwaysAbove at the civil one, twilight all night and no true darkness.
		SolarDay official = SolarCalendar.On(midsummer, 63.0, 10.0);
		SolarDay civil = SolarCalendar.On(midsummer, 63.0, 10.0, SolarCalendar.CivilZenithDegrees);

		Assert.AreEqual(SunState.RisesAndSets, official.State, "at 63°N midsummer the sun still crosses the horizon");
		Assert.IsNotNull(official.Sunrise);
		Assert.IsNotNull(official.Sunset);

		Assert.AreEqual(SunState.AlwaysAbove, civil.State, "the sun never reaches civil-twilight depth: a white night");
		Assert.IsNull(civil.Sunrise, "a white night has no civil dawn — twilight never ends");
		Assert.IsNull(civil.Sunset, "…and no civil dusk");
	}

	[TestMethod]
	public void Civil_Twilight_Brackets_Sunrise_And_Sunset()
	{
		DateOnly day = new(2024, 3, 20);   // near the equinox, well clear of midnight

		SolarDay official = SolarCalendar.On(day, 59.9, 10.7);   // Oslo-ish
		SolarDay civil = SolarCalendar.On(day, 59.9, 10.7, SolarCalendar.CivilZenithDegrees);

		Assert.AreEqual(SunState.RisesAndSets, official.State);
		Assert.AreEqual(SunState.RisesAndSets, civil.State);
		Assert.IsNotNull(official.Sunrise);
		Assert.IsNotNull(official.Sunset);
		Assert.IsNotNull(civil.Sunrise);
		Assert.IsNotNull(civil.Sunset);

		// Civil dawn is before sunrise, and civil dusk is after sunset: the sun is 6° further down at civil twilight,
		// so it reaches the horizon later in the morning and passes below it later in the evening.
		Assert.IsTrue(civil.Sunrise!.Value < official.Sunrise!.Value, "civil dawn precedes sunrise");
		Assert.IsTrue(official.Sunset!.Value < civil.Sunset!.Value, "civil dusk follows sunset");
	}
}
