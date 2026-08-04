using System.Reactive.Subjects;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Ha;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     How a room's warmth reaches its lights: the three <see cref="ColorControl"/> modes, and what
///     <see cref="ColorControl.Auto"/> reads off the fixtures to decide between the other two.
/// </summary>
/// <remarks>
///     The wire format is asserted here as well as in <c>HaAdapterTests</c>, because equal channels is the one
///     mode with no colour temperature to fall back on: a wrong key leaves the room white at the wrong level with
///     nothing in the log to say so.
/// </remarks>
[TestClass]
public sealed class ColorControlTests
{
	private const string Light = "light.a";
	private const string Motion = "binary_sensor.a_motion";
	private const string SupportedColorModes = "supported_color_modes";

	private static HaLightActuator Actuator(FakeHaContext ha) => new(ha, new GlobalConfig(), NullLogger.Instance);

	private static Dictionary<string, object> DataOf(ServiceCall call) => (Dictionary<string, object>)call.Data!;

	/// <summary>Resolves one area holding <paramref name="lights"/>, listed explicitly so discovery cannot drop one.</summary>
	private static ResolvedArea Resolve(FakeHaContext ha, ColorControl mode, params string[] lights)
	{
		FakeAreaRegistry registry = new();
		registry.Areas["a"] = [Motion];
		ha.SetState(Motion, "off", new() { ["device_class"] = "motion" });

		AreaEntityResolver resolver = new(ha, registry, new GlobalConfig(), NullLogger.Instance);
		AreaConfig area = new() { AreaId = "a", Lights = [.. lights], ColorControl = mode };

		Assert.IsTrue(resolver.TryResolve(area, new AreaSettings(), out ResolvedArea? resolved, out string? error), error);
		return resolved!;
	}

	// ===================== the wire format of each mode =====================

	[TestMethod]
	public void Kelvin_Sends_A_Colour_Temperature_And_No_Colour_Channels()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "off");

		Actuator(ha).Apply(Light, new LightCommand(true, 70, 2700, 15));

		Dictionary<string, object> data = DataOf(ha.Calls.Single());
		Assert.AreEqual(2700, data["color_temp_kelvin"]);
		Assert.IsFalse(data.ContainsKey("rgb_color"));
	}

	[TestMethod]
	public void Equal_Channels_Sends_One_Value_On_Every_Channel_And_No_Colour_Temperature()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "off", new() { [SupportedColorModes] = new[] { "rgb" } });

		Actuator(ha).Apply(Light, new LightCommand(true, 70, null, 15, EqualChannels: true));

		Dictionary<string, object> data = DataOf(ha.Calls.Single());
		CollectionAssert.AreEqual(new[] { 255, 255, 255 }, (int[])data["rgb_color"]);
		Assert.AreEqual(70d, data["brightness_pct"], "brightness still does the dimming; the channels only set the colour");
		Assert.IsFalse(data.ContainsKey("color_temp_kelvin"), "a fixture with no colour temperature would reject the key");
	}

	[TestMethod]
	public void An_RGBW_Fixture_Takes_Its_White_Channel_At_The_Same_Value()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "off", new() { [SupportedColorModes] = new[] { "rgbw" } });

		Actuator(ha).Apply(Light, new LightCommand(true, 70, null, 15, EqualChannels: true));

		CollectionAssert.AreEqual(new[] { 255, 255, 255, 255 }, (int[])DataOf(ha.Calls.Single())["rgbw_color"]);
	}

	[TestMethod]
	public void An_RGBWW_Fixture_Takes_Both_White_Channels_At_The_Same_Value()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "off", new() { [SupportedColorModes] = new[] { "rgb", "rgbww" } });

		Actuator(ha).Apply(Light, new LightCommand(true, 70, null, 15, EqualChannels: true));

		CollectionAssert.AreEqual(new[] { 255, 255, 255, 255, 255 }, (int[])DataOf(ha.Calls.Single())["rgbww_color"]);
	}

	[TestMethod]
	public void A_Fixture_That_Names_No_Colour_Mode_Still_Gets_Three_Channels()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "off");

		Actuator(ha).Apply(Light, new LightCommand(true, 70, null, 15, EqualChannels: true));

		CollectionAssert.AreEqual(new[] { 255, 255, 255 }, (int[])DataOf(ha.Calls.Single())["rgb_color"],
			"rgb_color is what every colour light accepts, so it is the answer when nothing could be read");
	}

	[TestMethod]
	public void A_Light_Already_On_Neutral_White_Is_Left_Alone()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "on", new()
		{
			["brightness"] = 178.5,
			[SupportedColorModes] = new[] { "rgb" },
			["rgb_color"] = new[] { 255, 255, 255 }
		});

		Actuator(ha).Apply(Light, new LightCommand(true, 70, null, 15, EqualChannels: true));

		Assert.AreEqual(0, ha.Calls.Count, "a light told to fade to where it already is visibly restarts the fade");
	}

	[TestMethod]
	public void A_Light_Sitting_On_A_Colour_Is_Commanded_Back_To_Neutral()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "on", new()
		{
			["brightness"] = 178.5,
			[SupportedColorModes] = new[] { "rgb" },
			["rgb_color"] = new[] { 255, 120, 40 }
		});

		Actuator(ha).Apply(Light, new LightCommand(true, 70, null, 15, EqualChannels: true));

		Assert.AreEqual(1, ha.Calls.Count);
	}

	[TestMethod]
	public void A_Light_Offering_A_Colour_Channel_And_Reporting_None_Is_Commanded()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "on", new() { ["brightness"] = 178.5, [SupportedColorModes] = new[] { "rgb" } });

		Actuator(ha).Apply(Light, new LightCommand(true, 70, null, 15, EqualChannels: true));

		Assert.AreEqual(1, ha.Calls.Count, "silence from a fixture that has the channel means it is not using it");
	}

	[TestMethod]
	public void A_Light_Sitting_In_Colour_Temp_Mode_Is_Moved_To_Equal_Channels()
	{
		// The case three reviews found: HA publishes rgbww_color only while the fixture is in that mode, so an
		// equal-channels room whose lamp is holding a kelvin read as already-correct and was never commanded.
		FakeHaContext ha = new();
		ha.SetState(Light, "on", new()
		{
			["brightness"] = 178.5,
			[SupportedColorModes] = new[] { "color_temp", "rgbww" },
			["color_mode"] = "color_temp",
			["color_temp_kelvin"] = 2700
		});

		Actuator(ha).Apply(Light, new LightCommand(true, 70, null, 15, EqualChannels: true));

		CollectionAssert.AreEqual(new[] { 255, 255, 255, 255, 255 }, (int[])DataOf(ha.Calls.Single())["rgbww_color"]);
	}

	[TestMethod]
	public void A_Light_With_No_Colour_Channel_At_All_Is_Left_Alone()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "on", new() { ["brightness"] = 178.5, [SupportedColorModes] = new[] { "brightness" } });

		Actuator(ha).Apply(Light, new LightCommand(true, 70, null, 15, EqualChannels: true));

		Assert.AreEqual(0, ha.Calls.Count, "a plain dimmer must not be re-commanded on every tick");
	}

	// ===================== Auto, read off the fixtures =====================

	[TestMethod]
	public void Auto_Is_Kelvin_When_A_Fixture_Reports_A_Colour_Temperature()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "off", new() { [SupportedColorModes] = new[] { "color_temp", "hs" } });

		Assert.AreEqual(ColorControl.Kelvin, Resolve(ha, ColorControl.Auto, Light).EffectiveColorControl);
	}

	[TestMethod]
	public void Auto_Is_Equal_Channels_When_No_Fixture_Reports_A_Colour_Temperature()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "off", new() { [SupportedColorModes] = new[] { "rgb" } });
		ha.SetState("light.b", "off", new() { [SupportedColorModes] = new[] { "hs", "xy" } });

		Assert.AreEqual(ColorControl.EqualChannels, Resolve(ha, ColorControl.Auto, Light, "light.b").EffectiveColorControl);
	}

	[TestMethod]
	public void One_Fixture_With_A_Colour_Temperature_Is_Enough_To_Keep_The_Whole_Area_On_Kelvin()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "off", new() { [SupportedColorModes] = new[] { "rgb" } });
		ha.SetState("light.b", "off", new() { [SupportedColorModes] = new[] { "color_temp" } });

		Assert.AreEqual(ColorControl.Kelvin, Resolve(ha, ColorControl.Auto, Light, "light.b").EffectiveColorControl);
	}

	[TestMethod]
	public void A_Light_Whose_State_Cannot_Be_Read_Does_Not_Decide_Auto_On_Its_Own()
	{
		FakeHaContext ha = new();
		// No state at all, which is what an explicitly listed but disabled or not-yet-arrived entity looks like.

		ResolvedArea area = Resolve(ha, ColorControl.Auto, Light);

		Assert.IsNull(area.LightsSupportColorTemp, "no fixture answered, so there is nothing to conclude");
		Assert.AreEqual(ColorControl.Kelvin, area.EffectiveColorControl,
			"absence of evidence is not evidence: a house still starting up must not resolve every room to equal channels");
	}

	[TestMethod]
	public void A_Fixture_With_No_Colour_Modes_Attribute_Is_Not_Counted_Either()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "off", new() { ["brightness"] = 128 });

		Assert.AreEqual(ColorControl.Kelvin, Resolve(ha, ColorControl.Auto, Light).EffectiveColorControl);
	}

	[TestMethod]
	public void One_Fixture_That_Answered_Decides_For_The_Ones_That_Did_Not()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "off", new() { [SupportedColorModes] = new[] { "rgb" } });
		// light.b has no state, so it neither carries nor cancels the verdict.

		Assert.AreEqual(ColorControl.EqualChannels, Resolve(ha, ColorControl.Auto, Light, "light.b").EffectiveColorControl);
	}

	// ===================== a person overruling the fixtures, both ways =====================

	[TestMethod]
	public void Kelvin_Overrules_Fixtures_That_Advertise_No_Colour_Temperature()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "off", new() { [SupportedColorModes] = new[] { "rgb" } });

		Assert.AreEqual(ColorControl.Kelvin, Resolve(ha, ColorControl.Kelvin, Light).EffectiveColorControl);
	}

	[TestMethod]
	public void Equal_Channels_Overrules_Fixtures_That_Do_Advertise_One()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "off", new() { [SupportedColorModes] = new[] { "color_temp" } });

		Assert.AreEqual(ColorControl.EqualChannels, Resolve(ha, ColorControl.EqualChannels, Light).EffectiveColorControl);
	}

	// ===================== what the controller then composes =====================

	[TestMethod]
	public void An_Equal_Channels_Area_Commands_No_Colour_Temperature()
	{
		FakeLightActuator actuator = Lit(ColorControl.EqualChannels);

		Assert.IsTrue(actuator.Last is { On: true, BrightnessPct: 70, ColorTempKelvin: null, EqualChannels: true });
	}

	[TestMethod]
	public void A_Kelvin_Area_Still_Commands_The_Schedules_Colour_Temperature()
	{
		FakeLightActuator actuator = Lit(ColorControl.Kelvin);

		Assert.IsTrue(actuator.Last is { On: true, BrightnessPct: 70, ColorTempKelvin: 2700, EqualChannels: false });
	}

	/// <summary>Starts an area at 20:00, inside "evening", and lights it with a movement.</summary>
	private static FakeLightActuator Lit(ColorControl mode)
	{
		TestScheduler scheduler = new();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		FakeHaContext ha = new();
		ha.SetState(Motion, "off");
		ha.SetState(Light, "off");

		AreaSettings settings = new() { Darkness = DarknessSource.Always, ColorControl = mode };
		GlobalConfig global = new() { SmoothTransitions = false, CircadianTickSeconds = 60 };

		List<TimePeriodConfig> table =
		[
			new() { Name = "day", Start = "07:00", BrightnessPct = 90, ColorTempKelvin = 4500 },
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 }
		];

		FakeLightActuator actuator = new();

		using AreaController controller = new(
			ha, scheduler, new ResolvedArea("Test", settings, [Light], [Motion], [], []), global, table,
			new CircadianCalculator(table, global, () => SunTimes.Unknown),
			actuator, new FakeStatePublisher(), new BehaviorSubject<HouseState>(HouseState.Initial),
			NullLoggerFactory.Instance);

		controller.Start();
		ha.Trigger(Motion, "on");

		return actuator;
	}
}
