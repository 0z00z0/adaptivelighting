using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     Presence, and the house-wide mode entities that ride alongside it.
/// </summary>
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

	// A tracker flickering while somebody is in the garden must not sweep the house dark.
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

}
