using System.Globalization;

using AdaptiveLighting.NetDaemon;

using Serilog;

namespace AdaptiveLighting.Tests.Logging;

/// <summary>The durable log's files: dated, bounded in days and in bytes, and never filling <c>/config</c>.</summary>
[TestClass]
public sealed class DurableLogFileTests
{
	private const long SmallCap = 4096;
	private const int SmallCount = 2;

	private sealed class TempDirectory : IDisposable
	{
		public TempDirectory() =>
			Path = Directory.CreateDirectory(
				System.IO.Path.Combine(System.IO.Path.GetTempPath(), "adaptive-lighting-log-" + Guid.NewGuid().ToString("N"))).FullName;

		public string Path { get; }

		public string LogFolder => System.IO.Path.Combine(Path, DurableLogFile.FolderName);

		public Serilog.Core.Logger Logger(string stem = "b1", long cap = SmallCap, int count = SmallCount) =>
			DurableLogFile.AddTo(new LoggerConfiguration().MinimumLevel.Debug(), LogFolder, stem, cap, count)
				.CreateLogger();

		public string[] Files => [.. Directory.GetFiles(LogFolder).Select(System.IO.Path.GetFileName)!];

		public long TotalBytes => Directory.GetFiles(LogFolder).Sum(path => new FileInfo(path).Length);

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

	private static void Fill(Serilog.Core.Logger logger, int lines)
	{
		for (int i = 0; i < lines; i++)
			logger.Debug("line {Index} {Padding}", i, new string('.', 40));
	}

	// ===================== what the bound actually is =====================

	/// <summary>The ceiling is quoted in megabytes in the documentation; a silent change to either constant is a lie there.</summary>
	[TestMethod]
	public void The_Stated_Ceiling_Is_What_The_Constants_Multiply_Out_To()
	{
		Assert.AreEqual(60L * 1024 * 1024, DurableLogFile.RetainedFileCount * DurableLogFile.MaxFileBytes);
		Assert.AreEqual(TimeSpan.FromDays(14), DurableLogFile.RetainedFileTime);
	}

	/// <summary>A fortnight at the measured 111 kB/h is 37 MB, so a day has to fit in one file for the day count to hold.</summary>
	[TestMethod]
	public void A_Day_At_The_Measured_Rate_Fits_In_One_File_So_Retention_Answers_In_Days()
	{
		const long MeasuredBytesPerHour = 111_000;

		Assert.IsTrue(
			MeasuredBytesPerHour * 24 < DurableLogFile.MaxFileBytes,
			$"a day is {MeasuredBytesPerHour * 24} bytes against a file limit of {DurableLogFile.MaxFileBytes}");

		Assert.IsTrue(
			MeasuredBytesPerHour * 24 * DurableLogFile.RetainedFileTime.Days
				< DurableLogFile.RetainedFileCount * DurableLogFile.MaxFileBytes,
			"the time limit has to bind before the count limit at the measured rate");
	}

	// ===================== where the files land =====================

	[TestMethod]
	public void The_Files_Take_Their_Name_From_The_Configuration_Document_And_Carry_The_Date()
	{
		using TempDirectory temp = new();

		using (Serilog.Core.Logger logger = temp.Logger("cabin"))
			logger.Debug("one line");

		string name = temp.Files.Single();

		StringAssert.StartsWith(name, "cabin-");
		StringAssert.EndsWith(name, ".log");
		StringAssert.Contains(name, DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
	}

	[TestMethod]
	public void The_Directory_Is_Created_So_The_First_Line_Has_Somewhere_To_Go()
	{
		using TempDirectory temp = new();

		Assert.IsFalse(Directory.Exists(temp.LogFolder));

		using (Serilog.Core.Logger logger = temp.Logger())
			logger.Debug("one line");

		Assert.IsTrue(Directory.Exists(temp.LogFolder));
	}

	// ===================== surviving a restart =====================

	[TestMethod]
	public void Lines_Written_Before_A_Restart_Are_Still_There_After_One()
	{
		using TempDirectory temp = new();

		using (Serilog.Core.Logger before = temp.Logger())
			before.Debug("before the restart");

		using (Serilog.Core.Logger after = temp.Logger())
			after.Debug("after the restart");

		string[] lines = File.ReadAllLines(Path.Combine(temp.LogFolder, temp.Files.Single()));

		Assert.AreEqual(2, lines.Length);
		StringAssert.Contains(lines[0], "before the restart");
		StringAssert.Contains(lines[1], "after the restart");
	}

	// ===================== the cap =====================

	/// <summary>The sink writes the last event within the limit in full, so the overshoot is one capped line and no more.</summary>
	[TestMethod]
	public void No_File_Exceeds_The_Cap_By_More_Than_A_Single_Line()
	{
		using TempDirectory temp = new();

		using (Serilog.Core.Logger logger = temp.Logger())
			Fill(logger, 2000);

		long largest = Directory.GetFiles(temp.LogFolder).Max(path => new FileInfo(path).Length);

		Assert.IsTrue(
			largest <= SmallCap + DurableLogFormatter.MaxLineChars,
			$"a file reached {largest} bytes against a cap of {SmallCap}");
	}

	// ===================== the rotation =====================

	[TestMethod]
	public void Rotation_Keeps_No_More_Than_The_Retained_Count_However_Long_The_House_Runs()
	{
		using TempDirectory temp = new();

		// Fifty times the cap: without retention this would leave a numbered series a hundred long.
		using (Serilog.Core.Logger logger = temp.Logger())
			Fill(logger, 4000);

		Assert.AreEqual(SmallCount, temp.Files.Length, string.Join(" | ", temp.Files));
	}

	[TestMethod]
	public void The_Directory_Stays_Under_The_Ceiling_The_Two_Limits_Multiply_Out_To()
	{
		using TempDirectory temp = new();

		using (Serilog.Core.Logger logger = temp.Logger())
			Fill(logger, 4000);

		long ceiling = SmallCount * (SmallCap + DurableLogFormatter.MaxLineChars);

		Assert.IsTrue(temp.TotalBytes <= ceiling, $"{temp.TotalBytes} bytes against a ceiling of {ceiling}");
	}

	[TestMethod]
	public void The_Newest_Lines_Are_The_Ones_Kept()
	{
		using TempDirectory temp = new();

		using (Serilog.Core.Logger logger = temp.Logger())
			Fill(logger, 4000);

		string newest = temp.Files.Order(StringComparer.Ordinal).Last();
		string oldest = temp.Files.Order(StringComparer.Ordinal).First();

		StringAssert.Contains(File.ReadAllText(Path.Combine(temp.LogFolder, newest)), "line 3999 ");
		Assert.IsFalse(
			File.ReadAllText(Path.Combine(temp.LogFolder, oldest)).Contains("line 3999 ", StringComparison.Ordinal),
			"the rolled file should hold older lines, not the newest one");
	}

	// ===================== retention that answers in days, not only in bytes =====================

	/// <summary>The count limit is left high, so only <see cref="DurableLogFile.RetainedFileTime"/> can be what removes this.</summary>
	[TestMethod]
	public void A_File_Older_Than_The_Retained_Time_Is_Removed_Even_When_The_Count_Limit_Is_Nowhere_Near()
	{
		using TempDirectory temp = new();
		Directory.CreateDirectory(temp.LogFolder);

		string stale = "b1-" + DateTime.Now.AddDays(-(DurableLogFile.RetainedFileTime.Days + 16))
			.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log";

		File.WriteAllText(Path.Combine(temp.LogFolder, stale), "older than a fortnight");

		using (Serilog.Core.Logger logger = temp.Logger(count: 60))
			logger.Debug("one line");

		CollectionAssert.DoesNotContain(temp.Files, stale, string.Join(" | ", temp.Files));
	}

	[TestMethod]
	public void A_File_Inside_The_Retained_Time_Is_Kept()
	{
		using TempDirectory temp = new();
		Directory.CreateDirectory(temp.LogFolder);

		string recent = "b1-" + DateTime.Now.AddDays(-2).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log";

		File.WriteAllText(Path.Combine(temp.LogFolder, recent), "from the day before yesterday");

		using (Serilog.Core.Logger logger = temp.Logger(count: 60))
			logger.Debug("one line");

		CollectionAssert.Contains(temp.Files, recent, string.Join(" | ", temp.Files));
	}

	// ===================== the undated pair an earlier version wrote =====================

	/// <summary>Serilog's retention matches the dated template, so it would never reach these and the ceiling would be wrong.</summary>
	[TestMethod]
	public void The_Undated_Files_An_Earlier_Version_Wrote_Are_Removed()
	{
		using TempDirectory temp = new();
		Directory.CreateDirectory(temp.LogFolder);

		File.WriteAllText(Path.Combine(temp.LogFolder, "b1.log"), "an old active file");
		File.WriteAllText(Path.Combine(temp.LogFolder, "b1.1.log"), "an old rolled file");

		using (Serilog.Core.Logger logger = temp.Logger())
			logger.Debug("one line");

		CollectionAssert.DoesNotContain(temp.Files, "b1.log");
		CollectionAssert.DoesNotContain(temp.Files, "b1.1.log");
		Assert.AreEqual(1, temp.Files.Length, string.Join(" | ", temp.Files));
	}

	[TestMethod]
	public void Another_Houses_Files_In_A_Shared_Directory_Are_Left_Alone()
	{
		using TempDirectory temp = new();
		Directory.CreateDirectory(temp.LogFolder);

		File.WriteAllText(Path.Combine(temp.LogFolder, "cabin.log"), "the other house");

		using (Serilog.Core.Logger logger = temp.Logger("b1"))
			logger.Debug("one line");

		CollectionAssert.Contains(temp.Files, "cabin.log");
	}

	// ===================== failure costs the line and nothing else =====================

	[TestMethod]
	public void A_File_That_Cannot_Be_Opened_Costs_The_Line_And_Does_Not_Throw()
	{
		using TempDirectory temp = new();
		Directory.CreateDirectory(temp.LogFolder);

		// A directory sitting where today's file goes: the write cannot succeed, and must not take the host with it.
		string blocked = Path.Combine(
			temp.LogFolder,
			"b1-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

		Directory.CreateDirectory(blocked);

		using (Serilog.Core.Logger logger = temp.Logger())
			logger.Debug("this line is lost");

		Assert.IsTrue(Directory.Exists(blocked));
	}

	[TestMethod]
	public void A_Directory_That_Cannot_Be_Created_Does_Not_Stop_The_Host_Starting()
	{
		using TempDirectory temp = new();

		// A file where the log folder goes, so creating the directory cannot succeed.
		File.WriteAllText(temp.LogFolder, "not a directory");

		using Serilog.Core.Logger logger = temp.Logger();

		logger.Debug("this line is lost");

		Assert.IsFalse(Directory.Exists(temp.LogFolder));
	}
}
