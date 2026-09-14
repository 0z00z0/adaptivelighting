using System.Reactive.Subjects;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>A save replaces every controller; the one thrown away must not act on anything already on its way to it.</summary>
[TestClass]
public sealed class AreaControllerDisposalTests
{
	private const string Motion = "binary_sensor.area_motion";
	private const string Light = "light.area";
	private const string Lux = "sensor.area_lux";

	private sealed record Fixture(
		TestScheduler Scheduler,
		InterleavingHaContext Ha,
		CapturingScheduler Timers,
		FakeLightActuator Actuator,
		FakeStatePublisher Publisher,
		BehaviorSubject<HouseState> House,
		AreaController Area);

	private static Fixture Build()
	{
		TestScheduler scheduler = new();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero).Ticks);

		FakeHaContext inner = new();
		inner.SetState(Motion, "off");
		inner.SetState(Light, "off");
		inner.SetState(Lux, "5");

		InterleavingHaContext ha = new(inner);
		CapturingScheduler timers = new(scheduler);

		AreaSettings settings = new()
		{
			VacancyTimeoutSeconds = 600,
			PreOffSeconds = 30,
			Darkness = DarknessSource.Lux,
			OverrideDurationMinutes = 120,
			OverrideUntilVacant = false,
			VacancyResetMinutes = 10
		};

		GlobalConfig global = new() { SmoothTransitions = false, CircadianTickSeconds = 60 };

		List<TimePeriodConfig> table =
		[
			new() { Name = "day", Start = "07:00", BrightnessPct = 90, ColorTempKelvin = 4500 },
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new() { Name = "night", Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200 }
		];

		ResolvedArea area = new("Test", settings, [Light], [Motion], [Lux], []);
		FakeLightActuator actuator = new();
		FakeStatePublisher publisher = new();
		BehaviorSubject<HouseState> house = new(HouseState.Initial);

		AreaController controller = new(
			ha, timers, area, global, table,
			new CircadianCalculator(table, global, () => SunTimes.Unknown, null, zone: TimeZoneInfo.Utc),
			actuator, publisher, house, NullLoggerFactory.Instance, areaId: "test_area");

		controller.Start();

		return new Fixture(scheduler, ha, timers, actuator, publisher, house, controller);
	}

	// The delivery runs on its own thread and is already waiting on the controller's lock when Dispose runs. The
	// tick holds that lock while it reads the lux sensor, so Dispose runs inside it and finishes before the
	// delivery gets in: what a save does to an event Home Assistant has already handed over.
	private static void DeliverWhileDisposing(Fixture t, Action deliver)
	{
		Exception? failure = null;
		bool waiting = false;

		Thread delivery = new(() =>
		{
			try
			{
				deliver();
			}
			catch (Exception exception)
			{
				failure = exception;
			}
		});

		t.Ha.BeforeNextRead(Lux, () =>
		{
			delivery.Start();
			waiting = SpinWait.SpinUntil(
				() => (delivery.ThreadState & ThreadState.WaitSleepJoin) != 0,
				TimeSpan.FromSeconds(5));

			t.Area.Dispose();
		});

		t.Scheduler.AdvanceBy(TimeSpan.FromSeconds(61).Ticks);

		Assert.IsTrue(delivery.Join(TimeSpan.FromSeconds(5)), "the delivery never finished");
		Assert.IsTrue(waiting, "the delivery never reached the controller's lock, so nothing raced");
		Assert.IsNull(failure, failure?.ToString());
	}

	[TestMethod]
	public void A_Vacancy_Timeout_Already_In_Flight_Commands_Nothing_Once_The_Controller_Is_Disposed()
	{
		Fixture t = Build();
		t.Ha.Inner.Trigger(Motion, "on");
		t.Ha.Inner.Trigger(Motion, "off");

		Assert.IsTrue(t.Actuator.Last is { On: true }, "the area has to be lit first");

		Action vacancy = t.Timers.LastScheduled(TimeSpan.FromSeconds(600));

		t.Area.Dispose();
		t.Actuator.Clear();
		t.Publisher.Snapshots.Clear();

		vacancy();

		Assert.AreEqual(0, t.Actuator.Applied.Count, "a discarded controller dimmed the lights");
		Assert.AreEqual(0, t.Publisher.Snapshots.Count, "a discarded controller reported over its replacement");
	}

	[TestMethod]
	public void Motion_Already_Delivered_Commands_Nothing_Once_The_Controller_Is_Disposed()
	{
		Fixture t = Build();
		t.Actuator.Clear();
		t.Publisher.Snapshots.Clear();

		DeliverWhileDisposing(t, () => t.Ha.Inner.Trigger(Motion, "on"));

		Assert.AreEqual(0, t.Actuator.Applied.Count, "a discarded controller lit the room");
		Assert.AreEqual(0, t.Publisher.Snapshots.Count, "a discarded controller reported over its replacement");
	}

	[TestMethod]
	public void A_House_Change_Already_Delivered_Commands_Nothing_Once_The_Controller_Is_Disposed()
	{
		Fixture t = Build();
		t.Ha.Inner.Trigger(Motion, "on");
		t.Ha.Inner.Trigger(Motion, "off");

		Assert.IsTrue(t.Actuator.Last is { On: true }, "the area has to be lit first");

		t.Actuator.Clear();
		t.Publisher.Snapshots.Clear();

		DeliverWhileDisposing(t, () => t.House.OnNext(new HouseState(true, ModeKind.Away, false) { ModeValue = "Away" }));

		Assert.AreEqual(0, t.Actuator.Applied.Count, "a discarded controller swept the room dark");
		Assert.AreEqual(0, t.Publisher.Snapshots.Count, "a discarded controller reported over its replacement");
	}
}
