using System.Reactive.Subjects;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Ha;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>How a room's warmth reaches its lights, and what <see cref="ColorControl.Auto"/> reads off the fixtures to decide.</summary>
/// <remarks>The wire format is asserted here too: equal channels has no colour temperature to fall back on, so a wrong key is silent.</remarks>
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
		// HA publishes rgbww_color only while the fixture is in that mode, so an equal-channels room whose lamp is
		// holding a kelvin reads as already-correct and is never commanded.
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

	// ===================== fixtures with no colour of any kind =====================

	[TestMethod]
	public void A_Brightness_Only_Room_Commands_No_Colour_At_All()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "off", new() { [SupportedColorModes] = new[] { "brightness" } });

		ResolvedArea area = Resolve(ha, ColorControl.Auto, Light);

		Assert.AreEqual<bool?>(false, area.LightsSupportAnyColour, "the fixture answered, and what it answered has no colour in it");
		Assert.IsFalse(area.CommandsColour);
		Assert.IsFalse(area.CommandsKelvin, "a colour temperature the fixtures cannot take must not be reported as theirs");
	}

	[TestMethod]
	public void One_Fixture_With_Colour_Keeps_A_Brightness_Only_Room_Commanding_It()
	{
		FakeHaContext ha = new();
		ha.SetState(Light, "off", new() { [SupportedColorModes] = new[] { "brightness" } });
		ha.SetState("light.b", "off", new() { [SupportedColorModes] = new[] { "color_temp" } });

		ResolvedArea area = Resolve(ha, ColorControl.Auto, Light, "light.b");

		Assert.IsTrue(area.CommandsColour, "the dimmer beside a real lamp must not take the lamp's kelvin away");
		Assert.IsTrue(area.CommandsKelvin);
	}

	[TestMethod]
	public void A_Room_Whose_Fixtures_Have_Not_Answered_Still_Commands_Colour()
	{
		FakeHaContext ha = new();
		// No state at all, which is what a house still starting up looks like.

		ResolvedArea area = Resolve(ha, ColorControl.Auto, Light);

		Assert.IsNull(area.LightsSupportAnyColour, "silence is not the same answer as 'no colour'");
		Assert.IsTrue(area.CommandsColour, "a start-up with nothing read yet must not lock the room out of colour");
		Assert.IsTrue(area.CommandsKelvin);
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

	// ===================== the four things Auto can find, as commands =====================

	[TestMethod]
	public void Auto_Over_A_Colour_Temperature_Fixture_Commands_Kelvin_And_No_Channels()
	{
		FakeLightActuator actuator = Lit(ColorControl.Auto, "color_temp");

		Assert.IsTrue(actuator.Last is { On: true, BrightnessPct: 70, ColorTempKelvin: 2700, EqualChannels: false });
	}

	[TestMethod]
	public void Auto_Over_A_Colour_Channel_Fixture_Commands_Channels_And_No_Kelvin()
	{
		FakeLightActuator actuator = Lit(ColorControl.Auto, "rgb");

		Assert.IsTrue(actuator.Last is { On: true, BrightnessPct: 70, ColorTempKelvin: null, EqualChannels: true });
	}

	[TestMethod]
	public void Auto_Over_A_Brightness_Only_Fixture_Commands_Neither_Kelvin_Nor_Channels()
	{
		FakeLightActuator actuator = Lit(ColorControl.Auto, "brightness");

		Assert.IsTrue(actuator.Last is { On: true, BrightnessPct: 70, ColorTempKelvin: null, EqualChannels: false },
			"a dimmer takes the brightness and nothing else; a kelvin here is a figure nobody can honour");
	}

	[TestMethod]
	public void Auto_Over_A_Fixture_That_Has_Not_Answered_Commands_Kelvin()
	{
		FakeLightActuator actuator = Lit(ColorControl.Auto);

		Assert.IsTrue(actuator.Last is { On: true, BrightnessPct: 70, ColorTempKelvin: 2700, EqualChannels: false },
			"a house still starting up keeps the schedule's kelvin until a fixture says otherwise");
	}

	[TestMethod]
	public void A_Stated_Kelvin_Reaches_A_Brightness_Only_Room_Anyway()
	{
		FakeLightActuator actuator = Lit(ColorControl.Kelvin, "brightness");

		Assert.IsTrue(actuator.Last is { ColorTempKelvin: 2700, EqualChannels: false },
			"detection only settles Auto; a stated answer is an owner overruling the fixtures");
	}

	[TestMethod]
	public void Stated_Equal_Channels_Reach_A_Brightness_Only_Room_Anyway()
	{
		FakeLightActuator actuator = Lit(ColorControl.EqualChannels, "brightness");

		Assert.IsTrue(actuator.Last is { ColorTempKelvin: null, EqualChannels: true });
	}

	/// <summary>Starts an area at 20:00, inside "evening", and lights it with a movement.</summary>
	/// <param name="mode">The room's warmth answer, stated or left on detect.</param>
	/// <param name="supportedColorModes">What the fixture advertises; none at all is a light that never answered.</param>
	private static FakeLightActuator Lit(ColorControl mode, params string[] supportedColorModes)
	{
		TestScheduler scheduler = new();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		FakeHaContext ha = new();

		if (supportedColorModes.Length > 0)
			ha.SetState(Light, "off", new() { [SupportedColorModes] = supportedColorModes });
		else
			ha.SetState(Light, "off");

		// Built through the resolver, or what the fixtures say would never reach the controller at all.
		ResolvedArea area = Resolve(ha, mode, Light) with
		{
			Settings = new AreaSettings { Darkness = DarknessSource.Always, ColorControl = mode }
		};

		GlobalConfig global = new() { SmoothTransitions = false, CircadianTickSeconds = 60 };

		List<TimePeriodConfig> table =
		[
			new() { Name = "day", Start = "07:00", BrightnessPct = 90, ColorTempKelvin = 4500 },
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 }
		];

		FakeLightActuator actuator = new();

		using AreaController controller = new(
			ha, scheduler, area, global, table,
			new CircadianCalculator(table, global, () => SunTimes.Unknown),
			actuator, new FakeStatePublisher(), new BehaviorSubject<HouseState>(HouseState.Initial),
			NullLoggerFactory.Instance);

		controller.Start();
		ha.Trigger(Motion, "on");

		return actuator;
	}
}
