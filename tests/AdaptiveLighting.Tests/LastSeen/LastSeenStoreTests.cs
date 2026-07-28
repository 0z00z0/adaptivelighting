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

	private static LastSeenDocument Document(LastSeenKind kind, DateTimeOffset savedAt, params (string EntityId, DateTimeOffset? LastSeen)[] entities)
	{
		LastSeenDocument document = new()
		{
			Kind = kind.Token(),
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

		Assert.AreEqual(Path.Combine(temp.Path, "cabin.last-seen.illuminance.json"), store.PathFor(LastSeenKind.Illuminance));
		Assert.AreEqual(Path.Combine(temp.Path, "cabin.last-seen.motion.json"), store.PathFor(LastSeenKind.Motion));
		Assert.AreEqual(Path.Combine(temp.Path, "cabin.last-seen.light.json"), store.PathFor(LastSeenKind.Light));
		Assert.AreEqual(Path.Combine(temp.Path, "cabin.last-seen.other.json"), store.PathFor(LastSeenKind.Other));

		Assert.AreEqual(temp.Path, store.DirectoryPath, "the cache lives beside the document, which is the directory a deploy does not wipe");
	}

	[TestMethod]
	public void Two_Houses_Sharing_A_Directory_Do_Not_Collide()
	{
		using TempDirectory temp = new();

		Assert.AreNotEqual(
			temp.Store("b1.yaml").PathFor(LastSeenKind.Motion),
			temp.Store("cabin.yaml").PathFor(LastSeenKind.Motion));
	}

	// ===================== round trip =====================

	[TestMethod]
	public void A_Saved_Bucket_Reads_Back()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		Assert.IsTrue(store.TrySave(LastSeenKind.Illuminance, Document(LastSeenKind.Illuminance, Noon, ("sensor.lux", Noon - TimeSpan.FromMinutes(3)))));

		LastSeenCacheLoad load = store.Load();

		Assert.AreEqual(1, load.FilesRead);
		Assert.AreEqual(0, load.FilesUnreadable);
		Assert.AreEqual(Noon - TimeSpan.FromMinutes(3), load.Entities["sensor.lux"].Entry.LastSeen);
		Assert.AreEqual(LastSeenKind.Illuminance, load.Entities["sensor.lux"].Kind);
	}

	[TestMethod]
	public void An_Entry_With_No_Evidence_Round_Trips_As_Unknown()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		store.TrySave(LastSeenKind.Motion, Document(LastSeenKind.Motion, Noon, ("binary_sensor.new", null)));

		LastSeenCacheLoad load = store.Load();

		Assert.IsTrue(load.Entities.ContainsKey("binary_sensor.new"), "'watching it, heard nothing' is worth keeping");
		Assert.IsNull(load.Entities["binary_sensor.new"].Entry.LastSeen);
	}

	[TestMethod]
	public void A_Rewrite_Keeps_One_Backup()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		store.TrySave(LastSeenKind.Motion, Document(LastSeenKind.Motion, Noon, ("binary_sensor.one", Noon)));
		store.TrySave(LastSeenKind.Motion, Document(LastSeenKind.Motion, Noon.AddMinutes(5), ("binary_sensor.two", Noon)));

		string backup = store.PathFor(LastSeenKind.Motion) + ".bak";

		Assert.IsTrue(File.Exists(backup));
		StringAssert.Contains(File.ReadAllText(backup), "binary_sensor.one");
		StringAssert.Contains(File.ReadAllText(store.PathFor(LastSeenKind.Motion)), "binary_sensor.two");
	}

	[TestMethod]
	public void The_File_Says_What_It_Is_And_That_Deleting_It_Is_Safe()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		store.TrySave(LastSeenKind.Motion, Document(LastSeenKind.Motion, Noon, ("binary_sensor.one", Noon)));

		string text = File.ReadAllText(store.PathFor(LastSeenKind.Motion));

		StringAssert.Contains(text, "_comment");
		StringAssert.Contains(text, "deleting this file is safe");
		StringAssert.Contains(text, "Not configuration");
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

		store.TrySave(LastSeenKind.Illuminance, Document(LastSeenKind.Illuminance, Noon, ("sensor.lux", Noon)));
		File.WriteAllText(store.PathFor(LastSeenKind.Motion), "{ \"entities\": [ truncated");

		LastSeenCacheLoad load = store.Load();

		Assert.AreEqual(1, load.FilesRead);
		Assert.AreEqual(1, load.FilesUnreadable);
		Assert.AreEqual(1, load.Entities.Count, "per-entity data is independent, so a torn set is recoverable");
		Assert.IsTrue(load.Entities.ContainsKey("sensor.lux"));
	}

	[TestMethod]
	public void An_Unrecognised_Kind_Token_Does_Not_Cost_The_History()
	{
		Assert.AreEqual(LastSeenKind.Other, LastSeenKinds.FromToken("humidity"));
		Assert.AreEqual(LastSeenKind.Other, LastSeenKinds.FromToken(null));
		Assert.AreEqual(LastSeenKind.Illuminance, LastSeenKinds.FromToken("illuminance"));
	}

	// ===================== a torn move =====================

	[TestMethod]
	public void An_Entity_Found_In_Two_Files_Is_Settled_By_The_Later_Write()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		// What a crash between the two halves of a move leaves behind: the old file still has it, the new one
		// already does too.
		store.TrySave(LastSeenKind.Motion, Document(LastSeenKind.Motion, Noon, ("binary_sensor.odd", Noon - TimeSpan.FromHours(9))));
		store.TrySave(LastSeenKind.Other, Document(LastSeenKind.Other, Noon.AddMinutes(5), ("binary_sensor.odd", Noon - TimeSpan.FromHours(1))));

		LastSeenCacheLoad load = store.Load();

		Assert.AreEqual(1, load.DuplicatesResolved);
		Assert.AreEqual(1, load.Entities.Count);
		Assert.AreEqual(Noon - TimeSpan.FromHours(1), load.Entities["binary_sensor.odd"].Entry.LastSeen, "last write wins");
		Assert.AreEqual(LastSeenKind.Other, load.Entities["binary_sensor.odd"].Kind);
	}

	[TestMethod]
	public void The_Newest_Restart_Estimate_Wins_Across_Files()
	{
		using TempDirectory temp = new();
		LastSeenStore store = temp.Store();

		LastSeenDocument stale = Document(LastSeenKind.Motion, Noon, ("binary_sensor.one", Noon));
		stale.HomeAssistantStarted = Noon - TimeSpan.FromDays(2);
		store.TrySave(LastSeenKind.Motion, stale);

		LastSeenDocument fresh = Document(LastSeenKind.Light, Noon.AddMinutes(1), ("light.one", Noon));
		fresh.HomeAssistantStarted = Noon - TimeSpan.FromHours(2);
		store.TrySave(LastSeenKind.Light, fresh);

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
		// and the rest everything else.
		Save(store, LastSeenKind.Illuminance, 8, "sensor.lux_");
		Save(store, LastSeenKind.Motion, 51, "binary_sensor.motion_");
		Save(store, LastSeenKind.Light, 40, "light.lamp_");
		Save(store, LastSeenKind.Other, 201, "sensor.other_");

		long total = 0;

		foreach (LastSeenKind kind in LastSeenKinds.All)
		{
			long size = new FileInfo(store.PathFor(kind)).Length;
			total += size;
			Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(store.PathFor(kind))}: {size} bytes"));
		}

		Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"total: {total} bytes for 300 entities"));

		// Not a benchmark, a guard rail: this is the size that justifies one batched write over any cleverer
		// scheme, and a change that made it an order of magnitude bigger would deserve a second look.
		Assert.IsTrue(total is > 10_000 and < 120_000, $"300 entities came to {total} bytes");

		LastSeenCacheLoad load = store.Load();
		Assert.AreEqual(300, load.Entities.Count);

		static void Save(LastSeenStore store, LastSeenKind kind, int count, string prefix)
		{
			LastSeenDocument document = new() { Kind = kind.Token(), SavedAt = Noon, HomeAssistantStarted = Noon.AddHours(-3) };

			for (int index = 0; index < count; index++)
				document.Entities[prefix + index.ToString(CultureInfo.InvariantCulture)] =
					new LastSeenEntry(Noon.AddMinutes(-index), Noon.AddDays(-30));

			store.TrySave(kind, document);
		}
	}

	[TestMethod]
	public void A_Blank_Configuration_Path_Is_Refused()
	{
		Assert.ThrowsException<ArgumentException>(() => new LastSeenStore("  ", NullLogger<LastSeenStore>.Instance));
	}
}
