using System.Globalization;

using AdaptiveLighting.NetDaemon;

namespace AdaptiveLighting.Tests.Logging;

/// <summary>The durable log's file: surviving a restart, stopping at the cap, and never filling <c>/config</c>.</summary>
[TestClass]
public sealed class CircularLogWriterTests
{
	private const int SmallCap = 4096;

	private sealed class TempDirectory : IDisposable
	{
		public TempDirectory() =>
			Path = Directory.CreateDirectory(
				System.IO.Path.Combine(System.IO.Path.GetTempPath(), "adaptive-lighting-log-" + Guid.NewGuid().ToString("N"))).FullName;

		public string Path { get; }

		public string LogFolder => System.IO.Path.Combine(Path, CircularLogWriter.FolderName);

		public CircularLogWriter Writer(string stem = "b1", int cap = SmallCap) => new(LogFolder, stem, cap);

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

	private static long TotalBytes(string directory) =>
		Directory.GetFiles(directory).Sum(path => new FileInfo(path).Length);

	// ===================== where the files land =====================

	[TestMethod]
	public void The_Files_Take_Their_Name_From_The_Configuration_Document()
	{
		using TempDirectory temp = new();
		CircularLogWriter writer = temp.Writer("cabin");

		Assert.AreEqual(Path.Combine(temp.LogFolder, "cabin.log"), writer.ActivePath);
		Assert.AreEqual(Path.Combine(temp.LogFolder, "cabin.1.log"), writer.RolledPath);
	}

	[TestMethod]
	public void The_Directory_Is_Created_On_Construction_So_The_First_Line_Has_Somewhere_To_Go()
	{
		using TempDirectory temp = new();

		Assert.IsFalse(Directory.Exists(temp.LogFolder));

		temp.Writer();

		Assert.IsTrue(Directory.Exists(temp.LogFolder));
	}

	// ===================== surviving a restart =====================

	[TestMethod]
	public void Lines_Written_Before_A_Restart_Are_Still_There_After_One()
	{
		using TempDirectory temp = new();

		temp.Writer().Append("before the restart");
		temp.Writer().Append("after the restart");

		string[] lines = File.ReadAllLines(temp.Writer().ActivePath);

		Assert.AreEqual(2, lines.Length);
		Assert.AreEqual("before the restart", lines[0]);
		Assert.AreEqual("after the restart", lines[1]);
	}

	// ===================== the cap =====================

	[TestMethod]
	public void The_Active_File_Never_Exceeds_The_Cap()
	{
		using TempDirectory temp = new();
		CircularLogWriter writer = temp.Writer();

		for (int i = 0; i < 2000; i++)
		{
			writer.Append($"2026-08-05 00:03:12.000+00:00 DBG AdaptiveLighting.Engine.AreaController | line {i}");

			long active = new FileInfo(writer.ActivePath).Length;

			Assert.IsTrue(active <= SmallCap, $"active file reached {active} bytes on line {i}");
		}
	}

	[TestMethod]
	public void One_Line_Is_Truncated_Rather_Than_Rotating_The_File_Away_On_Every_Call()
	{
		using TempDirectory temp = new();
		CircularLogWriter writer = temp.Writer(cap: 64);

		writer.Append(new string('x', 10_000));
		writer.Append(new string('y', 10_000));

		Assert.AreEqual(CircularLogWriter.MaxLineChars + 3, File.ReadAllLines(writer.ActivePath)[0].Length);
		Assert.IsTrue(File.Exists(writer.RolledPath));
		Assert.AreEqual(2, Directory.GetFiles(temp.LogFolder).Length);
	}

	// ===================== the rotation =====================

	[TestMethod]
	public void Rotation_Keeps_Exactly_Two_Files_However_Long_The_House_Runs()
	{
		using TempDirectory temp = new();
		CircularLogWriter writer = temp.Writer();

		// Fifty times the cap: a numbered series or a dated name would leave a hundred files behind.
		for (int i = 0; i < 4000; i++)
			writer.Append($"line {i} " + new string('.', 40));

		Assert.AreEqual(2, Directory.GetFiles(temp.LogFolder).Length);
		Assert.IsTrue(TotalBytes(temp.LogFolder) <= 2 * SmallCap, TotalBytes(temp.LogFolder).ToString(CultureInfo.InvariantCulture));
	}

	[TestMethod]
	public void A_Rolled_File_Is_Overwritten_By_The_Next_Rotation_Not_Joined_By_A_Second()
	{
		using TempDirectory temp = new();
		CircularLogWriter writer = temp.Writer();

		for (int i = 0; i < 4000; i++)
			writer.Append($"line {i} " + new string('.', 40));

		string[] names = [.. Directory.GetFiles(temp.LogFolder).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal)];

		CollectionAssert.AreEqual((string[])["b1.1.log", "b1.log"], names);
	}

	[TestMethod]
	public void The_Newest_Lines_Are_The_Ones_Kept()
	{
		using TempDirectory temp = new();
		CircularLogWriter writer = temp.Writer();

		for (int i = 0; i < 4000; i++)
			writer.Append($"line {i} " + new string('.', 40));

		string[] active = File.ReadAllLines(writer.ActivePath);

		StringAssert.StartsWith(active[^1], "line 3999 ");
		Assert.IsFalse(File.ReadAllText(writer.RolledPath).Contains("line 3999 ", StringComparison.Ordinal));
	}

	// ===================== failure costs the line and nothing else =====================

	[TestMethod]
	public void A_File_That_Cannot_Be_Opened_Costs_The_Line_And_Does_Not_Throw()
	{
		using TempDirectory temp = new();
		CircularLogWriter writer = temp.Writer();

		// A directory sitting where the file goes: the append cannot succeed, and must not take the host with it.
		Directory.CreateDirectory(writer.ActivePath);

		writer.Append("this line is lost");

		Assert.IsTrue(Directory.Exists(writer.ActivePath));
	}

	// ===================== a failure is visible =====================

	/// <summary>Nothing in this repository enables <c>Serilog.Debugging.SelfLog</c>, so a sink reporting only there would be silent.</summary>
	[TestMethod]
	public void A_Sink_That_Cannot_Write_Says_So_Through_A_Channel_That_Exists()
	{
		using TempDirectory temp = new();
		List<string> reported = [];
		CircularLogWriter writer = new(temp.LogFolder, "b1", SmallCap, reported.Add);

		writer.Append("this one lands");

		Assert.AreEqual(0, reported.Count, "a working sink is silent");

		File.Delete(writer.ActivePath);
		Directory.CreateDirectory(writer.ActivePath);
		writer.Append("this one cannot");

		Assert.AreEqual(1, reported.Count);
		StringAssert.Contains(reported[0], writer.ActivePath, StringComparison.Ordinal);
	}

	[TestMethod]
	public void A_Failing_Sink_Reports_Once_Per_Outage_And_Not_Once_Per_Line()
	{
		using TempDirectory temp = new();
		List<string> reported = [];
		CircularLogWriter writer = new(temp.LogFolder, "b1", SmallCap, reported.Add);

		Directory.CreateDirectory(writer.ActivePath);

		for (int i = 0; i < 200; i++)
			writer.Append($"line {i}");

		Assert.AreEqual(1, reported.Count, string.Join(" | ", reported));
	}

	/// <summary>The directory is created in the constructor, so recreating it is the failure path's job.</summary>
	[TestMethod]
	public void A_Log_Directory_Removed_Under_A_Running_House_Is_Put_Back_And_Reported_Again()
	{
		using TempDirectory temp = new();
		List<string> reported = [];
		CircularLogWriter writer = new(temp.LogFolder, "b1", SmallCap, reported.Add);

		writer.Append("before");
		Directory.Delete(temp.LogFolder, recursive: true);

		writer.Append("lost");
		writer.Append("after");

		Assert.AreEqual("after", File.ReadAllLines(writer.ActivePath).Single());
		Assert.AreEqual(1, reported.Count, string.Join(" | ", reported));

		Directory.Delete(temp.LogFolder, recursive: true);
		writer.Append("lost again");

		Assert.AreEqual(2, reported.Count, "a second outage is a second thing an operator has not been told");
	}
}
