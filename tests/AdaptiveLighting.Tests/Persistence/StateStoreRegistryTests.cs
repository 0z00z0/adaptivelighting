using AdaptiveLighting.Engine;
using AdaptiveLighting.Persistence;
using AdaptiveLighting.TestFakes;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

namespace AdaptiveLighting.Tests.Persistence;

/// <summary>The three conditions a state file must meet to survive a kill, a wrong clock and a refused write.</summary>
[TestClass]
public sealed class StateStoreRegistryTests
{
	private sealed class TempDirectory : IDisposable
	{
		public TempDirectory()
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "adaptive-lighting-state-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path);
		}

		public string Path { get; }

		public string ConfigPath => System.IO.Path.Combine(Path, "b1.yaml");

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

	// A kill between the two steps of a replace leaves only the backup; that must not read as a first run.
	[TestMethod]
	public void A_Missing_Main_File_Is_Restored_From_The_Backup()
	{
		using TempDirectory temp = new();
		LastPeriodStore writer = new(temp.ConfigPath, NullLogger<LastPeriodStore>.Instance);
		writer.TrySave("evening");
		writer.TrySave("night");
		File.Delete(writer.FilePath);

		using StateStoreRegistry registry = new(temp.ConfigPath, NullLogger<StateStoreRegistry>.Instance);
		LastPeriodStore restarted = new(temp.ConfigPath, NullLogger<LastPeriodStore>.Instance, registry);

		Assert.AreEqual("evening", restarted.Load(), "the backup holds the last completed write");
		Assert.AreEqual(StateRestore.RestoredFromBackup, registry.Stores.Single().LastRestore?.Outcome);
	}

	[TestMethod]
	public void A_Note_Saved_Ahead_Of_The_Clock_Is_Discarded_With_A_Warning()
	{
		using TempDirectory temp = new();
		DateTimeOffset now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
		RecordingLogger logger = new();
		StateStore<LastPeriodDocument> store = new(temp.ConfigPath, LastPeriodStore.Declaration, LastPeriodDocument.SerializerOptions, logger);

		store.Write(new LastPeriodDocument { SavedAt = now.AddHours(3), Period = "night" });

		Assert.IsNull(store.Restore(now), "nothing is restored from a note newer than now");
		Assert.AreEqual(StateRestore.Discarded, store.LastRestore?.Outcome);
		Assert.AreEqual(1, logger.Warnings.Count(warning => warning.Contains("ahead of the clock", StringComparison.Ordinal)));
	}

	// The transient Windows replace fault is about 1 % of writes; a write that fails must not be lost.
	[TestMethod]
	public void A_Failed_Write_Is_Retried_At_The_Next_Interval()
	{
		using TempDirectory temp = new();
		TestScheduler scheduler = new();
		using StateStoreRegistry registry = new(temp.ConfigPath, NullLogger<StateStoreRegistry>.Instance, scheduler, TimeSpan.FromMinutes(1));
		LastPeriodStore store = new(temp.ConfigPath, NullLogger<LastPeriodStore>.Instance, registry);

		// A directory where the note belongs makes the write fail.
		Directory.CreateDirectory(store.FilePath);
		Assert.IsFalse(store.TrySave("night"));
		Assert.IsTrue(registry.Stores.Single().IsDirty, "still waiting to be written");

		Directory.Delete(store.FilePath);
		scheduler.AdvanceBy(TimeSpan.FromMinutes(1).Ticks);

		Assert.IsTrue(File.Exists(store.FilePath), "the flusher wrote it at the next interval");
		Assert.AreEqual("night", store.Load());
		Assert.IsFalse(registry.Stores.Single().IsDirty);
	}
}
