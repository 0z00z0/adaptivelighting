using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The note recording which circadian period the engine was last running in: where it lands, what it survives,
///     and what a bad one costs.
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

	/// <summary>
	///     The file takes its name from the configuration document, so two houses in one directory cannot collide.
	/// </summary>
	[TestMethod]
	public void The_File_Takes_Its_Name_From_The_Configuration_Document()
	{
		using TempDirectory temp = new();

		Assert.AreEqual(Path.Combine(temp.Path, "cabin.last-period.json"), temp.Store("cabin.yaml").FilePath);
		Assert.AreEqual(Path.Combine(temp.Path, "b1.last-period.json"), temp.Store().FilePath);
	}

	/// <summary>A first run has nothing to recall, and that is not an error.</summary>
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

	/// <summary>
	///     A corrupt file is inert: it loads as "we do not know" and does not throw.
	/// </summary>
	/// <remarks>
	///     The bar this exists for: a blank <c>Areas:</c> line once took the host down unrecoverably. A note nobody
	///     can parse must cost the note and nothing else, and the caller reads the same <c>null</c> a first run does
	///     — because "we could not read it" and "there was nothing to read" are the same answer to the only question
	///     being asked.
	/// </remarks>
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

	/// <summary>A hand-edited file with comments and a trailing comma still reads, exactly as the cache files do.</summary>
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

	/// <summary>The previous note is kept as .bak, the same way the configuration document and the cache are.</summary>
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

	/// <summary>A write into a directory that does not exist yet creates it rather than failing.</summary>
	[TestMethod]
	public void A_Missing_Directory_Is_Created()
	{
		using TempDirectory temp = new();
		string nested = Path.Combine(temp.Path, "nested", "b1.yaml");
		LastPeriodStore store = new(nested, NullLogger<LastPeriodStore>.Instance);

		Assert.IsTrue(store.TrySave("night"));
		Assert.AreEqual("night", store.Load());
	}

	/// <summary>The file says what it is, so somebody who finds it does not take it for their settings.</summary>
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
