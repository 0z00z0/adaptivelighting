using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Persistence;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Persistence;

/// <summary>The activity record's on-disk journal: whether rows survive a restart, and whether pruning keeps the newest.</summary>
[TestClass]
public sealed class ActivityJournalStoreTests
{
	private sealed class TempDirectory : IDisposable
	{
		public TempDirectory()
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "adaptive-lighting-activity-" + Guid.NewGuid().ToString("N"));
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

	private static readonly DateTimeOffset Epoch = new(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);

	private static AreaSnapshot Snapshot(string area, DateTimeOffset at) => new(
		AreaName: area,
		State: AreaState.AutoVacant,
		Reason: TransitionReason.CircadianTick,
		Mode: ModeKind.Normal,
		KillSwitchActive: false,
		IsDark: true,
		PeriodName: "evening",
		BrightnessPct: 42,
		ColorTempKelvin: 2700,
		Timestamp: at,
		LastCommandAt: null,
		LastMotionAt: null,
		NextChangeAt: null,
		NextChangeFrom: null);

	// A kill between "wrote" and "process ends" is exactly what Dispose simulates: it flushes what a coalesced
	// write only marked dirty, the same way the registry's periodic flusher or a clean shutdown would.
	[TestMethod]
	public void Rows_Survive_A_Restart()
	{
		using TempDirectory temp = new();

		ActivityJournalRow[] rows =
		[
			new(1, Snapshot("Stue", Epoch), null),
			new(2, null, new EngineNotice(EngineNoticeKind.SettingsSaved, Epoch.AddMinutes(1)))
		];

		using (ActivityJournalStore store = new(temp.ConfigPath, NullLoggerFactory.Instance))
			Assert.IsTrue(store.TrySave(rows));

		using ActivityJournalStore restarted = new(temp.ConfigPath, NullLoggerFactory.Instance);
		IReadOnlyList<ActivityJournalRow> loaded = restarted.Load();

		Assert.AreEqual(2, loaded.Count);
		Assert.AreEqual("Stue", loaded[0].Snapshot?.AreaName);
		Assert.AreEqual(EngineNoticeKind.SettingsSaved, loaded[1].Notice?.Kind);
	}

	[TestMethod]
	public void Pruning_Keeps_The_Newest()
	{
		using TempDirectory temp = new();

		List<ActivityJournalRow> rows = [];
		for (long sequence = 1; sequence <= ActivityJournalStore.Capacity + 50; sequence++)
			rows.Add(new ActivityJournalRow(sequence, null, new EngineNotice(EngineNoticeKind.Started, Epoch.AddSeconds(sequence))));

		using (ActivityJournalStore store = new(temp.ConfigPath, NullLoggerFactory.Instance))
			Assert.IsTrue(store.TrySave(rows));

		using ActivityJournalStore restarted = new(temp.ConfigPath, NullLoggerFactory.Instance);
		IReadOnlyList<ActivityJournalRow> loaded = restarted.Load();

		Assert.AreEqual(ActivityJournalStore.Capacity, loaded.Count, "the file never grows past the bound");
		Assert.AreEqual(51, loaded[0].Sequence, "the oldest 50 fell off");
		Assert.AreEqual(rows[^1].Sequence, loaded[^1].Sequence, "the newest row is kept");
	}
}
