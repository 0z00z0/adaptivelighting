using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.LastSeen;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The one question that gates auto-on: is this area dark enough to be worth lighting?</summary>
[TestClass]
public sealed class IlluminanceGateTests
{
	private const string Lux = "sensor.area_lux";
	private const string Sun = "sun.sun";

	// Instance, not static: MSTest builds a fresh instance per test, so winding the clock cannot disturb another test.
	private DateTimeOffset _now = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

	private IlluminanceGate Build(
		FakeHaContext ha,
		DarknessSource source,
		string? luxSensor = Lux,
		Action<AreaSettings>? tweak = null,
		TimeSpan? staleAfter = null,
		IEntityLastSeen? lastSeen = null,
		ILogger? logger = null) =>
		BuildMany(ha, source, luxSensor is null ? [] : [luxSensor], tweak, staleAfter, lastSeen, logger);

	private IlluminanceGate BuildMany(
		FakeHaContext ha,
		DarknessSource source,
		IReadOnlyList<string> luxSensors,
		Action<AreaSettings>? tweak = null,
		TimeSpan? staleAfter = null,
		IEntityLastSeen? lastSeen = null,
		ILogger? logger = null)
	{
		var settings = new AreaSettings { Darkness = source, LuxThreshold = 40, LuxHysteresis = 10, SunElevationThreshold = 3 };
		tweak?.Invoke(settings);

		return new IlluminanceGate(
			ha, luxSensors, settings, staleAfter ?? TimeSpan.Zero, () => _now, logger ?? NullLogger.Instance, lastSeen);
	}

	/// <summary>Counts warnings, the only thing telling a room with dead sensors from one that never had any.</summary>
	private sealed class WarningCounter : ILogger
	{
		public int Warnings { get; private set; }

		public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state)!;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (logLevel == LogLevel.Warning)
				Warnings++;
		}
	}

	/// <summary>Answers whatever the test says, so the gate's preference for it can be asserted directly.</summary>
	private sealed class StubLastSeen(bool silent) : IEntityLastSeen
	{
		public DateTimeOffset? LastSeenUtc(string entityId) => null;

		public TimeSpan? SilenceOf(string entityId) => null;

		public bool HasBeenSilentFor(string entityId, TimeSpan threshold) => silent;

		public DateTimeOffset? HomeAssistantStartedUtc => null;

		public int TrackedCount => 0;
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

	// ===================== the default =====================

	// Asserted at high noon, the only place the current default and the retired Either disagree.
	[TestMethod]
	public void A_Room_That_Configures_Nothing_Gates_On_Lux_Alone()
	{
		AreaSettings untouched = new();

		Assert.AreEqual(DarknessSource.Lux, untouched.Darkness, "the house-wide default, inherited by every room");

		IlluminanceGate sensorless = new(
			Ha(sunElevation: 30), [], untouched, TimeSpan.Zero, () => _now, NullLogger.Instance);

		Assert.IsTrue(sensorless.IsDarkEnough(),
			"no sensor, high sun: the room counts as dark, so movement lights it");
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

	/// <summary>It takes 40 lux to become dark and 50 to stop being dark, so 45 keeps whichever verdict it had.</summary>
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
	public void An_Unparseable_Reading_Is_Dark_Whatever_The_Sun_Says()
	{
		Assert.IsTrue(Build(Ha(lux: "unavailable", sunElevation: -6), DarknessSource.Lux).IsDarkEnough());

		Assert.IsTrue(Build(Ha(lux: "unavailable", sunElevation: 30), DarknessSource.Lux).IsDarkEnough(),
			"high noon, and the room's only sensor will not read: still dark, because there is nothing to refuse on");
	}

	[TestMethod]
	public void No_Lux_Sensor_At_All_Is_Simply_Dark()
	{
		Assert.IsTrue(Build(Ha(sunElevation: -6), DarknessSource.Lux, luxSensor: null).IsDarkEnough());

		Assert.IsTrue(Build(Ha(sunElevation: 30), DarknessSource.Lux, luxSensor: null).IsDarkEnough(),
			"high noon, no sensor: the room still counts as dark rather than falling back to the sun");
	}

	// Nothing else covers the gate's Either arms: dropping "or DarknessSource.Either" from the switch leaves such
	// rooms on the default arm, holding their last verdict for ever.
	[TestMethod]
	public void The_Retired_Either_Decides_As_Lux_And_Never_Asks_The_Sun()
	{
		// Sun well down, lux comfortably above the threshold: the sun's word alone would call this dark.
		IlluminanceGate bright = Build(Ha(lux: "500", sunElevation: -6), DarknessSource.Either);

		Assert.IsFalse(bright.IsDarkEnough(),
			"the reading says bright, and Either now has no sun half to overrule it with");

		// Pinned positively: DarknessDetail has its own Either arm, and losing it drops the room to "unknown darkness
		// source", which contains no "sun" either, so a negative assertion alone would stay green.
		StringAssert.Contains(bright.DarknessDetail(), "lux 500",
			"the reading itself, which is what Lux's explanation carries");
		StringAssert.Contains(bright.DarknessDetail(), "dark below 40",
			"and the threshold it was compared against");

		Assert.IsFalse(bright.DarknessDetail().Contains("sun", StringComparison.OrdinalIgnoreCase),
			"and the explanation names no sun either, or the room would be told a reason that did not apply");

		Assert.IsTrue(Build(Ha(lux: "2", sunElevation: 30), DarknessSource.Either).IsDarkEnough(),
			"a dark reading at high noon is still dark: the lux half is the only half");
	}

	[TestMethod]
	public void A_Broken_Sensor_Decides_Like_No_Sensor_But_Warns()
	{
		WarningCounter broken = new();
		Assert.IsTrue(Build(Ha(lux: "unavailable", sunElevation: 30), DarknessSource.Lux, logger: broken).IsDarkEnough(),
			"the room's sensor is broken, so there is nothing to gate on and the room counts as dark");

		WarningCounter absent = new();
		Assert.IsTrue(Build(Ha(sunElevation: 30), DarknessSource.Lux, luxSensor: null, logger: absent).IsDarkEnough(),
			"the same verdict a room with no sensor at all reaches");

		Assert.AreEqual(1, broken.Warnings, "failed hardware is worth telling a household about");
		Assert.AreEqual(0, absent.Warnings, "having no sensor is an ordinary arrangement and warns about nothing");
	}

	// ===================== several sensors: the average =====================

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

	/// <summary>Non-positive readings are dropped before the average, but a room of nothing but them reads 0.</summary>
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

	[TestMethod]
	public void A_Stale_Sensor_Is_Dropped_And_The_Fresh_One_Decides()
	{
		DateTimeOffset start = _now;
		FakeHaContext ha = new();
		ha.SetStateReportedAt("sensor.stuck", "10000", start);
		ha.SetStateReportedAt("sensor.live", "5", start);

		// Built first, then the clock runs on: the grace period is measured from construction.
		IlluminanceGate gate = BuildMany(
			ha, DarknessSource.Lux, ["sensor.stuck", "sensor.live"], staleAfter: TimeSpan.FromHours(2));

		_now = start.AddHours(3);
		ha.SetStateReportedAt("sensor.live", "5", _now.AddMinutes(-1));   // still reporting; the other has not

		Assert.AreEqual(5d, gate.ReadLux()!.Value, 1e-9, "the stuck sensor is not reporting, so it does not vote");
		Assert.IsTrue(gate.IsDarkEnough());
	}

	// Staleness reads LastUpdated: LastChanged would condemn a sensor sitting at a steady 3 lx all night.
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

	[TestMethod]
	public void Every_Sensor_Stale_Is_Dark_Rather_Than_Sun_Dependent()
	{
		DateTimeOffset start = _now;
		FakeHaContext ha = new();
		ha.SetStateReportedAt("sensor.a", "10000", start);
		ha.SetStateReportedAt("sensor.b", "10000", start);
		ha.SetState(Sun, "above_horizon", new() { ["elevation"] = 30d });

		IlluminanceGate gate = BuildMany(
			ha, DarknessSource.Lux, ["sensor.a", "sensor.b"], staleAfter: TimeSpan.FromHours(2));

		_now = start.AddHours(3);

		Assert.IsNull(gate.ReadLux(), "both are dead, so the room has no reading at all");
		Assert.IsTrue(gate.IsDarkEnough(), "and no reading is dark, exactly as it is for a room with no sensor");
	}


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

	// Home Assistant resets every timestamp on restart: 2.3 hours after one, a flat two-hour rule called most of
	// the house dead.
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

	// ===================== always =====================




	[TestMethod]
	public void Always_Is_Dark_Whatever_The_Sensors_Say()
	{
		Assert.IsTrue(Build(Ha(lux: "5000", sunElevation: 60), DarknessSource.Always).IsDarkEnough(),
			"a room without daylight does not care what the sun is doing");
	}

	// ===================== the reason string =====================



	[TestMethod]
	public void Lux_With_No_Usable_Reading_Explains_Itself_Without_The_Sun()
	{
		IlluminanceGate gate = Build(Ha(lux: "unavailable", sunElevation: 30), DarknessSource.Lux);
		gate.IsDarkEnough();

		string detail = gate.DarknessDetail();
		Assert.AreEqual("no light sensor here is still reporting, so the room counts as dark", detail);
		Assert.IsFalse(detail.Contains("sun", StringComparison.OrdinalIgnoreCase),
			"the sun decides nothing on this setting, so it explains nothing either");
	}

	// ===================== the reading itself =====================

	// The gate is the single read path for an area's lux, so the brightness adjustment cannot read a different sensor.
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

	// ReadLux takes a reading and records none: the adjustment calls it every tick, and a recorded reading would
	// rewrite what the auto-on block log says.
	[TestMethod]
	public void ReadLux_Does_Not_Disturb_What_The_Gate_Reports()
	{
		IlluminanceGate gate = Build(Ha(lux: "39"), DarknessSource.Lux);
		gate.IsDarkEnough();

		string before = gate.DarknessDetail();
		gate.ReadLux();

		Assert.AreEqual(before, gate.DarknessDetail());
	}

	/// <summary>Reading lux is not the same question as gating on it, so the reading works in every mode.</summary>
	[TestMethod]
	public void ReadLux_Works_Whatever_The_Darkness_Source_Is()
	{
		Assert.AreEqual(800d, Build(Ha(lux: "800", sunElevation: 30), DarknessSource.Sun).ReadLux());
		Assert.AreEqual(800d, Build(Ha(lux: "800"), DarknessSource.Always).ReadLux());
	}

	// After a restart a sensor dead a week and one that reported a minute ago carry the same LastUpdated, so each
	// case is asserted against a timestamp opposite to the tracker's answer.
	[TestMethod]
	public void The_Tracker_Decides_Staleness_Rather_Than_Home_Assistants_Own_Timestamp()
	{
		FakeHaContext fresh = Ha(lux: "5");
		IlluminanceGate deadButFreshlyStamped = Build(
			fresh, DarknessSource.Lux, staleAfter: TimeSpan.FromHours(2), lastSeen: new StubLastSeen(silent: true));

		Assert.IsNull(deadButFreshlyStamped.ReadLux(),
			"a restart had just re-stamped this sensor, but the tracker knows it stopped reporting");

		FakeHaContext quiet = Ha(lux: "5");
		_now = _now.AddDays(3);
		IlluminanceGate aliveButStale = Build(
			quiet, DarknessSource.Lux, staleAfter: TimeSpan.FromHours(2), lastSeen: new StubLastSeen(silent: false));

		Assert.AreEqual(5, aliveButStale.ReadLux(),
			"the timestamp is three days behind the clock, but the tracker has heard from it since");
	}
}
