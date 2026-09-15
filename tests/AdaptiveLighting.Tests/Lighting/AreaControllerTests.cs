using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Tests.Common;

using Microsoft.Reactive.Testing;

using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

using Fixture = AdaptiveLighting.Tests.Common.AreaFixture;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Every arrow in the area state machine, driven through fakes and a <see cref="TestScheduler"/>.</summary>
/// <remarks>The scheduler is the controller's only clock. No test here reads wall-clock time.</remarks>
[TestClass]
public sealed partial class AreaControllerTests
{
	private const string Motion = AreaTestBuilder.Motion;
	private const string Light = AreaTestBuilder.Light;
	private const string SecondLight = "light.area_second";
	private const string Lux = AreaTestBuilder.Lux;
	private const string Blocker = "binary_sensor.projector";
	private const string Steps = "binary_sensor.front_steps_motion";

	/// <summary>Hands the test the period-boundary callback, so it can be called back by hand as Home Assistant's own thread would.</summary>
	private sealed class BoundaryCapturingScheduler : IScheduler
	{
		private readonly IScheduler _inner;

		public BoundaryCapturingScheduler(IScheduler inner) => _inner = inner;

		public DateTimeOffset Now => _inner.Now;

		public Action? Boundary { get; private set; }

		public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action) =>
			_inner.Schedule(state, action);

		public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action) =>
			_inner.Schedule(state, dueTime, action);

		public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
		{
			Boundary = () => action(this, state);

			return _inner.Schedule(state, dueTime, action);
		}
	}

	/// <summary>A sun the test moves by hand, and the announcement the orchestrator would make when it does.</summary>
	private sealed class MovableSun
	{
		private readonly Subject<Unit> _moved = new();

		public SunTimes Times { get; private set; } = SunTimes.Unknown;

		public IObservable<Unit> Moved => _moved;

		/// <summary>Moves the sun without announcing it, as an unread sun entity leaves it.</summary>
		public void SetQuietly(TimeOnly? sunrise, TimeOnly? sunset) => Times = new SunTimes(sunrise, sunset);

		public void MoveTo(TimeOnly? sunrise, TimeOnly? sunset)
		{
			SetQuietly(sunrise, sunset);
			_moved.OnNext(Unit.Default);
		}
	}

	/// <summary>A house-state snapshot with everything a call site does not mention defaulted.</summary>
	private static HouseState House(
		bool home = true,
		ModeKind kind = ModeKind.Normal,
		bool killed = false,
		string? modeValue = null,
		string? scene = null,
		ForcedMode? forced = null) =>
		new(home, kind, killed) { ModeValue = modeValue, ActiveScene = scene, Forced = forced };

	/// <summary>The house set to away, with the trackers still reading home: the selector is the only cause.</summary>
	private static HouseState AwayHouse() => House(kind: ModeKind.Away, modeValue: "Borte");

	/// <summary>An <c>input_boolean</c> left on, pinning the Away option over the select.</summary>
	private static ForcedMode ForcedAway(string entityId = "input_boolean.occupancy") =>
		new(ModeKind.Away, "Borte", ModeForceSource.WhileEntityOn, entityId, "on");

	/// <summary>Normal, Borte (away) and Sover (sleep, carrying no ClampPeriodId).</summary>
	private static HouseModeConfig SoverMode() => new()
	{
		Entity = "input_select.husmodus",
		Options =
		[
			new() { Value = "Normal", Kind = ModeKind.Normal },
			new() { Value = "Borte", Kind = ModeKind.Away },
			new() { Value = "Sover", Kind = ModeKind.Sleep }
		]
	};

	/// <summary>Builds a started area at 20:00, inside "evening", so its target holds for the length of a test.</summary>
	private static Fixture Build(
		Action<AreaSettings>? tweak = null,
		Action<GlobalConfig>? tweakGlobal = null,
		IReadOnlyList<string>? ignoreWhenOn = null,
		Action<FakeHaContext>? seed = null,
		IReadOnlyList<TimePeriodConfig>? periods = null,
		IReadOnlyList<RoomLevelOverride>? levels = null,
		HouseState? openingHouse = null,
		MovableSun? sun = null,
		bool watchSun = true,
		Func<IScheduler, IScheduler>? wrapScheduler = null,
		bool withMotionSensor = true,
		string? sceneOnMotion = null,
		IReadOnlyList<string>? lights = null,
		IReadOnlyDictionary<string, IReadOnlySet<string>>? leavesOfEntry = null,
		bool? treatAutomationsAsManual = null,
		IReadOnlyList<string>? leadIn = null,
		bool nameOrigins = false) =>
		new AreaTestBuilder()
			.Seed(seed)
			.Settings(tweak)
			.Global(tweakGlobal)
			.Periods(periods)
			.Lights(lights ?? [Light])
			.MotionSensors(withMotionSensor ? [Motion] : [])
			.IgnoreWhenOn(ignoreWhenOn ?? [])
			.Shape(area => area with
			{
				SceneOnMotion = sceneOnMotion,
				LeavesOfEntry = leavesOfEntry ?? area.LeavesOfEntry,
				TreatAutomationsAsManual = treatAutomationsAsManual,
				LeadInSensors = leadIn ?? []
			})
			.RoomLevels(levels)
			.Sun(() => sun?.Times ?? SunTimes.Unknown)
			.SunMoved(watchSun ? sun?.Moved : null)
			.WrapScheduler(scheduler => wrapScheduler?.Invoke(scheduler) ?? scheduler)
			.NameOrigins(nameOrigins)
			.OpeningHouse(openingHouse ?? House())
			.Build();

	private static void Advance(Fixture fixture, TimeSpan by) => fixture.Scheduler.AdvanceBy(by.Ticks);

	/// <summary>Builds an area whose light is already on before the engine starts.</summary>
	private static Fixture BuildAlreadyLit(Action<AreaSettings>? tweak = null, string lux = "5") =>
		Build(tweak, seed: ha =>
		{
			ha.SetState(Light, "on", new() { ["brightness"] = 178.5 });
			ha.SetState(Lux, lux);
		});

	/// <summary>A change with no user and no parent: a wall switch or dimmer acting on the light itself.</summary>
	private static Context PhysicalDevice() => new() { Id = "physical" };
}
