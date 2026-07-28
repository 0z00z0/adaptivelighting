using System.Text.Json;

using AdaptiveLighting.LastSeen;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

namespace AdaptiveLighting.Tests.LastSeen;

/// <summary>
///     The module's whole reason for existing: a Home Assistant restart resets every timestamp in the house, and
///     the record must not believe it.
/// </summary>
[TestClass]
public sealed class LastSeenTrackerTests
{
	private static readonly DateTimeOffset Noon = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

	private const string Dead = "binary_sensor.dead_for_a_week";
	private const string Lux = "sensor.hall_lux";

	// ===================== fixture =====================

	/// <summary>A temp directory, a fake house and a clock the test drives.</summary>
	private sealed class Fixture : IDisposable
	{
		public Fixture(LastSeenOptions? options = null)
		{
			Directory = Path.Combine(Path.GetTempPath(), "adaptive-lighting-last-seen-" + Guid.NewGuid().ToString("N"));
			System.IO.Directory.CreateDirectory(Directory);
			ConfigPath = Path.Combine(Directory, "b1.yaml");
			Options = options ?? new LastSeenOptions();
			Scheduler.AdvanceTo(Noon.Ticks);
		}

		public string Directory { get; }

		public string ConfigPath { get; }

		public LastSeenOptions Options { get; }

		public StampedHaContext Ha { get; } = new();

		public TestScheduler Scheduler { get; } = new();

		public DateTimeOffset Now => Scheduler.Now.ToUniversalTime();

		public LastSeenStore NewStore() => new(ConfigPath, NullLogger<LastSeenStore>.Instance);

		/// <summary>A tracker over the same directory and the same house. Not started.</summary>
		public LastSeenTracker NewTracker() =>
			new(Ha, Scheduler, NewStore(), Options, NullLogger<LastSeenTracker>.Instance);

		public LastSeenTracker Started()
		{
			LastSeenTracker tracker = NewTracker();
			tracker.Start();
			return tracker;
		}

		/// <summary>Runs one census.</summary>
		public void Tick() => Scheduler.AdvanceBy(Options.CensusInterval.Ticks);

		public void Advance(TimeSpan span) => Scheduler.AdvanceBy(span.Ticks);

		/// <summary>
		///     A house with a realistic spread of timestamps: one sensor dead for a week, the rest reporting at
		///     staggered intervals. The spread is what tells a running house from one that has just restarted.
		/// </summary>
		public void SeedHouse(int motionSensors = 19)
		{
			Ha.Set(Dead, "off", Now - TimeSpan.FromDays(7), "motion");

			for (int index = 0; index < motionSensors; index++)
				Ha.Set($"binary_sensor.motion_{index}", "off", Now - TimeSpan.FromMinutes(index * 7), "motion");

			Ha.Set(Lux, "3", Now - TimeSpan.FromMinutes(2), "illuminance");
		}

		public void Dispose()
		{
			try
			{
				System.IO.Directory.Delete(Directory, recursive: true);
			}
			catch (IOException)
			{
				// A leftover temp directory is litter, not a test failure.
			}
		}
	}

	// ===================== the restart trap =====================

	[TestMethod]
	public void A_Restart_Burst_Does_Not_Advance_The_Record()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();

		using LastSeenTracker tracker = fixture.Started();

		DateTimeOffset weekAgo = Noon - TimeSpan.FromDays(7);
		Assert.AreEqual(weekAgo, tracker.LastSeenUtc(Dead), "the dead sensor's real last report should have been seeded from Home Assistant");

		fixture.Advance(TimeSpan.FromMinutes(10));

		// Home Assistant restarts: every timestamp in the house collapses to one instant, which is exactly what was
		// measured on the live house — 51 motion sensors all reading the same 2.3 hours.
		DateTimeOffset restart = fixture.Now;
		fixture.Ha.RestartHomeAssistant(restart);
		fixture.Tick();

		Assert.AreEqual(restart, tracker.HomeAssistantStartedUtc, "the collapse should have been read as a restart");
		Assert.AreEqual(weekAgo, tracker.LastSeenUtc(Dead), "a restore is not evidence: the dead sensor must still read a week");
	}

	[TestMethod]
	public void Every_Entity_Keeps_Its_Own_Age_Across_A_Restart()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();

		using LastSeenTracker tracker = fixture.Started();

		DateTimeOffset before = tracker.LastSeenUtc("binary_sensor.motion_5") ?? default;

		fixture.Ha.RestartHomeAssistant(fixture.Now);
		fixture.Tick();

		Assert.AreEqual(before, tracker.LastSeenUtc("binary_sensor.motion_5"));
		Assert.AreEqual(Noon - TimeSpan.FromDays(7), tracker.LastSeenUtc(Dead));
		Assert.AreNotEqual(tracker.LastSeenUtc(Dead), tracker.LastSeenUtc("binary_sensor.motion_5"),
			"the whole point is that a dead sensor and a healthy one stop looking identical");
	}

	[TestMethod]
	public void A_Report_Inside_The_Restart_Window_Is_Not_Believed()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();

		using LastSeenTracker tracker = fixture.Started();

		fixture.Ha.RestartHomeAssistant(fixture.Now);
		fixture.Tick();

		// Two minutes in, well inside the five-minute grace. A restore arriving this late is indistinguishable
		// from this, which is why neither is believed.
		fixture.Advance(TimeSpan.FromMinutes(2));
		fixture.Ha.Set(Dead, "on", fixture.Now, "motion");
		fixture.Tick();

		Assert.AreEqual(Noon - TimeSpan.FromDays(7), tracker.LastSeenUtc(Dead));
	}

	[TestMethod]
	public void A_Genuine_Report_After_The_Restart_Window_Advances_The_Record()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();

		using LastSeenTracker tracker = fixture.Started();

		fixture.Ha.RestartHomeAssistant(fixture.Now);
		fixture.Tick();

		fixture.Advance(TimeSpan.FromMinutes(6));
		DateTimeOffset reportedAt = fixture.Now;
		fixture.Ha.Set("binary_sensor.motion_3", "on", reportedAt, "motion");
		fixture.Tick();

		Assert.AreEqual(reportedAt, tracker.LastSeenUtc("binary_sensor.motion_3"), "past the grace, a report is a report again");
		Assert.AreEqual(Noon - TimeSpan.FromDays(7), tracker.LastSeenUtc(Dead), "and the dead sensor is still dead");
	}

	[TestMethod]
	public void A_Tight_Population_Does_Not_Declare_A_Restart_Every_Census()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();

		using LastSeenTracker tracker = fixture.Started();

		fixture.Ha.RestartHomeAssistant(fixture.Now);
		fixture.Tick();

		DateTimeOffset? declared = tracker.HomeAssistantStartedUtc;

		// The population stays tight for a while after a restart. That is one restart, not five more.
		for (int census = 0; census < 5; census++)
			fixture.Tick();

		Assert.AreEqual(declared, tracker.HomeAssistantStartedUtc);
	}

	[TestMethod]
	public void The_HomeAssistant_Start_Event_Also_Opens_The_Window()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();

		using LastSeenTracker tracker = fixture.Started();

		DateTimeOffset announced = fixture.Now;
		fixture.Ha.FireEvent("homeassistant_start");

		Assert.AreEqual(announced, tracker.HomeAssistantStartedUtc);

		// A timestamp inside the window is refused even though the population has not visibly collapsed.
		fixture.Advance(TimeSpan.FromMinutes(1));
		fixture.Ha.Set(Dead, "on", fixture.Now, "motion");
		fixture.Tick();

		Assert.AreEqual(Noon - TimeSpan.FromDays(7), tracker.LastSeenUtc(Dead));
	}

	// ===================== quiet is not dead =====================

	[TestMethod]
	public void A_Sensor_Whose_Value_Never_Changes_Is_Still_Counted_As_Alive()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();

		using LastSeenTracker tracker = fixture.Started();

		// 3 lx all night. The state string never moves; Home Assistant's timestamp does, because the sensor keeps
		// reporting. Counting only value changes would call this healthy sensor dead by morning.
		DateTimeOffset lastReport = default;

		for (int minute = 0; minute < 5; minute++)
		{
			fixture.Advance(TimeSpan.FromMinutes(1));
			lastReport = fixture.Now;
			fixture.Ha.Set(Lux, "3", lastReport, "illuminance");
			fixture.Tick();
		}

		Assert.AreEqual(lastReport, tracker.LastSeenUtc(Lux));
		Assert.IsFalse(tracker.HasBeenSilentFor(Lux, TimeSpan.FromMinutes(2)));
	}

	[TestMethod]
	public void A_Sensor_That_Stops_Reporting_Goes_Silent_Even_While_Its_Value_Sits_There()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();

		using LastSeenTracker tracker = fixture.Started();

		// The value stays visible in Home Assistant. Nothing touches the timestamp, because nothing reported.
		fixture.Advance(TimeSpan.FromMinutes(30));
		fixture.Tick();

		Assert.IsTrue(tracker.HasBeenSilentFor(Lux, TimeSpan.FromMinutes(20)));
		Assert.IsFalse(tracker.HasBeenSilentFor(Lux, TimeSpan.FromHours(2)));
	}

	// ===================== the unknown contract =====================

	[TestMethod]
	public void An_Entity_Nothing_Has_Ever_Tracked_Is_Unknown_And_Not_Silent()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();

		using LastSeenTracker tracker = fixture.Started();

		Assert.IsNull(tracker.LastSeenUtc("sensor.never_heard_of_it"));
		Assert.IsNull(tracker.SilenceOf("sensor.never_heard_of_it"));
		Assert.IsFalse(tracker.HasBeenSilentFor("sensor.never_heard_of_it", TimeSpan.FromSeconds(1)),
			"unknown must read as 'carry on', never as 'stale'");
	}

	[TestMethod]
	public void A_Fresh_Install_During_A_Restart_Declares_Nothing_Dead()
	{
		using Fixture fixture = new();

		// No cache at all, and Home Assistant restarted a moment ago, so every timestamp in the house is a restore.
		fixture.SeedHouse();
		fixture.Ha.RestartHomeAssistant(fixture.Now);

		using LastSeenTracker tracker = fixture.Started();

		Assert.AreEqual(fixture.Now, tracker.HomeAssistantStartedUtc);

		foreach (string entityId in new[] { Dead, Lux, "binary_sensor.motion_3" })
		{
			Assert.IsNull(tracker.LastSeenUtc(entityId), $"{entityId} should be unknown, not seeded from a restore");
			Assert.IsFalse(tracker.HasBeenSilentFor(entityId, TimeSpan.FromMinutes(1)), $"{entityId} must not be called stale");
		}
	}

	[TestMethod]
	public void A_Fresh_Install_Against_A_Long_Running_Home_Assistant_Seeds_From_Its_Timestamps()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();

		using LastSeenTracker tracker = fixture.Started();

		// Nothing has reset these, so they are honest evidence and there is no reason to throw them away.
		Assert.AreEqual(Noon - TimeSpan.FromDays(7), tracker.LastSeenUtc(Dead));
		Assert.AreEqual(Noon - TimeSpan.FromMinutes(2), tracker.LastSeenUtc(Lux));
		Assert.IsTrue(tracker.HasBeenSilentFor(Dead, TimeSpan.FromHours(6)));
	}

	[TestMethod]
	public void HasBeenSilentFor_Answers_Only_What_It_Knows()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();

		using LastSeenTracker tracker = fixture.Started();

		Assert.IsTrue(tracker.HasBeenSilentFor(Dead, TimeSpan.FromDays(1)), "a week is longer than a day");
		Assert.IsFalse(tracker.HasBeenSilentFor(Dead, TimeSpan.FromDays(30)), "a week is not longer than a month");
		Assert.IsFalse(tracker.HasBeenSilentFor(Dead, TimeSpan.Zero), "a threshold of nothing never matches");
		Assert.IsFalse(tracker.HasBeenSilentFor("", TimeSpan.FromDays(1)));
	}

	[TestMethod]
	public void The_Home_Assistant_Timestamp_Survives_The_Wire_Format_Exactly()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();

		using LastSeenTracker tracker = fixture.Started();

		// Guards the DateTime/DateTimeOffset conversion: a timestamp that arrives without a UTC marker and is read
		// as local time would be silently hours out, and every threshold with it.
		Assert.AreEqual(TimeSpan.FromDays(7), tracker.SilenceOf(Dead));
	}

	// ===================== surviving an engine restart =====================

	[TestMethod]
	public void The_Record_Survives_An_Engine_Restart()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();

		LastSeenTracker first = fixture.Started();
		int tracked = first.TrackedCount;
		first.Dispose();

		using LastSeenTracker second = fixture.Started();

		Assert.AreEqual(tracked, second.TrackedCount);
		Assert.AreEqual(Noon - TimeSpan.FromDays(7), second.LastSeenUtc(Dead));
		Assert.AreEqual(Noon - TimeSpan.FromMinutes(2), second.LastSeenUtc(Lux));
	}

	[TestMethod]
	public void A_Restart_Detected_Before_A_Redeploy_Is_Still_Known_After_It()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();

		LastSeenTracker first = fixture.Started();
		DateTimeOffset restart = fixture.Now;
		fixture.Ha.RestartHomeAssistant(restart);
		fixture.Tick();
		first.Dispose();

		// The engine is redeployed a minute later, into a house whose timestamps are all still the restore values.
		fixture.Advance(TimeSpan.FromMinutes(1));

		using LastSeenTracker second = fixture.Started();

		Assert.AreEqual(restart, second.HomeAssistantStartedUtc, "the restart estimate has to outlive the process that made it");
		Assert.AreEqual(Noon - TimeSpan.FromDays(7), second.LastSeenUtc(Dead), "and the restore must still not be believed");
	}

	[TestMethod]
	public void A_Missing_Cache_Degrades_To_Unknown_Rather_Than_To_Dead()
	{
		using Fixture fixture = new();

		// Nothing in Home Assistant either: the tracker has neither a file nor a house to look at.
		using LastSeenTracker tracker = fixture.Started();

		Assert.AreEqual(0, tracker.TrackedCount);
		Assert.IsNull(tracker.LastSeenUtc(Lux));
		Assert.IsFalse(tracker.HasBeenSilentFor(Lux, TimeSpan.FromSeconds(1)));
	}

	[TestMethod]
	public void A_Corrupt_File_Costs_Its_Own_Entities_And_Nothing_Else()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();

		LastSeenTracker first = fixture.Started();
		first.Dispose();

		LastSeenStore store = fixture.NewStore();
		File.WriteAllText(store.PathFor(LastSeenBuckets.Motion), "{ this is not json");

		LastSeenCacheLoad load = store.Load();

		Assert.AreEqual(1, load.FilesUnreadable);
		Assert.IsFalse(load.Entities.ContainsKey(Dead), "the motion file was unreadable, so its entities come back as unknown");
		Assert.IsTrue(load.Entities.ContainsKey(Lux), "and the illuminance file, which was fine, keeps its history");

		// The tracker starts on that anyway: an unreadable file is a warning, never a fault.
		using LastSeenTracker second = fixture.Started();

		Assert.AreEqual(Noon - TimeSpan.FromMinutes(2), second.LastSeenUtc(Lux));
		Assert.IsFalse(second.HasBeenSilentFor("sensor.something_that_was_in_the_corrupt_file", TimeSpan.FromSeconds(1)),
			"a lost record must read as unknown, never as stale");
	}

	[TestMethod]
	public void An_Empty_Home_Assistant_Concludes_Nothing()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();

		LastSeenTracker first = fixture.Started();
		int tracked = first.TrackedCount;
		first.Dispose();

		foreach (string entityId in new[] { Dead, Lux })
			fixture.Ha.Remove(entityId);

		foreach (int index in Enumerable.Range(0, 19))
			fixture.Ha.Remove($"binary_sensor.motion_{index}");

		using LastSeenTracker second = fixture.Started();

		Assert.AreEqual(tracked, second.TrackedCount, "a house that answers nothing is a connection problem, not a house where everything died");
		Assert.AreEqual(Noon - TimeSpan.FromDays(7), second.LastSeenUtc(Dead));
	}

	// ===================== bounding =====================

	[TestMethod]
	public void An_Entity_That_Disappears_From_Home_Assistant_Is_Eventually_Dropped()
	{
		using Fixture fixture = new(new LastSeenOptions { Retention = TimeSpan.FromMinutes(10) });
		fixture.SeedHouse();

		using LastSeenTracker tracker = fixture.Started();
		int tracked = tracker.TrackedCount;

		fixture.Ha.Remove(Lux);

		fixture.Advance(TimeSpan.FromMinutes(5));
		Assert.AreEqual(tracked, tracker.TrackedCount, "not yet: the record is younger than the retention");

		fixture.Advance(TimeSpan.FromMinutes(10));

		Assert.AreEqual(tracked - 1, tracker.TrackedCount);
		Assert.IsNull(tracker.LastSeenUtc(Lux));
	}

	[TestMethod]
	public void An_Entity_That_Is_Merely_Quiet_Is_Never_Dropped()
	{
		using Fixture fixture = new(new LastSeenOptions { Retention = TimeSpan.FromMinutes(10) });
		fixture.SeedHouse();

		using LastSeenTracker tracker = fixture.Started();
		int tracked = tracker.TrackedCount;

		// The dead sensor is a week silent and still in Home Assistant. Forgetting it would erase the finding.
		fixture.Advance(TimeSpan.FromMinutes(20));

		Assert.AreEqual(tracked, tracker.TrackedCount);
		Assert.AreEqual(Noon - TimeSpan.FromDays(7), tracker.LastSeenUtc(Dead));
	}

	[TestMethod]
	public void The_Ceiling_Drops_The_Oldest_Absent_Records_First()
	{
		using Fixture fixture = new(new LastSeenOptions { MaxTracked = 3, Retention = TimeSpan.FromDays(365) });

		fixture.Ha.Set("sensor.a", "1", fixture.Now - TimeSpan.FromHours(5));
		fixture.Ha.Set("sensor.b", "1", fixture.Now - TimeSpan.FromHours(4));
		fixture.Ha.Set("sensor.c", "1", fixture.Now - TimeSpan.FromHours(3));
		fixture.Ha.Set("sensor.d", "1", fixture.Now - TimeSpan.FromHours(2));
		fixture.Ha.Set("sensor.e", "1", fixture.Now - TimeSpan.FromHours(1));

		using LastSeenTracker tracker = fixture.Started();
		Assert.AreEqual(5, tracker.TrackedCount);

		fixture.Ha.Remove("sensor.a");
		fixture.Ha.Remove("sensor.b");
		fixture.Ha.Remove("sensor.c");
		fixture.Tick();

		Assert.AreEqual(3, tracker.TrackedCount);
		Assert.IsNull(tracker.LastSeenUtc("sensor.a"), "the oldest absent record goes first");
		Assert.IsNull(tracker.LastSeenUtc("sensor.b"));
		Assert.IsNotNull(tracker.LastSeenUtc("sensor.c"), "and the ceiling stops as soon as it is satisfied");
		Assert.IsNotNull(tracker.LastSeenUtc("sensor.d"), "entities Home Assistant still reports are never dropped for the ceiling");
		Assert.IsNotNull(tracker.LastSeenUtc("sensor.e"));
	}

	[TestMethod]
	public void A_House_Larger_Than_The_Ceiling_Is_Still_Tracked_In_Full()
	{
		using Fixture fixture = new(new LastSeenOptions { MaxTracked = 3 });

		for (int index = 0; index < 6; index++)
			fixture.Ha.Set($"sensor.s{index}", "1", fixture.Now - TimeSpan.FromMinutes(index * 11));

		using LastSeenTracker tracker = fixture.Started();
		fixture.Tick();

		Assert.AreEqual(6, tracker.TrackedCount, "dropping present entities would only have them re-added next census");
	}

	// ===================== filing =====================

	[TestMethod]
	public void An_Entity_That_Changes_Class_Moves_Rather_Than_Duplicating()
	{
		using Fixture fixture = new();
		fixture.Ha.Set("binary_sensor.odd", "off", fixture.Now - TimeSpan.FromMinutes(1), "motion");

		LastSeenTracker first = fixture.Started();
		first.Dispose();

		LastSeenStore store = fixture.NewStore();
		StringAssert.Contains(File.ReadAllText(store.PathFor(LastSeenBuckets.Motion)), "binary_sensor.odd");

		// An integration update changes the device class, so the entity is no longer motion.
		fixture.Ha.Set("binary_sensor.odd", "off", fixture.Now, "door");

		LastSeenTracker second = fixture.Started();
		second.Dispose();

		Assert.IsFalse(File.Exists(store.PathFor(LastSeenBuckets.Motion)),
			"the old bucket has nothing left in it, so its file goes rather than sitting there empty");
		StringAssert.Contains(File.ReadAllText(store.PathFor("door")), "binary_sensor.odd");

		LastSeenCacheLoad load = store.Load();
		Assert.AreEqual(0, load.DuplicatesResolved);
		Assert.AreEqual(1, load.Entities.Count);
	}

	[TestMethod]
	public void The_Files_Hold_What_They_Are_Named_For()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();
		fixture.Ha.Set("light.kitchen", "on", fixture.Now - TimeSpan.FromMinutes(3));
		fixture.Ha.Set("sensor.washing_machine", "42", fixture.Now - TimeSpan.FromMinutes(3), "power");
		fixture.Ha.Set("sensor.hall_temperature", "21", fixture.Now - TimeSpan.FromMinutes(3), "temperature");
		fixture.Ha.Set("person.espen", "home", fixture.Now - TimeSpan.FromMinutes(3));

		LastSeenTracker tracker = fixture.Started();
		tracker.Dispose();

		LastSeenStore store = fixture.NewStore();

		// The three curated buckets are untouched by the split.
		StringAssert.Contains(File.ReadAllText(store.PathFor(LastSeenBuckets.Illuminance)), Lux);
		StringAssert.Contains(File.ReadAllText(store.PathFor(LastSeenBuckets.Motion)), Dead);
		StringAssert.Contains(File.ReadAllText(store.PathFor(LastSeenBuckets.Light)), "light.kitchen");

		// What used to be one catch-all file is now one file per class, and one per domain for a classless entity.
		StringAssert.Contains(File.ReadAllText(store.PathFor("power")), "sensor.washing_machine");
		StringAssert.Contains(File.ReadAllText(store.PathFor("temperature")), "sensor.hall_temperature");
		StringAssert.Contains(File.ReadAllText(store.PathFor("person")), "person.espen");

		Assert.IsFalse(File.Exists(store.PathFor(LastSeenBuckets.Unclassified)),
			"a house where everything has a class or a domain needs no catch-all file at all");

		Assert.IsFalse(File.ReadAllText(store.PathFor(LastSeenBuckets.Illuminance)).Contains(Dead, StringComparison.Ordinal),
			"somebody diagnosing their light-level sensors should not have to read past the motion ones");
	}

	[TestMethod]
	public void Nothing_Is_Dropped_By_The_Split()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();

		// A spread of the shapes a real instance has: classed sensors, classless ones, odd domains, and a device
		// class that has to be sanitised before it can be a file name.
		fixture.Ha.Set("light.kitchen", "on", fixture.Now - TimeSpan.FromMinutes(3));
		fixture.Ha.Set("sensor.washing_machine", "42", fixture.Now - TimeSpan.FromMinutes(3), "power");
		fixture.Ha.Set("sensor.hall_temperature", "21", fixture.Now - TimeSpan.FromMinutes(4), "temperature");
		fixture.Ha.Set("sensor.uptime", "5 days", fixture.Now - TimeSpan.FromMinutes(5));
		fixture.Ha.Set("binary_sensor.light_detected", "on", fixture.Now - TimeSpan.FromMinutes(6), "light");
		fixture.Ha.Set("person.espen", "home", fixture.Now - TimeSpan.FromMinutes(7));
		fixture.Ha.Set("sun.sun", "above_horizon", fixture.Now - TimeSpan.FromMinutes(8));
		fixture.Ha.Set("automation.wake_up", "on", fixture.Now - TimeSpan.FromMinutes(9));
		fixture.Ha.Set("sensor.weird", "1", fixture.Now - TimeSpan.FromMinutes(10), "Kitchen / Ambient");
		fixture.Ha.Set("no_domain_at_all", "1", fixture.Now - TimeSpan.FromMinutes(11));

		LastSeenTracker tracker = fixture.Started();
		int tracked = tracker.TrackedCount;
		tracker.Dispose();

		LastSeenStore store = fixture.NewStore();
		LastSeenCacheLoad load = store.Load();

		Assert.AreEqual(tracked, load.Entities.Count, "every tracked entity has to land in some file; a class nobody predicted is the one most likely to be misbehaving");
		Assert.AreEqual(0, load.DuplicatesResolved);

		StringAssert.Contains(File.ReadAllText(store.PathFor("Kitchen / Ambient")), "sensor.weird", "a class that needed sanitising still has a file");
		StringAssert.Contains(File.ReadAllText(store.PathFor("binary_sensor_light")), "binary_sensor.light_detected");
		StringAssert.Contains(File.ReadAllText(store.PathFor(LastSeenBuckets.Unclassified)), "no_domain_at_all",
			"an id with neither a class nor a domain still has a defined home");

		Assert.IsFalse(File.ReadAllText(store.PathFor(LastSeenBuckets.Light)).Contains("binary_sensor.light_detected", StringComparison.Ordinal),
			"a binary sensor that detects light is not a lamp, and the light file must hold exactly what it held before");
	}

	[TestMethod]
	public void One_Changed_Entity_Writes_One_File()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();
		fixture.Ha.Set("light.kitchen", "on", fixture.Now - TimeSpan.FromMinutes(3));
		fixture.Ha.Set("sensor.washing_machine", "42", fixture.Now - TimeSpan.FromMinutes(4), "power");
		fixture.Ha.Set("sensor.hall_temperature", "21", fixture.Now - TimeSpan.FromMinutes(6), "temperature");

		using LastSeenTracker tracker = fixture.Started();
		LastSeenStore store = fixture.NewStore();

		// The first flush writes every bucket, because every record in them is new.
		fixture.Advance(fixture.Options.FlushInterval);
		Dictionary<string, DateTimeOffset> before = SavedAtByFile(store);

		Assert.IsTrue(before.Count >= 5, $"the split should have given each class its own file, and produced {before.Count}");

		// One sensor reports. With dozens of files rather than four, rebuilding all of them would multiply the
		// write cost by the number of classes in the house for no gain at all.
		fixture.Advance(TimeSpan.FromMinutes(1));
		fixture.Ha.Set("sensor.washing_machine", "43", fixture.Now, "power");
		fixture.Advance(fixture.Options.FlushInterval);

		Dictionary<string, DateTimeOffset> after = SavedAtByFile(store);
		List<string> rewritten = [.. after.Where(pair => pair.Value != before[pair.Key]).Select(pair => pair.Key).Order(StringComparer.Ordinal)];

		CollectionAssert.AreEqual(new[] { "b1.last-seen.power.json" }, rewritten, $"rewritten: {string.Join(", ", rewritten)}");
	}

	// ===================== upgrading from the pre-split cache =====================

	[TestMethod]
	public void A_Pre_Split_Catch_All_Is_Redistributed_Without_Losing_A_Record()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();

		DateTimeOffset heardAt = Noon - TimeSpan.FromHours(3);

		// Older in Home Assistant than in the cache, so anything the cache carried over is visible rather than
		// immediately overwritten by a fresh census.
		fixture.Ha.Set("sensor.washing_machine", "0", Noon - TimeSpan.FromHours(5), "power");
		fixture.Ha.Set("person.espen", "home", Noon - TimeSpan.FromHours(5));

		WritePreSplitCatchAll(fixture, heardAt, "sensor.washing_machine", "person.espen", "sensor.long_gone");

		LastSeenStore store = fixture.NewStore();
		Assert.IsTrue(File.Exists(store.PathFor(LastSeenBuckets.Unclassified)), "the file an installation upgrading already has");

		LastSeenTracker tracker = fixture.Started();

		Assert.AreEqual(heardAt, tracker.LastSeenUtc("sensor.washing_machine"), "a record that survived a restart must not be lost to a rename");
		Assert.AreEqual(heardAt, tracker.LastSeenUtc("person.espen"));
		Assert.AreEqual(heardAt, tracker.LastSeenUtc("sensor.long_gone"), "and one Home Assistant no longer reports is still a record, not a leftover");

		tracker.Dispose();

		StringAssert.Contains(File.ReadAllText(store.PathFor("power")), "sensor.washing_machine");
		StringAssert.Contains(File.ReadAllText(store.PathFor("person")), "person.espen");

		string catchAll = File.ReadAllText(store.PathFor(LastSeenBuckets.Unclassified));

		StringAssert.Contains(catchAll, "sensor.long_gone", "an entity nothing can re-classify keeps the only bucket anything knows about it");
		Assert.IsFalse(catchAll.Contains("sensor.washing_machine", StringComparison.Ordinal), "and the ones that moved are rewritten out of it");

		Assert.AreEqual(0, store.Load().DuplicatesResolved, "moved, never copied");
	}

	[TestMethod]
	public void A_Pre_Split_Catch_All_Takes_Its_Own_File_Away_Once_It_Is_Empty()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();
		fixture.Ha.Set("sensor.washing_machine", "0", Noon - TimeSpan.FromHours(5), "power");

		WritePreSplitCatchAll(fixture, Noon - TimeSpan.FromHours(3), "sensor.washing_machine");

		LastSeenTracker tracker = fixture.Started();
		tracker.Dispose();

		Assert.IsFalse(File.Exists(fixture.NewStore().PathFor(LastSeenBuckets.Unclassified)),
			"every record in it was re-filed by class, so the pre-split file is not orphaned — it is gone");
	}

	/// <summary>Writes the file a version-1 installation has: one catch-all holding the whole rest of the house.</summary>
	private static void WritePreSplitCatchAll(Fixture fixture, DateTimeOffset lastSeen, params string[] entityIds)
	{
		LastSeenDocument document = new()
		{
			Bucket = LastSeenBuckets.Unclassified,
			Version = LastSeenDocument.PreSplitVersion,
			SavedAt = Noon - TimeSpan.FromMinutes(30)
		};

		foreach (string entityId in entityIds)
			document.Entities[entityId] = new LastSeenEntry(lastSeen, Noon - TimeSpan.FromDays(20));

		File.WriteAllText(
			fixture.NewStore().PathFor(LastSeenBuckets.Unclassified),
			JsonSerializer.Serialize(document, LastSeenDocument.SerializerOptions));
	}

	/// <summary>Each cache file on disk and the moment the flush that wrote it happened.</summary>
	private static Dictionary<string, DateTimeOffset> SavedAtByFile(LastSeenStore store)
	{
		Dictionary<string, DateTimeOffset> stamps = new(StringComparer.Ordinal);

		foreach (string path in store.FilePaths)
			stamps[Path.GetFileName(path)] =
				JsonSerializer.Deserialize<LastSeenDocument>(File.ReadAllText(path), LastSeenDocument.SerializerOptions)!.SavedAt;

		return stamps;
	}

	[TestMethod]
	public void An_Entity_Without_A_Timestamp_Is_Not_Invented_Into_The_Record()
	{
		using Fixture fixture = new();
		fixture.SeedHouse();
		fixture.Ha.SetWithoutStamp("sensor.no_clock", "1");

		using LastSeenTracker tracker = fixture.Started();

		Assert.IsNull(tracker.LastSeenUtc("sensor.no_clock"));
		Assert.IsFalse(tracker.HasBeenSilentFor("sensor.no_clock", TimeSpan.FromSeconds(1)));
	}

	[TestMethod]
	public void Starting_Twice_Is_A_Programming_Error()
	{
		using Fixture fixture = new();
		using LastSeenTracker tracker = fixture.Started();

		Assert.ThrowsException<InvalidOperationException>(() => tracker.Start());
	}
}
