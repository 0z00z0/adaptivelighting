using System.Text.RegularExpressions;

using AdaptiveLighting.NetDaemon;

using Microsoft.Extensions.Logging;

using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Parsing;

namespace AdaptiveLighting.Tests.Logging;

/// <summary>What one log event becomes on disk: a dated single line, never the interpolated message.</summary>
[TestClass]
public sealed class CircularLogSinkTests
{
	private const string LongLivedToken =
		"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiI5YjEyIiwiaWF0IjoxNzU0MzUyMDAwfQ.Qm9ndXNTaWduYXR1cmVGb3JBVGVzdA";

	private static readonly DateTimeOffset Midnight = new(2026, 8, 5, 0, 3, 12, TimeSpan.FromHours(2));

	private static readonly MessageTemplateParser Parser = new();

	private static LogEvent Event(
		string template,
		Exception? exception = null,
		LogEventLevel level = LogEventLevel.Debug,
		params (string Name, object? Value)[] properties) =>
		new(
			Midnight,
			level,
			exception,
			Parser.Parse(template),
			[.. properties.Select(pair => new LogEventProperty(pair.Name, new ScalarValue(pair.Value)))]);

	private sealed class TempDirectory : IDisposable
	{
		public TempDirectory() =>
			Path = Directory.CreateDirectory(
				System.IO.Path.Combine(System.IO.Path.GetTempPath(), "adaptive-lighting-sink-" + Guid.NewGuid().ToString("N"))).FullName;

		public string Path { get; }

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

	// ===================== the line's shape =====================

	[TestMethod]
	public void Every_Line_Opens_With_A_Full_Iso_Date_And_An_Offset()
	{
		string line = CircularLogSink.Render(Event("nothing happened"));

		// Shape, not a wall clock: CI runs in UTC.
		Assert.IsTrue(
			Regex.IsMatch(line, @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}[+-]\d{2}:\d{2} "),
			line);
	}

	[TestMethod]
	public void The_Timestamp_Is_The_Events_Own_Instant_And_Keeps_Its_Offset()
	{
		string line = CircularLogSink.Render(Event("nothing happened"));

		StringAssert.StartsWith(line, "2026-08-05 00:03:12.000+02:00 ");
	}

	[TestMethod]
	public void The_Level_And_The_Source_Are_Written_The_Way_The_Console_Writes_Them()
	{
		LogEvent logEvent = Event(
			"stue is lit",
			level: LogEventLevel.Warning,
			properties: [("SourceContext", "AdaptiveLighting.Engine.AreaController")]);

		StringAssert.Contains(CircularLogSink.Render(logEvent), "WRN AdaptiveLighting.Engine.AreaController | stue is lit");
	}

	[TestMethod]
	public void A_Missing_Source_Context_Reads_As_A_Dash_Rather_Than_A_Gap()
	{
		StringAssert.Contains(CircularLogSink.Render(Event("nothing happened")), " DBG - | nothing happened");
	}

	[TestMethod]
	public void An_Exception_Joins_The_Same_Line_Rather_Than_Starting_New_Ones()
	{
		string line = CircularLogSink.Render(Event("could not write", new IOException("the disk is full")));

		Assert.IsFalse(line.Contains('\n'), line);
		StringAssert.Contains(line, "System.IO.IOException");
		StringAssert.Contains(line, "the disk is full");
	}

	[TestMethod]
	public void A_Multi_Line_Property_Cannot_Fake_A_Second_Entry()
	{
		string line = CircularLogSink.Render(Event(
			"{Detail}",
			properties: [("Detail", "lux 86\r\n2026-08-05 00:00:00.000+02:00 ERR forged | everything is fine")]));

		Assert.IsFalse(line.Contains('\n'), line);
		Assert.IsFalse(line.Contains('\r'), line);
	}

	// ===================== the message is rendered, never taken =====================

	[TestMethod]
	public void A_Token_Passed_As_A_Property_Never_Reaches_The_Line()
	{
		string line = CircularLogSink.Render(Event(
			"connecting to {Host} with {Token}",
			properties: [("Host", "10.0.0.22"), ("Token", LongLivedToken)]));

		Assert.IsFalse(line.Contains("eyJ", StringComparison.Ordinal), line);
		StringAssert.Contains(line, "connecting to 10.0.0.22 with " + LoggedValue.Hidden);
	}

	[TestMethod]
	public void A_Token_Under_An_Innocent_Property_Name_Is_Still_Caught_By_Its_Shape()
	{
		string line = CircularLogSink.Render(Event("read {Value}", properties: [("Value", LongLivedToken)]));

		Assert.IsFalse(line.Contains("eyJ", StringComparison.Ordinal), line);
	}

	[TestMethod]
	public void A_Template_That_Is_Itself_Runtime_Text_Is_Filtered_Like_Any_Other_Value()
	{
		// ILogger.Log(someString) compiles, and then the template is runtime data too.
		string line = CircularLogSink.Render(Event("mounting smb://espen:hunter2@nas/config"));

		Assert.IsFalse(line.Contains("hunter2", StringComparison.Ordinal), line);
	}

	[TestMethod]
	public void A_Credential_Inside_An_Exception_Message_Is_Filtered_Too()
	{
		string line = CircularLogSink.Render(Event(
			"login failed",
			new InvalidOperationException("rejected password=hunter2 for the share")));

		Assert.IsFalse(line.Contains("hunter2", StringComparison.Ordinal), line);
	}

	[TestMethod]
	public void A_Property_The_Template_Names_But_The_Event_Lacks_Is_Left_As_Its_Placeholder()
	{
		StringAssert.Contains(CircularLogSink.Render(Event("area {AreaName} reported")), "area {AreaName} reported");
	}

	// ===================== through the real logging pipeline =====================

	[TestMethod]
	public void A_Token_Logged_Through_ILogger_Never_Reaches_The_File()
	{
		using TempDirectory temp = new();
		CircularLogWriter writer = new(temp.Path, "b1");

		using Serilog.Core.Logger serilog = new LoggerConfiguration()
			.MinimumLevel.Debug()
			.WriteTo.Sink(new CircularLogSink(writer))
			.CreateLogger();

		using SerilogLoggerFactory factory = new(serilog);

		factory.CreateLogger("AdaptiveLighting.Ha.Connection")
			.LogInformation("connecting to {Host} as {Token}", "10.0.0.22", LongLivedToken);

		string written = File.ReadAllText(writer.ActivePath);

		Assert.IsFalse(written.Contains("eyJ", StringComparison.Ordinal), written);
		StringAssert.Contains(written, "INF AdaptiveLighting.Ha.Connection | connecting to 10.0.0.22 as " + LoggedValue.Hidden);
	}
}
