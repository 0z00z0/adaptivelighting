using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The note recording which rooms have already been reported as impossible to set up: what it remembers, what clears it, and what a bad one costs.</summary>
[TestClass]
public sealed class AreaSetupMemoryStoreTests
{
	private sealed class TempDirectory : IDisposable
	{
		public TempDirectory()
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "adaptive-lighting-setup-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path);
		}

		public string Path { get; }

		public string ConfigPath(string name = "b1.yaml") => System.IO.Path.Combine(Path, name);

		public AreaSetupMemoryStore Store(string name = "b1.yaml") => new(ConfigPath(name), NullLogger<AreaSetupMemoryStore>.Instance);

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

	private static AreaSetupFault NoLights(string key = "stue") =>
		new(key, "Stua", $"No lights discovered in area '{key}'. Assign lights to the area in HA, or list them explicitly.");

	private static AreaSetupFault NoSuchArea(string key = "stue") =>
		new(key, "Stua", $"Home Assistant has no area '{key}'. It must be the area's id, the slug, not its display name.");

	// Named after the configuration document, so two houses in one directory cannot collide.
	[TestMethod]
	public void The_File_Takes_Its_Name_From_The_Configuration_Document()
	{
		using TempDirectory temp = new();

		Assert.AreEqual(Path.Combine(temp.Path, "cabin.setup-faults.json"), temp.Store("cabin.yaml").FilePath);
		Assert.AreEqual(Path.Combine(temp.Path, "b1.setup-faults.json"), temp.Store().FilePath);
	}

	[TestMethod]
	public void A_Problem_Is_Unreported_The_First_Time_And_Silent_Afterwards()
	{
		using TempDirectory temp = new();

		Assert.AreEqual(1, temp.Store().Record([NoLights()]).Count, "nothing is remembered on a first run");

		// A fresh store is what a restart gives.
		Assert.AreEqual(0, temp.Store().Record([NoLights()]).Count, "the same problem, still standing, is not said again");
		Assert.AreEqual(0, temp.Store().Record([NoLights()]).Count, "nor at the start after that");
	}

	[TestMethod]
	public void A_Room_Whose_Problem_Changes_Is_A_New_Thing_To_Say()
	{
		using TempDirectory temp = new();
		temp.Store().Record([NoLights()]);

		IReadOnlyList<AreaSetupFault> again = temp.Store().Record([NoSuchArea()]);

		Assert.AreEqual(1, again.Count, "the area id turns out to be wrong too: a different problem");
		Assert.AreEqual(0, temp.Store().Record([NoSuchArea()]).Count, "and the new problem is then remembered in its turn");
	}

	[TestMethod]
	public void A_Room_That_Resolves_Is_Forgotten_So_A_Regression_Is_Reported_Again()
	{
		using TempDirectory temp = new();
		temp.Store().Record([NoLights()]);

		Assert.AreEqual(0, temp.Store().Record([]).Count, "a start with nothing wrong says nothing");
		Assert.IsFalse(File.Exists(temp.Store().FilePath), "and leaves no note behind");

		Assert.AreEqual(1, temp.Store().Record([NoLights()]).Count, "so the same problem coming back is said again");
	}

	// One room resolving must not silence another whose problem still stands, nor re-report it.
	[TestMethod]
	public void Rooms_Are_Remembered_One_By_One()
	{
		using TempDirectory temp = new();
		temp.Store().Record([NoLights("stue"), NoSuchArea("gang")]);

		IReadOnlyList<AreaSetupFault> next = temp.Store().Record([NoSuchArea("gang"), NoLights("kjokken")]);

		Assert.AreEqual(1, next.Count);
		Assert.AreEqual("kjokken", next[0].Key, "only the room nobody has been told about yet");

		Assert.AreEqual(1, temp.Store().Record([NoLights("stue"), NoSuchArea("gang"), NoLights("kjokken")]).Count,
			"and the room that resolved in between is new again when it fails once more");
	}

	// A note nobody can parse costs the note and nothing else: it degrades to reporting, never to silence.
	[TestMethod]
	public void A_Corrupt_File_Reports_Everything_Again_Without_Throwing()
	{
		using TempDirectory temp = new();
		AreaSetupMemoryStore store = temp.Store();
		store.Record([NoLights()]);

		File.WriteAllText(store.FilePath, "{ this is not json");
		Assert.AreEqual(1, temp.Store().Record([NoLights()]).Count, "unparseable is nothing remembered");

		File.WriteAllText(store.FilePath, "[1, 2, 3]");
		Assert.AreEqual(1, temp.Store().Record([NoLights()]).Count, "valid JSON of the wrong shape is the same");

		File.WriteAllText(store.FilePath, """{ "version": 1 }""");
		Assert.AreEqual(1, temp.Store().Record([NoLights()]).Count, "and so is a file that names no rooms");
	}

	[TestMethod]
	public void A_Missing_Directory_Is_Created()
	{
		using TempDirectory temp = new();
		string nested = Path.Combine(temp.Path, "nested", "b1.yaml");

		Assert.AreEqual(1, new AreaSetupMemoryStore(nested, NullLogger<AreaSetupMemoryStore>.Instance).Record([NoLights()]).Count);
		Assert.AreEqual(0, new AreaSetupMemoryStore(nested, NullLogger<AreaSetupMemoryStore>.Instance).Record([NoLights()]).Count);
	}

	// The file says what it is, so nobody finding it takes it for their settings.
	[TestMethod]
	public void The_File_Explains_Itself_And_Names_The_Room()
	{
		using TempDirectory temp = new();
		AreaSetupMemoryStore store = temp.Store();
		store.Record([NoLights()]);

		string text = File.ReadAllText(store.FilePath);
		StringAssert.Contains(text, "Machine-written");
		StringAssert.Contains(text, "deleting this file is safe");
		StringAssert.Contains(text, "stue");
	}
}
