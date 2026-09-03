using AdaptiveLighting.NetDaemon;

namespace AdaptiveLighting.Tests.Logging;

/// <summary>A sink that cannot write says so once, through a channel that exists.</summary>
[TestClass]
public sealed class LogFailureReportTests
{
	private static readonly DateTimeOffset Start = new(2026, 8, 5, 0, 3, 12, TimeSpan.FromHours(2));

	private sealed class Clock
	{
		public DateTimeOffset Now { get; set; } = Start;

		public DateTimeOffset Read() => Now;
	}

	private static string SelfLogLine(string body) =>
		Start.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + " " + body + Environment.NewLine;

	[TestMethod]
	public void A_Failure_Is_Reported()
	{
		List<string> reported = [];
		LogFailureReport report = new(reported.Add, () => Start);

		report.Write(SelfLogLine("could not open b1-20260805.log"));

		Assert.AreEqual(1, reported.Count);
		StringAssert.Contains(reported[0], "could not open b1-20260805.log");
	}

	[TestMethod]
	public void The_Same_Failure_Is_Reported_Once_Per_Outage_And_Not_Once_Per_Line()
	{
		List<string> reported = [];
		Clock clock = new();
		LogFailureReport report = new(reported.Add, clock.Read);

		for (int i = 0; i < 200; i++)
		{
			clock.Now = Start.AddSeconds(i);
			report.Write(SelfLogLine("could not open b1-20260805.log"));
		}

		Assert.AreEqual(1, reported.Count, string.Join(" | ", reported));
	}

	/// <summary>SelfLog prefixes its own timestamp, which differs on every repeat and would defeat a whole-line comparison.</summary>
	[TestMethod]
	public void A_Repeat_Is_Recognised_Even_Though_SelfLog_Restamps_It()
	{
		List<string> reported = [];
		LogFailureReport report = new(reported.Add, () => Start);

		report.Write("2026-08-05T00:03:12.0000000Z the disk is full");
		report.Write("2026-08-05T00:07:44.0000000Z the disk is full");

		Assert.AreEqual(1, reported.Count, string.Join(" | ", reported));
	}

	[TestMethod]
	public void A_Different_Failure_Is_Never_Held_Back()
	{
		List<string> reported = [];
		LogFailureReport report = new(reported.Add, () => Start);

		report.Write(SelfLogLine("could not open b1-20260805.log"));
		report.Write(SelfLogLine("the disk is full"));

		Assert.AreEqual(2, reported.Count);
	}

	[TestMethod]
	public void A_Later_Outage_Is_A_Second_Thing_An_Operator_Has_Not_Been_Told()
	{
		List<string> reported = [];
		Clock clock = new();
		LogFailureReport report = new(reported.Add, clock.Read);

		report.Write(SelfLogLine("the disk is full"));

		clock.Now = Start + LogFailureReport.RepeatAfter + TimeSpan.FromSeconds(1);
		report.Write(SelfLogLine("the disk is full"));

		Assert.AreEqual(2, reported.Count);
	}

	[TestMethod]
	public void A_Working_Sink_Is_Silent()
	{
		List<string> reported = [];

		_ = new LogFailureReport(reported.Add, () => Start);

		Assert.AreEqual(0, reported.Count);
	}
}
