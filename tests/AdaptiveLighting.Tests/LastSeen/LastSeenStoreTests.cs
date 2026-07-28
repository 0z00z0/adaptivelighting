using System.Globalization;

using AdaptiveLighting.LastSeen;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.LastSeen;

/// <summary>
///     The cache's files: where they land, how a torn set reads back, and what one costs on disk.
/// </summary>
[TestClass]
public sealed class LastSeenStoreTests
{
	private static readonly DateTimeOffset Noon = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

	private sealed class TempDirectory : IDisposable
	{
		public TempDirectory()
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "adaptive-lighting-store-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path);
		}

		public string Path { get; }

		public string ConfigPath(string name = "b1.yaml") => System.IO.Path.Combine(Path, name);

		public LastSeenStore Store(string name = "b1.yaml") => new(ConfigPath(name), NullLogger<LastSeenStore>.Instance);

		public void Dispose()
		{
			try
			{
				Directory.Delete(Path, recursive: true);
			}
			catch (IOException)
			{
				// Litter, not a failure.
			}
		}
	}

	private static LastSeenDocument Document(string bucket, DateTimeOffset savedAt, params (string EntityId, DateTimeOffset? LastSeen)[] entities)
	{
		LastSeenDocument document = new()
		{
			Bucket = bucket,
			SavedAt = savedAt,
			HomeAssistantStarted = null
		};

		foreach ((string entityId, DateTimeOffset? lastSeen) in entities)
			document.Entities[entityId] = new LastSeenEntry(lastSeen, savedAt - TimeSpan.FromDays(1));

		return document;
	}

	// ===================== where the files land =====================

	[TestMethod]
	public void The_Files_Take_Their_Name_From_The_Configuration_Document()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store("cabin.yaml");

		Assert.AreEqual(Path.Combine(temp.Path, "cabin.last-seen.illuminance.json"), store.PathFor(LastSeenBuckets.Illuminance));
		Assert.AreEqual(Path.Combine(temp.Path, "cabin.last-seen.motion.json"), store.PathFor(LastSeenBuckets.Motion));
		Assert.AreEqual(Path.Combine(temp.Path, "cabin.last-seen.light.json"), store.PathFor(LastSeenBuckets.Light));
		Assert.AreEqual(Path.Combine(temp.Path, "cabin.last-seen.other.json"), store.PathFor(LastSeenBuckets.Unclassified));

		// The names the split adds. A device class is a word a person recognises, which is the point of it.
		Assert.AreEqual(Path.Combine(temp.Path, "cabin.last-seen.temperature.json"), store.PathFor("temperature"));
		Assert.AreEqual(Path.Combine(temp.Path, "cabin.last-seen.input_boolean.json"), store.PathFor("input_boolean"));

		Assert.AreEqual(temp.Path, store.DirectoryPath, "the cache lives beside the document, which is the directory a deploy does not wipe");
	}

	[TestMethod]
	public void A_Device_Class_Cannot_Write_Outside_The_Cache_Directory()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		// device_class arrives from an external system and ends up in a path. It must not be able to steer one.
		foreach (string hostile in new[] { "../../etc/passwd", @"..\..\config\secrets.yaml", "C:/windows/system32", "with space" })
		{
			string path = store.PathFor(hostile);

			Assert.AreEqual(temp.Path, Path.GetDirectoryName(path), hostile);
			StringAssert.StartsWith(Path.GetFileName(path), "b1.last-seen.");
			StringAssert.EndsWith(Path.GetFileName(path), ".json");
		}
	}

	[TestMethod]
	public void Two_Houses_Sharing_A_Directory_Do_Not_Collide()
	{
		using TempDirectory temp = new();

		Assert.AreNotEqual(
			temp.Store("b1.yaml").PathFor(LastSeenBuckets.Motion),
			temp.Store("cabin.yaml").PathFor(LastSeenBuckets.Motion));
	}

	[TestMethod]
	public void Two_Classes_That_Sanitise_Alike_Get_Two_Files()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		Assert.AreNotEqual(store.PathFor("a/b"), store.PathFor(@"a\b"));

		store.TrySave("a/b", Document("a/b", Noon, ("sensor.slash", Noon)));
		store.TrySave(@"a\b", Document(@"a\b", Noon.AddMinutes(1), ("sensor.backslash", Noon)));

		LastSeenCacheLoad load = store.Load();

		Assert.AreEqual(2, load.FilesRead, "one file holding two classes' histories is the data loss this guards against");
		Assert.AreEqual(0, load.DuplicatesResolved);
		Assert.AreEqual("a/b", load.Entities["sensor.slash"].Bucket, "and the real key survives, not just the sanitised name");
		Assert.AreEqual(@"a\b", load.Entities["sensor.backslash"].Bucket);
	}

	// ===================== round trip =====================

	[TestMethod]
	public void A_Saved_Bucket_Reads_Back()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		Assert.IsTrue(store.TrySave(LastSeenBuckets.Illuminance, Document(LastSeenBuckets.Illuminance, Noon, ("sensor.lux", Noon - TimeSpan.FromMinutes(3)))));

		LastSeenCacheLoad load = store.Load();

		Assert.AreEqual(1, load.FilesRead);
		Assert.AreEqual(0, load.FilesUnreadable);
		Assert.AreEqual(Noon - TimeSpan.FromMinutes(3), load.Entities["sensor.lux"].Entry.LastSeen);
		Assert.AreEqual(LastSeenBuckets.Illuminance, load.Entities["sensor.lux"].Bucket);
	}

	[TestMethod]
	public void A_Class_Bucket_Reads_Back_Under_Its_Own_Name()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		store.TrySave("temperature", Document("temperature", Noon, ("sensor.hall_temperature", Noon)));
		store.TrySave("battery", Document("battery", Noon, ("sensor.remote_battery", Noon)));

		LastSeenCacheLoad load = store.Load();

		Assert.AreEqual(2, load.FilesRead);
		Assert.AreEqual("temperature", load.Entities["sensor.hall_temperature"].Bucket);
		Assert.AreEqual("battery", load.Entities["sensor.remote_battery"].Bucket);
		Assert.AreEqual(0, load.PreSplitRecords, "a version-2 file is not a pile awaiting redistribution");
	}

	[TestMethod]
	public void An_Entry_With_No_Evidence_Round_Trips_As_Unknown()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		store.TrySave(LastSeenBuckets.Motion, Document(LastSeenBuckets.Motion, Noon, ("binary_sensor.new", null)));

		LastSeenCacheLoad load = store.Load();

		Assert.IsTrue(load.Entities.ContainsKey("binary_sensor.new"), "'watching it, heard nothing' is worth keeping");
		Assert.IsNull(load.Entities["binary_sensor.new"].Entry.LastSeen);
	}

	[TestMethod]
	public void A_Rewrite_Keeps_One_Backup()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		store.TrySave(LastSeenBuckets.Motion, Document(LastSeenBuckets.Motion, Noon, ("binary_sensor.one", Noon)));
		store.TrySave(LastSeenBuckets.Motion, Document(LastSeenBuckets.Motion, Noon.AddMinutes(5), ("binary_sensor.two", Noon)));

		string backup = store.PathFor(LastSeenBuckets.Motion) + ".bak";

		Assert.IsTrue(File.Exists(backup));
		StringAssert.Contains(File.ReadAllText(backup), "binary_sensor.one");
		StringAssert.Contains(File.ReadAllText(store.PathFor(LastSeenBuckets.Motion)), "binary_sensor.two");
	}

	[TestMethod]
	public void The_File_Says_What_It_Is_And_That_Deleting_It_Is_Safe()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		store.TrySave(LastSeenBuckets.Motion, Document(LastSeenBuckets.Motion, Noon, ("binary_sensor.one", Noon)));

		string text = File.ReadAllText(store.PathFor(LastSeenBuckets.Motion));

		StringAssert.Contains(text, "_comment");
		StringAssert.Contains(text, "deleting this file is safe");
		StringAssert.Contains(text, "Not configuration");
		StringAssert.Contains(text, "\"kind\": \"motion\"", "the bucket key is still spelled 'kind' on disk, so an older file still reads");
	}

	// ===================== an emptied bucket takes its file with it =====================

	[TestMethod]
	public void An_Emptied_Bucket_Removes_Its_File_Rather_Than_Leaving_A_Husk()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		store.TrySave("battery", Document("battery", Noon, ("sensor.remote_battery", Noon)));
		store.TrySave("battery", Document("battery", Noon.AddMinutes(5), ("sensor.remote_battery", Noon)));

		Assert.IsTrue(File.Exists(store.PathFor("battery")));
		Assert.IsTrue(File.Exists(store.PathFor("battery") + ".bak"));

		// The last battery sensor leaves the house. Buckets are device classes now, so they come and go with the
		// hardware, and a folder of husks is what splitting the cache was meant to avoid.
		Assert.IsTrue(store.TrySave("battery", Document("battery", Noon.AddMinutes(10))));

		Assert.IsFalse(File.Exists(store.PathFor("battery")));
		Assert.IsFalse(File.Exists(store.PathFor("battery") + ".bak"));
		Assert.AreEqual(0, store.Load().FilesRead);
	}

	// ===================== degrading =====================

	[TestMethod]
	public void No_Files_At_All_Is_A_First_Run_Not_A_Failure()
	{
		using TempDirectory temp = new();

		LastSeenCacheLoad load = temp.Store().Load();

		Assert.AreEqual(0, load.FilesRead);
		Assert.AreEqual(0, load.Entities.Count);
		Assert.IsNull(load.HomeAssistantStarted);
	}

	[TestMethod]
	public void A_Corrupt_File_Costs_Only_Its_Own_Entities()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		store.TrySave(LastSeenBuckets.Illuminance, Document(LastSeenBuckets.Illuminance, Noon, ("sensor.lux", Noon)));
		store.TrySave("temperature", Document("temperature", Noon, ("sensor.hall_temperature", Noon)));
		store.TrySave("battery", Document("battery", Noon, ("sensor.remote_battery", Noon)));
		File.WriteAllText(store.PathFor("temperature"), "{ \"entities\": [ truncated");

		LastSeenCacheLoad load = store.Load();

		Assert.AreEqual(2, load.FilesRead);
		Assert.AreEqual(1, load.FilesUnreadable);
		Assert.AreEqual(2, load.Entities.Count, "per-entity data is independent, so a torn set is recoverable");
		Assert.IsTrue(load.Entities.ContainsKey("sensor.lux"));
		Assert.IsTrue(load.Entities.ContainsKey("sensor.remote_battery"));
		Assert.IsFalse(load.Entities.ContainsKey("sensor.hall_temperature"), "and the one file's entities are unknown, not dead");
	}

	[TestMethod]
	public void A_File_Whose_Body_Lost_Its_Key_Falls_Back_To_Its_Name()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		File.WriteAllText(
			store.PathFor("humidity"),
			"{ \"savedAt\": \"2026-07-28T12:00:00+00:00\", \"entities\": { \"sensor.bath\": { \"trackedSince\": \"2026-07-01T00:00:00+00:00\" } } }");

		LastSeenCacheLoad load = store.Load();

		Assert.AreEqual("humidity", load.Entities["sensor.bath"].Bucket, "filing it under its own name beats sweeping it into the catch-all");
	}

	[TestMethod]
	public void Backups_And_Half_Written_Files_Are_Not_Mistaken_For_Buckets()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		store.TrySave("power", Document("power", Noon, ("sensor.washer", Noon)));
		store.TrySave("power", Document("power", Noon.AddMinutes(1), ("sensor.washer", Noon)));
		File.WriteAllText(Path.Combine(temp.Path, ".b1.last-seen.power.json.abc123.tmp"), "half written");
		File.WriteAllText(Path.Combine(temp.Path, "b1.yaml"), "unrelated: true");
		File.WriteAllText(Path.Combine(temp.Path, "b1.last-seen.json"), "not a bucket file");

		LastSeenCacheLoad load = store.Load();

		Assert.AreEqual(1, load.FilesRead);
		Assert.AreEqual(0, load.FilesUnreadable);
		Assert.AreEqual(1, store.FilePaths.Count);
	}

	// ===================== the pre-split cache =====================

	[TestMethod]
	public void A_Pre_Split_Other_File_Is_Read_And_Counted_Rather_Than_Orphaned()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		WritePreSplitOther(store, ("sensor.washing_machine_power", Noon - TimeSpan.FromMinutes(4)), ("person.espen", Noon - TimeSpan.FromHours(2)));

		LastSeenCacheLoad load = store.Load();

		Assert.AreEqual(1, load.FilesRead);
		Assert.AreEqual(2, load.PreSplitRecords, "a version-1 'other' file is a pile awaiting redistribution, and the log should say so");
		Assert.AreEqual(Noon - TimeSpan.FromMinutes(4), load.Entities["sensor.washing_machine_power"].Entry.LastSeen,
			"a record that survived a restart must not be lost to a rename");
		Assert.AreEqual(LastSeenBuckets.Unclassified, load.Entities["person.espen"].Bucket);
	}

	[TestMethod]
	public void A_Current_Other_File_Is_A_Bucket_Not_A_Pile()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		store.TrySave(LastSeenBuckets.Unclassified, Document(LastSeenBuckets.Unclassified, Noon, ("nonsense", Noon)));

		Assert.AreEqual(0, store.Load().PreSplitRecords, "the catch-all still exists; it is only version 1 that means 'everything else'");
	}

	private static void WritePreSplitOther(LastSeenStore store, params (string EntityId, DateTimeOffset LastSeen)[] entities)
	{
		// Written by hand rather than through the store, because the store cannot write version 1 any more and the
		// file this has to cope with is one an installation already has on disk.
		LastSeenDocument document = Document(LastSeenBuckets.Unclassified, Noon,
			[.. entities.Select(entity => (entity.EntityId, (DateTimeOffset?)entity.LastSeen))]);

		document.Version = LastSeenDocument.PreSplitVersion;

		File.WriteAllText(
			store.PathFor(LastSeenBuckets.Unclassified),
			System.Text.Json.JsonSerializer.Serialize(document, LastSeenDocument.SerializerOptions));
	}

	// ===================== a torn move =====================

	[TestMethod]
	public void An_Entity_Found_In_Two_Files_Is_Settled_By_The_Later_Write()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		// What a crash between the two halves of a move leaves behind: the old file still has it, the new one
		// already does too.
		store.TrySave(LastSeenBuckets.Motion, Document(LastSeenBuckets.Motion, Noon, ("binary_sensor.odd", Noon - TimeSpan.FromHours(9))));
		store.TrySave("door", Document("door", Noon.AddMinutes(5), ("binary_sensor.odd", Noon - TimeSpan.FromHours(1))));

		LastSeenCacheLoad load = store.Load();

		Assert.AreEqual(1, load.DuplicatesResolved);
		Assert.AreEqual(1, load.Entities.Count);
		Assert.AreEqual(Noon - TimeSpan.FromHours(1), load.Entities["binary_sensor.odd"].Entry.LastSeen, "last write wins");
		Assert.AreEqual("door", load.Entities["binary_sensor.odd"].Bucket);
	}

	[TestMethod]
	public void The_Newest_Restart_Estimate_Wins_Across_Files()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		LastSeenDocument stale = Document(LastSeenBuckets.Motion, Noon, ("binary_sensor.one", Noon));
		stale.HomeAssistantStarted = Noon - TimeSpan.FromDays(2);
		store.TrySave(LastSeenBuckets.Motion, stale);

		LastSeenDocument fresh = Document(LastSeenBuckets.Light, Noon.AddMinutes(1), ("light.one", Noon));
		fresh.HomeAssistantStarted = Noon - TimeSpan.FromHours(2);
		store.TrySave(LastSeenBuckets.Light, fresh);

		// It only ever moves forwards, so the newest value found is the right one however stale a file is.
		Assert.AreEqual(Noon - TimeSpan.FromHours(2), store.Load().HomeAssistantStarted);
	}

	// ===================== what it costs on disk =====================

	[TestMethod]
	public void A_House_This_Size_Costs_Tens_Of_Kilobytes()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		// The live house's shape: ~300 entities, of which about 50 are motion, a handful illuminance, some lights,
		// and the rest spread across the classes the split now gives their own files.
		Save(store, LastSeenBuckets.Illuminance, 8, "sensor.lux_");
		Save(store, LastSeenBuckets.Motion, 51, "binary_sensor.motion_");
		Save(store, LastSeenBuckets.Light, 40, "light.lamp_");
		Save(store, "temperature", 60, "sensor.temperature_");
		Save(store, "battery", 55, "sensor.battery_");
		Save(store, "power", 46, "sensor.power_");
		Save(store, "automation", 40, "automation.rule_");

		long total = 0;

		foreach (string path in store.FilePaths)
		{
			long size = new FileInfo(path).Length;
			total += size;
			Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(path)}: {size} bytes"));
		}

		Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"total: {total} bytes for 300 entities"));

		// Not a benchmark, a guard rail: this is the size that justifies one batched write over any cleverer
		// scheme, and a change that made it an order of magnitude bigger would deserve a second look. Splitting
		// further costs one header per new file, which is the only thing that grew.
		Assert.IsTrue(total is > 10_000 and < 120_000, $"300 entities came to {total} bytes");

		LastSeenCacheLoad load = store.Load();
		Assert.AreEqual(300, load.Entities.Count);

		static void Save(LastSeenStore store, string bucket, int count, string prefix)
		{
			LastSeenDocument document = new() { Bucket = bucket, SavedAt = Noon, HomeAssistantStarted = Noon.AddHours(-3) };

			for (int index = 0; index < count; index++)
				document.Entities[prefix + index.ToString(CultureInfo.InvariantCulture)] =
					new LastSeenEntry(Noon.AddMinutes(-index), Noon.AddDays(-30));

			store.TrySave(bucket, document);
		}
	}

	[TestMethod]
	public void A_Blank_Configuration_Path_Is_Refused()
	{
		Assert.ThrowsException<ArgumentException>(() => new LastSeenStore("  ", NullLogger<LastSeenStore>.Instance));
	}
}
