using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.LastSeen;

using Microsoft.Extensions.Logging;
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

	/// <summary>
	///     Counts warnings, because the warning is now the only thing that tells a room with dead sensors from a
	///     room that never had one — the verdict no longer does.
	/// </summary>
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

	/// <summary>
	///     A room that configures nothing gates on lux alone, and a room with no sensor therefore always lights.
	/// </summary>
	/// <remarks>
	///     The owner's rule in one test: use the light sensor where there is one, and where there is none always
	///     light. The default was <see cref="DarknessSource.Either"/>, whose sun half overruled a good reading at
	///     dusk and put a sun clause into every explanation the gate wrote. Asserted at high noon, which is where
	///     the two defaults disagree.
	/// </remarks>
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

	/// <summary>
	///     A reading nothing can parse is no reading, and no reading is dark whatever the sun is doing.
	/// </summary>
	/// <remarks>
	///     <b>This test's contract changed: it used to assert a fall back to the sun.</b> The owner's rule is that a
	///     room with nothing to read counts as dark however the reading came to be missing, so an <c>unavailable</c>
	///     sensor at noon now lights the room rather than deferring to a sun that says it is the middle of the day.
	/// </remarks>
	[TestMethod]
	public void An_Unparseable_Reading_Is_Dark_Whatever_The_Sun_Says()
	{
		Assert.IsTrue(Build(Ha(lux: "unavailable", sunElevation: -6), DarknessSource.Lux).IsDarkEnough());

		Assert.IsTrue(Build(Ha(lux: "unavailable", sunElevation: 30), DarknessSource.Lux).IsDarkEnough(),
			"high noon, and the room's only sensor will not read: still dark, because there is nothing to refuse on");
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
	///     A broken sensor now reaches the same verdict as no sensor, and the difference survives only as a warning.
	/// </summary>
	/// <remarks>
	///     <b>This test's contract changed: it used to assert that the two cases decided differently.</b> They no
	///     longer do — both are dark — but they are still worth telling apart, because a room that never had a sensor
	///     is a supported arrangement while a room whose sensor has stopped answering is somebody's battery, radio or
	///     integration. So the distinction moved into the log, and that is what is asserted here.
	/// </remarks>
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
	///     Every sensor stale leaves the room with no usable reading, and a room with no reading is dark.
	/// </summary>
	/// <remarks>
	///     <b>This test's contract changed: the sun used to answer here.</b> Asserted against a high sun, which is
	///     the case that tells the two rules apart — the old one left a house of dead sensors unlit all day.
	/// </remarks>
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

	/// <summary>
	///     <see cref="DarknessSource.Either"/> is untouched by that: a room of dead sensors still asks the sun.
	/// </summary>
	/// <remarks>
	///     The absent lux verdict is read as "not dark by lux" and the <c>||</c> hands the question to the sun,
	///     exactly as before. Somebody who set Either asked for the sun, and gets it.
	/// </remarks>
	[TestMethod]
	public void Either_With_Every_Sensor_Dead_Still_Lets_The_Sun_Answer()
	{
		DateTimeOffset start = _now;
		FakeHaContext bright = new();
		bright.SetStateReportedAt("sensor.a", "10000", start);
		bright.SetState(Sun, "above_horizon", new() { ["elevation"] = 30d });

		IlluminanceGate day = BuildMany(bright, DarknessSource.Either, ["sensor.a"], staleAfter: TimeSpan.FromHours(2));

		FakeHaContext dusk = new();
		dusk.SetStateReportedAt("sensor.a", "10000", start);
		dusk.SetState(Sun, "below_horizon", new() { ["elevation"] = -6d });

		IlluminanceGate night = BuildMany(dusk, DarknessSource.Either, ["sensor.a"], staleAfter: TimeSpan.FromHours(2));

		_now = start.AddHours(3);

		Assert.IsFalse(day.IsDarkEnough(), "dead sensor, high sun: Either is still not dark");
		Assert.IsTrue(night.IsDarkEnough(), "dead sensor, sun down: the sun answers");
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

	// ===================== the reason string =====================

	/// <summary>
	///     Under <see cref="DarknessSource.Either"/> the explanation names what settled the verdict, and only that.
	/// </summary>
	/// <remarks>
	///     <b>These four cases are the whole of the branch, read off <c>lux-is-dark || sun-is-down</c>.</b> The rule
	///     it replaces concatenated both details unconditionally, which produced lines the owner called meaningless:
	///     a lux reading that had already settled the question followed by a sun clause that had not, and — worse —
	///     by "no sun elevation from sun.sun", which reports what was missing rather than what was decided.
	/// </remarks>
	[TestMethod]
	public void Either_Explains_The_Verdict_With_The_Reading_That_Settled_It()
	{
		IlluminanceGate luxDecided = Build(Ha(lux: "5", sunElevation: 30), DarknessSource.Either);
		luxDecided.IsDarkEnough();
		Assert.AreEqual("lux 5, dark below 40", luxDecided.DarknessDetail(),
			"the lux reading short-circuits the sun, so there is no sun reading of this verdict to report");

		IlluminanceGate sunDecided = Build(Ha(lux: "500", sunElevation: -6), DarknessSource.Either);
		sunDecided.IsDarkEnough();
		Assert.AreEqual("sun elevation -6°, dark below 3°", sunDecided.DarknessDetail(),
			"lux said bright, so the sun carried it alone and is the answer");

		IlluminanceGate neither = Build(Ha(lux: "500", sunElevation: 30), DarknessSource.Either);
		neither.IsDarkEnough();
		Assert.AreEqual("lux 500, dark below 40; sun elevation 30°, dark below 3°", neither.DarknessDetail(),
			"not dark is the one outcome both halves had to agree on, so both readings explain it");

		IlluminanceGate noSunEntity = Build(Ha(lux: "500"), DarknessSource.Either);
		noSunEntity.IsDarkEnough();
		Assert.AreEqual("lux 500, dark below 40", noSunEntity.DarknessDetail(),
			"a half that produced no number is dropped, not described as missing");
	}

	/// <summary>Neither half readable is the one case where the absence itself is the whole of the answer.</summary>
	[TestMethod]
	public void Either_With_Nothing_Readable_Says_So_Plainly()
	{
		IlluminanceGate gate = Build(Ha(lux: "unavailable"), DarknessSource.Either);
		gate.IsDarkEnough();

		Assert.AreEqual("no lux reading, and no sun elevation from sun.sun", gate.DarknessDetail());
	}

	/// <summary>
	///     Under <see cref="DarknessSource.Lux"/> a room whose sensors have all gone quiet says so, and says what
	///     follows from it — with no sun clause, because the sun was never asked.
	/// </summary>
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

	/// <summary>
	///     The tracker's verdict outranks Home Assistant's own timestamp, in both directions.
	/// </summary>
	/// <remarks>
	///     Home Assistant resets every entity's <c>LastUpdated</c> when it restarts, so shortly afterwards a sensor
	///     that died last week and one that reported a minute before the restart look identical — measured on a live
	///     house where the oldest timestamp anywhere was the restart itself, 2.3 hours old. A gate reading only that
	///     field therefore trusts a dead sensor and, once the grace period lapses, doubts a healthy quiet one. These
	///     two cases are the whole reason the tracker exists, so they are asserted against a fresh timestamp and an
	///     ancient one respectively — each the opposite of the answer the tracker gives.
	/// </remarks>
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
