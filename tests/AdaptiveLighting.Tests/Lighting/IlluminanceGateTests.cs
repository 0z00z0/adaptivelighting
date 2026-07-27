using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The one question that gates auto-on: is this area dark enough to be worth lighting?
/// </summary>
[TestClass]
public sealed class IlluminanceGateTests
{
	private const string Lux = "sensor.area_lux";
	private const string Sun = "sun.sun";

	private static IlluminanceGate Build(FakeHaContext ha, DarknessSource source, string? luxSensor = Lux, Action<AreaSettings>? tweak = null)
	{
		var settings = new AreaSettings { Darkness = source, LuxThreshold = 40, LuxHysteresis = 10, SunElevationThreshold = 3 };
		tweak?.Invoke(settings);
		return new IlluminanceGate(ha, luxSensor, settings, NullLogger.Instance);
	}

	private static FakeHaContext Ha(string? lux = null, double? sunElevation = null)
	{
		var ha = new FakeHaContext();
		if (lux is not null)
			ha.SetState(Lux, lux);

		if (sunElevation is { } elevation)
			ha.SetState(Sun, "below_horizon", new() { ["elevation"] = elevation });

		return ha;
	}

	// ===================== lux =====================

	[TestMethod]
	public void Lux_Below_The_Threshold_Is_Dark()
	{
		Assert.IsTrue(Build(Ha(lux: "39"), DarknessSource.Lux).IsDarkEnough());
	}

	[TestMethod]
	public void Lux_Above_The_Threshold_Is_Not_Dark()
	{
		Assert.IsFalse(Build(Ha(lux: "45"), DarknessSource.Lux).IsDarkEnough());
	}

	[TestMethod]
	public void The_Threshold_Itself_Is_Not_Dark()
	{
		Assert.IsFalse(Build(Ha(lux: "40"), DarknessSource.Lux).IsDarkEnough());
	}

	[TestMethod]
	public void DarknessDetail_Reports_The_Lux_Reading_And_The_Threshold()
	{
		var gate = Build(Ha(lux: "86"), DarknessSource.Lux);
		gate.IsDarkEnough();   // take a reading first, which DarknessDetail then explains

		string detail = gate.DarknessDetail();
		StringAssert.Contains(detail, "86", "the detail names the actual lux reading");
		StringAssert.Contains(detail, "40", "the detail names the configured threshold");
	}

	/// <summary>
	///     Hysteresis is what stops a sensor resting on the threshold from strobing the room. It takes 40 lux to
	///     become dark and 50 to stop being dark, so 45 keeps whichever verdict it already had.
	/// </summary>
	[TestMethod]
	public void Hysteresis_Holds_The_Verdict_Across_The_Threshold()
	{
		var ha = Ha(lux: "39");
		var gate = Build(ha, DarknessSource.Lux);
		Assert.IsTrue(gate.IsDarkEnough());

		ha.SetState(Lux, "45");
		Assert.IsTrue(gate.IsDarkEnough(), "45 is above the threshold but inside the hysteresis: still dark");

		ha.SetState(Lux, "51");
		Assert.IsFalse(gate.IsDarkEnough(), "past threshold plus hysteresis, it is finally light");

		ha.SetState(Lux, "45");
		Assert.IsFalse(gate.IsDarkEnough(), "and 45 now keeps the light verdict, which is the whole point");
	}

	[TestMethod]
	public void A_Decimal_Reading_Parses_Regardless_Of_The_Hosts_Culture()
	{
		Assert.IsTrue(Build(Ha(lux: "12.5"), DarknessSource.Lux).IsDarkEnough(),
			"a Norwegian host must read a YAML-shaped number the same way an English one does");
	}

	[TestMethod]
	public void An_Unparseable_Reading_Falls_Back_To_The_Sun()
	{
		var dark = Build(Ha(lux: "unavailable", sunElevation: -6), DarknessSource.Lux);
		Assert.IsTrue(dark.IsDarkEnough());

		var light = Build(Ha(lux: "unavailable", sunElevation: 30), DarknessSource.Lux);
		Assert.IsFalse(light.IsDarkEnough());
	}

	[TestMethod]
	public void No_Lux_Sensor_At_All_Falls_Back_To_The_Sun()
	{
		Assert.IsTrue(Build(Ha(sunElevation: -6), DarknessSource.Lux, luxSensor: null).IsDarkEnough());
	}

	// ===================== sun =====================

	[TestMethod]
	public void Sun_Below_The_Elevation_Threshold_Is_Dark()
	{
		Assert.IsTrue(Build(Ha(sunElevation: 2.9), DarknessSource.Sun).IsDarkEnough());
		Assert.IsFalse(Build(Ha(sunElevation: 3.1), DarknessSource.Sun).IsDarkEnough());
	}

	[TestMethod]
	public void Sun_Mode_Ignores_The_Lux_Sensor_Entirely()
	{
		Assert.IsFalse(Build(Ha(lux: "0", sunElevation: 30), DarknessSource.Sun).IsDarkEnough());
	}

	[TestMethod]
	public void A_Missing_Sun_Entity_Is_Not_Dark()
	{
		Assert.IsFalse(Build(Ha(), DarknessSource.Sun).IsDarkEnough(),
			"an absent sun entity is not a reason to floodlight the house at noon");
	}

	// ===================== either / always =====================

	[TestMethod]
	public void Either_Is_Dark_When_The_Lux_Sensor_Says_So()
	{
		Assert.IsTrue(Build(Ha(lux: "5", sunElevation: 30), DarknessSource.Either).IsDarkEnough(),
			"a dim room under a high sun — curtains drawn — is still dark");
	}

	[TestMethod]
	public void Either_Is_Dark_When_The_Sun_Says_So()
	{
		Assert.IsTrue(Build(Ha(lux: "500", sunElevation: -6), DarknessSource.Either).IsDarkEnough());
	}

	[TestMethod]
	public void Either_Is_Light_Only_When_Both_Agree()
	{
		Assert.IsFalse(Build(Ha(lux: "500", sunElevation: 30), DarknessSource.Either).IsDarkEnough());
	}

	[TestMethod]
	public void Always_Is_Dark_Whatever_The_Sensors_Say()
	{
		Assert.IsTrue(Build(Ha(lux: "5000", sunElevation: 60), DarknessSource.Always).IsDarkEnough(),
			"a room without daylight does not care what the sun is doing");
	}

	// ===================== the reading itself =====================

	/// <summary>
	///     The gate is the one place the area's lux is read, so that anything else needing the number — the
	///     daylight brightness adjustment — is guaranteed to be looking at the same sensor as the darkness verdict.
	/// </summary>
	[TestMethod]
	public void ReadLux_Hands_Back_The_Sensors_Number()
	{
		Assert.AreEqual(1234.5, Build(Ha(lux: "1234.5"), DarknessSource.Lux).ReadLux());
	}

	[TestMethod]
	public void ReadLux_Is_Null_When_There_Is_No_Sensor_Or_No_Number()
	{
		Assert.IsNull(Build(Ha(), DarknessSource.Lux, luxSensor: null).ReadLux());
		Assert.IsNull(Build(Ha(lux: "unavailable"), DarknessSource.Lux).ReadLux());
	}

	/// <summary>
	///     It takes a reading; it does not record one. The adjustment reads lux on every tick, and if that counted
	///     as a verdict it would rewrite what the auto-on block log says the gate last decided and why.
	/// </summary>
	[TestMethod]
	public void ReadLux_Does_Not_Disturb_What_The_Gate_Reports()
	{
		IlluminanceGate gate = Build(Ha(lux: "39"), DarknessSource.Lux);
		gate.IsDarkEnough();

		string before = gate.DarknessDetail();
		gate.ReadLux();

		Assert.AreEqual(before, gate.DarknessDetail());
	}

	/// <summary>
	///     Reading lux is not the same question as gating on it. A hallway may well decide darkness from the sun
	///     while still following an outdoor lux sensor for its level, so the reading is available in every mode.
	/// </summary>
	[TestMethod]
	public void ReadLux_Works_Whatever_The_Darkness_Source_Is()
	{
		Assert.AreEqual(800d, Build(Ha(lux: "800", sunElevation: 30), DarknessSource.Sun).ReadLux());
		Assert.AreEqual(800d, Build(Ha(lux: "800"), DarknessSource.Always).ReadLux());
	}
}
