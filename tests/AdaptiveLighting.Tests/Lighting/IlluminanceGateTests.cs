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

	/// <summary>
	///     The gate's clock. An instance field, so the tests that wind it forward cannot disturb the ones that do
	///     not — MSTest builds a fresh instance per test method, and a shared static would make that untrue.
	/// </summary>
	private DateTimeOffset _now = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

	private IlluminanceGate Build(
		FakeHaContext ha,
		DarknessSource source,
		string? luxSensor = Lux,
		Action<AreaSettings>? tweak = null,
		TimeSpan? staleAfter = null) =>
		BuildMany(ha, source, luxSensor is null ? [] : [luxSensor], tweak, staleAfter);

	private IlluminanceGate BuildMany(
		FakeHaContext ha,
		DarknessSource source,
		IReadOnlyList<string> luxSensors,
		Action<AreaSettings>? tweak = null,
		TimeSpan? staleAfter = null)
	{
		var settings = new AreaSettings { Darkness = source, LuxThreshold = 40, LuxHysteresis = 10, SunElevationThreshold = 3 };
		tweak?.Invoke(settings);

		return new IlluminanceGate(
			ha, luxSensors, settings, staleAfter ?? TimeSpan.Zero, () => _now, NullLogger.Instance);
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

	/// <summary>
	///     A room with no lux sensor is simply dark, whatever the sun is doing.
	/// </summary>
	/// <remarks>
	///     <b>This test's contract changed, and the daylight case is the whole of it.</b> It used to assert that
	///     such a room fell back to sun elevation, which was true and is no longer: the owner's rule is that no
	///     sensor means nothing for the lux gate to refuse on, so movement lights the room. Better to light up too
	///     early than never. A room that wants a reading names one, or follows the house's outdoor sensor.
	/// </remarks>
	[TestMethod]
	public void No_Lux_Sensor_At_All_Is_Simply_Dark()
	{
		Assert.IsTrue(Build(Ha(sunElevation: -6), DarknessSource.Lux, luxSensor: null).IsDarkEnough());

		Assert.IsTrue(Build(Ha(sunElevation: 30), DarknessSource.Lux, luxSensor: null).IsDarkEnough(),
			"high noon, no sensor: the room still counts as dark rather than falling back to the sun");

		Assert.IsTrue(Build(Ha(sunElevation: 30), DarknessSource.Either, luxSensor: null).IsDarkEnough(),
			"and Either is dark when either half says so, which the absent lux half now does");
	}

	/// <summary>
	///     A sensor that exists and will not read is <i>not</i> the same as no sensor, and must not be.
	/// </summary>
	/// <remarks>
	///     The area was told to gate on something real that is merely broken, so the sun answers instead. Reading
	///     a Zigbee dropout as "no sensor, therefore dark" would turn every radio hiccup into a lit room at noon.
	/// </remarks>
	[TestMethod]
	public void A_Broken_Sensor_Is_Not_The_Same_As_No_Sensor()
	{
		Assert.IsFalse(Build(Ha(lux: "unavailable", sunElevation: 30), DarknessSource.Lux).IsDarkEnough(),
			"the room has a sensor; it is broken, and the sun says it is the middle of the day");
	}

	// ===================== several sensors: the average =====================

	/// <summary>
	///     Two sensors are averaged rather than chosen between. The area used to use neither.
	/// </summary>
	/// <remarks>
	///     Refusing was defensible while the alternative was picking one arbitrarily — a real house offered the
	///     probe inside its fridge as a candidate — but it left a better-instrumented room strictly worse off than
	///     a bare one, which is how eight of seventeen rooms once stopped working.
	/// </remarks>
	[TestMethod]
	public void Two_Sensors_Are_Averaged_Geometrically()
	{
		FakeHaContext ha = new();
		ha.SetState("sensor.a", "170");
		ha.SetState("sensor.b", "3000");

		IlluminanceGate gate = BuildMany(ha, DarknessSource.Lux, ["sensor.a", "sensor.b"]);

		Assert.AreEqual(Math.Sqrt(170d * 3000d), gate.ReadLux()!.Value, 1e-9,
			"the geometric mean is 714, not the arithmetic 1585 — brightness is perceived logarithmically");
	}

	[TestMethod]
	public void Three_Sensors_Are_Averaged_Geometrically_Too()
	{
		FakeHaContext ha = new();
		ha.SetState("sensor.a", "10");
		ha.SetState("sensor.b", "100");
		ha.SetState("sensor.c", "1000");

		IlluminanceGate gate = BuildMany(ha, DarknessSource.Lux, ["sensor.a", "sensor.b", "sensor.c"]);

		Assert.AreEqual(100d, gate.ReadLux()!.Value, 1e-9, "three decades centred on the middle one");
	}

	/// <summary>
	///     The geometric mean multiplies, so one zero would drag a bright room to pitch dark. Non-positive
	///     readings are dropped before the average — and a room of nothing but them really is 0.
	/// </summary>
	[TestMethod]
	public void A_Zero_Reading_Does_Not_Drag_The_Whole_Room_To_Zero()
	{
		FakeHaContext ha = new();
		ha.SetState("sensor.a", "0");
		ha.SetState("sensor.b", "900");

		Assert.AreEqual(900d, BuildMany(ha, DarknessSource.Lux, ["sensor.a", "sensor.b"]).ReadLux()!.Value, 1e-9,
			"a logarithm has no value at zero, so the zero is dropped rather than allowed to multiply the room away");

		FakeHaContext allDark = new();
		allDark.SetState("sensor.a", "0");
		allDark.SetState("sensor.b", "0");

		Assert.AreEqual(0d, BuildMany(allDark, DarknessSource.Lux, ["sensor.a", "sensor.b"]).ReadLux()!.Value,
			"every sensor reading zero is a pitch-dark room, which is a reading and not an absence of one");
	}

	[TestMethod]
	public void A_Sensor_That_Will_Not_Read_Leaves_The_Average_To_The_Others()
	{
		FakeHaContext ha = new();
		ha.SetState("sensor.a", "unavailable");
		ha.SetState("sensor.b", "36");

		Assert.AreEqual(36d, BuildMany(ha, DarknessSource.Lux, ["sensor.a", "sensor.b"]).ReadLux()!.Value, 1e-9);
	}

	// ===================== several sensors: the dead ones =====================

	/// <summary>
	///     A sensor that has not reported for longer than the window is dropped, and the fresh one decides.
	/// </summary>
	/// <remarks>
	///     A stuck sensor keeps its last value for ever, so without this it would drag the room's average with it
	///     for as long as nobody noticed. Asserted with a stale reading that would flip the verdict if it counted.
	/// </remarks>
	[TestMethod]
	public void A_Stale_Sensor_Is_Dropped_And_The_Fresh_One_Decides()
	{
		DateTimeOffset start = _now;
		FakeHaContext ha = new();
		ha.SetStateReportedAt("sensor.stuck", "10000", start);
		ha.SetStateReportedAt("sensor.live", "5", start);

		// Built first, then the clock runs on: the grace period is measured from here, so the rule only comes
		// alive once the engine has been watching for longer than the window.
		IlluminanceGate gate = BuildMany(
			ha, DarknessSource.Lux, ["sensor.stuck", "sensor.live"], staleAfter: TimeSpan.FromHours(2));

		_now = start.AddHours(3);
		ha.SetStateReportedAt("sensor.live", "5", _now.AddMinutes(-1));   // still reporting; the other has not

		Assert.AreEqual(5d, gate.ReadLux()!.Value, 1e-9, "the stuck sensor is not reporting, so it does not vote");
		Assert.IsTrue(gate.IsDarkEnough());
	}

	/// <summary>
	///     <b>The trap in the timestamp choice.</b> A sensor sitting at a steady 3 lx all night is the healthiest
	///     thing in a dark room, and <c>LastChanged</c> would condemn it for being consistent. The rule reads
	///     <c>LastUpdated</c>, which moves whenever Home Assistant heard from the entity at all.
	/// </summary>
	[TestMethod]
	public void A_Sensor_Reporting_The_Same_Value_Over_And_Over_Is_Not_Dead()
	{
		DateTimeOffset start = _now;
		FakeHaContext ha = new();
		ha.SetStateReportedAt("sensor.steady", "3", start);

		IlluminanceGate gate = BuildMany(
			ha, DarknessSource.Lux, ["sensor.steady"], staleAfter: TimeSpan.FromHours(2));

		// Six hours later the value has never once changed, and Home Assistant heard from it a minute ago.
		_now = start.AddHours(6);
		ha.SetStateReportedAt("sensor.steady", "3", _now.AddMinutes(-1));

		Assert.AreEqual(3d, gate.ReadLux()!.Value, 1e-9, "a constant reading is a working sensor, not a dead one");
	}

	/// <summary>
	///     Every sensor stale leaves the room with no usable reading — which the caller reads as "no verdict", so
	///     <see cref="DarknessSource.Lux"/> falls back to the sun rather than pretending the room is dark.
	/// </summary>
	[TestMethod]
	public void Every_Sensor_Stale_Leaves_The_Room_Without_A_Reading()
	{
		DateTimeOffset start = _now;
		FakeHaContext ha = new();
		ha.SetStateReportedAt("sensor.a", "10000", start);
		ha.SetStateReportedAt("sensor.b", "10000", start);
		ha.SetState(Sun, "below_horizon", new() { ["elevation"] = -6d });

		IlluminanceGate gate = BuildMany(
			ha, DarknessSource.Lux, ["sensor.a", "sensor.b"], staleAfter: TimeSpan.FromHours(2));

		_now = start.AddHours(3);

		Assert.IsNull(gate.ReadLux(), "both are dead, so the room has no reading at all");
		Assert.IsTrue(gate.IsDarkEnough(), "and the sun answers, exactly as it does for a sensor that will not read");
	}

	/// <summary>The window is the house's to set, and zero switches the rule off entirely.</summary>
	[TestMethod]
	public void The_Staleness_Window_Is_Configurable()
	{
		DateTimeOffset start = _now;
		FakeHaContext ha = new();
		ha.SetStateReportedAt("sensor.a", "10000", start);

		IlluminanceGate strict = BuildMany(ha, DarknessSource.Lux, ["sensor.a"], staleAfter: TimeSpan.FromMinutes(10));
		IlluminanceGate lenient = BuildMany(ha, DarknessSource.Lux, ["sensor.a"], staleAfter: TimeSpan.FromHours(2));
		IlluminanceGate off = BuildMany(ha, DarknessSource.Lux, ["sensor.a"], staleAfter: TimeSpan.Zero);

		_now = start.AddMinutes(30);

		Assert.IsNull(strict.ReadLux(), "half an hour of silence is dead under a ten-minute window");

		Assert.AreEqual(10000d, lenient.ReadLux()!.Value, "and alive under a two-hour one");

		Assert.AreEqual(10000d, off.ReadLux()!.Value,
			"zero switches the rule off for a house whose sensors genuinely report rarely");
	}

	/// <summary>
	///     Nothing is condemned before the engine has been watching for as long as the window itself.
	/// </summary>
	/// <remarks>
	///     Home Assistant resets every entity's timestamps when it restarts. Measured on one live instance 2.3
	///     hours after a restart, a flat two-hour rule would have called most of the house dead — so "it has not
	///     reported since we started watching" has to be told apart from "we have not been watching long".
	/// </remarks>
	[TestMethod]
	public void Nothing_Is_Called_Dead_Before_The_Engine_Has_Watched_Long_Enough()
	{
		FakeHaContext ha = new();
		ha.SetStateReportedAt("sensor.a", "10000", _now.AddHours(-5));

		IlluminanceGate gate = BuildMany(
			ha, DarknessSource.Lux, ["sensor.a"], staleAfter: TimeSpan.FromHours(2));

		Assert.AreEqual(10000d, gate.ReadLux()!.Value,
			"five hours of silence, but the engine started a moment ago and has no right to that conclusion yet");

		_now = _now.AddHours(3);

		Assert.IsNull(gate.ReadLux(), "once it has watched for longer than the window, silence means something");
	}

	/// <summary>A state with no timestamp at all is absence of evidence, not evidence of death.</summary>
	[TestMethod]
	public void A_State_With_No_Timestamp_Is_Not_Stale()
	{
		DateTimeOffset start = _now;
		FakeHaContext ha = new();
		ha.SetState("sensor.a", "42");

		IlluminanceGate gate = BuildMany(ha, DarknessSource.Lux, ["sensor.a"], staleAfter: TimeSpan.FromMinutes(1));

		_now = start.AddHours(6);

		Assert.AreEqual(42d, gate.ReadLux()!.Value);
	}

	/// <summary>The detail line says how many sensors answered, which is the only place a dead one is visible.</summary>
	[TestMethod]
	public void DarknessDetail_Counts_The_Sensors_Behind_An_Average()
	{
		FakeHaContext ha = new();
		ha.SetState("sensor.a", "unavailable");
		ha.SetState("sensor.b", "36");
		ha.SetState("sensor.c", "49");

		IlluminanceGate gate = BuildMany(ha, DarknessSource.Lux, ["sensor.a", "sensor.b", "sensor.c"]);
		gate.IsDarkEnough();

		StringAssert.Contains(gate.DarknessDetail(), "2 of 3 sensors",
			"a puzzling average is only diagnosable if the row says how much of the room it came from");
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
