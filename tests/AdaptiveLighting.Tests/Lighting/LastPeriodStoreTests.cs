using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The note recording which circadian period the engine was last running in: where it lands, what it
///     survives, and what a bad one costs.
/// </summary>
[TestClass]
public sealed class LastPeriodStoreTests
{
	private sealed class TempDirectory : IDisposable
	{
		public TempDirectory()
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "adaptive-lighting-period-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path);
		}

		public string Path { get; }

		public string ConfigPath(string name = "b1.yaml") => System.IO.Path.Combine(Path, name);

		public LastPeriodStore Store(string name = "b1.yaml") => new(ConfigPath(name), NullLogger<LastPeriodStore>.Instance);

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

	// Named after the configuration document, so two houses in one directory cannot collide.
	[TestMethod]
	public void The_File_Takes_Its_Name_From_The_Configuration_Document()
	{
		using TempDirectory temp = new();

		Assert.AreEqual(Path.Combine(temp.Path, "cabin.last-period.json"), temp.Store("cabin.yaml").FilePath);
		Assert.AreEqual(Path.Combine(temp.Path, "b1.last-period.json"), temp.Store().FilePath);
	}

	[TestMethod]
	public void A_Missing_File_Loads_As_Nothing_Recalled()
	{
		using TempDirectory temp = new();

		Assert.IsNull(temp.Store().Load());
	}

	[TestMethod]
	public void A_Saved_Period_Reads_Back()
	{
		using TempDirectory temp = new();
		LastPeriodStore store = temp.Store();

		Assert.IsTrue(store.TrySave("night"));
		Assert.AreEqual("night", store.Load());

		Assert.IsTrue(store.TrySave("morning"), "and the note is overwritten rather than appended to");
		Assert.AreEqual("morning", temp.Store().Load(), "read by a fresh store, which is what a restart does");
	}

	// A note nobody can parse costs the note and nothing else. Unreadable and absent both load as null.
	[TestMethod]
	public void A_Corrupt_File_Loads_As_Nothing_Recalled_Without_Throwing()
	{
		using TempDirectory temp = new();
		LastPeriodStore store = temp.Store();

		File.WriteAllText(store.FilePath, "{ this is not json");
		Assert.IsNull(store.Load(), "unparseable is unknown");

		File.WriteAllText(store.FilePath, "[1, 2, 3]");
		Assert.IsNull(store.Load(), "valid JSON of the wrong shape is unknown too");

		File.WriteAllText(store.FilePath, """{ "version": 1, "period": "" }""");
		Assert.IsNull(store.Load(), "a blank period name is no period name");

		File.WriteAllText(store.FilePath, """{ "version": 1 }""");
		Assert.IsNull(store.Load(), "and a file that never had one is the same");
	}

	// Comments and a trailing comma are allowed here, as they are in the cache files.
	[TestMethod]
	public void A_Hand_Edited_File_Still_Reads()
	{
		using TempDirectory temp = new();
		LastPeriodStore store = temp.Store();

		File.WriteAllText(store.FilePath, """
			{
				// somebody opened it to see what it was
				"version": 1,
				"period": "  evening  ",
			}
			""");

		Assert.AreEqual("evening", store.Load(), "trimmed, because the name is compared against the table's");
	}

	[TestMethod]
	public void A_Rewrite_Keeps_The_Previous_Note_As_A_Backup()
	{
		using TempDirectory temp = new();
		LastPeriodStore store = temp.Store();

		store.TrySave("evening");
		Assert.IsFalse(File.Exists(store.FilePath + ".bak"), "nothing to back up on the first write");

		store.TrySave("night");
		Assert.IsTrue(File.Exists(store.FilePath + ".bak"));
		StringAssert.Contains(File.ReadAllText(store.FilePath + ".bak"), "evening");
	}

	[TestMethod]
	public void A_Missing_Directory_Is_Created()
	{
		using TempDirectory temp = new();
		string nested = Path.Combine(temp.Path, "nested", "b1.yaml");
		LastPeriodStore store = new(nested, NullLogger<LastPeriodStore>.Instance);

		Assert.IsTrue(store.TrySave("night"));
		Assert.AreEqual("night", store.Load());
	}

	// The file says what it is, so nobody finding it takes it for their settings.
	[TestMethod]
	public void The_File_Explains_Itself()
	{
		using TempDirectory temp = new();
		LastPeriodStore store = temp.Store();
		store.TrySave("night");

		string text = File.ReadAllText(store.FilePath);
		StringAssert.Contains(text, "Machine-written");
		StringAssert.Contains(text, "deleting this file is safe");
	}
}
