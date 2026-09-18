using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Tests.Lighting;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

using NetDaemon.HassModel;

namespace AdaptiveLighting.Tests.Common;

/// <summary>One started area and the fakes around it.</summary>
public sealed record AreaFixture(
	TestScheduler Scheduler,
	FakeHaContext Ha,
	FakeLightActuator Actuator,
	FakeStatePublisher Publisher,
	BehaviorSubject<HouseState> House,
	AreaController Area);

/// <summary>One started orchestrator and the fakes around it.</summary>
public sealed record OrchestratorFixture(
	TestScheduler Scheduler,
	FakeHaContext Ha,
	FakeLightActuator Actuator,
	LightingOrchestrator Orchestrator)
{
	public AreaController Room => Orchestrator.Areas[0];
}

/// <summary>Builds a started area at 2026-01-15 20:00 UTC, inside "evening".</summary>
// Every default is one the area tests rely on. Changing one here moves every test that does not set it.
public sealed class AreaTestBuilder
{
	public const string Motion = "binary_sensor.area_motion";
	public const string Light = "light.area";
	public const string Lux = "sensor.area_lux";

	public static readonly DateTimeOffset StartsAt = new(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);

	private readonly List<Action<FakeHaContext>> _seeds = [];
	private readonly List<Action<AreaSettings>> _settingsTweaks = [];
	private readonly List<Action<GlobalConfig>> _globalTweaks = [];
	private readonly List<Func<ResolvedArea, ResolvedArea>> _shapes = [];

	private Action<FakeHaContext> _states = StandardStates;
	private Func<AreaSettings> _settings = PinnedSettings;
	private IReadOnlyList<TimePeriodConfig>? _periods;
	private string _name = "Test";
	private string? _areaId = "test_area";
	private IReadOnlyList<string> _lights = [Light];
	private IReadOnlyList<string> _motionSensors = [Motion];
	private IReadOnlyList<string> _luxSensors = [Lux];
	private IReadOnlyList<string> _ignoreWhenOn = [];
	private bool _followOutdoorLux;
	private IReadOnlyList<RoomLevelOverride>? _roomLevels;
	private IReadOnlyList<LightLevelOverride>? _lightLevels;
	private bool _statesLightLevels;
	private Func<SunTimes> _sun = () => SunTimes.Unknown;
	private IObservable<Unit>? _sunMoved;
	private TimeZoneInfo _zone = TimeZoneInfo.Utc;
	private bool _nameOrigins;
	private Func<IScheduler, IScheduler> _wrapScheduler = scheduler => scheduler;
	private Func<FakeHaContext, IHaContext> _wrapHa = ha => ha;
	private HouseState? _houseBeforeStart = new(true, ModeKind.Normal, false);
	private HouseState? _houseAfterStart;

	/// <summary>Day, evening and night, so 20:00 sits in "evening" at 70 % and 2700 K.</summary>
	public static List<TimePeriodConfig> StandardPeriods() =>
	[
		new() { Name = "day", Start = "07:00", BrightnessPct = 90, ColorTempKelvin = 4500 },
		new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
		new() { Name = "night", Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200 }
	];

	private static void StandardStates(FakeHaContext ha)
	{
		ha.SetState(Motion, "off");
		ha.SetState(Light, "off");
		ha.SetState(Lux, "5");
	}

	private static AreaSettings PinnedSettings() => new()
	{
		VacancyTimeoutSeconds = 600,
		PreOffSeconds = 30,
		Darkness = DarknessSource.Lux,
		OverrideDurationMinutes = 120,

		// Unlike the shipped default: most area tests time the fixed hold, not a hold that ends with the room.
		OverrideUntilVacant = false,
		VacancyResetMinutes = 10
	};

	/// <summary>Replaces the standard world of motion off, light off and lux 5.</summary>
	public AreaTestBuilder States(Action<FakeHaContext> states)
	{
		_states = states;
		return this;
	}

	/// <summary>Adds states after the standard world and before the area starts.</summary>
	public AreaTestBuilder Seed(Action<FakeHaContext>? seed)
	{
		if (seed is not null)
			_seeds.Add(seed);

		return this;
	}

	/// <summary>Starts from the shipped settings instead of the pinned ones.</summary>
	public AreaTestBuilder ShippedSettings()
	{
		_settings = () => new AreaSettings();
		return this;
	}

	public AreaTestBuilder Settings(Action<AreaSettings>? tweak)
	{
		if (tweak is not null)
			_settingsTweaks.Add(tweak);

		return this;
	}

	public AreaTestBuilder Global(Action<GlobalConfig>? tweak)
	{
		if (tweak is not null)
			_globalTweaks.Add(tweak);

		return this;
	}

	/// <summary>Replaces the standard periods; null keeps them.</summary>
	public AreaTestBuilder Periods(IReadOnlyList<TimePeriodConfig>? periods)
	{
		_periods = periods;
		return this;
	}

	public AreaTestBuilder Named(string name, string? areaId)
	{
		_name = name;
		_areaId = areaId;
		return this;
	}

	public AreaTestBuilder Lights(IReadOnlyList<string> lights)
	{
		_lights = lights;
		return this;
	}

	public AreaTestBuilder MotionSensors(IReadOnlyList<string> motionSensors)
	{
		_motionSensors = motionSensors;
		return this;
	}

	public AreaTestBuilder LuxSensors(IReadOnlyList<string> luxSensors)
	{
		_luxSensors = luxSensors;
		return this;
	}

	public AreaTestBuilder IgnoreWhenOn(IReadOnlyList<string> ignoreWhenOn)
	{
		_ignoreWhenOn = ignoreWhenOn;
		return this;
	}

	public AreaTestBuilder FollowOutdoorLux(bool follow)
	{
		_followOutdoorLux = follow;
		return this;
	}

	/// <summary>Sets what the resolved area carries beyond its entities, as a <c>with</c> expression.</summary>
	public AreaTestBuilder Shape(Func<ResolvedArea, ResolvedArea> shape)
	{
		_shapes.Add(shape);
		return this;
	}

	public AreaTestBuilder RoomLevels(IReadOnlyList<RoomLevelOverride>? levels)
	{
		_roomLevels = levels;
		return this;
	}

	/// <summary>States per-light levels, each merged onto the room's levels with a calculator of its own.</summary>
	// Once called, the area's LightLevels is replaced even when the list is empty.
	public AreaTestBuilder LightLevels(IReadOnlyList<LightLevelOverride>? lightLevels)
	{
		_lightLevels = lightLevels;
		_statesLightLevels = true;
		return this;
	}

	public AreaTestBuilder Sun(Func<SunTimes> sun)
	{
		_sun = sun;
		return this;
	}

	public AreaTestBuilder SunMoved(IObservable<Unit>? sunMoved)
	{
		_sunMoved = sunMoved;
		return this;
	}

	/// <summary>The zone periods are read in. UTC unless set.</summary>
	public AreaTestBuilder Zone(TimeZoneInfo zone)
	{
		_zone = zone;
		return this;
	}

	public AreaTestBuilder NameOrigins(bool nameOrigins)
	{
		_nameOrigins = nameOrigins;
		return this;
	}

	/// <summary>Hands the controller a scheduler wrapped around the test clock.</summary>
	public AreaTestBuilder WrapScheduler(Func<IScheduler, IScheduler> wrap)
	{
		_wrapScheduler = wrap;
		return this;
	}

	/// <summary>Hands the controller a context wrapped around the seeded fake.</summary>
	public AreaTestBuilder WrapHa(Func<FakeHaContext, IHaContext> wrap)
	{
		_wrapHa = wrap;
		return this;
	}

	/// <summary>The house state published before the area starts, as the orchestrator does; null publishes none.</summary>
	public AreaTestBuilder OpeningHouse(HouseState? house)
	{
		_houseBeforeStart = house;
		return this;
	}

	/// <summary>A house state published once the area has started; null publishes none.</summary>
	public AreaTestBuilder HouseAfterStart(HouseState? house)
	{
		_houseAfterStart = house;
		return this;
	}

	public AreaFixture Build()
	{
		TestScheduler scheduler = NewScheduler();
		FakeHaContext ha = NewHa();

		AreaSettings settings = _settings();
		foreach (Action<AreaSettings> tweak in _settingsTweaks)
			tweak(settings);

		GlobalConfig global = new() { SmoothTransitions = false, CircadianTickSeconds = 60 };
		foreach (Action<GlobalConfig> tweak in _globalTweaks)
			tweak(global);

		IReadOnlyList<TimePeriodConfig> table = _periods ?? StandardPeriods();

		ResolvedArea area = new(_name, settings, _lights, _motionSensors, _luxSensors, _ignoreWhenOn, _followOutdoorLux);

		Dictionary<string, CircadianCalculator> perLight = new(StringComparer.OrdinalIgnoreCase);
		if (_statesLightLevels)
		{
			Dictionary<string, IReadOnlyList<RoomLevelOverride>> stated = new(StringComparer.OrdinalIgnoreCase);
			foreach (LightLevelOverride light in _lightLevels ?? [])
				stated[light.EntityId] = light.Levels;

			area = area with { LightLevels = stated };

			foreach ((string leaf, IReadOnlyList<RoomLevelOverride> rows) in stated)
				perLight[leaf] = new CircadianCalculator(
					table, global, _sun, LightLevelMerge.MergeOnto(_roomLevels, rows), zone: _zone);
		}

		foreach (Func<ResolvedArea, ResolvedArea> shape in _shapes)
			area = shape(area);

		FakeLightActuator actuator = new();
		FakeStatePublisher publisher = new();
		BehaviorSubject<HouseState> house = new(HouseState.Initial);

		HouseWiring wiring = new(
			_wrapHa(ha),
			_wrapScheduler(scheduler),
			global,
			table,
			actuator,
			publisher,
			house,
			NullLoggerFactory.Instance,
			LastSeen: null,
			OriginNames: _nameOrigins ? new ChangeOriginNames(ha, NullLogger.Instance) : null,
			OwnUserId: static () => null);

		AreaController controller = new(
			wiring,
			area,
			new CircadianCalculator(table, global, _sun, _roomLevels, zone: _zone),
			areaId: _areaId,
			sunMoved: _sunMoved,
			lightCalculators: perLight.Count > 0 ? perLight : null);

		if (_houseBeforeStart is not null)
			house.OnNext(_houseBeforeStart);

		controller.Start();

		if (_houseAfterStart is not null)
			house.OnNext(_houseAfterStart);

		return new AreaFixture(scheduler, ha, actuator, publisher, house, controller);
	}

	/// <summary>Starts a whole engine on the clock and states; the area inputs above do not apply.</summary>
	public OrchestratorFixture StartOrchestrator(AdaptiveLightingConfig config)
	{
		TestScheduler scheduler = NewScheduler();
		FakeHaContext ha = NewHa();
		FakeLightActuator actuator = new();

		LightingOrchestrator orchestrator = new(
			ha, new FakeHaRegistry(), scheduler, config,
			actuator, new FakeStatePublisher(), new FakeNotifier(), NullLoggerFactory.Instance);

		orchestrator.Start();

		return new OrchestratorFixture(scheduler, ha, actuator, orchestrator);
	}

	private static TestScheduler NewScheduler()
	{
		TestScheduler scheduler = new();
		scheduler.AdvanceTo(StartsAt.Ticks);
		return scheduler;
	}

	private FakeHaContext NewHa()
	{
		FakeHaContext ha = new();
		_states(ha);

		foreach (Action<FakeHaContext> seed in _seeds)
			seed(ha);

		return ha;
	}
}
