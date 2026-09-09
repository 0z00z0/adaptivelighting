using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

namespace AdaptiveLighting.Tests.Lighting;

[TestClass]
public sealed class PresenceMonitorTests
{
	private static (TestScheduler Scheduler, FakeHaContext Ha) Fixture()
	{
		var scheduler = new TestScheduler();
		scheduler.AdvanceTo(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero).Ticks);
		return (scheduler, new FakeHaContext());
	}

	[TestMethod]
	public void Leaving_Is_Announced_Only_After_The_Debounce()
	{
		var (scheduler, ha) = Fixture();
		ha.SetState("person.a", "home");
		ha.SetState("person.b", "not_home");

		using var monitor = new PresenceMonitor(
			ha, scheduler,
			new GlobalConfig { Persons = ["person.a", "person.b"], AwayDebounceMinutes = 5 },
			NullLogger.Instance);

		var events = new List<PresenceEvent>();
		using var subscription = monitor.Events.Subscribe(events.Add);
		monitor.Start();
		Assert.IsTrue(monitor.IsAnyoneHome);

		ha.Trigger("person.a", "not_home");
		Assert.IsFalse(monitor.IsAnyoneHome, "the flag flips at once; only the announcement waits");

		scheduler.AdvanceBy(TimeSpan.FromMinutes(4).Ticks);
		Assert.AreEqual(0, events.Count);

		scheduler.AdvanceBy(TimeSpan.FromMinutes(1).Ticks);
		CollectionAssert.AreEqual(new[] { PresenceEvent.EveryoneLeft }, events);
	}

	[TestMethod]
	public void Arriving_Is_Not_Debounced()
	{
		var (scheduler, ha) = Fixture();
		ha.SetState("person.a", "home");

		using var monitor = new PresenceMonitor(
			ha, scheduler,
			new GlobalConfig { Persons = ["person.a"], AwayDebounceMinutes = 5 },
			NullLogger.Instance);

		var events = new List<PresenceEvent>();
		using var subscription = monitor.Events.Subscribe(events.Add);
		monitor.Start();

		ha.Trigger("person.a", "not_home");
		scheduler.AdvanceBy(TimeSpan.FromMinutes(5).Ticks);
		events.Clear();

		ha.Trigger("person.a", "home");

		// Not one tick of the scheduler has passed since the tracker reported.
		CollectionAssert.AreEqual(new[] { PresenceEvent.FirstPersonArrived }, events);
	}

	[TestMethod]
	public void A_Full_Leave_And_Return_Emits_Both_Transitions_In_Order()
	{
		var (scheduler, ha) = Fixture();
		ha.SetState("person.a", "home");
		ha.SetState("person.b", "not_home");

		using var monitor = new PresenceMonitor(
			ha, scheduler,
			new GlobalConfig { Persons = ["person.a", "person.b"], AwayDebounceMinutes = 5 },
			NullLogger.Instance);

		var events = new List<PresenceEvent>();
		using var subscription = monitor.Events.Subscribe(events.Add);
		monitor.Start();

		ha.Trigger("person.a", "not_home");
		scheduler.AdvanceBy(TimeSpan.FromMinutes(5).Ticks);
		ha.Trigger("person.b", "home");

		CollectionAssert.AreEqual(new[] { PresenceEvent.EveryoneLeft, PresenceEvent.FirstPersonArrived }, events);
	}

	// A tracker flickering while a person is in the garden must not sweep the house dark.
	[TestMethod]
	public void A_Flicker_Inside_The_Debounce_Is_Not_A_Departure()
	{
		var (scheduler, ha) = Fixture();
		ha.SetState("person.a", "home");

		using var monitor = new PresenceMonitor(
			ha, scheduler,
			new GlobalConfig { Persons = ["person.a"], AwayDebounceMinutes = 5 },
			NullLogger.Instance);

		var events = new List<PresenceEvent>();
		using var subscription = monitor.Events.Subscribe(events.Add);
		monitor.Start();

		ha.Trigger("person.a", "not_home");
		scheduler.AdvanceBy(TimeSpan.FromMinutes(2).Ticks);
		ha.Trigger("person.a", "home");
		scheduler.AdvanceBy(TimeSpan.FromMinutes(10).Ticks);

		Assert.AreEqual(0, events.Count, "no departure was ever announced, so there is no arrival to announce either");
	}

	[TestMethod]
	public void The_House_Is_Home_While_Anyone_Is_Home()
	{
		var (scheduler, ha) = Fixture();
		ha.SetState("person.a", "home");
		ha.SetState("person.b", "home");

		using var monitor = new PresenceMonitor(
			ha, scheduler,
			new GlobalConfig { Persons = ["person.a", "person.b"], AwayDebounceMinutes = 5 },
			NullLogger.Instance);

		var events = new List<PresenceEvent>();
		using var subscription = monitor.Events.Subscribe(events.Add);
		monitor.Start();

		ha.Trigger("person.a", "not_home");
		scheduler.AdvanceBy(TimeSpan.FromMinutes(10).Ticks);

		Assert.IsTrue(monitor.IsAnyoneHome);
		Assert.AreEqual(0, events.Count);
	}

	[TestMethod]
	public void Person_Entities_Are_Discovered_When_None_Are_Configured()
	{
		var (scheduler, ha) = Fixture();
		ha.SetState("person.x", "home");
		ha.SetState("person.a", "home");
		ha.SetState("light.y", "on");

		using var monitor = new PresenceMonitor(ha, scheduler, new GlobalConfig(), NullLogger.Instance);

		CollectionAssert.AreEqual(new[] { "person.a", "person.x" }, monitor.WatchedEntityIds.ToArray());
	}

	[TestMethod]
	public void A_House_With_Nobody_To_Watch_Is_Assumed_Permanently_Occupied()
	{
		var (scheduler, ha) = Fixture();

		using var monitor = new PresenceMonitor(ha, scheduler, new GlobalConfig(), NullLogger.Instance);
		monitor.Start();

		Assert.AreEqual(0, monitor.WatchedEntityIds.Count);
		Assert.IsTrue(monitor.IsAnyoneHome,
			"an engine that decides nobody is home and sweeps every light off is far worse than one that never sweeps");
	}

	// The opening publication tells the areas the house is away, so that is an announcement and the first arrival
	// has to be one too. Without this the engine started in an empty house never comes home at all.
	[TestMethod]
	public void An_Arrival_At_A_House_That_Started_Empty_Is_Announced()
	{
		var (scheduler, ha) = Fixture();
		ha.SetState("person.a", "not_home");

		using var monitor = new PresenceMonitor(
			ha, scheduler,
			new GlobalConfig { Persons = ["person.a"], AwayDebounceMinutes = 5 },
			NullLogger.Instance);

		var events = new List<PresenceEvent>();
		using var subscription = monitor.Events.Subscribe(events.Add);
		monitor.Start();

		Assert.IsFalse(monitor.IsAnyoneHome, "arranged: the engine started in an empty house");

		ha.Trigger("person.a", "home");

		CollectionAssert.AreEqual(new[] { PresenceEvent.FirstPersonArrived }, events);
	}

	// ---- movement counts as somebody being home ------------------------------------------------

	private const string Motion = "binary_sensor.stue_bevegelse";

	private static PresenceMonitor WithMotion(FakeHaContext ha, TestScheduler scheduler, int windowMinutes = 30) =>
		new(ha, scheduler,
			new GlobalConfig
			{
				Persons = ["person.a"],
				AwayDebounceMinutes = 5,
				MotionPresenceMinutes = windowMinutes
			},
			NullLogger.Instance,
			[Motion]);

	/// <summary>An empty house that has already announced its departure, so the next thing to happen is the arrival.</summary>
	private static (TestScheduler Scheduler, FakeHaContext Ha, PresenceMonitor Monitor, List<PresenceEvent> Events) AwayFixture(
		int windowMinutes = 30)
	{
		var (scheduler, ha) = Fixture();
		ha.SetState("person.a", "home");
		ha.SetState(Motion, "off");

		PresenceMonitor monitor = WithMotion(ha, scheduler, windowMinutes);

		var events = new List<PresenceEvent>();
		monitor.Events.Subscribe(events.Add);
		monitor.Start();

		ha.Trigger("person.a", "not_home");
		scheduler.AdvanceBy(TimeSpan.FromMinutes(5).Ticks);
		Assert.IsFalse(monitor.IsAnyoneHome, "arranged: the house is empty and has said so");
		events.Clear();

		return (scheduler, ha, monitor, events);
	}

	[TestMethod]
	public void Movement_Makes_The_House_Occupied_As_A_Tracker_Would()
	{
		var (_, ha, monitor, events) = AwayFixture();
		using var _monitor = monitor;

		ha.Trigger(Motion, "on");

		Assert.IsTrue(monitor.IsAnyoneHome, "somebody is moving about, so somebody is home");
		CollectionAssert.AreEqual(new[] { PresenceEvent.FirstPersonArrived }, events,
			"an arrival, announced at once, exactly as a phone walking in is");
	}

	[TestMethod]
	public void Movement_Holds_The_House_Occupied_For_The_Window_And_No_Longer()
	{
		var (scheduler, ha, monitor, events) = AwayFixture();
		using var _monitor = monitor;

		ha.Trigger(Motion, "on");
		events.Clear();

		scheduler.AdvanceBy(TimeSpan.FromMinutes(29).Ticks);
		Assert.IsTrue(monitor.IsAnyoneHome, "still inside the 30-minute window");
		Assert.AreEqual(0, events.Count);

		// The window runs out. That is a departure, so it is debounced like any other.
		scheduler.AdvanceBy(TimeSpan.FromMinutes(1).Ticks);
		Assert.IsFalse(monitor.IsAnyoneHome);
		Assert.AreEqual(0, events.Count, "the flag flips at once; only the announcement waits");

		scheduler.AdvanceBy(TimeSpan.FromMinutes(5).Ticks);
		CollectionAssert.AreEqual(new[] { PresenceEvent.EveryoneLeft }, events);
	}

	[TestMethod]
	public void Each_Movement_Starts_The_Window_Again()
	{
		var (scheduler, ha, monitor, events) = AwayFixture();
		using var _monitor = monitor;

		ha.Trigger(Motion, "on");
		events.Clear();

		for (int step = 0; step < 5; step++)
		{
			scheduler.AdvanceBy(TimeSpan.FromMinutes(25).Ticks);
			ha.Trigger(Motion, "off");
			ha.Trigger(Motion, "on");
		}

		Assert.IsTrue(monitor.IsAnyoneHome, "two hours of movement, none of it more than 25 minutes apart");
		Assert.AreEqual(0, events.Count, "nothing left and nothing arrived; the house was occupied throughout");

		scheduler.AdvanceBy(TimeSpan.FromMinutes(35).Ticks);
		CollectionAssert.AreEqual(new[] { PresenceEvent.EveryoneLeft }, events,
			"the last movement's window is the one that runs out");
	}

	// A tracker walking back in must not be cut short by a motion window opened before it.
	[TestMethod]
	public void An_Expiring_Window_Does_Not_Send_A_House_With_A_Tracker_In_It_Away()
	{
		var (scheduler, ha, monitor, events) = AwayFixture();
		using var _monitor = monitor;

		ha.Trigger(Motion, "on");
		events.Clear();

		ha.Trigger("person.a", "home");
		scheduler.AdvanceBy(TimeSpan.FromMinutes(60).Ticks);

		Assert.IsTrue(monitor.IsAnyoneHome);
		Assert.AreEqual(0, events.Count, "the window ran out under a phone that is standing in the hall");
	}

	[TestMethod]
	public void Movement_Inside_The_Departure_Debounce_Cancels_The_Departure()
	{
		var (scheduler, ha) = Fixture();
		ha.SetState("person.a", "home");
		ha.SetState(Motion, "off");

		using PresenceMonitor monitor = WithMotion(ha, scheduler);

		var events = new List<PresenceEvent>();
		using var subscription = monitor.Events.Subscribe(events.Add);
		monitor.Start();

		ha.Trigger("person.a", "not_home");
		scheduler.AdvanceBy(TimeSpan.FromMinutes(2).Ticks);
		ha.Trigger(Motion, "on");
		scheduler.AdvanceBy(TimeSpan.FromMinutes(10).Ticks);

		Assert.AreEqual(0, events.Count,
			"the phone left and its owner did not; no departure was announced, so there is no arrival either");
		Assert.IsTrue(monitor.IsAnyoneHome);
	}

	[TestMethod]
	public void A_Zero_Window_Watches_Trackers_Only()
	{
		var (_, ha, monitor, events) = AwayFixture(windowMinutes: 0);
		using var _monitor = monitor;

		ha.Trigger(Motion, "on");

		Assert.IsFalse(monitor.IsAnyoneHome, "movement-as-presence is switched off, so the house is still empty");
		Assert.AreEqual(0, events.Count);
		Assert.AreEqual(0, monitor.WatchedMotionSensors.Count);
	}

	[TestMethod]
	public void A_House_With_Nobody_To_Watch_Stays_Permanently_Occupied_Whatever_Moves()
	{
		var (scheduler, ha) = Fixture();
		ha.SetState(Motion, "off");

		using var monitor = new PresenceMonitor(
			ha, scheduler,
			new GlobalConfig { MotionPresenceMinutes = 30 },
			NullLogger.Instance,
			[Motion]);

		monitor.Start();
		scheduler.AdvanceBy(TimeSpan.FromHours(4).Ticks);

		Assert.IsTrue(monitor.IsAnyoneHome,
			"with no tracker to compose against, a quiet house would start sweeping itself; it must not");
	}
}
